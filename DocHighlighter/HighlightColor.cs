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
    // A modern traffic-light palette (green, amber, red). Amber leans toward yellow and is lighter, red is deeper,
    // so the two differ in both hue and brightness and stay apart even as a faint tint.
    // The row fill is a light ~14% tint so the row text stays the focus, and the strip is the same hue at full
    // strength so the color is still clear at a glance and on selected rows.
    static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x5E);
    static readonly Color Orange = Color.FromRgb(0xF5, 0x9E, 0x0B);
    static readonly Color Red = Color.FromRgb(0xDC, 0x26, 0x26);
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
