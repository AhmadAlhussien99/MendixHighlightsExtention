using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Colors App Explorer rows. Studio Pro has no public API for decorating App Explorer items,
/// so this finds the App Explorer's WPF DataGrid and paints the realized rows directly.
/// It only touches visuals in memory; it never modifies the app model.
/// </summary>
internal sealed class AppExplorerPainter
{
    const string ExplorerGridTypeName = "TreeControlEtoDataGrid";
    static readonly Thickness StripThickness = new(4, 0, 0, 0);
    static AppExplorerPainter? instance;

    readonly Func<string?> getAppDirectory;
    readonly ILogService log;
    readonly bool diagnostics = Environment.GetEnvironmentVariable("MX_DOCHIGHLIGHTER_DIAG") == "1";
    readonly Dispatcher dispatcher;
    readonly DispatcherTimer timer;
    readonly AppExplorerContextMenu contextMenu;
    readonly List<WeakReference<DataGrid>> grids = new();
    readonly ConditionalWeakTable<DataGrid, object> hookedGrids = new();
    readonly ConditionalWeakTable<DataGridRow, RowState> rowStates = new();
    readonly Dictionary<(Type, string), PropertyInfo?> propertyCache = new();
    int tickCount;
    int scansWithoutGrid;
    bool errorLogged;
    int diagnosticRowsLogged;

    AppExplorerPainter(Dispatcher dispatcher, Func<string?> getAppDirectory, ModuleIndex modules, ILogService log)
    {
        this.dispatcher = dispatcher;
        this.getAppDirectory = getAppDirectory;
        this.log = log;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        timer.Start();
        contextMenu = new AppExplorerContextMenu(this, modules, log);
        log.Info("DocHighlighter: started; highlights are stored in " + HighlightStore.StorageRoot);
    }

    public bool Diagnostics => diagnostics;

    public static void EnsureStarted(Func<string?> getAppDirectory, ModuleIndex modules, ILogService log)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() => instance ??= new AppExplorerPainter(dispatcher, getAppDirectory, modules, log));
    }

    public static void RequestRepaint()
    {
        var painter = instance;
        painter?.dispatcher.BeginInvoke(painter.Tick);
    }

    /// <summary>
    /// Called when Studio Pro unloads the extension (for app-level installs this happens every time an app closes):
    /// stops all activity and gives every row its original look back, so a reloaded copy starts clean.
    /// </summary>
    public static void Shutdown()
    {
        var painter = instance;
        if (painter is null) return;
        instance = null;
        painter.dispatcher.Invoke(painter.Stop);
    }

    void Stop()
    {
        timer.Stop();
        contextMenu.Disable();
        foreach (var reference in grids)
        {
            if (!reference.TryGetTarget(out var grid)) continue;
            grid.LoadingRow -= OnLoadingRow;
            grid.SelectionChanged -= OnSelectionChanged;
            ForEachRealizedRow(grid, row => Apply(row, null));
        }
        grids.Clear();
        log.Info("DocHighlighter: stopped");
    }

    void Tick()
    {
        try
        {
            var store = CurrentStore();
            store?.ReloadIfChanged();

            // A full visual-tree scan is the expensive part, so only rescan every few seconds
            // or when no App Explorer grid is known yet.
            if (tickCount++ % 6 == 0 || grids.Count == 0)
            {
                ScanForGrids();
                if (store != null) WarnIfExplorerNotFound();
            }

            for (var i = grids.Count - 1; i >= 0; i--)
            {
                if (grids[i].TryGetTarget(out var grid) && PresentationSource.FromVisual(grid) != null)
                    PaintGrid(grid, store);
                else
                    grids.RemoveAt(i);
            }
        }
        catch (Exception ex)
        {
            if (!errorLogged)
            {
                errorLogged = true;
                log.Error("DocHighlighter: painting failed", ex);
            }
        }
    }

    public HighlightStore? CurrentStore()
    {
        var appDirectory = getAppDirectory();
        return appDirectory is null ? null : HighlightStore.ForApp(appDirectory, log);
    }

    /// <summary>Walks every Studio Pro window to find App Explorer grids (also floating or re-docked ones).</summary>
    void ScanForGrids()
    {
        foreach (Window window in Application.Current.Windows)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(window);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is DataGrid grid && IsExplorerGrid(grid))
                {
                    TrackGrid(grid);
                    continue;
                }
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    /// <summary>
    /// The App Explorer grid is found by Studio Pro's internal type name, which a future version may change.
    /// If an app has been open for about 30 seconds without it being found, say so once in the log.
    /// </summary>
    void WarnIfExplorerNotFound()
    {
        if (grids.Count > 0)
        {
            scansWithoutGrid = 0;
            return;
        }
        if (++scansWithoutGrid != 40) return;
        log.Warn($"DocHighlighter: the App Explorer ({ExplorerGridTypeName}) was not found. If it is open, this Studio Pro " +
                 "version may have changed its internals: highlight colors and the Highlight menu are unavailable. " +
                 "Please report your Studio Pro version.");
    }

    public static bool IsExplorerGrid(DataGrid grid)
    {
        for (var type = grid.GetType(); type != null; type = type.BaseType)
            if (type.Name == ExplorerGridTypeName) return true;
        return false;
    }

    void TrackGrid(DataGrid grid)
    {
        if (hookedGrids.TryGetValue(grid, out _)) return;
        hookedGrids.Add(grid, new object());
        grids.Add(new WeakReference<DataGrid>(grid));
        grid.LoadingRow += OnLoadingRow;
        grid.SelectionChanged += OnSelectionChanged;
        var store = CurrentStore();
        log.Info($"DocHighlighter: attached to a document tree ({grid.GetType().FullName}); {store?.Count ?? 0} highlight(s) in {store?.FilePath ?? "<no app open>"}");
    }

    // Rows are virtualized and recycled while scrolling, so each newly loaded row is painted right away.
    void OnLoadingRow(object? sender, DataGridRowEventArgs e) => PaintRow(e.Row, CurrentStore());

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RequestRepaint();

    void PaintGrid(DataGrid grid, HighlightStore? store) => ForEachRealizedRow(grid, row => PaintRow(row, store));

    static void ForEachRealizedRow(DataGrid grid, Action<DataGridRow> action)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(grid);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is DataGridRow row)
            {
                action(row);
                continue;
            }
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
                stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    void PaintRow(DataGridRow row, HighlightStore? store)
    {
        HighlightColor? color = null;
        if (store != null && store.Count > 0)
        {
            var id = GetUnitId(row.Item);
            if (id != null) color = store.Get(id);
            if (diagnostics && diagnosticRowsLogged < 15)
            {
                diagnosticRowsLogged++;
                log.Info($"DocHighlighter[diag]: row item={row.Item?.GetType().FullName} id={id} color={color}");
            }
        }
        Apply(row, color);
    }

    /// <summary>
    /// The row item is Studio Pro's tree item view model; its Node is a ProjectExplorerNode whose Unit
    /// carries the same id that the public extensions API exposes as IAbstractUnit.Id.
    /// </summary>
    public string? GetUnitId(object? item)
    {
        if (item is null) return null;
        var node = GetProperty(item, "Node") ?? item;
        var unit = GetProperty(node, "Unit");
        return IdUtil.Normalize((unit != null ? GetProperty(unit, "Id") : null) ?? GetProperty(node, "Id"));
    }

    /// <summary>The text App Explorer shows for the row; only used to make the stored JSON readable.</summary>
    public string GetLabel(object item)
    {
        var node = GetProperty(item, "Node") ?? item;
        return (GetProperty(node, "Label") ?? GetProperty(node, "ViewLabel"))?.ToString() ?? "";
    }

    object? GetProperty(object target, string name)
    {
        var key = (target.GetType(), name);
        if (!propertyCache.TryGetValue(key, out var property))
        {
            property = FindProperty(key.Item1, name);
            propertyCache[key] = property;
        }
        try
        {
            return property?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static PropertyInfo? FindProperty(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var property in t.GetProperties(flags))
                if (property.Name == name && property.GetIndexParameters().Length == 0)
                    return property;
        }
        // Properties only reachable through an interface.
        foreach (var iface in type.GetInterfaces())
        {
            var property = iface.GetProperty(name);
            if (property != null && property.GetIndexParameters().Length == 0) return property;
        }
        return null;
    }

    void Apply(DataGridRow row, HighlightColor? color)
    {
        if (!rowStates.TryGetValue(row, out var state))
        {
            if (color is null) return;
            state = new RowState(
                row.ReadLocalValue(Control.BackgroundProperty),
                row.ReadLocalValue(Control.BorderBrushProperty),
                row.ReadLocalValue(Control.BorderThicknessProperty));
            rowStates.Add(row, state);
        }

        var selected = row.IsSelected;
        if (state.Applied == color && state.AppliedWhileSelected == selected) return;
        state.Applied = color;
        state.AppliedWhileSelected = selected;

        if (color is null)
        {
            Restore(row, Control.BackgroundProperty, state.Background);
            Restore(row, Control.BorderBrushProperty, state.BorderBrush);
            Restore(row, Control.BorderThicknessProperty, state.BorderThickness);
            return;
        }

        row.BorderBrush = HighlightBrushes.Strip(color.Value);
        row.BorderThickness = StripThickness;
        // Keep Studio Pro's own selection color visible on selected rows; the strip still shows the highlight.
        if (selected)
            Restore(row, Control.BackgroundProperty, state.Background);
        else
            row.Background = HighlightBrushes.Fill(color.Value);
    }

    static void Restore(DependencyObject target, DependencyProperty property, object original)
    {
        if (original == DependencyProperty.UnsetValue)
            target.ClearValue(property);
        else
            target.SetValue(property, original);
    }

    sealed class RowState(object background, object borderBrush, object borderThickness)
    {
        public object Background { get; } = background;
        public object BorderBrush { get; } = borderBrush;
        public object BorderThickness { get; } = borderThickness;
        public HighlightColor? Applied { get; set; }
        public bool AppliedWhileSelected { get; set; }
    }
}
