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
    static readonly SolidColorBrush GreenFill = Freeze(Color.FromArgb(0x45, 0x43, 0xA0, 0x47));
    static readonly SolidColorBrush OrangeFill = Freeze(Color.FromArgb(0x45, 0xFB, 0x8C, 0x00));
    static readonly SolidColorBrush RedFill = Freeze(Color.FromArgb(0x45, 0xE5, 0x39, 0x35));
    static readonly SolidColorBrush GreenStrip = Freeze(Color.FromRgb(0x43, 0xA0, 0x47));
    static readonly SolidColorBrush OrangeStrip = Freeze(Color.FromRgb(0xFB, 0x8C, 0x00));
    static readonly SolidColorBrush RedStrip = Freeze(Color.FromRgb(0xE5, 0x39, 0x35));

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
