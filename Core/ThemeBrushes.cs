using System.Windows.Media;

namespace KronosScreenRemote;

/// <summary>
/// Single factory for immutable (frozen) <see cref="SolidColorBrush"/>es built from raw RGB
/// bytes. Replaces the per-file copies this used to have (SysExToolWindow's <c>Frozen</c> and
/// <c>MakeBrush</c>, MainWindow's <c>FrozenBrush</c>, VuMeterBar's <c>Freeze</c>). Frozen brushes
/// are immutable, freely shareable across threads, and cheaper for WPF to render.
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
