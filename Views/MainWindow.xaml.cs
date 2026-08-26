using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

public partial class MainWindow : ThemedWindow, ICtrlSender
{
    // ── Connection settings ───────────────────────────────────────────────────
    string _host     = "";
    int    _port     = StreamReceiver.StreamPort;
    int    _ctrlPort = CtrlQuery.CtrlPort;
    bool   _pullMode = false;
    int    _fps      = 15;

    // ── Frame state ───────────────────────────────────────────────────────────
    // Nominal Kronos display size (px). Used as the frame default and as the fixed
    // basis for window-size and layout-column math (the live stream may report other
    // dimensions, which then overwrite _frameW/_frameH).
    const int       FrameDesignWidth  = 800;
    const int       FrameDesignHeight = 600;
    int             _frameW = FrameDesignWidth;
    int             _frameH = FrameDesignHeight;
    PaletteEntry[]  _basePal  = new PaletteEntry[256];
    byte[]?         _rawFrame = null;   // == _frameBuf once the first frame arrives; null before
    byte[]?         _frameBuf = null;   // UI-owned copy target for StreamReceiver.TryCopyLatestFrame
    int[]           _lut      = new int[256];

    WriteableBitmap? _wb;

    // Persisted per-index color overrides, baked into the display LUT (RebuildLut) and
    // applied by the zoom loupe. Loaded from disk on startup.
    Dictionary<int, PaletteEntry> _overrides = new();

    // ── Display state ─────────────────────────────────────────────────────────
    bool   _mirrorState = false;
    bool   _zoomOn = false;
    double _zoomLevel = 2.5;
    bool   _hideDataInput = false;
    bool   _scrollDirection = false;
    bool   _hideValueInput = false;
    bool   _focusedDataExpanded = false;
    bool   _focusedValueExpanded = false;
    double _currentScale    = 1.0;
    // Size SetWindowSize last applied, so a subsequent manual resize can be told apart from
    // one we caused. NaN = SetWindowSize has not run yet.
    double _scaledW = double.NaN, _scaledH = double.NaN;
    Rect   _frameRect;

    // ── Data wheel drag / animation ──────────────────────────────────────────
    readonly WheelState _wheel = new();

    // ── Value slider state ────────────────────────────────────────────────────
    bool   _vsliderDragActive = false;
    int    _vsliderValue      = 0;
    const double VSliderTravel    = 228.0;
    const double VSliderThumbHalf = 21.0;

    // ── Drag / touch state (incl. the fading touch marker) ─────────────────────
    readonly DragState _drag = new();

    // ── Calibration state (mesh, bias dots, drag/hover, undo stack) ────────────
    readonly CalibrationState _cal = new();

    bool     _helpActive          = false;
    Mode     _pendingMode         = Mode.Unknown; // set while awaiting detection confirmation
    DateTime _pendingModeDeadline = DateTime.MinValue;
    const double PendingModeTimeoutSec = 3.0;

    // ── SysEx service ────────────────────────────────────────────────────────
    ISysExService _sysExService = null!;
    // Picks/switches the MIDI backend (TCP daemon vs direct USB) behind the service.
    MidiTransportCoordinator _midiCoord = null!;

    // ── SysEx status-bar indicators ───────────────────────────────────────────
    DateTime _sysExRxLastAt = DateTime.MinValue;
    DateTime _sysExTxLastAt = DateTime.MinValue;
    readonly DispatcherTimer _sysExDimTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    const double SysExDwellMs = 350;

    // ── Mode history (for transition detection) ───────────────────────────────
    Mode _currentMode = Mode.Unknown;   // last mode applied by SetModeButton
    Mode _prevMode    = Mode.Unknown;   // mode before the current one; survives across frames

    int _sysExDotPending;                   // 1 = a dot repaint is already queued

    // ── Program-edit-context state (daemon EDITCTX; Combi or Sequence origin) ──
    readonly EditContextState _editCtx = new();

    // ── Help window ──────────────────────────────────────────────────────────
    HelpWindow?          _helpWin;
    KeyboardInfoWindow?  _kbdInfoWin;
    SysExToolWindow?     _sysExToolWin;
    LibrarianShellWindow? _librarianShellWin;
    SampleEditorWindow?   _sampleEditorWin;
    LocalLibraryCache    _localLibraryCache = null!;

    // ── Misc ──────────────────────────────────────────────────────────────────
    System.Windows.Forms.NotifyIcon? _trayIcon;

    ICtrlClient      _ctrl        = null!;
    ScreenSession    _screenSession = null!;
    Action<string>?  _ctrlErrorHandler;   // held so SetCtrlClient/OnClosing can move or detach the instance CtrlError subscription
    readonly SeqTransportViewModel _seqTransport;
    double           _pixPerDip   = 1.0;
    bool             _shiftHeld   = false;
    readonly FullscreenState _fs = new();
    bool             _kbdCapture      = false;
    bool             _extendedKey     = false;   // set by LLKeyboardProc; true = numpad/extended key
    LowLevelKbProc?  _llKbProc        = null;    // field keeps delegate alive so GC won't collect it
    IntPtr           _llKbHook        = IntPtr.Zero;
    bool             _kbdSendEnabled  = true;   // false = capture active but nothing forwarded to Kronos
    HashSet<Key>     _instantKeys     = new();  // shifted overrides sent as press+release pair
    HashSet<Key>     _capsShiftedKeys = new();  // letters whose KEY 42 was injected for CapsLock mode
    // Raw mapping actually emitted on key-down, released verbatim on key-up.  Keyed by host Key
    // so a Shift change mid-hold can't make key-up release a different code than key-down pressed
    // (which used to leave the pressed code stuck down + its repeat timer running).
    Dictionary<Key, RawMapping> _activeRawKeys = new();

    // ── Key repeat ────────────────────────────────────────────────────────────
    readonly DispatcherTimer _repeatTimer = new();
    bool _repeatPhase = false;
    int  _repeatCode  = 0;
    AppSettings      _settings     = new();

    enum ConnState { Disconnected, Connecting, Connected }
    ConnState _connState     = ConnState.Disconnected;

    // Single source of truth for "a live stream is connected".
    bool IsConnected => _connState == ConnState.Connected;
    readonly FpsCounter _fpsCounter = new();

    // Frame classification - read by mode + combi + help detection.
    bool _detectedModeEver = false;  // set by SetModeButton
    bool _daemonBooting    = true;   // mirrors the daemon's own fail-safe default until the first STATE poll response
    readonly TopLeftOcr _topLeftOcr = new();
    readonly HelpDetector _helpDetector = new();

    // ── Layout preset ─────────────────────────────────────────────────────────
    LayoutPreset           _layoutPreset      = LayoutPreset.Full;
    FileManagerWindow?     _fileManagerWin;

    // The only window permitted to minimize - it collapses to the system tray (see InitTrayIcon),
    // rather than stranding itself in a screen corner. Every other window inherits ThemedWindow's
    // default (no minimize box).
    protected override bool AllowMinimize => true;

    public MainWindow()
    {
        InitializeComponent();

        OverlayLayer.RenderCallback = DrawOverlay;

        _settings  = Storage.LoadSettings();
        _zoomLevel = _settings.ZoomDefaultLevel;
        AppLog.DebugEnabled = _settings.DebugLogging;
        AppLog.Info($"[init] settings loaded - host={_settings.KronosHost} mode={(_settings.PullMode ? "pull" : "change")} fps={_settings.MaxFps} debug={_settings.DebugLogging}");
        _host     = _settings.KronosHost;
        _port     = _settings.StreamPort;
        _ctrlPort = _settings.CtrlPort;
        _pullMode = _settings.PullMode;
        _fps      = _settings.MaxFps;
        ParseArgs();

        // Kick off the Local Library's one-time referrer-catalog build (LocalLibraryCache.
        // BuildCatalogAsync - see its own comment for why this is otherwise a real 10-20s
        // stall) as soon as the app starts, not when the Librarian menu item is first clicked.
        // BuildCatalogAsync memoizes (a no-op if already built/building), so opening the
        // Librarian later just picks up whatever this warm-up has already finished, same as
        // LibrarianShellViewModel's own ctor-time WarmCatalogAsync - this just gives it a
        // multi-minute head start instead of starting cold at first open.
        _localLibraryCache = LocalLibraryCache.Open();
        _ = WarmLocalLibraryCatalogAsync();

        // Log daemon-side ERR responses and surface them in the notification bubble. Fires on
        // a background thread; SetNotification handles its own dispatch. Held in a field so
        // SetCtrlClient can move the subscription to each new CtrlClient instance (a host change
        // creates a fresh instance) and OnClosing can detach it.
        _ctrlErrorHandler = msg =>
        {
            AppLog.Warn($"[ctrl] daemon error: {msg}");
            SetNotification(msg, isError: true);
        };
        SetCtrlClient(_host, _ctrlPort);
        _screenSession = new ScreenSession(_ctrl);
        _screenSession.Connected += OnSessionConnected;
        _screenSession.ConnectionFailed += OnSessionConnectionFailed;
        _screenSession.Disconnected += OnSessionDisconnected;
        _screenSession.StateReceived += OnSessionStateReceived;
        _sysExService = new SysExService(Dispatcher);
        _sysExService.ValueSliderCc = _settings.ValueSliderCc;
        _sysExService.PullNamesOnChange = _settings.PullNamesOnChange;
        _sysExService.ValueSliderChanged += OnValueSliderSync;
        _sysExService.SysExTraffic += OnSysExTraffic;
        PerfStatusBarItem.DataContext = _sysExService;

        _seqTransport = new SeqTransportViewModel(this);
        SeqTransportBarItem.DataContext = _seqTransport;
        SeqSaveBarItem.DataContext = _seqTransport;

        // MIDI backend selection: prefers a directly-connected Kronos over USB
        // (Auto), independent of the TCP screen connection. Starts USB standalone
        // at launch if a device is present; the connect flow supplies TCP.
        _midiCoord = new MidiTransportCoordinator(_sysExService);
        _midiCoord.ActiveTransportChanged += OnMidiTransportChanged;
        _sysExService.ApplyMidiSettings(
            _settings.MidiMonitorEnabled, _settings.ProactiveSysExPolling,
            _settings.SysExPollIntervalSec, _settings.SysExPollOnChanges);
        _midiCoord.ApplySettings(_settings.MidiTransport, _settings.UsbMidiDeviceName);
        _midiCoord.Start();
        _sysExDimTimer.Tick += (_, _) => UpdateSysExDots();

        NotifyBubble.MouseLeftButtonDown += (_, _) => OnNotifyBubbleClick();
        KbdInfoBtn.MouseLeftButtonDown   += (_, _) => OpenKeyboardInfoWindow();

        _hideDataInput  = _settings.HideDataInput;
        _scrollDirection = _settings.ReverseScrolling;
        _hideValueInput = _settings.HideValueInput;
        _overrides    = Storage.LoadOverrides();
        (_cal.Mesh, _cal.BiasDots) = Storage.LoadCal();
        if (!_cal.Mesh.IsIdentity() || _cal.BiasDots.Count > 0)
            Console.WriteLine($"[cal] mesh loaded, {_cal.BiasDots.Count} bias dot(s)");

        Loaded      += OnLoaded;
        Closing     += OnClosing;
        Deactivated += (sender, e) =>
        {
            _kbdCapture = false;
            _instantKeys.Clear();
            StopRepeat();
            ReleaseActiveRawKeys();
            if (_capsShiftedKeys.Count > 0) { Ctrl(DaemonCommand.Shift(false)); _capsShiftedKeys.Clear(); }
            _cal.DraggingNode = null;
            UpdateKbdStatus();
            OverlayLayer.InvalidateVisual();
        };

        KeyDown    += OnKeyDown;
        KeyUp      += OnKeyUp;
        MouseMove  += OnMouseMove;
        MouseDown  += OnMouseDown;
        MouseUp    += OnMouseUp;
        MouseLeave += OnMouseLeave;
        MouseWheel += OnMouseWheel;
        FrameImage.LostMouseCapture += OnFrameLostMouseCapture;
        SizeChanged += (sender, e) => RefreshFrameRect();

        // The window's own SizeChanged is NOT enough. FrameImage gets resized or moved by
        // plenty of things that leave the window's size alone - the status bar growing when
        // the sequencer transport items appear on a mode change, the performance-name binding
        // getting a longer string, the MIDI badge showing up, a rail collapsing. None of those
        // fire Window.SizeChanged, so _frameRect would go on describing a layout that no longer
        // exists. Every click on the screen then falls outside it: no TOUCH_DOWN is sent, no
        // click marker is drawn, and _kbdCapture never turns on - clicks AND keyboard both go
        // dead with nothing logged, until something happens to resize the window. Track the
        // element's own geometry instead of trying to enumerate the things that can change it.
        // (SizeChanged only, deliberately - not LayoutUpdated, which fires on every completed
        // layout pass anywhere in the tree and would put a TranslatePoint on a hot path. In this
        // Grid of star-sized columns every known trigger resizes FrameImage rather than purely
        // moving it, so SizeChanged covers them.)
        FrameImage.SizeChanged += (sender, e) => RefreshFrameRect();

        // Last-resort reconciliation for the wheel/slider capture hazard described at
        // EndWheelDrag. If either still holds capture while claiming no active drag, a MouseUp
        // went missing - drop it before this click is routed, or the captured element eats the
        // event and marks it Handled and the window never sees another click. Deliberately
        // scoped to those two elements: menus, popups and ComboBoxes hold capture legitimately.
        PreviewMouseDown += (sender, e) =>
        {
            var captured = Mouse.Captured;
            if (captured == null) return;
            if (ReferenceEquals(captured, Data_Wheel)        && !_wheel.DragActive)  Mouse.Capture(null);
            if (ReferenceEquals(captured, ValueSliderCanvas) && !_vsliderDragActive) Mouse.Capture(null);
        };

        // Build the command registry BEFORE any surface wires to it (WireButtons here, WireMenu in
        // OnLoaded). ToDictionary fails fast on a duplicate Id - a launch is what trips it.
        _commands = BuildCommandRegistry().ToDictionary(c => c.Id);
        WireButtons();
        InitWheelDrag();
        InitValueSlider();

        _editCtx.FlashTimer.Interval = TimeSpan.FromMilliseconds(420);
        _editCtx.FlashTimer.Tick += (sender, e) =>
        {
            if (!_editCtx.Active) { _editCtx.FlashTimer.Stop(); return; }
            _editCtx.FlashState = !_editCtx.FlashState;
            BTN_Program.IsActive = _editCtx.FlashState;
        };
    }

    // The button icon changes only when SetModeButton() is called by detection; if
    // detection never confirms within PendingModeTimeoutSec, RenderTick applies the fallback.
    void SetPendingMode(Mode mode)
    {
        _pendingMode         = mode;
        _pendingModeDeadline = DateTime.Now.AddSeconds(PendingModeTimeoutSec);
        _sysExService.NotifyUserActivity();
    }

    void SendMode(Mode mode)
    {
        // Ignore mode changes until the board is verified booted. The Kronos front
        // panel ignores mode keys during boot anyway, so a press there does nothing
        // useful - but it would still stamp a pending mode whose timeout fallback
        // later lights the wrong button, and could perturb the boot sequence.
        // "Booted" = a real mode has been confirmed (_detectedModeEver) - resets on
        // (re)connect, so a fresh connection to a still-booting board also blocks
        // until its first mode is detected. The daemon itself now also refuses
        // MODE/EDITCTX-bearing data and rejects mutating commands with
        // "ERR BOOTING" while it considers the board still booting (see
        // KronosScreenRemoteDaemon/docs/api.md's "Boot gate" section), so this is
        // a client-side belt-and-suspenders check, not the only thing preventing
        // a stray press during boot.
        if (!IsConnected || !_detectedModeEver)
        {
            AppLog.Debug($"[mode] SendMode({mode}) ignored - board not booted " +
                         $"(connected={IsConnected}, detectedMode={_detectedModeEver})");
            return;
        }

        if (mode.ButtonName().Length == 0) return;
        SetPendingMode(mode);
        Ctrl(DaemonCommand.Button(mode));
    }

    void WireButtons()
    {
        // Mode buttons - send the hardware packet and record pending mode (via the shared "Mode ..."
        // registry commands). Icon only lights up once detection confirms (or timeout fallback fires).
        WireCommand(BTN_Setlist,  "Mode Setlist");
        WireCommand(BTN_Combi,    "Mode Combi");
        WireCommand(BTN_Program,  "Mode Program");
        WireCommand(BTN_Sequence, "Mode Sequence");
        WireCommand(BTN_Sampling, "Mode Sampling");
        WireCommand(BTN_Global,   "Mode Global");
        WireCommand(BTN_Disk,     "Mode Disk");

        BTN_Help.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Help));
        BTN_Compare.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Compare));

        BTN_data_dash.Click   += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.NumDash));
        BTN_data0.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(0));
        BTN_data_period.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.NumDot));
        BTN_data1.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(1));
        BTN_data2.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(2));
        BTN_data3.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(3));
        BTN_data4.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(4));
        BTN_data5.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(5));
        BTN_data6.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(6));
        BTN_data7.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(7));
        BTN_data8.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(8));
        BTN_data9.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(9));

        BTN_Exit.Click  += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Exit));
        BTN_Enter.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Enter));

        BTN_Inc.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Inc));
        BTN_Dec.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Dec));

        // Sequencer transport - daemon maps each to a front-panel SEQUENCER key press.
        // Record/Start are handled by SeqTransportBarItem's DataContext (SeqTransportViewModel)
        // via Command/IsChecked bindings in XAML instead of code-behind - see _seqTransport.
        WireCommand(BTN_SeqLocate, "Seq Locate");
        WireCommand(BTN_SeqRew,    "Seq Rewind");
        WireCommand(BTN_SeqFf,     "Seq Forward");
        WireCommand(BTN_SeqPause,  "Seq Pause");
        WireCommand(BTN_TapTempo,  "Tap Tempo");   // global (not seq-mode gated) - see command registry

        foreach (var btn in new KronosButton[] { BTN_Setlist, BTN_Combi, BTN_Program, BTN_Sequence,
                                                  BTN_Sampling, BTN_Global, BTN_Disk, BTN_Help, BTN_Compare })
            AddButtonContextMenu(btn);
    }

    void AddButtonContextMenu(KronosButton btn)
    {
        var cm    = new ContextMenu();
        var miKey = new MenuItem { Header = "Map to _Key..." };
        miKey.Click += (_, _) => OpenSettingsDialog(SettingsTab.KeyBindings);
        var miMacro = new MenuItem { Header = "_Assign Macro..." };
        miMacro.Click += (_, _) => OpenSettingsDialog(SettingsTab.Macros);
        cm.Items.Add(miKey);
        cm.Items.Add(miMacro);
        btn.ContextMenu = cm;
    }

    KronosButton? NumButton(int n) => n switch
    {
        0 => BTN_data0, 1 => BTN_data1, 2 => BTN_data2, 3 => BTN_data3,
        4 => BTN_data4, 5 => BTN_data5, 6 => BTN_data6, 7 => BTN_data7,
        8 => BTN_data8, 9 => BTN_data9, _ => null
    };

    void InitWheelDrag()
    {
        Data_Wheel.MouseDown        += OnWheelMouseDown;
        Data_Wheel.MouseMove        += OnWheelMouseMove;
        Data_Wheel.MouseUp          += OnWheelMouseUp;
        Data_Wheel.LostMouseCapture += (sender, e) => _wheel.DragActive = false;

        _wheel.AnimTimer.Interval = TimeSpan.FromMilliseconds(WheelState.AnimIntervalMs);
        _wheel.AnimTimer.Tick    += (sender, e) => AdvanceWheelAnim();
    }

    // Capturing the mouse AND marking the event Handled is a dangerous pair: if the MouseUp that
    // should end the gesture is ever lost (a dialog opening mid-drag, a popup taking capture),
    // the capture persists, every later click is routed here instead of to whatever was clicked,
    // and this handler marks it Handled - which starves the window-level OnMouseDown that owns
    // screen touches and keyboard capture. Worse, it is self-sustaining: each swallowed click
    // re-enters this method and re-captures. So never trust DragActive alone - reconcile against
    // the physical button state, which cannot go stale.
    void EndWheelDrag()
    {
        _wheel.DragActive = false;
        if (Data_Wheel.IsMouseCaptured) Data_Wheel.ReleaseMouseCapture();
    }

    void OnWheelMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { EndWheelDrag(); return; }
        _wheel.DragActive = true;
        _wheel.DragStartY = e.GetPosition(Data_Wheel).Y;
        _wheel.DragSteps  = 0;
        Data_Wheel.CaptureMouse();
        e.Handled = true;
    }

    void OnWheelMouseMove(object s, MouseEventArgs e)
    {
        if (!_wheel.DragActive) return;
        // Primary recovery point: the first mouse move after a missed MouseUp ends the drag
        // instead of spinning the wheel for every pixel of ordinary cursor movement.
        if (e.LeftButton != MouseButtonState.Pressed) { EndWheelDrag(); return; }
        double dy    = _wheel.DragStartY - e.GetPosition(Data_Wheel).Y; // +ve = up = CW
        int    steps = (int)(dy / WheelState.PxPerStep);
        
        int    diff  = steps - _wheel.DragSteps;

        if (diff > 0)
            for (int i = 0; i < diff;  i++) { Ctrl(DaemonCommand.Wheel(true));  TriggerWheelAnim(1);  }
        else if (diff < 0)
            for (int i = 0; i < -diff; i++) { Ctrl(DaemonCommand.Wheel(false)); TriggerWheelAnim(-1); }

        if (diff != 0) _wheel.DragSteps = steps;
        e.Handled = true;
    }

    void OnWheelMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EndWheelDrag();
        e.Handled = true;
    }

    void TriggerWheelAnim(int dir)
    {
        _wheel.AnimDir      = dir;
        _wheel.LastActivity = DateTime.Now;
        if (!_wheel.AnimTimer.IsEnabled)
        {
            _wheel.AnimTimer.Start();
            AdvanceWheelAnim();     // jump to next state immediately on first trigger
        }
    }

    void AdvanceWheelAnim()
    {
        if ((DateTime.Now - _wheel.LastActivity).TotalMilliseconds > WheelState.AnimIdleMs)
        {
            _wheel.AnimTimer.Stop();
            return;                 // hold current state - no snap-back
        }
        _wheel.AnimState = (_wheel.AnimState + _wheel.AnimDir + 3) % 3;
        SetWheelAngle(WheelState.Angles[_wheel.AnimState]);
    }

    void SetWheelAngle(double angle)
    {
        WheelRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        WheelRotate.Angle = angle;
    }

    void InitValueSlider()
    {
        ValueSliderCanvas.MouseDown        += OnVSliderMouseDown;
        ValueSliderCanvas.MouseMove        += OnVSliderMouseMove;
        ValueSliderCanvas.MouseUp          += OnVSliderMouseUp;
        ValueSliderCanvas.LostMouseCapture += (_, _) => _vsliderDragActive = false;
    }

    // Same capture-plus-Handled hazard as the data wheel - see EndWheelDrag.
    void EndVSliderDrag()
    {
        _vsliderDragActive = false;
        if (ValueSliderCanvas.IsMouseCaptured) ValueSliderCanvas.ReleaseMouseCapture();
    }

    void OnVSliderMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { EndVSliderDrag(); return; }
        if (e.ClickCount == 2)
        {
            double centerY = VSliderTravel * (127 - 64) / 127.0 + VSliderThumbHalf;
            UpdateVSliderFromMouse(centerY);
            e.Handled = true;
            return;
        }
        _vsliderDragActive = true;
        ValueSliderCanvas.CaptureMouse();
        UpdateVSliderFromMouse(e.GetPosition(ValueSliderCanvas).Y);
        e.Handled = true;
    }

    void OnVSliderMouseMove(object s, MouseEventArgs e)
    {
        if (!_vsliderDragActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndVSliderDrag(); return; }
        UpdateVSliderFromMouse(e.GetPosition(ValueSliderCanvas).Y);
        e.Handled = true;
    }

    void OnVSliderMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EndVSliderDrag();
        e.Handled = true;
    }


    void UpdateVSliderFromMouse(double mouseY)
    {
        double thumbTop = Math.Clamp(mouseY - VSliderThumbHalf, 0, VSliderTravel);
        System.Windows.Controls.Canvas.SetTop(ValueSliderThumb, thumbTop);
        int newVal = (int)Math.Round(127.0 * (VSliderTravel - thumbTop) / VSliderTravel);
        if (newVal != _vsliderValue)
        {
            _vsliderValue = newVal;
            Ctrl(DaemonCommand.ValueSlider(_vsliderValue));
        }
    }

    // Move the thumb to a 0-127 value WITHOUT sending VSLIDER back to the Kronos.
    // Inverse of the mouse mapping above.
    void SetVSliderValue(int val)
    {
        val = Math.Clamp(val, 0, 127);
        double thumbTop = VSliderTravel * (127 - val) / 127.0;
        System.Windows.Controls.Canvas.SetTop(ValueSliderThumb, thumbTop);
        _vsliderValue = val;
    }

    // ── SysEx-driven UI sync (runs on the UI thread - events are marshaled
    //    by SysExService before they reach here) ────────────────────────────────

    // Follow the hardware VALUE slider (incoming CC#ValueSliderCc). Ignore while
    // the user is dragging the UI slider so an echo can't fight the drag.
    void OnValueSliderSync(int val)
    {
        if (_vsliderDragActive) return;
        SetVSliderValue(val);
    }

    void WireMenu()
    {
        MENU_Connection.SubmenuOpened += (sender, e) =>
        {
            MNU_Disconnect.IsEnabled     = _connState != ConnState.Disconnected;  // allow aborting a hanging connect
            MNU_RefreshDisplay.IsEnabled = IsConnected;                           // REFRESH needs a live stream
        };
        MNU_Reconnect.Click  += (sender, e) => UserInitiatedReconnect();
        MNU_RefreshDisplay.Click += (sender, e) => Ctrl(DaemonCommand.RefreshDisplay);
        MNU_Disconnect.Click += (sender, e) => Disconnect();

        MENU_RecentHosts.SubmenuOpened += (sender, e) =>
        {
            // Rebuild list each time so it reflects newly added hosts immediately
            MENU_RecentHosts.Items.Clear();
            if (_settings.RecentHosts.Count == 0)
            {
                MENU_RecentHosts.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
                return;
            }
            foreach (var h in _settings.RecentHosts)
            {
                var host = h;
                var mi = new MenuItem { Header = host };
                mi.Click += (_, _) => { _host = host; _settings.KronosHost = host; Storage.SaveSettings(_settings); SetCtrlClient(_host, _ctrlPort); TriggerReconnect(); };
                MENU_RecentHosts.Items.Add(mi);
            }
            MENU_RecentHosts.Items.Add(new Separator());
            var miClear = new MenuItem { Header = "C_lear All" };
            miClear.Click += (_, _) => { _settings.RecentHosts.Clear(); Storage.SaveSettings(_settings); };
            MENU_RecentHosts.Items.Add(miClear);
        };

        MNU_CopyIP.Click += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(_host))
                Clipboard.SetText(_host);
        };
        MNU_Quit.Click += (sender, e) => TryQuit();

        MENU_View.SubmenuOpened += (sender, e) =>
        {
            MNU_Zoom.IsChecked         = _zoomOn;
            MNU_HideDataInput.IsChecked  = _hideDataInput;
            MNU_HideValueInput.IsChecked = _hideValueInput;
            MNU_AlwaysOnTop.IsChecked  = _settings.AlwaysOnTop;
            MNU_ScaleSharp.IsChecked   = _settings.ImageScalingMode == ScalingQuality.Sharp;
            MNU_ScaleSmooth.IsChecked  = _settings.ImageScalingMode == ScalingQuality.Smooth;
            MNU_ScaleHQ.IsChecked      = _settings.ImageScalingMode == ScalingQuality.HighQuality;
        };
        MNU_ScaleSharp.Click  += (sender, e) => SetScalingMode(ScalingQuality.Sharp);
        MNU_ScaleSmooth.Click += (sender, e) => SetScalingMode(ScalingQuality.Smooth);
        MNU_ScaleHQ.Click     += (sender, e) => SetScalingMode(ScalingQuality.HighQuality);
        MNU_ImageAdjust.Click += (sender, e) => OpenSettingsDialog(SettingsTab.Image);
        MNU_AlwaysOnTop.Click += (sender, e) =>
        {
            _settings.AlwaysOnTop = MNU_AlwaysOnTop.IsChecked;
            Topmost = _settings.AlwaysOnTop;
            Storage.SaveSettings(_settings);
        };
        MNU_Zoom.Click         += (sender, e) => { _zoomOn = MNU_Zoom.IsChecked; OverlayLayer.InvalidateVisual(); };
        MNU_Fullscreen.Click   += (sender, e) => ToggleFullscreen();
        MNU_HideDataInput.Click  += (sender, e) => ToggleHideDataInput();
        MNU_HideValueInput.Click += (sender, e) => ToggleHideValueInput();

        MENU_WinSize.SubmenuOpened += (sender, e) =>
        {
            MNU_Size75.IsChecked  = Math.Abs(_currentScale - 0.75) < 0.01;
            MNU_Size100.IsChecked = Math.Abs(_currentScale - 1.00) < 0.01;
            MNU_Size125.IsChecked = Math.Abs(_currentScale - 1.25) < 0.01;
            MNU_Size150.IsChecked = Math.Abs(_currentScale - 1.50) < 0.01;
            MNU_Size200.IsChecked = Math.Abs(_currentScale - 2.00) < 0.01;
        };
        MNU_Size75.Click  += (sender, e) => SetWindowSize(0.75);
        MNU_Size100.Click += (sender, e) => SetWindowSize(1.0);
        MNU_Size125.Click += (sender, e) => SetWindowSize(1.25);
        MNU_Size150.Click += (sender, e) => SetWindowSize(1.50);
        MNU_Size200.Click += (sender, e) => SetWindowSize(2.00);

        MENU_Tools.SubmenuOpened += (sender, e) =>
        {
            MNU_CalMode.IsChecked    = _cal.Mode;
            MNU_DisableKbd.IsChecked = !_kbdSendEnabled;
        };
        MNU_CalMode.Click   += (sender, e) => { _cal.Mode = MNU_CalMode.IsChecked; if (_cal.Mode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); };

        MNU_SettingsDlg.Click += (sender, e) => OpenSettingsDialog();
        MNU_ExportSettings.Click += (_, _) => ExportSettings();
        MNU_ImportSettings.Click += (_, _) => ImportSettings();

        MNU_FileManager.Click    += (_, _) => OpenFileManagerWindow();

        MNU_ShowHelp.Click       += (sender, e) => OpenHelpWindow();
        MNU_CommandPalette.Click += (sender, e) => OpenCommandPalette();
        MNU_About.Click          += (sender, e) => OpenAboutWindow();

        MENU_LayoutPreset.SubmenuOpened += (sender, e) =>
        {
            MNU_PresetFull.IsChecked    = _layoutPreset == LayoutPreset.Full;
            MNU_PresetFocused.IsChecked = _layoutPreset == LayoutPreset.Focused;
            MNU_HideDataInput.IsEnabled  = _layoutPreset == LayoutPreset.Full;
            MNU_HideValueInput.IsEnabled = _layoutPreset == LayoutPreset.Full;
        };
        MNU_PresetFull.Click    += (sender, e) => ApplyLayoutPreset(LayoutPreset.Full);
        MNU_PresetFocused.Click += (sender, e) => ApplyLayoutPreset(LayoutPreset.Focused);

        MNU_DisableKbd.Click += (sender, e) =>
        {
            _kbdSendEnabled = !MNU_DisableKbd.IsChecked;
            _instantKeys.Clear();
            StopRepeat();
            UpdateKbdStatus();
            OverlayLayer.InvalidateVisual();
        };
        MNU_InputTester.Click  += (sender, e) => new InputTesterWindow(_ctrl).OwnedBy(this).Show();
        MNU_SysExTool.Click    += (sender, e) => OpenSysExToolWindow();
        MNU_Librarian.Click    += (sender, e) => OpenLibrarianShellWindow();
        MNU_SampleEditor.Click += (sender, e) => OpenSampleEditorWindow();
        MNU_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        CTX_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        MNU_KbdWarp.Visibility = Visibility.Collapsed;

        // Bank Select - items built in code to avoid 28 x:Name declarations in XAML.
        // Nested into Internal/User/U-User sub-dropdowns (each just A-G) so the top
        // Bank Select popup stays a 3-item list instead of one flat 21-item dropdown.
        char[] bankLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G'];

        var bankInternal = new MenuItem { Header = "_Internal (A-G)" };
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"_{letter}" };
            WireCommand(mi, $"Bank I-{letter}");
            bankInternal.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(bankInternal);

        var bankUser = new MenuItem { Header = "_User (A-G)" };
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"_{letter}" };
            WireCommand(mi, $"Bank U-{letter}");
            bankUser.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(bankUser);

        var bankUUser = new MenuItem { Header = "Us_er (AA–GG)" };
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"_{letter}{letter}" };
            WireCommand(mi, $"Bank U-{letter}{letter}");
            bankUUser.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(bankUUser);

        WireCommand(MNU_Mode_Setlist,  "Mode Setlist");
        WireCommand(MNU_Mode_Combi,    "Mode Combi");
        WireCommand(MNU_Mode_Program,  "Mode Program");
        WireCommand(MNU_Mode_Sequence, "Mode Sequence");
        WireCommand(MNU_Mode_Sampling, "Mode Sampling");
        WireCommand(MNU_Mode_Global,   "Mode Global");
        WireCommand(MNU_Mode_Disk,     "Mode Disk");

        MENU_CalGrid.SubmenuOpened += (sender, e) =>
        {
            MNU_CalGrid3.IsChecked = _cal.Mesh.Cols == 3;
            MNU_CalGrid4.IsChecked = _cal.Mesh.Cols == 4;
            MNU_CalGrid5.IsChecked = _cal.Mesh.Cols == 5;
        };
        MNU_CalGrid3.Click += (sender, e) => SetCalGridSize(3);
        MNU_CalGrid4.Click += (sender, e) => SetCalGridSize(4);
        MNU_CalGrid5.Click += (sender, e) => SetCalGridSize(5);

        MNU_TestMode.Click += async (sender, e) =>
        {
            var result = MessageBox.Show(
                AppMessages.TestMode.Warning,
                AppMessages.TestMode.Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            Ctrl(DaemonCommand.Button(Mode.Program));
            await Task.Delay(500);
            Ctrl(DaemonCommand.EnterTestMode);
        };

        MNU_Screenshot.Click            += (sender, e) => SaveScreenshot();
        MNU_QuickSave.Click             += (sender, e) => QuickSaveScreenshot();
        MNU_CopyFrame.Click             += (sender, e) => CopyFrameToClipboard();
        MNU_OpenScreenshotsFolder.Click += (sender, e) => OpenScreenshotsFolder();
        MNU_OpenLog.Click               += (sender, e) => OnNotifyBubbleClick();
        MNU_CheckForUpdates.Click       += (sender, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Enigmahack/KronosScreenRemote/releases") { UseShellExecute = true }); }
            catch { }
        };
        MNU_ReportIssue.Click           += (sender, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Enigmahack/KronosScreenRemote/issues") { UseShellExecute = true }); }
            catch { }
        };

        CTX_Screenshot.Click     += (sender, e) => SaveScreenshot();
        CTX_QuickSave.Click      += (sender, e) => QuickSaveScreenshot();
        CTX_CopyFrame.Click      += (sender, e) => CopyFrameToClipboard();
        CTX_OpenScreenshots.Click += (sender, e) => OpenScreenshotsFolder();
        CTX_ZoomIn.Click         += (sender, e) => { _zoomLevel = Math.Min(10.0, Math.Round(_zoomLevel + 0.5, 1)); _zoomOn = true; OverlayLayer.InvalidateVisual(); };
        CTX_ZoomOut.Click        += (sender, e) => { _zoomLevel = Math.Max(_settings.ZoomDefaultLevel, Math.Round(_zoomLevel - 0.5, 1)); OverlayLayer.InvalidateVisual(); };
        CTX_ZoomReset.Click      += (sender, e) => { _zoomLevel = _settings.ZoomDefaultLevel; _zoomOn = false; OverlayLayer.InvalidateVisual(); };
        CTX_Fullscreen.Click     += (sender, e) => ToggleFullscreen();
        CTX_ScaleSharp.Click     += (sender, e) => SetScalingMode(ScalingQuality.Sharp);
        CTX_ScaleSmooth.Click    += (sender, e) => SetScalingMode(ScalingQuality.Smooth);
        CTX_ScaleHQ.Click        += (sender, e) => SetScalingMode(ScalingQuality.HighQuality);
        CTX_ImageAdjust.Click    += (sender, e) => OpenSettingsDialog(SettingsTab.Image);
        CTX_Reconnect.Click      += (sender, e) => UserInitiatedReconnect();
        CTX_Disconnect.Click     += (sender, e) => Disconnect();
        FrameImage.ContextMenuOpening += (s, e) =>
        {
            if (_cal.Mode) { e.Handled = true; return; }
            CTX_Fullscreen.IsChecked = _fs.Active;
            CTX_Disconnect.IsEnabled = _connState != ConnState.Disconnected;
            CTX_ZoomOut.IsEnabled    = _zoomOn && _zoomLevel > _settings.ZoomDefaultLevel;
            CTX_ZoomReset.IsEnabled  = _zoomOn;
            CTX_ScaleSharp.IsChecked  = _settings.ImageScalingMode == ScalingQuality.Sharp;
            CTX_ScaleSmooth.IsChecked = _settings.ImageScalingMode == ScalingQuality.Smooth;
            CTX_ScaleHQ.IsChecked     = _settings.ImageScalingMode == ScalingQuality.HighQuality;
        };

        CTX_WheelSensitivity.Click += (sender, e) => OpenSettingsDialog(SettingsTab.View);
        CTX_WheelReset.Click       += (sender, e) => { SetWheelAngle(0); _wheel.AnimState = 0; };

        CTX_StatusReconnect.Click  += (sender, e) => UserInitiatedReconnect();
        CTX_StatusDisconnect.Click += (sender, e) => Disconnect();
        CTX_StatusCopyIP.Click      += (sender, e) => { if (!string.IsNullOrEmpty(_host)) Clipboard.SetText(_host); };
        CTX_KbdEnable.Click         += (sender, e) => { _kbdSendEnabled = true;  _instantKeys.Clear(); StopRepeat(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_KbdDisable.Click        += (sender, e) => { _kbdSendEnabled = false; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_SetMaxFps.Click         += (sender, e) => OpenSettingsDialog(SettingsTab.Streaming);
        WireCommand(CTX_Mode_Setlist,  "Mode Setlist");
        WireCommand(CTX_Mode_Combi,    "Mode Combi");
        WireCommand(CTX_Mode_Program,  "Mode Program");
        WireCommand(CTX_Mode_Sequence, "Mode Sequence");
        WireCommand(CTX_Mode_Sampling, "Mode Sampling");
        WireCommand(CTX_Mode_Global,   "Mode Global");
        WireCommand(CTX_Mode_Disk,     "Mode Disk");
        CTX_OpenLogFile.Click       += (sender, e) => OnNotifyBubbleClick();
        CTX_ClearNotification.Click += (sender, e) => ClearNotification();

        MNU_HideDataInput.IsChecked  = _hideDataInput;
        MNU_HideValueInput.IsChecked = _hideValueInput;
    }

    // Change the upscale filter from the View / context menus.  Persist and apply immediately -
    // FrameImage repaints the current bitmap with the new filter without needing a fresh frame.
    void SetScalingMode(ScalingQuality mode)
    {
        _settings.ImageScalingMode = mode;
        Storage.SaveSettings(_settings);
        ApplyScalingMode();
    }

    string EffectiveScreenshotDir
    {
        get
        {
            var configured = _settings.ScreenshotDirectory;
            return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
                ? configured
                : Storage.DataDir;
        }
    }

    void SaveScreenshot()
    {
        if (_wb == null)
        {
            MessageBox.Show(AppMessages.Screenshot.NoFrameAvailable,
                AppMessages.Screenshot.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title            = "Save Kronos Screenshot",
            Filter           = "PNG Image|*.png",
            FileName         = $"kronos_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = EffectiveScreenshotDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            SaveFramePng(_wb, dlg.FileName);
            Console.WriteLine($"[screenshot] saved → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(AppMessages.Screenshot.SaveFailed(ex.Message),
                AppMessages.Screenshot.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void QuickSaveScreenshot()
    {
        if (_wb == null) { SetNotification(AppMessages.Notify.NoFrameToSave, isError: true); return; }
        try
        {
            var path = Path.Combine(EffectiveScreenshotDir, $"kronos_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveFramePng(_wb, path);
            SetNotification(AppMessages.Notify.Saved(System.IO.Path.GetFileName(path)), isError: false);
            Console.WriteLine($"[screenshot] quick-saved → {path}");
        }
        catch (Exception ex) { SetNotification(AppMessages.Notify.ScreenshotFailed(ex.Message), isError: true); }
    }

    static void SaveFramePng(BitmapSource frame, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var fs = File.OpenWrite(path);
        encoder.Save(fs);
    }

    void CopyFrameToClipboard()
    {
        if (_wb == null) { SetNotification(AppMessages.Notify.NoFrameToCopy, isError: true); return; }
        try
        {
            Clipboard.SetImage(_wb);
            SetNotification(AppMessages.Notify.FrameCopied, isError: false);
        }
        catch (Exception ex) { SetNotification(AppMessages.Notify.CopyFailed(ex.Message), isError: true); }
    }

    void OpenScreenshotsFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EffectiveScreenshotDir) { UseShellExecute = true }); }
        catch (Exception ex) { SetNotification(AppMessages.Notify.CouldNotOpenFolder(ex.Message), isError: true); }
    }

    void AddRecentHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _settings.RecentHosts.Remove(host);
        _settings.RecentHosts.Insert(0, host);
        if (_settings.RecentHosts.Count > 5)
            _settings.RecentHosts.RemoveRange(5, _settings.RecentHosts.Count - 5);
        Storage.SaveSettings(_settings);
    }

    void ParseArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host":  _host     = args[++i]; break;
                case "--port":  if (int.TryParse(args[++i], out int p))  _port     = p; break;
                case "--ctrl":  if (int.TryParse(args[++i], out int cp)) _ctrlPort = cp; break;
                case "--fps":   if (int.TryParse(args[++i], out int f))  _fps      = Math.Clamp(f, 0, 15); break;   // 0 = daemon max
                case "--mode":  _pullMode = args[++i] == "pull"; break;
            }
        }
    }

    bool IsAction(string action, KeyEventArgs e)
    {
        var bind = _settings.GetKeybind(action);
        if (bind.Key == Key.None) return false;
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        return bind.Key == k && Keyboard.Modifiers == bind.Modifiers;
    }

    // WPF saves and restores owner window geometry when a modal dialog is dismissed.
    // This undoes any maximized or manually-resized state the user had. Call this
    // wrapper instead of ShowDialog() directly to preserve the owner's geometry.
    bool ShowDialogPreservingGeometry(Window dialog)
    {
        var  priorState = WindowState;
        double priorW = Width, priorH = Height, priorL = Left, priorT = Top;

        bool result = dialog.ShowDialog() == true;

        bool stateChanged = WindowState != priorState;
        bool sizeChanged  = Math.Abs(Width - priorW) > 0.5 || Math.Abs(Height - priorH) > 0.5;
        bool posChanged   = Math.Abs(Left  - priorL) > 0.5 || Math.Abs(Top    - priorT) > 0.5;

        if (!stateChanged && !sizeChanged && !posChanged) return result;

        if (_fs.Active)
        {
            // Fullscreen is always Maximized+borderless; re-apply to recalculate bounds.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (priorState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = priorState;
                Width  = priorW;
                Height = priorH;
                Left   = priorL;
                Top    = priorT;
            }
        }
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
        return result;
    }

    void OpenSettingsDialog(SettingsTab tab = SettingsTab.General)
    {
        // Snapshot the current image-adjustment values so Cancel can undo any live preview.
        int    imgB  = _settings.ImageBrightness, imgC = _settings.ImageContrast,
               imgSat = _settings.ImageSaturation, imgSh = _settings.ImageSharpen;
        double imgG  = _settings.ImageGamma;

        var dlg = new SettingsWindow(_settings, m => _ = RunUserMacroAsync(m),
            showInputTester: () => new InputTesterWindow(_ctrl).OwnedBy(this).Show(),
            initialTab: tab,
            onImagePreview: PreviewImageAdjust)
            .OwnedBy(this);
        bool ok = ShowDialogPreservingGeometry(dlg);

        if (!ok)
        {
            // Undo the live preview - restore the values in effect before the dialog opened.
            PreviewImageAdjust(new ImagePreview(imgB, imgC, imgG, imgSat, imgSh));
            return;
        }
        ApplySettingsResult(dlg.Result, dlg.WasReset);
    }

    // Live image-adjustment preview driven by the Settings dialog's sliders: apply the values to
    // the in-memory settings and re-bake the tone LUT / re-render the current frame immediately.
    // Not persisted here (BtnOK's ApplySettingsResult saves; Cancel reverts to the pre-dialog snapshot).
    void PreviewImageAdjust(ImagePreview p)
    {
        _settings.ImageBrightness = p.Brightness;
        _settings.ImageContrast   = p.Contrast;
        _settings.ImageGamma      = p.Gamma;
        _settings.ImageSaturation = p.Saturation;
        _settings.ImageSharpen    = p.Sharpen;
        RebuildLut();
        if (_wb != null && _rawFrame != null) ApplyLut();
    }

    // Applies a new settings object live - shared by the Settings dialog's OK path
    // and the File ▸ Import Settings menu action. Persists, re-derives endpoints,
    // re-bakes the image pipeline, pushes MIDI/mirror/screensaver, and reconnects
    // when streaming parameters changed.
    void ApplySettingsResult(AppSettings newSettings, bool wasReset)
    {
        bool streamChanged = _settings.PullMode    != newSettings.PullMode  ||
                             _settings.MaxFps      != newSettings.MaxFps    ||
                             _settings.KronosHost  != newSettings.KronosHost||
                             _settings.StreamPort  != newSettings.StreamPort;

        _settings = newSettings;
        AppLog.DebugEnabled = _settings.DebugLogging;
        _host     = _settings.KronosHost;
        _port     = _settings.StreamPort;
        _ctrlPort = _settings.CtrlPort;
        _pullMode = _settings.PullMode;
        _fps      = _settings.MaxFps;
        _hideDataInput  = _settings.HideDataInput;
        _hideValueInput = _settings.HideValueInput;
        _scrollDirection = _settings.ReverseScrolling;
        SetCtrlClient(_host, _ctrlPort);
        Storage.SaveSettings(_settings);

        // Apply image-quality settings immediately: scaling filter, then re-bake the tone LUT and
        // re-render the current frame so brightness/contrast/gamma/saturation/sharpen take effect
        // now (even while disconnected, if a frame is still shown).
        ApplyScalingMode();
        RebuildLut();
        if (_wb != null && _rawFrame != null) ApplyLut();

        if (_rawFrame != null && _lut != null)
            Ctrl(DaemonCommand.RefreshDisplay);
        ApplyHideInputPanels();
        MNU_HideDataInput.IsChecked  = _hideDataInput;
        MNU_HideValueInput.IsChecked = _hideValueInput;

        _sysExService.ValueSliderCc = _settings.ValueSliderCc;
        _sysExService.PullNamesOnChange = _settings.PullNamesOnChange;
        _sysExService.ApplyMidiSettings(
            _settings.MidiMonitorEnabled, _settings.ProactiveSysExPolling,
            _settings.SysExPollIntervalSec, _settings.SysExPollOnChanges);
        // Re-pick the MIDI backend if the transport mode / USB device name changed.
        _midiCoord.ApplySettings(_settings.MidiTransport, _settings.UsbMidiDeviceName);
        ApplyMidiMonitorMenuState();

        if (wasReset)
        {
            if (IsConnected) TriggerReconnect();
            MessageBox.Show(
                AppMessages.SettingsReset.Done,
                AppMessages.SettingsReset.DoneTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsConnected && streamChanged)
        {
            // Streaming parameters changed - reconnect so new mode/fps take effect now.
            TriggerReconnect();
        }
        else if (IsConnected)
        {
            // Push VGA mirror + screensaver to daemon immediately if connected
            _mirrorState = _settings.VgaMirrorEnabled;
            Ctrl(DaemonCommand.VgaMirror(_mirrorState));
            Ctrl(DaemonCommand.ScreensaverTimeout(_settings.ScreensaverTimeout));
        }
    }

    // ── Settings import / export (File menu) ──────────────────────────────────

    void ExportSettings()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title    = "Export Settings",
            Filter   = "JSON Settings|*.json",
            FileName = "kronos_screenremote_settings.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Storage.SaveSettingsTo(_settings, dlg.FileName);
            MessageBox.Show(this, AppMessages.SettingsIo.Exported(dlg.FileName),
                AppMessages.Titles.ExportComplete, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppMessages.SettingsIo.ExportFailed(ex.Message),
                AppMessages.Titles.ExportFailed, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ImportSettings()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Import Settings",
            Filter = "JSON Settings|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // Reuse the dialog's live-apply path so an import behaves exactly like
            // editing settings and clicking OK (persist + reconnect if needed).
            var imported = Storage.LoadSettingsFrom(dlg.FileName);
            ApplySettingsResult(imported, wasReset: false);
            MessageBox.Show(this, AppMessages.SettingsIo.ImportedAndApplied,
                AppMessages.Titles.ImportComplete, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppMessages.SettingsIo.ImportFailed(ex.Message),
                AppMessages.Titles.ImportFailed, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── FTP File Manager ─────────────────────────────────────────────────────

    void UserInitiatedReconnect()
    {
        // No IP configured yet → don't pop the FTP-login dialog against an empty host.
        // Hand off to the normal connect command, which surfaces the "enter IP address"
        // message and opens the Connection settings screen (ConnectAsync's empty-host branch).
        if (string.IsNullOrWhiteSpace(_host))
        {
            TriggerReconnect();
            return;
        }
        if (string.IsNullOrEmpty(_settings.FtpUsername))
        {
            ShowFtpCredentialsDialog();
            if (string.IsNullOrEmpty(_settings.FtpUsername)) return;
        }
        TriggerReconnect();
    }

    void OpenFileManagerWindow()
    {
        if (_fileManagerWin != null)
        {
            _fileManagerWin.Activate();
            return;
        }
        if (!IsConnected)
        {
            MessageBox.Show(AppMessages.FileManager.OpenNotConnected,
                AppMessages.FileManager.OpenNotConnectedTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureHasFtpCredentials()) return;

        _fileManagerWin = new FileManagerWindow(_host, _settings.FtpPort,
                                                _settings.FtpUsername, _settings.FtpPassword)
                          .OwnedBy(this);
        _fileManagerWin.Closed += (_, _) => _fileManagerWin = null;
        _fileManagerWin.Show();
    }

    void ShowFtpCredentialsDialog()
    {
        var dlg = new LoginDialog(_host, _settings.FtpPort,
                                  _settings.FtpUsername, _settings.FtpPassword)
                  .OwnedBy(this);
        if (ShowDialogPreservingGeometry(dlg))
        {
            _settings.FtpUsername = dlg.Username;
            _settings.FtpPassword = dlg.Password;
            if (dlg.SavePassword) Storage.SaveSettings(_settings);
        }
    }

    bool EnsureHasFtpCredentials()
    {
        if (!string.IsNullOrEmpty(_settings.FtpUsername)) return true;
        var dlg = new LoginDialog(_host, _settings.FtpPort).OwnedBy(this);
        if (!ShowDialogPreservingGeometry(dlg)) return false;
        _settings.FtpUsername = dlg.Username;
        _settings.FtpPassword = dlg.Password;
        if (dlg.SavePassword) Storage.SaveSettings(_settings);
        return true;
    }

    // ── Control port helper ───────────────────────────────────────────────────

    void Ctrl(string cmd)
    {
        AppLog.Debug($"[ctrl] {cmd}");
        _sysExService.NotifyUserActivity();
        _ctrl.Send(cmd);
    }

    // Single funnel for (re)pointing the ctrl client at an endpoint. CtrlClient is now a
    // per-endpoint instance (not a process-global static), so every create/swap MUST move the
    // ERR-error subscription to the new instance and dispose the old one - otherwise a host
    // change leaks a send loop + socket and daemon ERR responses stop surfacing. All four call
    // sites (ctor + settings-apply + recent-host pick) go through here.
    void SetCtrlClient(string host, int port)
    {
        var old = _ctrl;
        if (old != null)
        {
            if (_ctrlErrorHandler != null) old.CtrlError -= _ctrlErrorHandler;
            (old as IDisposable)?.Dispose();
        }
        var next = new CtrlClient(host, port);
        if (_ctrlErrorHandler != null) next.CtrlError += _ctrlErrorHandler;
        _ctrl = next;
        _screenSession?.SetCtrlClient(next);
    }

    void ICtrlSender.Send(string cmd) => Ctrl(cmd);

    // ── Keyboard status indicator ─────────────────────────────────────────────

    void UpdateKbdStatus()
    {
        Brush color;
        bool  slash;
        string tip;

        if (!_kbdSendEnabled)
        {
            color = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
            slash = true;
            tip   = "Keyboard send disabled";
        }
        else if (!_kbdCapture)
        {
            color = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            slash = true;
            tip   = "Keyboard: click screen panel to capture";
        }
        else
        {
            color = new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0xDD));
            slash = false;
            tip   = "Keyboard: forwarding keystrokes to Kronos";
        }

        KbdStatusIcon.Foreground = color;
        KbdStatusSlash.Stroke    = color;
        KbdStatusSlash.Visibility = slash ? Visibility.Visible : Visibility.Hidden;
        KbdStatusGrid.ToolTip    = tip;
    }

    // ── Notification bubble ───────────────────────────────────────────────────

    static readonly Color NotifyColorIdle  = Color.FromRgb(0x3A, 0x3A, 0x3A);
    static readonly Color NotifyColorError = Color.FromRgb(0xCC, 0x33, 0x33);

    // Thread-safe: dispatches to UI thread if called from a background thread.
    void SetNotification(string msg, bool isError)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => SetNotification(msg, isError)); return; }
        NotifyBubblePath.Fill = new SolidColorBrush(isError ? NotifyColorError : NotifyColorIdle);
        NotifyBubble.ToolTip  = msg + AppMessages.Notify.LogHintSuffix;
    }

    void ClearNotification()
    {
        NotifyBubblePath.Fill = new SolidColorBrush(NotifyColorIdle);
        NotifyBubble.ToolTip  = AppMessages.Notify.ClickToOpenLog;
    }

    void OnNotifyBubbleClick()
    {
        if (AppLog.LogPath is string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn($"[log] failed to open log file: {ex.Message}"); }
        }
        ClearNotification();
    }

    // ── Title management ──────────────────────────────────────────────────────

    void UpdateTitle(string? suffix = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => UpdateTitle(suffix)); return; }
        Title = suffix == null ? "Kronos ScreenRemote"
                               : $"Kronos ScreenRemote - {suffix}";
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    void SetConnectionStatus(ConnState state)
    {
        _connState = state;
        // Do NOT access IsLoaded here - FrameworkElement.IsLoaded calls VerifyAccess() in
        // .NET 10 WPF and throws InvalidOperationException when called from a non-UI thread.
        // Dispatcher.InvokeAsync is safe from any thread and queues the lambda for later.
        Dispatcher.InvokeAsync(() =>
        {
            StatusDot.Fill = state switch
            {
                ConnState.Connected  => Brushes.LimeGreen,
                ConnState.Connecting => Brushes.Gold,
                _                    => new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55))
            };
            StatusText.Foreground = state == ConnState.Connected
                ? new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            StatusText.Text = state switch
            {
                ConnState.Connected  => AppMessages.Connection.Connected(_host),
                ConnState.Connecting => AppMessages.Connection.Connecting(_host),
                // When USB MIDI is live, say so - otherwise a disconnected screen reads
                // as "broken" even though the SysEx features are fully working over USB.
                _ when _midiCoord.UsingUsb => AppMessages.Connection.UsbMidiScreenNotConnected,
                _                    => AppMessages.Connection.NotConnected
            };
            ConnModeText.Text = state == ConnState.Connected
                ? (_pullMode ? "Pull" : "Change")
                : "";
            // Tap tempo injects a front-panel key over the daemon ctrl channel, so it's
            // meaningful only while connected (unlike USB-MIDI SysEx features).
            BTN_TapTempo.IsEnabled = state == ConnState.Connected;
            if (state != ConnState.Connected) { FpsText.Text = ""; PingText.Text = ""; _fpsCounter.Reset(); }
            if (state == ConnState.Connected) StartPing(); else StopPing();
            if (state == ConnState.Connected) StartAudioCapture(); else StopAudioCapture();
            if (state != ConnState.Connected)
            {
                _rawFrame = null;
                _frameBuf = null;
                _wb = null;
                FrameImage.Source = null;
            }
            if (state == ConnState.Disconnected)
            {
                // Drop only the TCP MIDI path; a standalone USB transport (Auto/USB
                // mode with a device present) keeps running independent of the screen.
                _midiCoord.SetScreenConnection(false, _host, _ctrlPort);
                if (_editCtx.Active) { _editCtx.Active = false; _editCtx.FlashTimer.Stop(); }
                _editCtx.Origin = EditContext.None;
                _currentMode   = Mode.Unknown;
                _prevMode      = Mode.Unknown;
                _daemonBooting = true;   // fail-safe default until the next connection's first STATE poll
                ClearModeButtons();
                _seqTransport.Reset();
                _seqTransport.CurrentMode = Mode.Unknown;
                OverlayLayer.InvalidateVisual();
            }
        });
    }

    // ── Help window ───────────────────────────────────────────────────────────

    void OpenHelpWindow()
    {
        if (_helpWin != null && _helpWin.IsLoaded)
        {
            _helpWin.Activate();
            _helpWin.Focus();
            return;
        }
        _helpWin = new HelpWindow(_settings).OwnedBy(this);
        _helpWin.Show();
    }

    void OpenAboutWindow()
    {
        string? host = string.IsNullOrEmpty(_host) ? null : _host;
        ShowDialogPreservingGeometry(new AboutWindow(host, _ctrlPort).OwnedBy(this));
    }

    // The MIDI backend changed (TCP daemon ⇄ direct USB, or none). Surface it on
    // the performance status indicator so it's clear which path is live.
    void OnMidiTransportChanged(string? description)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PerfStatusBarItem.ToolTip = description == null
                ? "MIDI: not connected"
                : $"MIDI via {description}";
            UpdateMidiLinkBadge();
            // Mirror the active stream into the open SysEx monitor so it's clear which
            // link its traffic is flowing over.
            if (_sysExToolWin is { IsLoaded: true } tool)
                tool.SetActiveStream(_midiCoord.ActiveLinkLabel);
            // A USB hot-plug/removal while the screen is disconnected flips the status
            // line between "Not connected" and "USB MIDI - screen not connected". Update
            // just the text - not the full disconnected teardown, which would clear the
            // mode buttons USB is actively driving.
            if (!IsConnected)
                StatusText.Text = _midiCoord.UsingUsb
                    ? AppMessages.Connection.UsbMidiScreenNotConnected
                    : AppMessages.Connection.NotConnected;
        });
    }

    // Footer badge colours per link kind: USB green (native, fast), DIN amber (5-pin
    // interface, slow), TCP blue (network), None dim.
    static readonly System.Windows.Media.SolidColorBrush LinkUsbBrush  = ThemeBrushes.Frozen(0x7D, 0xC9, 0x7D);
    static readonly System.Windows.Media.SolidColorBrush LinkDinBrush  = ThemeBrushes.Frozen(0xCC, 0xAA, 0x33);
    static readonly System.Windows.Media.SolidColorBrush LinkTcpBrush  = ThemeBrushes.Frozen(0x88, 0xAA, 0xDD);
    static readonly System.Windows.Media.SolidColorBrush LinkNoneBrush = ThemeBrushes.Frozen(0x77, 0x77, 0x77);

    // Paint the footer TCP/USB/DIN badge from the coordinator's current link.
    void UpdateMidiLinkBadge()
    {
        var (text, brush) = _midiCoord.ActiveLink switch
        {
            MidiLinkKind.Usb => ("USB", LinkUsbBrush),
            MidiLinkKind.Din => ("DIN", LinkDinBrush),
            MidiLinkKind.Tcp => ("TCP", LinkTcpBrush),
            _                => ("-",   LinkNoneBrush),
        };
        MidiLinkBadge.Text            = text;
        MidiLinkBadge.Foreground      = brush;
        MidiLinkBadgeBorder.BorderBrush = brush;
    }

    void OnSysExTraffic(SysExTrafficEntry entry)
    {
        if (entry.IsSend) _sysExTxLastAt = DateTime.Now;
        else              _sysExRxLastAt = DateTime.Now;

        // Coalesce: the 50 ms dim timer repaints the dots continuously, so we only
        // need to poke it awake. One pending repaint at a time - a memory-speed event
        // flood (or per-change name pulls) would otherwise queue a Dispatcher call per
        // message and swamp the UI thread. The timestamps above are what actually
        // drive the dot state; this just ensures the timer is running.
        if (Interlocked.Exchange(ref _sysExDotPending, 1) == 0)
            Dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _sysExDotPending, 0);
                UpdateSysExDots();
                if (!_sysExDimTimer.IsEnabled) _sysExDimTimer.Start();
            });
    }

    static readonly System.Windows.Media.SolidColorBrush SysExRxActiveBrush =
        new(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
    static readonly System.Windows.Media.SolidColorBrush SysExTxActiveBrush =
        new(System.Windows.Media.Color.FromRgb(0xAF, 0x4C, 0x50));
    static readonly System.Windows.Media.SolidColorBrush SysExRxDimBrush =
        new(System.Windows.Media.Color.FromRgb(0x2A, 0x3A, 0x2A));
    static readonly System.Windows.Media.SolidColorBrush SysExTxDimBrush =
        new(System.Windows.Media.Color.FromRgb(0x3A, 0x2A, 0x2A));

    void UpdateSysExDots()
    {
        var now = DateTime.Now;
        bool rxActive = (now - _sysExRxLastAt).TotalMilliseconds < SysExDwellMs;
        bool txActive = (now - _sysExTxLastAt).TotalMilliseconds < SysExDwellMs;

        SysExRxDot.Fill   = rxActive ? SysExRxActiveBrush : SysExRxDimBrush;
        SysExRxArrow.Foreground = rxActive ? SysExRxActiveBrush : SysExRxDimBrush;
        SysExTxDot.Fill   = txActive ? SysExTxActiveBrush : SysExTxDimBrush;
        SysExTxArrow.Foreground = txActive ? SysExTxActiveBrush : SysExTxDimBrush;

        if (!rxActive && !txActive) _sysExDimTimer.Stop();
    }

    void ApplyMidiMonitorMenuState()
    {
        bool enabled = _settings.MidiMonitorEnabled;
        MNU_SysExTool.IsEnabled = enabled;
        MNU_SysExTool.Opacity   = enabled ? 1.0 : 0.4;
    }

    void OpenSysExToolWindow()
    {
        if (_sysExToolWin != null && _sysExToolWin.IsLoaded)
        {
            _sysExToolWin.Activate();
            _sysExToolWin.Focus();
            return;
        }
        _sysExToolWin = new SysExToolWindow(_sysExService, _settings.MidiOutputChannel).OwnedBy(this);
        _sysExToolWin.Closed += (_, _) =>
        {
            _settings.MidiOutputChannel = _sysExToolWin.SelectedChannel;
            Storage.SaveSettings(_settings);
        };
        _sysExToolWin.SetActiveStream(_midiCoord.ActiveLinkLabel);   // seed with the current link
        _sysExToolWin.Show();
    }

    // Fire-and-forget from the constructor, mirroring LibrarianShellViewModel.WarmCatalogAsync's
    // own exception handling - without an explicit catch/log here, a blob-IO failure (e.g. the
    // library share going away) would be an unobserved task exception, invisible until the
    // Librarian window is opened and pays the same cost again. LocalPane.IsIndexing (which hides
    // the Local Library tree mid-build) is owned by LibrarianShellViewModel, not this window, so
    // it isn't touched here - nothing local-library-shaped is on screen yet at app startup.
    async Task WarmLocalLibraryCatalogAsync()
    {
        try { await _localLibraryCache.BuildCatalogAsync(); }
        catch (Exception ex) { AppLog.Warn($"[librarian] startup catalog warm-up failed: {ex.Message}"); }
    }

    // The rebuilt Librarian - Phase 7's cutover retired the classic LibrarianWindow (and its
    // SetListWindow/SetListSlotEditDialog satellites) entirely; this is now the only entry point.
    void OpenLibrarianShellWindow()
    {
        if (_librarianShellWin != null && _librarianShellWin.IsLoaded)
        {
            _librarianShellWin.Activate();
            _librarianShellWin.Focus();
            return;
        }

        // _localLibraryCache's catalog build was already kicked off at app startup (see the
        // constructor's WarmLocalLibraryCatalogAsync call) - LibrarianShellViewModel's own ctor
        // calls BuildCatalogAsync() again, which is a no-op if that build already finished, or
        // just awaits whatever's left of it otherwise. Either way, opening the window no longer
        // depends on paying this cost cold.
        _librarianShellWin = new LibrarianShellWindow(_sysExService, _localLibraryCache, _settings, _host).OwnedBy(this);
        _librarianShellWin.Closed += (_, _) =>
        {
            _librarianShellWin = null;
            Dispatcher.BeginInvoke(Activate, DispatcherPriority.ApplicationIdle);
        };
        _librarianShellWin.Show();
    }

    void OpenSampleEditorWindow()
    {
        if (_sampleEditorWin != null && _sampleEditorWin.IsLoaded)
        {
            _sampleEditorWin.Activate();
            _sampleEditorWin.Focus();
            return;
        }
        // Deliberately NOT .OwnedBy(this) - a WPF/Win32 owned window is permanently kept
        // ABOVE its owner in z-order for as long as both are visible, even once the
        // owner is the active/focused window. That's real "always on top" (of this one
        // window, not the whole desktop) and was exactly the complaint: the user needs
        // to click back to the live Kronos screen on MainWindow while the editor stays
        // open, which an owned window never allows. Centering (normally ThemedWindow's
        // own CenterOwner default, which needs a real Owner to work) and "closes when
        // MainWindow closes" (normally automatic for an owned window) are both
        // reproduced manually instead - see the manual Left/Top below and OnClosing's
        // own explicit _sampleEditorWin.Close() call.
        var win = new SampleEditorWindow { WindowStartupLocation = WindowStartupLocation.Manual };
        win.Left = Left + (ActualWidth - win.Width) / 2;
        win.Top = Top + (ActualHeight - win.Height) / 2;
        _sampleEditorWin = win;
        _sampleEditorWin.Closed += (_, _) => _sampleEditorWin = null;
        _sampleEditorWin.Show();
    }

    void OpenKeyboardInfoWindow()
    {
        if (_kbdInfoWin != null && _kbdInfoWin.IsLoaded)
        {
            _kbdInfoWin.Activate();
            _kbdInfoWin.Focus();
            return;
        }
        _kbdInfoWin = new KeyboardInfoWindow(_host, _ctrlPort, () => IsConnected).OwnedBy(this);
        _kbdInfoWin.Show();
    }

    // ── Command palette ───────────────────────────────────────────────────────

    void OpenCommandPalette()
    {
        AppLog.Info("[palette] opening");
        // Fresh build so KeyHints reflect the current keybinds (a rebind since launch shows up
        // immediately) - same behaviour as before the registry existed.
        var pal = new CommandPaletteWindow(BuildCommandRegistry()).OwnedBy(this);
        pal.Show();
    }

    // THE command table - one definition per action. The palette consumes the whole list; the
    // buttons, menu items, context items, and keybind chain each consume the subset they expose,
    // by Id (see WireCommand / RunCommand), so an action like SendMode(Setlist) is defined here
    // ONCE instead of hand-wired into five parallel dispatch tables. For rebindable actions the
    // Id is the same action-name string IsAction / GetKeyName key off ("Mode Setlist", "Bank
    // I-A", "Seq Locate", ...); palette-only entries get a unique synthetic Id. Ids MUST be unique
    // - the ctor's ToDictionary fails fast on a duplicate.
    List<CommandEntry> BuildCommandRegistry()
    {
        string K(string action) => _settings.GetKeyName(action);
        return
        [
            // ── Connection
            new("Reconnect",       "Reconnect",       "",              () => TriggerReconnect()),
            new("RefreshDisplay",  "Refresh Display", "",              () => Ctrl(DaemonCommand.RefreshDisplay)),
            new("Disconnect",      "Disconnect",      "",              () => Disconnect()),
            new("Settings",        "Settings...",       "",              () => OpenSettingsDialog()),
            // ── View
            new("Fullscreen",      "Toggle Fullscreen",  K("Fullscreen"),    () => ToggleFullscreen()),
            new("Zoom Window",     "Toggle Zoom Window", K("Zoom Window"),   () => { _zoomOn = !_zoomOn; OverlayLayer.InvalidateVisual(); }),
            new("Zoom In",         "Zoom In",            K("Zoom In"),       () => DoZoomIn()),
            new("Zoom Out",        "Zoom Out",           K("Zoom Out"),      () => DoZoomOut()),
            new("WindowSize75",    "Window Size: Small (75%)",        "Ctrl+1", () => SetWindowSize(0.75)),
            new("WindowSize100",   "Window Size: Normal (100%)",      "Ctrl+2", () => SetWindowSize(1.0)),
            new("WindowSize125",   "Window Size: Large (125%)",       "Ctrl+3", () => SetWindowSize(1.25)),
            new("WindowSize150",   "Window Size: Extra Large (150%)", "Ctrl+4", () => SetWindowSize(1.50)),
            new("WindowSize200",   "Window Size: Huge (200%)",        "Ctrl+5", () => SetWindowSize(2.00)),
            new("HideDataInput",   "Hide/Show Data Input",  K("HideDataInput"),  () => ToggleHideDataInput()),
            new("HideValueInput",  "Hide/Show Value Input", K("HideValueInput"), () => ToggleHideValueInput()),
            new("LayoutFull",      "Layout: Full",    "", () => ApplyLayoutPreset(LayoutPreset.Full)),
            new("LayoutFocused",   "Layout: Focused", "", () => ApplyLayoutPreset(LayoutPreset.Focused)),
            // ── Tools
            new("KeyboardInfo",    "Keyboard Info",           "",              () => OpenKeyboardInfoWindow()),
            new("Mirror",          "Toggle VGA Mirror",       K("Mirror"),        () => { _mirrorState = !_mirrorState; Ctrl(DaemonCommand.VgaMirror(_mirrorState)); }),
            new("Calibrate",       "Toggle Calibration Mode", K("Calibrate"),     () => { _cal.Mode = !_cal.Mode; if (_cal.Mode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); }),
            new("SaveScreenshot",  "Save Screenshot...",        "",              () => SaveScreenshot()),
            new("ToggleKeyboardSend", "Toggle Keyboard Send", "",              () => { _kbdSendEnabled = !_kbdSendEnabled; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); }),
            // ── Mode select (Id = action name; also wired to mode buttons + menu + context menu + keybinds)
            new("Mode Setlist",  "Mode: Setlist",  K("Mode Setlist"),  () => SendMode(Mode.Setlist)),
            new("Mode Combi",    "Mode: Combi",    K("Mode Combi"),    () => SendMode(Mode.Combi)),
            new("Mode Program",  "Mode: Program",  K("Mode Program"),  () => SendMode(Mode.Program)),
            new("Mode Sequence", "Mode: Sequence", K("Mode Sequence"), () => SendMode(Mode.Sequence)),
            new("Mode Sampling", "Mode: Sampling", K("Mode Sampling"), () => SendMode(Mode.Sampling)),
            new("Mode Global",   "Mode: Global",   K("Mode Global"),   () => SendMode(Mode.Global)),
            new("Mode Disk",     "Mode: Disk",     K("Mode Disk"),     () => SendMode(Mode.Disk)),
            // ── Bank select (Id = action name; also wired to the Bank Select menu + keybinds)
            new("Bank I-A",  "Bank I-A",  K("Bank I-A"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'A'))),
            new("Bank I-B",  "Bank I-B",  K("Bank I-B"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'B'))),
            new("Bank I-C",  "Bank I-C",  K("Bank I-C"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'C'))),
            new("Bank I-D",  "Bank I-D",  K("Bank I-D"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'D'))),
            new("Bank I-E",  "Bank I-E",  K("Bank I-E"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'E'))),
            new("Bank I-F",  "Bank I-F",  K("Bank I-F"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'F'))),
            new("Bank I-G",  "Bank I-G",  K("Bank I-G"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'G'))),
            new("Bank U-A",  "Bank U-A",  K("Bank U-A"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'A'))),
            new("Bank U-B",  "Bank U-B",  K("Bank U-B"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'B'))),
            new("Bank U-C",  "Bank U-C",  K("Bank U-C"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'C'))),
            new("Bank U-D",  "Bank U-D",  K("Bank U-D"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'D'))),
            new("Bank U-E",  "Bank U-E",  K("Bank U-E"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'E'))),
            new("Bank U-F",  "Bank U-F",  K("Bank U-F"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'F'))),
            new("Bank U-G",  "Bank U-G",  K("Bank U-G"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'G'))),
            new("Bank U-AA", "Bank U-AA", K("Bank U-AA"), () => Ctrl(DaemonCommand.DoubleUserBank('A'))),
            new("Bank U-BB", "Bank U-BB", K("Bank U-BB"), () => Ctrl(DaemonCommand.DoubleUserBank('B'))),
            new("Bank U-CC", "Bank U-CC", K("Bank U-CC"), () => Ctrl(DaemonCommand.DoubleUserBank('C'))),
            new("Bank U-DD", "Bank U-DD", K("Bank U-DD"), () => Ctrl(DaemonCommand.DoubleUserBank('D'))),
            new("Bank U-EE", "Bank U-EE", K("Bank U-EE"), () => Ctrl(DaemonCommand.DoubleUserBank('E'))),
            new("Bank U-FF", "Bank U-FF", K("Bank U-FF"), () => Ctrl(DaemonCommand.DoubleUserBank('F'))),
            new("Bank U-GG", "Bank U-GG", K("Bank U-GG"), () => Ctrl(DaemonCommand.DoubleUserBank('G'))),
            // ── Sequencer transport (Id = action name; also wired to seq buttons + keybinds)
            new("Seq Locate",  "Seq: Locate",       K("Seq Locate"),  () => Ctrl(DaemonCommand.Button(PanelButton.SeqLocate))),
            new("Seq Rewind",  "Seq: Rewind",       K("Seq Rewind"),  () => Ctrl(DaemonCommand.Button(PanelButton.SeqRewind))),
            new("Seq Forward", "Seq: Fast-Forward", K("Seq Forward"), () => Ctrl(DaemonCommand.Button(PanelButton.SeqForward))),
            new("Seq Pause",   "Seq: Pause",        K("Seq Pause"),   () => Ctrl(DaemonCommand.Button(PanelButton.SeqPause))),
            new("Seq Record",  "Seq: Record",       K("Seq Record"),  () => _seqTransport.RecordCommand.Execute(null)),
            new("Seq Start",   "Seq: Start/Stop",   K("Seq Start"),   () => _seqTransport.StartStopCommand.Execute(null)),
            new("Seq Save",    "Write / Save",      K("Seq Save"),    () => _seqTransport.RecordCommand.Execute(null)),
            // Tap tempo: one BUTTON TAP_TEMPO press per invocation; the Kronos averages
            // successive taps itself. Global - enabled whenever connected, not seq-gated.
            new("Tap Tempo",   "Tap Tempo",         K("Tap Tempo"),   TapTempoOnce),
            // ── Help
            new("Help",  "Show Help", K("Help"), () => OpenHelpWindow()),
            new("About", "About",     "",        () => OpenAboutWindow()),
            new("Quit",  "Quit",      K("Quit"),  () => TryQuit()),
        ];
    }

    // ── Command registry dispatch ──────────────────────────────────────────────
    // Built once (ctor, before WireButtons) from BuildCommandRegistry. Lookup is BY ID AT
    // INVOCATION so a keybind rebind - or any future registry rebuild - can never stale the
    // wiring that buttons/menus captured. Buttons/menus/context/keybinds all reach the action
    // through here; only the palette holds its own freshly-built entries (for live KeyHints).
    Dictionary<string, CommandEntry> _commands = new();

    void RunCommand(string id)
    {
        if (_commands.TryGetValue(id, out var cmd)) cmd.Execute();
        else AppLog.Warn($"[cmd] unknown command id '{id}'");
    }

    // Wire a button / menu item's click straight to a registry command by Id. REPLACES the
    // old inline lambda - never add alongside one, or the command double-fires.
    void WireCommand(System.Windows.Controls.Primitives.ButtonBase btn, string id)
        => btn.Click += (_, _) => RunCommand(id);
    void WireCommand(MenuItem mi, string id)
        => mi.Click += (_, _) => RunCommand(id);

    // ── Tap tempo ───────────────────────────────────────────────────────────────
    // One tap = one front-panel TAP TEMPO press; the Kronos does its own averaging
    // (two taps set the tempo), so the client never computes BPM. Routed through the
    // command registry so the footer button, the "Tap Tempo" keybind, and the command
    // palette all share this one action. Requires the daemon ctrl channel (front-panel
    // injection is not available over the USB-MIDI-only path), so the button is enabled
    // only while connected - see SetConnectionStatus.
    void TapTempoOnce()
    {
        FlashTapTempo();
        Ctrl(DaemonCommand.Button(PanelButton.TapTempo));
    }

    // Briefly highlight the footer Tap Tempo button so each tap (mouse or keybind) reads
    // as registered - the plain footer Button has no KronosButton.FlashDepress. One shared
    // single-shot timer clears it back to the style's transparent base.
    static readonly Brush TapFlashBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));
    System.Windows.Threading.DispatcherTimer? _tapFlashTimer;
    void FlashTapTempo()
    {
        BTN_TapTempo.Background = TapFlashBrush;
        if (_tapFlashTimer == null)
        {
            _tapFlashTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(110),
            };
            _tapFlashTimer.Tick += (_, _) =>
            {
                _tapFlashTimer!.Stop();
                BTN_TapTempo.Background = Brushes.Transparent;
            };
        }
        _tapFlashTimer.Stop();
        _tapFlashTimer.Start();
    }

    // The mode-select registry Ids, in button/menu order - the keybind chain iterates these so
    // adding a mode is a one-line registry change, not a new hand-wired IsAction branch.
    static readonly string[] ModeCommandIds =
        ["Mode Setlist", "Mode Combi", "Mode Program", "Mode Sequence", "Mode Sampling", "Mode Global", "Mode Disk"];

    // ── Layout presets ────────────────────────────────────────────────────────

    void ApplyLayoutPreset(LayoutPreset preset, bool saveSettings = true)
    {
        _layoutPreset = preset;
        if (saveSettings)
        {
            _settings.LayoutPreset = preset;
            Storage.SaveSettings(_settings);
        }

        switch (preset)
        {
            case LayoutPreset.Full:
                _focusedDataExpanded  = false;
                _focusedValueExpanded = false;
                ControlRail.Visibility    = Visibility.Collapsed;
                ValueRail.Visibility      = Visibility.Collapsed;
                ControlViewbox.Visibility = Visibility.Visible;
                ControlsColumn.Width = _hideDataInput
                    ? new GridLength(0, GridUnitType.Star)
                    : new GridLength(FrameDesignWidth, GridUnitType.Star);
                ShowLeftPanel(!_hideValueInput);
                break;

            case LayoutPreset.Focused:
                _focusedDataExpanded  = _settings.FocusedDataExpanded;
                _focusedValueExpanded = _settings.FocusedValueExpanded;
                ControlRail.Visibility    = Visibility.Visible;
                ValueRail.Visibility      = Visibility.Visible;
                ControlViewbox.Visibility = _focusedDataExpanded ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)BtnRailExpand.Content).Text = _focusedDataExpanded ? "‹" : "›";
                BtnRailExpand.ToolTip = _focusedDataExpanded ? "Collapse data input" : "Expand data input";
                ((TextBlock)BtnValueRailExpand.Content).Text = _focusedValueExpanded ? "›" : "‹";
                BtnValueRailExpand.ToolTip = _focusedValueExpanded ? "Collapse value input" : "Expand value input";
                ControlsColumn.Width = _focusedDataExpanded
                    ? new GridLength(FrameDesignWidth, GridUnitType.Star)
                    : new GridLength(28);
                ShowLeftPanel(_focusedValueExpanded, showRail: true);
                break;

        }

        ResizeAndRefresh();

        MNU_PresetFull.IsChecked     = preset == LayoutPreset.Full;
        MNU_PresetFocused.IsChecked  = preset == LayoutPreset.Focused;
        MNU_HideDataInput.IsEnabled  = preset == LayoutPreset.Full;
        MNU_HideValueInput.IsEnabled = preset == LayoutPreset.Full;
    }

    void ToggleFocusedDataExpand()
    {
        if (_layoutPreset != LayoutPreset.Focused) return;
        _focusedDataExpanded = !_focusedDataExpanded;
        _settings.FocusedDataExpanded = _focusedDataExpanded;
        Storage.SaveSettings(_settings);
        ControlViewbox.Visibility = _focusedDataExpanded ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)BtnRailExpand.Content).Text = _focusedDataExpanded ? "‹" : "›";
        BtnRailExpand.ToolTip = _focusedDataExpanded ? "Collapse data input" : "Expand data input";
        ControlsColumn.Width = _focusedDataExpanded
            ? new GridLength(FrameDesignWidth, GridUnitType.Star)
            : new GridLength(28);
        ResizeAndRefresh();
    }

    void ToggleFocusedValueExpand()
    {
        if (_layoutPreset != LayoutPreset.Focused) return;
        _focusedValueExpanded = !_focusedValueExpanded;
        _settings.FocusedValueExpanded = _focusedValueExpanded;
        Storage.SaveSettings(_settings);
        ((TextBlock)BtnValueRailExpand.Content).Text = _focusedValueExpanded ? "›" : "‹";
        BtnValueRailExpand.ToolTip = _focusedValueExpanded ? "Collapse value input" : "Expand value input";
        ShowLeftPanel(_focusedValueExpanded, showRail: true);
        ResizeAndRefresh();
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────

    void ToggleFullscreen()
    {
        if (_fs.Active)
        {
            WindowStyle   = _fs.SavedStyle;
            WindowState   = _fs.SavedState;
            ResizeMode    = _fs.SavedResize;
            MainMenu.Visibility = Visibility.Visible;
            _fs.Active = false;
        }
        else
        {
            _fs.SavedState   = WindowState;
            _fs.SavedStyle   = WindowStyle;
            _fs.SavedResize  = ResizeMode;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = ResizeMode.NoResize;
            // Must pass through Normal so WPF recalculates maximized bounds for the
            // borderless style; skipping this leaves the old chrome-inclusive bounds
            // in place and the window overflows the screen by ~8 px on each edge.
            WindowState   = WindowState.Normal;
            WindowState   = WindowState.Maximized;
            MainMenu.Visibility = Visibility.Collapsed;
            _fs.Active = true;
        }
        // SizeChanged fires for the WindowState change but not for the menu
        // visibility change, so explicitly refresh after layout settles.
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    // Smallest client width that still shows every footer status-bar item (icons +
    // spacing) without clipping. Measured live from the bar's own content, so adding
    // items in XAML automatically widens the floor - there's no constant to keep in sync.
    // Probes at infinite width (rather than reading DesiredSize at the current width) so
    // the result is correct even when this runs while the window is already narrow, e.g.
    // when Focused is the startup preset.
    double MeasureStatusBarNaturalWidth()
    {
        double h = AppStatusBar.ActualHeight > 0 ? AppStatusBar.ActualHeight : 28;
        AppStatusBar.Measure(new Size(double.PositiveInfinity, h));
        double w = AppStatusBar.DesiredSize.Width;
        AppStatusBar.InvalidateMeasure();   // undo the probe; the real constraint re-applies next pass
        return w;
    }

    // Pin the window's minimum width to the footer's natural width so it can never be
    // dragged - or preset-sized - narrower than the status-bar icons. Computed once at
    // load while the stretchy performance-name item is still empty, so the floor is
    // "icons + spacing", not "icons + whatever performance name is showing".
    void UpdateMinimumWindowWidth()
    {
        if (!IsLoaded) return;
        UpdateLayout();                        // ensure ActualWidth reflects the current chrome
        var dp = (FrameworkElement)Content;
        if (dp.ActualWidth <= 0) return;
        double chromeW = Width - dp.ActualWidth;
        MinWidth = MeasureStatusBarNaturalWidth() + chromeW;
    }

    void SetWindowSize(double scale)
    {
        if (!IsLoaded) return;
        if (_fs.Active) return;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        _currentScale  = scale;
        var dp         = (FrameworkElement)Content;
        double chromeW = Width  - dp.ActualWidth;
        double chromeH = Height - dp.ActualHeight;
        double menuH   = dp.ActualHeight - RootGrid.ActualHeight;
        double targetW = _layoutPreset switch
        {
            LayoutPreset.Focused  => FrameDesignWidth
                                     + (_focusedValueExpanded ? 282.0 : 28.0)
                                     + (_focusedDataExpanded  ? FrameDesignWidth : 28.0),
            _                     => FrameDesignWidth
                                     + (_hideValueInput ? 0.0 : 282.0)
                                     + (_hideDataInput  ? 0.0 : FrameDesignWidth)
        };
        Width  = targetW * scale + chromeW;
        Height = FrameDesignHeight * scale + menuH + chromeH;
        _scaledW = Width; _scaledH = Height;
    }

    void ResizeAndRefresh()
    {
        if (!IsLoaded) return;
        SyncMainAreaColumn();
        // Skip SetWindowSize when already maximized (non-fullscreen): the window fills
        // the screen and there is nothing to resize. SetWindowSize would force it back
        // to Normal, which is exactly the bug we are avoiding here.
        // Re-apply the preset scale only while the window is still exactly the size
        // SetWindowSize last produced. Once it has been dragged to a size of the user's own,
        // forcing _currentScale back would silently discard it - which is what made OK on a
        // child dialog snap the main window back to the default. Unknown (_scaledW == NaN,
        // i.e. SetWindowSize has not run yet) keeps the original behaviour.
        bool userResized = !double.IsNaN(_scaledW)
                           && (Math.Abs(Width - _scaledW) > 0.5 || Math.Abs(Height - _scaledH) > 0.5);
        if (!_fs.Active && WindowState != WindowState.Maximized && !userResized)
            SetWindowSize(_currentScale);
        // SizeChanged may not fire (fullscreen, or maximized with no size change),
        // so always defer an explicit layout refresh.
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    void SyncMainAreaColumn()
    {
        MainAreaColumn.Width = new GridLength(FrameDesignWidth + ControlsColumn.Width.Value, GridUnitType.Star);
    }

    void ShowLeftPanel(bool show, bool showRail = false)
    {
        if (show)
        {
            LeftPanelColumn.Width    = new GridLength(282, GridUnitType.Star);
            LeftPanelColumn.MaxWidth = double.PositiveInfinity;
        }
        else if (showRail)
        {
            LeftPanelColumn.Width    = new GridLength(28);
            LeftPanelColumn.MaxWidth = 28;
        }
        else
        {
            LeftPanelColumn.Width    = new GridLength(0);
            LeftPanelColumn.MaxWidth = 0;
        }
        LeftPanelViewbox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyHideInputPanels()
    {
        if (_layoutPreset == LayoutPreset.Full)
        {
            ControlsColumn.Width = _hideDataInput
                ? new GridLength(0, GridUnitType.Star)
                : new GridLength(FrameDesignWidth, GridUnitType.Star);
            ShowLeftPanel(!_hideValueInput);
        }
        ResizeAndRefresh();
    }

    void ToggleHideDataInput()
    {
        _hideDataInput = !_hideDataInput;
        _settings.HideDataInput = _hideDataInput;
        Storage.SaveSettings(_settings);
        ApplyHideInputPanels();
        MNU_HideDataInput.IsChecked = _hideDataInput;
    }

    void ToggleHideValueInput()
    {
        _hideValueInput = !_hideValueInput;
        _settings.HideValueInput = _hideValueInput;
        Storage.SaveSettings(_settings);
        ApplyHideInputPanels();
        MNU_HideValueInput.IsChecked = _hideValueInput;
    }

    void TryQuit() => Close();

    // ── System tray ───────────────────────────────────────────────────────────

    void InitTrayIcon()
    {
        System.Drawing.Icon appIcon;
        try
        {
            var sri = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/Icons/AppIcon.ico"));
            appIcon = sri != null
                ? new System.Drawing.Icon(sri.Stream)
                : System.Drawing.SystemIcons.Application;
        }
        catch { appIcon = System.Drawing.SystemIcons.Application; }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Reconnect",  null, (_, _) => { RestoreFromTray(); TriggerReconnect(); });
        menu.Items.Add("Disconnect", null, (_, _) => Disconnect());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { RestoreFromTray(); TryQuit(); });

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon             = appIcon,
            Text             = "Kronos ScreenRemote",
            ContextMenuStrip = menu,
            Visible          = false
        };
        // Single LEFT click restores (matches how every other tray/taskbar app behaves - a
        // double-click used to be required). Right click still falls through to the context menu.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _trayIcon != null)
            {
                Hide();
                _trayIcon.Visible = true;
            }
        };
    }

    void RestoreFromTray()
    {
        if (_trayIcon != null) _trayIcon.Visible = false;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _fs.SavedState == WindowState.Minimized ? WindowState.Normal : _fs.SavedState;
        Activate();
    }
}
