using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
    static AppExplorerPainter? instance;
    static int shutdownCount;

    readonly Func<string?> getAppDirectory;
    readonly ModuleIndex modules;
    readonly ILogService log;
    readonly bool diagnostics = Environment.GetEnvironmentVariable("MX_DOCHIGHLIGHTER_DIAG") == "1";
    readonly Dispatcher dispatcher;
    readonly DispatcherTimer timer;
    readonly AppExplorerContextMenu contextMenu;
    readonly List<WeakReference<DataGrid>> grids = new();
    readonly ConditionalWeakTable<DataGrid, object> hookedGrids = new();
    readonly ConditionalWeakTable<DataGridRow, RowState> rowStates = new();
    readonly ConditionalWeakTable<DataGridRow, RowMarks> marks = new();
    readonly Dictionary<(Type, string), PropertyInfo?> propertyCache = new();
    HashSet<string>? flaggedContainers;
    HighlightStore? flaggedContainersStore;
    int flaggedContainersVersion;
    DateTime flaggedContainersTimeUtc;
    int ticksUntilScan;
    bool redrawAll;
    int scansWithoutGrid;
    bool errorLogged;
    int diagnosticRowsLogged;

    AppExplorerPainter(Dispatcher dispatcher, Func<string?> getAppDirectory, ModuleIndex modules, ILogService log)
    {
        this.dispatcher = dispatcher;
        this.getAppDirectory = getAppDirectory;
        this.modules = modules;
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
        // If Shutdown comes before this queued start runs, the start must not happen, or a painter would keep running
        // for an extension that has already unloaded.
        var shutdownsBefore = Volatile.Read(ref shutdownCount);
        dispatcher.BeginInvoke(() =>
        {
            if (instance != null || Volatile.Read(ref shutdownCount) != shutdownsBefore) return;
            try
            {
                instance = new AppExplorerPainter(dispatcher, getAppDirectory, modules, log);
            }
            catch (Exception ex)
            {
                log.Error("DocHighlighter: could not start", ex);
            }
        });
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
        Interlocked.Increment(ref shutdownCount);
        var painter = Interlocked.Exchange(ref instance, null);
        if (painter is null) return;
        try
        {
            // While Studio Pro itself is closing the dispatcher may be gone; there is nothing left to restore then.
            if (!painter.dispatcher.HasShutdownStarted)
                painter.dispatcher.Invoke(painter.Stop);
        }
        catch (Exception ex)
        {
            painter.log.Error("DocHighlighter: could not stop cleanly", ex);
        }
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
            grid.PreviewKeyDown -= OnPreviewKeyDown;
            grid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            ForEachRealizedRow(grid, row =>
            {
                Apply(row, null);
                SetMarks(row, null, false);
            });
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

            // A full visual-tree scan is the expensive part, so only rescan every few seconds, a little sooner while no
            // App Explorer grid is known (it is closed, or no app is open) so it is picked up quickly once it appears.
            // The marks are also redrawn on these passes, in case a row changed in a way RowMarks.RenderKey misses.
            redrawAll = --ticksUntilScan <= 0;
            if (redrawAll)
            {
                ScanForGrids();
                ticksUntilScan = grids.Count == 0 ? 2 : 6;
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
            LogPaintError(ex);
        }
        finally
        {
            redrawAll = false;
        }
    }

    void LogPaintError(Exception ex)
    {
        if (errorLogged) return;
        errorLogged = true;
        log.Error("DocHighlighter: painting failed", ex);
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
        // Without a grid, scans run every 2 ticks of 750 ms, so 20 scans is about 30 seconds.
        if (++scansWithoutGrid != 20) return;
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
        grid.PreviewKeyDown += OnPreviewKeyDown;
        grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        var store = CurrentStore();
        log.Info($"DocHighlighter: attached to a document tree ({grid.GetType().FullName}); {store?.Count ?? 0} highlight(s) in {store?.FilePath ?? "<no app open>"}");
    }

    // Rows are virtualized and recycled while scrolling, so each newly loaded row is painted right away.
    // An exception here would escape into the DataGrid's row generation and so into Studio Pro; it must never leave.
    void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        try
        {
            PaintRow(e.Row, CurrentStore());
        }
        catch (Exception ex)
        {
            LogPaintError(ex);
        }
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RequestRepaint();

    // The strip follows the visible left edge, so redraw the marks right away when the tree scrolls sideways.
    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange == 0 || sender is not DataGrid grid) return;
        ForEachRealizedRow(grid, row =>
        {
            if (marks.TryGetValue(row, out var rowMarks)) rowMarks.InvalidateVisual();
        });
    }

    // Only fires while the App Explorer has keyboard focus, so the shortcuts never affect editors or other panes.
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is DataGrid grid && contextMenu.HandleHotkey(grid, e)) e.Handled = true;
    }

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
        var flag = false;
        if (store != null && store.Count > 0)
        {
            var id = GetUnitId(row.Item);
            if (id != null)
            {
                color = store.Get(id);
                if (color is null) flag = HasHighlightsInside(id, store);
            }
            if (diagnostics && diagnosticRowsLogged < 15)
            {
                diagnosticRowsLogged++;
                log.Info($"DocHighlighter[diag]: row item={row.Item?.GetType().FullName} id={id} color={color}");
            }
        }
        Apply(row, color);
        SetMarks(row, color is null ? null : HighlightBrushes.Strip(color.Value), flag);
    }

    /// <summary>
    /// Whether this is a module or folder row with highlighted items inside (its own highlight does not count). Every
    /// module and folder that holds a highlighted item, at any depth, is worked out in one pass and kept until the
    /// highlights change, and for at most 10 seconds to pick up documents moved between modules and folders.
    /// </summary>
    bool HasHighlightsInside(string id, HighlightStore store)
    {
        if (flaggedContainers is null || !ReferenceEquals(store, flaggedContainersStore) ||
            store.Version != flaggedContainersVersion || DateTime.UtcNow - flaggedContainersTimeUtc > TimeSpan.FromSeconds(10))
        {
            flaggedContainers = FindFlaggedContainers(store);
            flaggedContainersStore = store;
            flaggedContainersVersion = store.Version;
            flaggedContainersTimeUtc = DateTime.UtcNow;
        }
        return flaggedContainers.Contains(id);
    }

    HashSet<string> FindFlaggedContainers(HighlightStore store)
    {
        var parents = modules.GetContainerParents();
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, fileModule) in store.Highlighted())
        {
            // Start at the item's own container, so a highlighted module or folder does not flag itself. Items that are
            // files rather than model units are not in the model; for those the module recorded with them is used.
            var container = parents.TryGetValue(id, out var parent) ? parent : fileModule;
            // Once a container is in the set, so are all the ones above it.
            while (container != null && flagged.Add(container))
                container = parents.GetValueOrDefault(container);
        }
        return flagged;
    }

    /// <summary>Shows the strip and/or flag on a row, or removes the overlay when the row needs neither.</summary>
    void SetMarks(DataGridRow row, Brush? strip, bool flag)
    {
        marks.TryGetValue(row, out var rowMarks);
        // Rows recycled while scrolling can stay in the tree but hidden; they must not keep their marks.
        if ((strip is null && !flag) || !row.IsVisible)
        {
            if (rowMarks != null) RemoveMarks(row, rowMarks);
            return;
        }
        if (rowMarks is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(row);
            if (layer is null) return;
            rowMarks = new RowMarks(row, layer, LogPaintError);
            layer.Add(rowMarks);
            marks.Add(row, rowMarks);
            row.Unloaded += OnRowUnloaded;
        }
        // Redraw only when something the marks depend on changed: a recycled row showing another item, a rename (the
        // flag follows the name), selection (the flag takes the text color), size or theme. Horizontal scrolling is
        // handled by OnScrollChanged, and the regular full pass (redrawAll) catches anything else.
        var renderKey = (strip, flag, row.Item, row.Item is null ? null : GetLabel(row.Item), row.IsSelected,
            row.ActualWidth, row.ActualHeight, row.Foreground);
        if (!redrawAll && Equals(rowMarks.RenderKey, renderKey)) return;
        rowMarks.Strip = strip;
        rowMarks.Flag = flag;
        rowMarks.RenderKey = renderKey;
        rowMarks.InvalidateVisual();
    }

    void RemoveMarks(DataGridRow row, RowMarks rowMarks)
    {
        row.Unloaded -= OnRowUnloaded;
        // The layer the marks were added to: once the row has left the tree it can no longer be looked up from the row.
        rowMarks.Layer.Remove(rowMarks);
        marks.Remove(row);
    }

    // A row the grid discards (rather than recycles) is never painted again, and the adorner layer would keep it, and
    // the Studio Pro item behind it, alive. Rows that come back (a pane re-docked) are given new marks by the next pass.
    void OnRowUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is DataGridRow row && marks.TryGetValue(row, out var rowMarks)) RemoveMarks(row, rowMarks);
        }
        catch (Exception ex)
        {
            LogPaintError(ex);
        }
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
            state = new RowState(row.ReadLocalValue(Control.BackgroundProperty));
            rowStates.Add(row, state);
        }

        if (color is null)
        {
            if (state.Applied is null) return;
            state.Applied = null;
            Restore(row, Control.BackgroundProperty, state.Background);
            return;
        }
        state.Applied = color;

        // Compare with what the row shows right now, not with what was last set: Studio Pro can reset a row's look on
        // its own (while refreshing the tree or redrawing the selection), and the row must then be painted again.
        var fill = HighlightBrushes.Fill(color.Value);
        // Keep Studio Pro's own selection color visible on selected rows; the strip (see RowMarks) still shows the
        // highlight.
        if (row.IsSelected)
        {
            if (row.Background is SolidColorBrush current && IsHighlightFill(current)) Restore(row, Control.BackgroundProperty, state.Background);
        }
        else if (!ReferenceEquals(row.Background, fill))
        {
            row.Background = fill;
        }
    }

    static bool IsHighlightFill(Brush brush) =>
        Enum.GetValues<HighlightColor>().Any(c => ReferenceEquals(HighlightBrushes.Fill(c), brush));

    static void Restore(DependencyObject target, DependencyProperty property, object original)
    {
        if (original == DependencyProperty.UnsetValue)
            target.ClearValue(property);
        else
            target.SetValue(property, original);
    }

    sealed class RowState(object background)
    {
        public object Background { get; } = background;
        public HighlightColor? Applied { get; set; }
    }
}
