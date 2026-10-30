using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DocHighlighter;

/// <summary>
/// A small flag drawn just after the name of a module row that is not highlighted itself but contains highlighted
/// items, in the most serious color inside. It is an adorner: drawn on a layer above the row, so Studio Pro's own
/// row content is never changed, and it ignores the mouse.
/// </summary>
internal sealed class ModuleFlag : Adorner
{
    const double PoleWidth = 1.5;
    const double PoleHeight = 11;
    const double FlagWidth = 8;
    const double FlagHeight = 6;
    const double Gap = 6;

    HighlightColor color;

    public ModuleFlag(DataGridRow row, HighlightColor color) : base(row)
    {
        this.color = color;
        IsHitTestVisible = false;
    }

    public HighlightColor Color
    {
        get => color;
        set
        {
            if (color == value) return;
            color = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var row = (FrameworkElement)AdornedElement;
        var x = TextEnd(row) + Gap;
        var top = Math.Round((row.ActualHeight - PoleHeight) / 2);
        if (x + PoleWidth + FlagWidth > row.ActualWidth) return;

        var brush = HighlightBrushes.Strip(color);
        drawing.DrawRectangle(brush, null, new Rect(x, top, PoleWidth, PoleHeight));
        var pennant = new StreamGeometry();
        using (var context = pennant.Open())
        {
            context.BeginFigure(new Point(x + PoleWidth, top), true, true);
            context.LineTo(new Point(x + PoleWidth + FlagWidth, top + FlagHeight / 2), true, false);
            context.LineTo(new Point(x + PoleWidth, top + FlagHeight), true, false);
        }
        pennant.Freeze();
        drawing.DrawGeometry(brush, null, pennant);
    }

    /// <summary>Where the row's text ends: the module name, or a version label shown after it.</summary>
    static double TextEnd(FrameworkElement row)
    {
        double end = 0;
        foreach (var text in Descendants(row).OfType<TextBlock>())
        {
            if (!text.IsVisible || string.IsNullOrWhiteSpace(text.Text)) continue;
            // A stretched TextBlock can be wider than its text; DesiredSize is the width the text itself needs.
            var width = Math.Min(text.ActualWidth, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right);
            end = Math.Max(end, text.TransformToAncestor(row).Transform(new Point(width, 0)).X);
        }
        return end;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                yield return child;
                pending.Push(child);
            }
        }
    }
}
