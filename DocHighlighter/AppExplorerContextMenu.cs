using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Appends "Highlight", then Green / Orange / Red / Remove highlight, to the App Explorer's own right-click menu,
/// so it works for every item there (documents of any type, folders, modules, ...). On a module row, "Highlight"
/// also shows the module's counts and opens the bulk-removal submenu. The same actions are on Ctrl+Alt+1/2/3/0.
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

            var targets = SelectedTargets(grid);
            if (targets.Count == 0) return;

            // Module-only entries are offered on module rows themselves, so they are never hit by accident.
            var moduleIds = modules.GetModules().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var onModules = targets.All(t => moduleIds.Contains(t.Id));

            var items = BuildMenuItems(store, grid, targets, onModules);
            MatchNativeStyle(menu, items);
            foreach (var item in items)
                menu.Items.Add(item);
            if (painter.Diagnostics) LogMenuLayout(menu);
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: could not extend the App Explorer context menu", ex);
        }
    }

    /// <summary>
    /// Ctrl+Alt+1/2/3 colors the selected App Explorer items green/orange/red; Ctrl+Alt+0 removes their highlight.
    /// Returns whether the key was one of these, so the caller can mark it handled.
    /// </summary>
    public bool HandleHotkey(DataGrid grid, KeyEventArgs e)
    {
        // Leave keys alone while text is being typed (e.g. an inline rename), and when the extension is unloading.
        if (!enabled || e.OriginalSource is TextBoxBase || !TryGetHotkeyColor(e, out var color)) return false;
        try
        {
            var store = painter.CurrentStore();
            var targets = SelectedTargets(grid);
            if (store is null || targets.Count == 0) return false;
            SetColor(store, grid, targets, color);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: could not apply the highlight shortcut", ex);
            return false;
        }
    }

    static bool TryGetHotkeyColor(KeyEventArgs e, out HighlightColor? color)
    {
        color = null;
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt)) return false;
        // With Alt held, WPF can report the key as Key.System and put the real key in SystemKey.
        switch (e.Key == Key.System ? e.SystemKey : e.Key)
        {
            case Key.D0 or Key.NumPad0: return true;
            case Key.D1 or Key.NumPad1: color = HighlightColor.Green; return true;
            case Key.D2 or Key.NumPad2: color = HighlightColor.Orange; return true;
            case Key.D3 or Key.NumPad3: color = HighlightColor.Red; return true;
            default: return false;
        }
    }

    /// <summary>The shortcut shown next to a menu entry: Ctrl+Alt+1/2/3 for the colors, Ctrl+Alt+0 to remove.</summary>
    static string Gesture(HighlightColor? color) => "Ctrl+Alt+" + (color is null ? 0 : (int)color + 1);

    List<HighlightTarget> SelectedTargets(DataGrid grid)
    {
        var targets = new List<HighlightTarget>();
        foreach (var item in grid.SelectedItems)
            if (painter.GetUnitId(item) is { } id)
                targets.Add(new HighlightTarget(id, painter.GetLabel(item), item));
        return targets;
    }

    /// <summary>
    /// The entries appended to the menu: a separator, the "Highlight" title, then the colors and "Remove highlight"
    /// directly in the menu. On a module row the title shows the module's counts and opens a submenu holding only the
    /// bulk removal, one level deeper so it is never hit by accident. Elsewhere the title is a plain label.
    /// </summary>
    List<Control> BuildMenuItems(HighlightStore store, DataGrid grid, List<HighlightTarget> targets, bool onModules)
    {
        var colors = targets.Select(t => store.Get(t.Id)).Distinct().ToList();
        HighlightColor? sharedColor = colors.Count == 1 ? colors[0] : null;
        var items = new List<Control> { new Separator { Tag = InjectedTag } };

        if (onModules)
        {
            var removeModule = new MenuItem { Header = "Remove all highlights in this module", IsEnabled = store.Count > 0 };
            removeModule.Click += (_, _) => RemoveModuleHighlights(store, targets);
            var bulk = new MenuItem { Header = "Bulk remove" };
            bulk.Items.Add(removeModule);
            var title = new MenuItem { Header = ModuleCountsHeader(store, targets), Tag = InjectedTag };
            title.Items.Add(bulk);
            items.Add(title);
        }
        else
        {
            // A label only: it cannot be clicked or focused, and unlike a disabled item it is not greyed out.
            items.Add(new MenuItem { Header = "Highlight", Tag = InjectedTag, IsHitTestVisible = false, Focusable = false });
        }

        foreach (var color in Enum.GetValues<HighlightColor>())
        {
            // IsChecked shows a check mark (in place of the swatch) on the color the selection already has.
            var item = new MenuItem
            {
                Header = color.ToString(),
                Icon = Swatch(color),
                IsChecked = sharedColor == color,
                InputGestureText = Gesture(color),
                Tag = InjectedTag,
            };
            item.Click += (_, _) => SetColor(store, grid, targets, color);
            items.Add(item);
        }

        var remove = new MenuItem
        {
            Header = "Remove highlight",
            IsEnabled = colors.Any(c => c != null),
            InputGestureText = Gesture(null),
            Tag = InjectedTag,
        };
        remove.Click += (_, _) => SetColor(store, grid, targets, null);
        items.Add(remove);
        return items;
    }

    /// <summary>
    /// "Highlight  ■ 3  ■ 1  ■ 2": the Highlight title of a module row, with how many items in the selected
    /// module(s) have each color.
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
    static void MatchNativeStyle(ContextMenu menu, List<Control> injected)
    {
        var nativeItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => !ReferenceEquals(i.Tag, InjectedTag));
        var nativeSeparator = menu.Items.OfType<Separator>().FirstOrDefault(s => !ReferenceEquals(s.Tag, InjectedTag));
        var itemStyle = nativeItem?.Style;
        var separatorStyle = nativeSeparator?.Style;

        var pending = new Stack<Control>(injected);
        while (pending.Count > 0)
        {
            var control = pending.Pop();
            if (control is Separator && separatorStyle != null && separatorStyle.TargetType.IsInstanceOfType(control))
                control.Style = separatorStyle;
            if (control is MenuItem item && itemStyle != null && itemStyle.TargetType.IsInstanceOfType(item))
            {
                item.Style = itemStyle;
                foreach (var child in item.Items.OfType<MenuItem>())
                    pending.Push(child);
            }
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

    HashSet<string> IdsInModules(HighlightStore store, List<HighlightTarget> moduleTargets) =>
        modules.GetIdsInModules(moduleTargets.Select(t => t.Id).ToList(), store);

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
