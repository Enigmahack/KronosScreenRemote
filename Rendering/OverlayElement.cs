using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

/// <summary>
/// Transparent top-layer element. MainWindow sets RenderCallback and calls
/// InvalidateVisual() whenever state changes that should update the overlay.
/// </summary>
sealed class OverlayElement : FrameworkElement
{
    public Action<DrawingContext, Size>? RenderCallback;

    protected override void OnRender(DrawingContext dc)
        => RenderCallback?.Invoke(dc, new Size(ActualWidth, ActualHeight));
}
