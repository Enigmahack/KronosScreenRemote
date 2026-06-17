using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class MainWindow
{
    void DrawOverlay(DrawingContext dc, Size winSize)
    {
        var mouse = Mouse.GetPosition(RootGrid);

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

        if (_hoverIdx.HasValue)
            OverlayRenderer.DrawHoverTooltip(dc, _hoverIdx.Value,
                _basePal, _overrides, mouse, winSize, _pixPerDip);

        if (_edOpen)
        {
            (_panelRect, _gridOrigin, _sliderTop) = OverlayRenderer.DrawEditor(
                dc, winSize, _basePal, _overrides,
                _edSel, _edCh, _edTyped, _hoverIdx, _locked, _pixPerDip);
        }

        if (_zoomOn && _wb != null)
            OverlayRenderer.DrawZoomLoupe(dc, _wb, _rawFrame,
                _frameW, _frameH, _frameRect, mouse,
                _basePal, _overrides, _zoomLevel,
                Math.Clamp(_settings.ZoomWindowSize, 1.0, 3.5), winSize, _pixPerDip);

        if (_calMode)
            OverlayRenderer.DrawCalOverlay(dc, _calMesh, _calBiasDots,
                _calHoverNode, _calDraggingNode, _calDirty, _frameRect,
                _frameW, _frameH, winSize, _pixPerDip);

        if (_touchMarker.HasValue && (_dragActive || _dragPending ||
            (DateTime.Now - _touchMarker.Value.t).TotalSeconds < 0.4))
        {
            bool persistent = _dragActive || _dragPending;
            double t = persistent ? 0.0
                : Math.Clamp((DateTime.Now - _touchMarker.Value.t).TotalSeconds / 0.6, 0, 1.0);
            OverlayRenderer.DrawTouchMarker(dc, _touchMarker.Value.pos, t);
        }

        // Boot splash — shown during boot/update; dismissed immediately when a mode is confirmed.
        // _frameIsLikelyBootScreen (≥60% black) prevents the splash from overlaying real UI content
        // when mode detection fails due to a popup (e.g. Category/Program select) covering the
        // mode indicator, which can happen during reconnect or streaming-mode changes.
        if (_bootPhase && _frameRect.Width > 0 && _connState == ConnState.Connected && _frameIsLikelyBootScreen)
        {
            var splash = GetBootSplash();
            if (splash != null)
                OverlayRenderer.DrawBootOverlay(dc, _frameRect, splash, ComputeBootFillFraction());
        }

        if (_helpOpen)
            OverlayRenderer.DrawHelp(dc, winSize, _pixPerDip, _settings.GetKeyName);
    }
}
