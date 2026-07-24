using System.Windows;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    const int CalPad = 20;

    void SetCalGridSize(int size)
    {
        if (_cal.Mesh.Cols == size) return;

        if (!_cal.Mesh.IsIdentity() || _cal.BiasDots.Count > 0)
        {
            var result = MessageBox.Show(
                AppMessages.Calibration.GridChangeWarning(size),
                AppMessages.Calibration.GridChangeTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _cal.Mesh = new CalMesh(size, size);
        _cal.BiasDots.Clear();
        _cal.Dirty = false;
        _cal.DraggingNode = null;
        _cal.HoverNode    = null;
        _cal.History.Clear(); _cal.HistPos = -1;
        Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
        Console.WriteLine($"[cal] grid size changed to {size}×{size}");
        OverlayLayer.InvalidateVisual();
    }

    void EnterCalMode()
    {
        FrameImage.Margin = new Thickness(CalPad);
        var r = _frameRect;
        r.Inflate(-CalPad, -CalPad);
        _frameRect = r;
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    void ExitCalMode()
    {
        _cal.DraggingNode = null;
        _cal.HoverNode    = null;
        FrameImage.Margin = new Thickness(0);
        var r = _frameRect;
        r.Inflate(CalPad, CalPad);
        _frameRect = r;
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    // Returns the hit-test rect for cal mode: expanded back to the original frame area
    // so that nodes dragged into the 20px margin can still be grabbed.
    Rect CalHitRect { get {
        if (!_cal.Mode) return _frameRect;
        var r = _frameRect;
        r.Inflate(CalPad, CalPad);
        return r;
    } }

    // ── Coordinate transforms ─────────────────────────────────────────────────

    (int nx, int ny) ScreenToKronos(Point screen)
    {
        double fx = _frameRect.X, fy = _frameRect.Y,
               fw = _frameRect.Width, fh = _frameRect.Height;
        int nx = Math.Clamp((int)Math.Round((screen.X - fx) / fw * (_frameW - 1)), 0, _frameW - 1);
        int ny = Math.Clamp((int)Math.Round((screen.Y - fy) / fh * (_frameH - 1)), 0, _frameH - 1);
        return (nx, ny);
    }

    // Unclamped version for node dragging — allows offsets beyond the frame boundary
    (int nx, int ny) ScreenToKronosNode(Point screen) =>
        ((int)((screen.X - _frameRect.X) / _frameRect.Width  * _frameW),
         (int)((screen.Y - _frameRect.Y) / _frameRect.Height * _frameH));

    (int cx, int cy) ApplyCal(int nx, int ny) =>
        _cal.Mesh.InverseApply(nx, ny, _frameW, _frameH);

    Point KronosToScreen(int kx, int ky) =>
        new(_frameRect.X + kx * _frameRect.Width  / (_frameW - 1),
            _frameRect.Y + ky * _frameRect.Height / (_frameH - 1));

    (int col, int row)? FindNearestCalNode(Point screenPos)
    {
        double bestDist = CalibrationState.NodeHitRadius;
        (int col, int row)? best = null;
        for (int c = 0; c < _cal.Mesh.Cols; c++)
            for (int r = 0; r < _cal.Mesh.Rows; r++)
            {
                var (kx, ky) = _cal.Mesh.NodeDst(c, r, _frameW, _frameH);
                var sp = KronosToScreen(kx, ky);
                double dx = sp.X - screenPos.X, dy = sp.Y - screenPos.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist) { bestDist = dist; best = (c, r); }
            }
        return best;
    }

    int? FindNearestBiasDot(Point screenPos)
    {
        double bestDist = CalibrationState.DotHitRadius;
        int? best = null;
        for (int i = 0; i < _cal.BiasDots.Count; i++)
        {
            var dot = _cal.BiasDots[i];
            var (kx, ky) = _cal.Mesh.Apply(dot.Nx, dot.Ny, _frameW, _frameH);
            var sp = KronosToScreen(kx, ky);
            double dx = sp.X - screenPos.X, dy = sp.Y - screenPos.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // ── Calibration history ───────────────────────────────────────────────────

    void CalHistTruncateFuture()
    {
        if (_cal.HistPos < _cal.History.Count - 1)
            _cal.History.RemoveRange(_cal.HistPos + 1, _cal.History.Count - _cal.HistPos - 1);
    }

    void CalHistPush(CalHistEntry entry)
    {
        CalHistTruncateFuture();
        _cal.History.Add(entry);
        _cal.HistPos = _cal.History.Count - 1;
    }

    void CalHistUndo()
    {
        if (_cal.HistPos < 0) return;
        var e = _cal.History[_cal.HistPos--];
        switch (e.Kind)
        {
            case CalHistKind.NodeMove:
                _cal.Mesh.SetOffset(e.Col, e.Row, e.OldOffX, e.OldOffY);
                _cal.Dirty = true;
                break;
            case CalHistKind.DotAdded:
                _cal.BiasDots.RemoveAt(e.DotIdx);
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                break;
            case CalHistKind.DotRemoved:
                _cal.BiasDots.Insert(e.DotIdx, e.Dot);
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                break;
        }
    }

    void CalHistRedo()
    {
        if (_cal.HistPos >= _cal.History.Count - 1) return;
        var e = _cal.History[++_cal.HistPos];
        switch (e.Kind)
        {
            case CalHistKind.NodeMove:
                _cal.Mesh.SetOffset(e.Col, e.Row, e.NewOffX, e.NewOffY);
                _cal.Dirty = true;
                break;
            case CalHistKind.DotAdded:
                _cal.BiasDots.Insert(e.DotIdx, e.Dot);
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                break;
            case CalHistKind.DotRemoved:
                _cal.BiasDots.RemoveAt(e.DotIdx);
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                break;
        }
    }
}
