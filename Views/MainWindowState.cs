using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

// Cohesive state holders extracted from MainWindow's former flat field wall.  Each is a plain
// reference type held in a single readonly MainWindow field, so closures that capture the holder
// (timer ticks, event lambdas) stay valid.  Behaviour is unchanged — MainWindow's partial methods
// now read/write `_group.Field` instead of a loose `_groupField`.  Only trivially-pure helper
// logic (no WPF/UI/socket access) is moved in; UI-touching orchestration stays in MainWindow.

// Rolling one-second FPS counter for the status bar.
sealed class FpsCounter
{
    int _frames;
    DateTime _lastCheck = DateTime.MinValue;

    // Feed one received frame.  Returns a new reading to display when the 1 s window rolls over
    // (and 0.0 on the very first frame), or null when there's nothing new to show.
    public double? Tick(DateTime now)
    {
        _frames++;
        if (_lastCheck == DateTime.MinValue) { _lastCheck = now; return 0.0; }
        if ((now - _lastCheck).TotalSeconds < 1.0) return null;

        double fps = _frames / (now - _lastCheck).TotalSeconds;
        _frames    = 0;
        _lastCheck = now;
        return fps;
    }

    public void Reset()
    {
        _frames    = 0;
        _lastCheck = DateTime.MinValue;
    }
}

// Data-wheel drag + spin animation state (the wheel on the left value panel).
sealed class WheelState
{
    public const double PxPerStep      = 12;    // design-space px per drag step
    public const int    AnimIntervalMs = 100;
    public const int    AnimIdleMs     = 400;
    public static readonly double[] Angles = { 0.0, 10.0, -10.0 };

    public readonly DispatcherTimer AnimTimer = new();
    public bool     DragActive;
    public double   DragStartY;
    public int      DragSteps;
    public int      AnimState;
    public int      AnimDir = 1;
    public DateTime LastActivity = DateTime.MinValue;
}

// Touch/drag gesture state on the streamed frame (tap vs drag, and the fading tap marker).
sealed class DragState
{
    public const int StartThresh = 8;   // px before a press becomes a drag
    public const int MoveThresh  = 3;    // px before a drag move is re-sent

    public bool           Pending;       // mouse down, not yet moved past StartThresh
    public (int x, int y) PendingPos;
    public bool           Active;        // dragging
    public (int x, int y) Last;
    public (Point pos, DateTime t)? Marker;   // fading touch-tap marker
}

// Touch-calibration mode: warp mesh, bias dots, drag/hover state, and the undo stack.
// (The undo push/undo/redo logic stays in MainWindow.Calibration.cs — it also flips Dirty and
// invalidates the overlay, so it's not pure enough to move here.)
sealed class CalibrationState
{
    public const double NodeHitRadius = 18.0;
    public const double DotHitRadius  = 12.0;

    public bool             Mode;      // calibration mode active
    public bool             Dirty;     // mesh has changes not yet written to disk
    public CalMesh          Mesh = new();
    public List<CalBiasDot> BiasDots = new();
    public (int col, int row)? DraggingNode;
    public (int col, int row)? HoverNode;

    public List<CalHistEntry>   History = new();
    public int                  HistPos = -1;
    public (int offX, int offY) DragStartOffset;
}

// "Editing a Program from within a Combi/Sequence" (daemon EDITCTX) state + its
// flashing-button animation. Driven directly by the daemon's STATE poll — EDITCTX is
// exact per call, so (unlike the old pixel-badge heuristic this replaced) no holdoff
// is needed: a failed poll just leaves the state unchanged until the next success.
sealed class EditContextState
{
    public bool        Active;
    public bool        FlashState;
    public EditContext Origin;
    public readonly DispatcherTimer FlashTimer = new();
}

// Fullscreen toggle: whether we're fullscreen, plus the window chrome saved to restore afterward.
sealed class FullscreenState
{
    public bool        Active;
    public WindowState SavedState  = WindowState.Normal;
    public WindowStyle SavedStyle  = WindowStyle.SingleBorderWindow;
    public ResizeMode  SavedResize = ResizeMode.CanResize;
}

// Boot-splash overlay + load-phase progress bar state.  (Cross-cutting frame-classification flags
// — _frameIsMostlyBlack / _frameIsLikelyBootScreen / _detectedModeEver — stay in MainWindow, since
// mode and combi-edit detection read them too.)  The preload-schedule and fill-fraction math live
// in MainWindow.BootSplash.cs.
sealed class BootState
{
    public const double EntryDelaySec = 0.5;   // show overlay only after this long with no mode

    // Bar fill fractions (0..1) — resolution-independent, from the 1600-wide splash reference.
    public const double BarStaticEnd  = 724.0  / 1302;
    public const double BarPreloadEnd = 1190.0 / 1302;
    public const double BarBankStart  = BarPreloadEnd;   // bank fill begins where preload ends
    public const double BarBankEnd    = 1.0;

    public BitmapSource? Splash;
    public bool          Phase;                 // boot overlay active
    public DateTime      FirstFrame = DateTime.MinValue;

    public BootPhaseDetector.Phase LoadPhase = BootPhaseDetector.Phase.None;
    public DateTime      PreloadTimerStart  = DateTime.MinValue; // latched at boot entry
    public DateTime      BankDataDetectedAt = DateTime.MinValue;
    public double        FinishingFillFrac  = BarStaticEnd;      // snapshotted on Finishing detect

    // (WallEnd, ProgressEnd) pairs per active/pause segment; built once at boot entry, null until then.
    public (double WallEnd, double ProgressEnd)[]? PreloadSchedule;
}
