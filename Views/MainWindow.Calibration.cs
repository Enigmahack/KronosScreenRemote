using System.Windows;

namespace KronosScreenRemote;

public partial class MainWindow
{
    void SetCalGridSize(int size)
    {
        if (_calMesh.Cols == size) return;

        if (!_calMesh.IsIdentity() || _calBiasDots.Count > 0)
        {
            var result = MessageBox.Show(
                $"Changing grid size to {size}×{size} will clear existing calibration data.\nProceed?",
                "Change Calibration Grid",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _calMesh = new CalMesh(size, size);
        _calBiasDots.Clear();
        _calDirty = false;
        _calDraggingNode = null;
        _calHoverNode    = null;
        _calHistory.Clear(); _calHistPos = -1;
        Storage.SaveCal(_calMesh, _calBiasDots);
        Console.WriteLine($"[cal] grid size changed to {size}×{size}");
        OverlayLayer.InvalidateVisual();
    }

    void ExitCalMode()
    {
        _warpMode        = false;
        _calDraggingNode = null;
        _calHoverNode    = null;
    }

    // ── Coordinate transforms ─────────────────────────────────────────────────

    (int nx, int ny) ScreenToKronos(Point screen)
    {
        double fx = _frameRect.X, fy = _frameRect.Y,
               fw = _frameRect.Width, fh = _frameRect.Height;
        int nx = Math.Clamp((int)((screen.X - fx) / fw * _frameW), 0, _frameW - 1);
        int ny = Math.Clamp((int)((screen.Y - fy) / fh * _frameH), 0, _frameH - 1);
        return (nx, ny);
    }

    (int cx, int cy) ApplyCal(int nx, int ny) =>
        _calMesh.InverseApply(nx, ny, _frameW, _frameH);

    Point KronosToScreen(int kx, int ky) =>
        new(_frameRect.X + kx * _frameRect.Width  / _frameW,
            _frameRect.Y + ky * _frameRect.Height / _frameH);

    (int col, int row)? FindNearestCalNode(Point screenPos)
    {
        double bestDist = CalNodeHitRadius;
        (int col, int row)? best = null;
        for (int c = 0; c < _calMesh.Cols; c++)
            for (int r = 0; r < _calMesh.Rows; r++)
            {
                var (kx, ky) = _calMesh.NodeDst(c, r, _frameW, _frameH);
                var sp = KronosToScreen(kx, ky);
                double dx = sp.X - screenPos.X, dy = sp.Y - screenPos.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist) { bestDist = dist; best = (c, r); }
            }
        return best;
    }

    int? FindNearestBiasDot(Point screenPos)
    {
        double bestDist = CalDotHitRadius;
        int? best = null;
        for (int i = 0; i < _calBiasDots.Count; i++)
        {
            var dot = _calBiasDots[i];
            var (kx, ky) = _calMesh.Apply(dot.Nx, dot.Ny, _frameW, _frameH);
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
        if (_calHistPos < _calHistory.Count - 1)
            _calHistory.RemoveRange(_calHistPos + 1, _calHistory.Count - _calHistPos - 1);
    }

    void CalHistPush(CalHistEntry entry)
    {
        CalHistTruncateFuture();
        _calHistory.Add(entry);
        _calHistPos = _calHistory.Count - 1;
    }

    void CalHistUndo()
    {
        if (_calHistPos < 0) return;
        var e = _calHistory[_calHistPos--];
        switch (e.Kind)
        {
            case CalHistKind.NodeMove:
                _calMesh.SetOffset(e.Col, e.Row, e.OldOffX, e.OldOffY);
                _calDirty = true;
                break;
            case CalHistKind.DotAdded:
                _calBiasDots.RemoveAt(e.DotIdx);
                Storage.SaveCal(_calMesh, _calBiasDots);
                break;
            case CalHistKind.DotRemoved:
                _calBiasDots.Insert(e.DotIdx, e.Dot);
                Storage.SaveCal(_calMesh, _calBiasDots);
                break;
        }
    }

    void CalHistRedo()
    {
        if (_calHistPos >= _calHistory.Count - 1) return;
        var e = _calHistory[++_calHistPos];
        switch (e.Kind)
        {
            case CalHistKind.NodeMove:
                _calMesh.SetOffset(e.Col, e.Row, e.NewOffX, e.NewOffY);
                _calDirty = true;
                break;
            case CalHistKind.DotAdded:
                _calBiasDots.Insert(e.DotIdx, e.Dot);
                Storage.SaveCal(_calMesh, _calBiasDots);
                break;
            case CalHistKind.DotRemoved:
                _calBiasDots.RemoveAt(e.DotIdx);
                Storage.SaveCal(_calMesh, _calBiasDots);
                break;
        }
    }
}
