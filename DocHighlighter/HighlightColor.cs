using System.Windows.Media;

namespace DocHighlighter;

internal enum HighlightColor
{
    Green,
    Orange,
    Red,
}

internal static class HighlightBrushes
{
    // A modern palette (emerald, orange, rose). The row fill is a light ~14% tint so the row text stays the focus,
    // and the strip is the same hue at full strength so the color is still clear at a glance and on selected rows.
    static readonly Color Green = Color.FromRgb(0x10, 0xB9, 0x81);
    static readonly Color Orange = Color.FromRgb(0xF9, 0x73, 0x16);
    static readonly Color Red = Color.FromRgb(0xF4, 0x3F, 0x5E);
    const byte FillAlpha = 0x24;

    static readonly SolidColorBrush GreenFill = Freeze(Color.FromArgb(FillAlpha, Green.R, Green.G, Green.B));
    static readonly SolidColorBrush OrangeFill = Freeze(Color.FromArgb(FillAlpha, Orange.R, Orange.G, Orange.B));
    static readonly SolidColorBrush RedFill = Freeze(Color.FromArgb(FillAlpha, Red.R, Red.G, Red.B));
    static readonly SolidColorBrush GreenStrip = Freeze(Green);
    static readonly SolidColorBrush OrangeStrip = Freeze(Orange);
    static readonly SolidColorBrush RedStrip = Freeze(Red);

    public static Brush Fill(HighlightColor color) => color switch
    {
        HighlightColor.Green => GreenFill,
        HighlightColor.Orange => OrangeFill,
        _ => RedFill,
    };

    public static Brush Strip(HighlightColor color) => color switch
    {
        HighlightColor.Green => GreenStrip,
        HighlightColor.Orange => OrangeStrip,
        _ => RedStrip,
    };

    static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
