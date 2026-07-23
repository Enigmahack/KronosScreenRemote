using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class MainWindow
{
    void DrawOverlay(DrawingContext dc, Size winSize)
    {
        var mouse = Mouse.GetPosition(RootGrid);

        if (_connState == ConnState.Disconnected && _frameRect.Width > 0)
            OverlayRenderer.DrawDisconnectedOverlay(dc, _frameRect, _pixPerDip);

        if (_zoomOn && _wb != null)
            OverlayRenderer.DrawZoomLoupe(dc, _wb, _rawFrame,
                _frameW, _frameH, _frameRect, mouse,
                _basePal, _overrides, _zoomLevel,
                Math.Clamp(_settings.ZoomWindowSize, 1.0, 3.5), winSize, _pixPerDip);

        if (_cal.Mode)
            OverlayRenderer.DrawCalOverlay(dc, _cal.Mesh, _cal.BiasDots,
                _cal.HoverNode, _cal.DraggingNode, _cal.Dirty, _frameRect,
                _frameW, _frameH, winSize, _pixPerDip);

        if (_drag.Marker.HasValue && (_drag.Active || _drag.Pending ||
            (DateTime.Now - _drag.Marker.Value.t).TotalSeconds < 0.4))
        {
            bool persistent = _drag.Active || _drag.Pending;
            double t = persistent ? 0.0
                : Math.Clamp((DateTime.Now - _drag.Marker.Value.t).TotalSeconds / 0.6, 0, 1.0);
            OverlayRenderer.DrawTouchMarker(dc, _drag.Marker.Value.pos, t);
        }
    }
}
