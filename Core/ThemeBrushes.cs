using System.Windows.Media;

namespace KronosScreenRemote;

/// <summary>
/// Factory for immutable (frozen) <see cref="SolidColorBrush"/>es built from raw RGB bytes.
/// Frozen brushes are immutable, freely shareable across threads, and cheaper for WPF to render.
/// </summary>
public static class ThemeBrushes
{
    public static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
