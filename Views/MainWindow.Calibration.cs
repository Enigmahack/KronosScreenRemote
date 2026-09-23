using System.Windows;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    const int CalPad = 20;

    // The calibration lives on the unit (daemon CAL_GET/CAL_SET, stored in
    // /korg/rw/HD/ScreenRemote/calibration.txt), so it follows the instrument rather than this
    // PC. It is read on every connect; the mesh from the last unit stays in effect while
    // disconnected, but can only be saved while connected.
    void ApplyUnitCalibration(string? text)
    {
        if (_cal.Mode)
        {
            _cal.Mode = false;
            ExitCalMode();
        }
        if (_cal.Dirty)
            AppLog.Info("[cal] unsaved calibration changes discarded (new connection)");

        (CalMesh mesh, List<CalBiasDot> dots)? parsed = text is { Length: > 0 } ? CalText.Parse(text) : null;
        (_cal.Mesh, _cal.BiasDots) = parsed ?? (new CalMesh(), new List<CalBiasDot>());
        _cal.Dirty = false;
        _cal.DraggingNode = null;
        _cal.HoverNode    = null;
        _cal.History.Clear(); _cal.HistPos = -1;

        AppLog.Info(text switch
        {
            null                   => "[cal] daemon can't store calibration (needs 3.1.2+) - uncalibrated",
            ""                     => "[cal] unit has no stored calibration",
            _ when parsed == null  => $"[cal] unit's stored calibration is unreadable, ignored: {text}",
            _                      => $"[cal] calibration loaded from unit, {_cal.BiasDots.Count} bias dot(s)",
        });
        OverlayLayer.InvalidateVisual();
    }

    readonly SemaphoreSlim _calSaveLock = new(1, 1);

    void SaveCalibration() => _ = SaveCalibrationAsync();

    // Sends the current mesh to the unit. On failure the mesh is marked dirty again (callers
    // clear Dirty right after calling this) and the reason is shown in the status bar.
    //
    // Saves run one at a time, and each one serializes the mesh only once it is its turn, so
    // whichever save reaches the unit last always carries the newest state - two quick bias-dot
    // edits can't land in the wrong order and leave the older one stored.
    async Task<bool> SaveCalibrationAsync()
    {
        string? error;
        await _calSaveLock.WaitAsync();
        try
        {
            var ctrl = _ctrl;
            bool connected = IsConnected;
            var text = CalText.Serialize(_cal.Mesh, _cal.BiasDots);
            error = await Task.Run(() => SendCalibrationToUnit(ctrl, connected, text));
        }
        finally { _calSaveLock.Release(); }
        if (error == null)
        {
            AppLog.Info("[cal] calibration saved to unit");
            return true;
        }
        _cal.Dirty = true;
        AppLog.Info($"[cal] save failed: {error}");
        SetNotification(error, isError: true);
        return false;
    }

    // Blocking form for the exit prompt, where the window is about to close and an async save
    // would be cut off. Runs off the UI thread and waits a bounded time.
    string? SaveCalibrationBlocking()
    {
        var ctrl = _ctrl;
        bool connected = IsConnected;
        var text = CalText.Serialize(_cal.Mesh, _cal.BiasDots);
        var task = Task.Run(() => SendCalibrationToUnit(ctrl, connected, text));
        return task.Wait(4000) ? task.Result : AppMessages.Calibration.SaveNoAnswer;
    }

    // null on success, otherwise the message to show.
    static async Task<string?> SendCalibrationToUnit(ICtrlClient ctrl, bool connected, string text)
    {
        if (!connected) return AppMessages.Calibration.SaveNotConnected;
        if (text.Length > CalText.MaxLength) return AppMessages.Calibration.SaveTooLarge;
        var reply = (await ctrl.QueryAsync(DaemonCommand.CalibrationSet(text), 3000).ConfigureAwait(false))?.Trim();
        return reply switch
        {
            "OK"  => null,
            null  => AppMessages.Calibration.SaveNoAnswer,
            "ERR" => AppMessages.Calibration.SaveDaemonTooOld,
            _     => AppMessages.Calibration.SaveRejected(reply),
        };
    }

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
        SaveCalibration();
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

    (int nx, int ny) ScreenToKronos(Point screen)
    {
        double fx = _frameRect.X, fy = _frameRect.Y,
               fw = _frameRect.Width, fh = _frameRect.Height;
        int nx = Math.Clamp((int)Math.Round((screen.X - fx) / fw * (_frameW - 1)), 0, _frameW - 1);
        int ny = Math.Clamp((int)Math.Round((screen.Y - fy) / fh * (_frameH - 1)), 0, _frameH - 1);
        return (nx, ny);
    }

    // Unclamped version for node dragging - allows offsets beyond the frame boundary
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
                SaveCalibration();
                break;
            case CalHistKind.DotRemoved:
                _cal.BiasDots.Insert(e.DotIdx, e.Dot);
                SaveCalibration();
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
                SaveCalibration();
                break;
            case CalHistKind.DotRemoved:
                _cal.BiasDots.RemoveAt(e.DotIdx);
                SaveCalibration();
                break;
        }
    }
}
