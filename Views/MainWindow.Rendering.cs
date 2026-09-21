using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class MainWindow
{
    void DrawOverlay(DrawingContext dc, Size winSize)
    {
        var mouse = Mouse.GetPosition(RootGrid);

        // The overlay is a transient aid for one editing session - drop it if the editor
        // closed by any path (Escape already clears it explicitly; this catches the rest:
        // the P hotkey/menu/command-palette toggle, and ApplyDeviceFamilyUi's forced close).
        if (!_edOpen && _ovlBitmapSrc != null) ClearPaletteOverlay();

        if (_edOpen)
        {
            if (!_panelRect.Contains(mouse))
                _hoverIdx = RawIdxAt(mouse);
            else
                _hoverIdx = OverlayRenderer.SwatchAt(mouse, _gridOrigin);
        }
        else
        {
            _hoverIdx = null;
        }

        if (_connState == ConnState.Disconnected && _frameRect.Width > 0)
            OverlayRenderer.DrawDisconnectedOverlay(dc, _frameRect, _pixPerDip);

        if (_edOpen && _ovlBitmapSrc != null)
            OverlayRenderer.DrawPaletteOverlay(dc, _ovlRect, _ovlBitmapSrc);

        if (_hoverIdx.HasValue)
            OverlayRenderer.DrawHoverTooltip(dc, _hoverIdx.Value,
                _basePal, EffectiveOverrides, mouse, winSize, _pixPerDip);

        if (_edOpen)
        {
            (_panelRect, _gridOrigin, _sliderTop) = OverlayRenderer.DrawEditor(
                dc, winSize, _basePal, EffectiveOverrides,
                _edSel, _edCh, _edTyped, _hoverIdx, _locked, _paletteDirty, _pixPerDip);
        }

        if (_zoomOn && _wb != null)
            OverlayRenderer.DrawZoomLoupe(dc, _wb, _rawFrame,
                _frameW, _frameH, _frameRect, mouse,
                _basePal, EffectiveOverrides, _zoomLevel,
                Math.Clamp(_settings.ZoomWindowSize, 1.0, 3.5), winSize, _pixPerDip, _bpp);

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
