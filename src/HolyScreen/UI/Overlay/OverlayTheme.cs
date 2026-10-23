using System.Windows;
using System.Windows.Media;

namespace HolyScreen.UI.Overlay;

/// <summary>Colours, pens and type for everything drawn on top of the frozen screen.</summary>
internal static class OverlayTheme
{
    public static readonly Color Accent = Color.FromRgb(0x5A, 0xA9, 0xFF);

    /// <summary>The veil over everything outside the selection: a cool near-black, translucent.</summary>
    public static readonly Brush Dim = Frozen(Color.FromArgb(0x8A, 0x06, 0x08, 0x0D));

    public static readonly Brush SelectionLine = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    public static readonly Brush SelectionShade = Frozen(Color.FromArgb(0x70, 0x00, 0x00, 0x00));
    public static readonly Brush Crosshair = Frozen(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF));
    public static readonly Brush CrosshairShade = Frozen(Color.FromArgb(0x38, 0x00, 0x00, 0x00));
    public static readonly Brush Highlight = Frozen(Accent);
    public static readonly Brush HighlightShade = Frozen(Color.FromArgb(0x55, 0x00, 0x00, 0x00));

    public static readonly Brush PillBackground = Frozen(Color.FromArgb(0xEB, 0x17, 0x1A, 0x20));
    public static readonly Pen PillBorder = FrozenPen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 1);
    public static readonly Brush Text = Frozen(Color.FromRgb(0xF5, 0xF6, 0xF8));
    public static readonly Brush SecondaryText = Frozen(Color.FromRgb(0xA9, 0xAE, 0xB8));

    public static readonly Brush HandleFill = Frozen(Colors.White);
    public static readonly Brush HandleShade = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    public static readonly Brush HandleActive = Frozen(Accent);

    public static readonly Brush Flash = Frozen(Colors.White);

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    public static readonly Typeface Regular = new(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface Semibold = new(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
