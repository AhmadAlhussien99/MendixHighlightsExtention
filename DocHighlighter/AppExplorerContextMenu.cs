using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Appends "Highlight > Green / Orange / Red / Remove highlight / Bulk remove" to the App Explorer's own
/// right-click menu, so it works for every item there (documents of any type, folders, modules, ...).
/// </summary>
internal sealed class AppExplorerContextMenu
{
    static readonly object InjectedTag = new();

    readonly AppExplorerPainter painter;
    readonly ModuleIndex modules;
    readonly ILogService log;
    bool enabled = true;

    public AppExplorerContextMenu(AppExplorerPainter painter, ModuleIndex modules, ILogService log)
    {
        this.painter = painter;
        this.modules = modules;
        this.log = log;
        // WPF class handlers cannot be unregistered, so Disable() turns this one into a no-op instead.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
    }

    public void Disable() => enabled = false;

    void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!enabled || sender is not ContextMenu menu || !ReferenceEquals(e.OriginalSource, menu)) return;
        try
        {
            RemoveInjectedItems(menu);

            // Use keyboard focus only when the menu has no target of its own; otherwise a menu opened elsewhere
            // (e.g. on an editor tab) while App Explorer still has focus would get a Highlight entry too.
            var anchor = menu.PlacementTarget as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
            var grid = FindExplorerGrid(anchor);
            if (grid is null) return;

            var store = painter.CurrentStore();
            if (store is null) return;

            var targets = new List<HighlightTarget>();
            foreach (var item in grid.SelectedItems)
                if (painter.GetUnitId(item) is { } id)
                    targets.Add(new HighlightTarget(id, painter.GetLabel(item), item));
            if (targets.Count == 0) return;

            // Module-only entries are offered on module rows themselves, so they are never hit by accident.
            var moduleIds = modules.GetModules().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var onModules = targets.All(t => moduleIds.Contains(t.Id));

            var separator = new Separator { Tag = InjectedTag };
            var highlight = BuildHighlightMenu(store, grid, targets, onModules);
            MatchNativeStyle(menu, separator, highlight);
            menu.Items.Add(separator);
            menu.Items.Add(highlight);
            if (painter.Diagnostics) LogMenuLayout(menu);
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: could not extend the App Explorer context menu", ex);
        }
    }

    MenuItem BuildHighlightMenu(HighlightStore store, DataGrid grid, List<HighlightTarget> targets, bool onModules)
    {
        var colors = targets.Select(t => store.Get(t.Id)).Distinct().ToList();
        HighlightColor? sharedColor = colors.Count == 1 ? colors[0] : null;

        var root = new MenuItem { Header = onModules ? ModuleCountsHeader(store, targets) : "Highlight", Tag = InjectedTag };
        foreach (var color in Enum.GetValues<HighlightColor>())
        {
            // IsChecked shows a check mark (in place of the swatch) on the color the selection already has.
            var item = new MenuItem { Header = color.ToString(), Icon = Swatch(color), IsChecked = sharedColor == color };
            item.Click += (_, _) => SetColor(store, grid, targets, color);
            root.Items.Add(item);
        }
        root.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Remove highlight", IsEnabled = colors.Any(c => c != null) };
        remove.Click += (_, _) => SetColor(store, grid, targets, null);
        root.Items.Add(remove);

        // Bulk removal sits one level deeper still, so it is never hit by accident.
        if (onModules)
        {
            var removeModule = new MenuItem { Header = "Remove all highlights in this module", IsEnabled = store.Count > 0 };
            removeModule.Click += (_, _) => RemoveModuleHighlights(store, targets);
            var bulk = new MenuItem { Header = "Bulk remove" };
            bulk.Items.Add(removeModule);
            root.Items.Add(bulk);
        }
        return root;
    }

    /// <summary>
    /// "Highlight  ■ 3  ■ 1  ■ 2": the Highlight header of a module row, with how many items in the selected
    /// module(s) have each color, so the counts are visible without opening the submenu.
    /// The modules' own highlights are not counted.
    /// </summary>
    StackPanel ModuleCountsHeader(HighlightStore store, List<HighlightTarget> moduleTargets)
    {
        var moduleIds = moduleTargets.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<HighlightColor, int>();
        foreach (var id in IdsInModules(store, moduleTargets))
            if (!moduleIds.Contains(id) && store.Get(id) is { } color)
                counts[color] = counts.GetValueOrDefault(color) + 1;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "Highlight", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var color in Enum.GetValues<HighlightColor>())
        {
            var swatch = Swatch(color);
            swatch.Width = swatch.Height = 10;
            swatch.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(swatch);
            header.Children.Add(new TextBlock
            {
                Text = counts.GetValueOrDefault(color).ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 6, 0),
            });
        }
        return header;
    }

    /// <summary>
    /// Items created in code get WPF's default menu look, whose padding and columns are wider than Studio Pro's.
    /// Borrow the styles Studio Pro gives its own items so the injected entries line up and the menu keeps its width.
    /// </summary>
    static void MatchNativeStyle(ContextMenu menu, Separator separator, MenuItem highlight)
    {
        var nativeItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => !ReferenceEquals(i.Tag, InjectedTag));
        var nativeSeparator = menu.Items.OfType<Separator>().FirstOrDefault(s => !ReferenceEquals(s.Tag, InjectedTag));
        if (nativeSeparator?.Style is { } separatorStyle && separatorStyle.TargetType.IsInstanceOfType(separator))
            separator.Style = separatorStyle;
        if (nativeItem?.Style is not { } itemStyle || !itemStyle.TargetType.IsInstanceOfType(highlight)) return;

        var pending = new Stack<MenuItem>();
        pending.Push(highlight);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            item.Style = itemStyle;
            foreach (var child in item.Items.OfType<MenuItem>())
                pending.Push(child);
        }
    }

    /// <summary>With MX_DOCHIGHLIGHTER_DIAG=1, logs each menu entry's type, style and width once the menu is laid out.</summary>
    void LogMenuLayout(ContextMenu menu) =>
        menu.Dispatcher.BeginInvoke(() =>
        {
            foreach (var entry in menu.Items.OfType<FrameworkElement>())
                log.Info($"DocHighlighter[diag]: menu entry {entry.GetType().FullName} header={(entry as HeaderedItemsControl)?.Header} " +
                         $"style={entry.Style?.TargetType.FullName ?? "<default>"} width={entry.ActualWidth:0} injected={ReferenceEquals(entry.Tag, InjectedTag)}");
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    void SetColor(HighlightStore store, DataGrid grid, List<HighlightTarget> targets, HighlightColor? color) =>
        Run("could not save highlight", () =>
        {
            var moduleIds = modules.GetModules().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var modelUnits = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                var fileModule = color is null ? null : FileModule(grid, target, moduleIds, modelUnits);
                store.Set(target.Id, target.Name, color, fileModule);
            }
        });

    /// <summary>
    /// Some App Explorer items are files on disk, not model units (for example Maia skills in Studio Pro 11, kept under
    /// skillssource), so the model cannot tell which module they are in. For those, the module is taken from the tree:
    /// the nearest module row above the item. Returns null for model units, whose module is always read from the model.
    /// </summary>
    string? FileModule(DataGrid grid, HighlightTarget target, HashSet<string> moduleIds, Dictionary<string, HashSet<string>> modelUnits)
    {
        // The grid's items are the expanded tree flattened in display order, so a module's contents follow its row.
        var items = grid.Items;
        for (var i = items.IndexOf(target.Item); i >= 0; i--)
        {
            if (painter.GetUnitId(items[i]) is not { } id || !moduleIds.Contains(id)) continue;
            if (!modelUnits.TryGetValue(id, out var units))
                modelUnits[id] = units = modules.GetUnitIdsInModule(id);
            return units.Contains(target.Id) ? null : id;
        }
        return null;
    }

    void RemoveModuleHighlights(HighlightStore store, List<HighlightTarget> moduleTargets) =>
        Run("could not remove the module's highlights", () => store.Remove(IdsInModules(store, moduleTargets)));

    /// <summary>Ids of everything in the modules: their model units, plus highlighted files recorded as being in them.</summary>
    HashSet<string> IdsInModules(HighlightStore store, List<HighlightTarget> moduleTargets)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in moduleTargets)
            ids.UnionWith(modules.GetUnitIdsInModule(module.Id));
        ids.UnionWith(store.IdsInModules(moduleTargets.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        return ids;
    }

    void Run(string failureMessage, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: " + failureMessage, ex);
        }
        AppExplorerPainter.RequestRepaint();
    }

    static Rectangle Swatch(HighlightColor color) => new()
    {
        Width = 12,
        Height = 12,
        RadiusX = 2,
        RadiusY = 2,
        Fill = HighlightBrushes.Strip(color),
    };

    static void RemoveInjectedItems(ContextMenu menu)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
            if (menu.Items[i] is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, InjectedTag))
                menu.Items.RemoveAt(i);
    }

    static DataGrid? FindExplorerGrid(DependencyObject? element)
    {
        for (var current = element; current != null; current = Parent(current))
            if (current is DataGrid grid && AppExplorerPainter.IsExplorerGrid(grid))
                return grid;
        return null;
    }

    static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    /// <summary>An App Explorer item the menu acts on: its unit id, the label shown in the tree, and its row item.</summary>
    sealed record HighlightTarget(string Id, string Name, object Item);
}
