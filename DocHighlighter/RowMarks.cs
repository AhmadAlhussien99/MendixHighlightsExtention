using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DocHighlighter;

/// <summary>
/// What the extension draws on top of an App Explorer row: the colored strip on the left of a highlighted row, and the
/// small flag after the name of a module that is not highlighted itself but contains highlighted items. It is an
/// adorner: drawn on a layer above the row, so Studio Pro's own row content is never changed, and it ignores the mouse.
/// </summary>
internal sealed class RowMarks : Adorner
{
    const double StripWidth = 3;
    const double PoleWidth = 1.5;
    const double PoleHeight = 11;
    const double FlagWidth = 8;
    const double FlagHeight = 6;
    const double FlagGap = 6;

    public RowMarks(DataGridRow row) : base(row)
    {
        IsHitTestVisible = false;
    }

    /// <summary>The strip color of a highlighted row, or null for none.</summary>
    public Brush? Strip { get; set; }

    /// <summary>Whether to draw the flag of a module with highlighted items inside.</summary>
    public bool Flag { get; set; }

    protected override void OnRender(DrawingContext drawing)
    {
        var row = (DataGridRow)AdornedElement;
        if (Strip != null)
            drawing.DrawRectangle(Strip, null, new Rect(VisibleLeft(row), 0, StripWidth, row.ActualHeight));
        if (Flag)
            DrawFlag(drawing, row);
    }

    /// <summary>
    /// The left edge of the part of the row that is in view, in row coordinates. When the App Explorer is scrolled
    /// sideways the row's own left edge is out of view, and the strip would be too.
    /// </summary>
    static double VisibleLeft(DataGridRow row)
    {
        for (var parent = VisualTreeHelper.GetParent(row); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
                return Math.Max(0, -row.TranslatePoint(new Point(0, 0), viewer).X);
        return 0;
    }

    /// <summary>The flag goes after the last text (the name, or a version label shown after it), in the name's own
    /// color, so it follows the theme and the selected-row text color.</summary>
    static void DrawFlag(DrawingContext drawing, DataGridRow row)
    {
        var texts = VisibleTexts(row).ToList();
        var x = texts.Count == 0 ? 0 : texts.Max(t => t.Right) + FlagGap;
        var brush = texts.Count == 0 ? row.Foreground : texts.MinBy(t => t.Left).Text.Foreground;
        var top = Math.Round((row.ActualHeight - PoleHeight) / 2);
        if (brush is null || x + PoleWidth + FlagWidth > row.ActualWidth) return;

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

    /// <summary>The row's visible text blocks with where each starts and ends, relative to the row.</summary>
    static IEnumerable<(TextBlock Text, double Left, double Right)> VisibleTexts(FrameworkElement row)
    {
        foreach (var text in Descendants(row).OfType<TextBlock>())
        {
            if (!text.IsVisible || string.IsNullOrWhiteSpace(text.Text)) continue;
            // A stretched TextBlock can be wider than its text; DesiredSize is the width the text itself needs.
            var width = Math.Min(text.ActualWidth, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right);
            var toRow = text.TransformToAncestor(row);
            yield return (text, toRow.Transform(new Point(0, 0)).X, toRow.Transform(new Point(width, 0)).X);
        }
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
