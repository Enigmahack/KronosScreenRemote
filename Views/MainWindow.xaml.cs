using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow : Window
{
    // ── Connection settings ───────────────────────────────────────────────────
    string _host     = "";
    int    _port     = StreamReceiver.StreamPort;
    int    _ctrlPort = CtrlClient.CtrlPort;
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
    bool   _aspectLock   = true;
    bool   _mirrorState  = false;
    bool   _helpOpen     = false;
    bool   _zoomOn       = false;
    double _zoomLevel    = 2.5;
    bool   _hideDataInput       = false;
    bool   _hideValueInput      = false;
    bool   _focusedDataExpanded  = false;
    bool   _focusedValueExpanded = false;
    double _currentScale    = 1.0;
    Rect   _frameRect;           // screen rect of displayed frame

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

    // ── Mode polling ──────────────────────────────────────────────────────────
    CancellationTokenSource? _modePollCts;
    DateTime _lastUserModeChange  = DateTime.MinValue;
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

    // SysEx is the source of truth for the mode, but only while it is actively
    // transmitting. A live Mode Change (func 0x4E) stamps _lastSysExModeAt; screen
    // detection then defers to it for SysExModeGraceSec (covers screen-redraw lag).
    // Once SysEx goes silent — transmit off at the Kronos, or MIDI monitoring off
    // in-app — the grace lapses and screen detection drives the mode immediately.
    // Recency subsumes the old "proven" latch: if a 0x4E never fired, the stamp is
    // never fresh and the screen leads, so a SysEx-off Kronos degrades gracefully.
    Mode     _lastSysExMode   = Mode.Unknown;    // mode from the last live func 0x4E
    DateTime _lastSysExModeAt = DateTime.MinValue;
    const double SysExModeGraceSec = 1.0;
    int      _sysExDotPending;                   // 1 = a dot repaint is already queued

    // ── Combi program-edit state ──────────────────────────────────────────────
    readonly CombiEditState _combi = new();

    // ── Help window ──────────────────────────────────────────────────────────
    HelpWindow?          _helpWin;
    KeyboardInfoWindow?  _kbdInfoWin;
    SysExToolWindow?     _sysExToolWin;
    SetListWindow?       _setListWin;

    // ── Misc ──────────────────────────────────────────────────────────────────
    System.Windows.Forms.NotifyIcon? _trayIcon;

    // Cancels an in-flight connect when a newer connect/disconnect supersedes it, so a reconnect
    // issued while a prior attempt is stuck (FTP verify / 10 s TCP watchdog) is no longer swallowed.
    CancellationTokenSource? _connectCts;
    IStreamReceiver? _receiver;
    ICtrlClient      _ctrl        = null!;
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

    // Single source of truth for "a live stream is connected".  _connState is authoritative;
    // _receiver can briefly outlive the Connected state after a silent drop (OnDisconnected sets
    // Disconnected without nulling _receiver), so gate connected-only behaviour on this, never on
    // _receiver != null.
    bool IsConnected => _connState == ConnState.Connected;
    readonly FpsCounter _fpsCounter = new();

    // ── Boot splash / load-phase state ────────────────────────────────────────
    readonly BootState _boot = new();

    // Cross-cutting frame classification — read by mode + combi detection, not only boot.
    bool _detectedModeEver        = false;  // set by SetModeButton; boot exits once a mode is confirmed
    bool _frameIsMostlyBlack      = false;  // ≥90% black — suppresses mode/combi detection
    bool _frameIsLikelyBootScreen = false;  // ≥60% black — gates splash display

    // ── Layout preset ─────────────────────────────────────────────────────────
    LayoutPreset           _layoutPreset      = LayoutPreset.Full;
    FileManagerWindow?     _fileManagerWin;

    public MainWindow()
    {
        InitializeComponent();

        OverlayLayer.RenderCallback = DrawOverlay;
        WindowTheme.ApplyDarkCaption(this);

        _settings  = Storage.LoadSettings();
        _zoomLevel = _settings.ZoomDefaultLevel;
        AppLog.DebugEnabled = _settings.DebugLogging;
        AppLog.Info($"[init] settings loaded — host={_settings.KronosHost} mode={(_settings.PullMode ? "pull" : "change")} fps={_settings.MaxFps} debug={_settings.DebugLogging}");
        _host     = _settings.KronosHost;
        _port     = _settings.StreamPort;
        _ctrlPort = _settings.CtrlPort;
        _pullMode = _settings.PullMode;
        _fps      = _settings.MaxFps;
        ParseArgs();  // CLI args still win

        _ctrl = new CtrlClientAdapter(_host, _ctrlPort);
        _sysExService = new SysExService(Dispatcher);
        _sysExService.ValueSliderCc = _settings.ValueSliderCc;
        _sysExService.PullNamesOnChange = _settings.PullNamesOnChange;
        _sysExService.InitialModeDetected += OnSysExInitialMode;
        _sysExService.ModeChanged += OnSysExModeChange;
        _sysExService.ValueSliderChanged += OnValueSliderSync;
        _sysExService.SysExTraffic += OnSysExTraffic;
        PerfStatusBarItem.DataContext = _sysExService;

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

        // Log daemon-side ERR responses and surface them in the notification bubble.
        // Fires on a background thread; SetNotification handles its own dispatch.
        CtrlClient.OnCtrlError += msg =>
        {
            AppLog.Warn($"[ctrl] daemon error: {msg}");
            SetNotification(msg, isError: true);
        };

        NotifyBubble.MouseLeftButtonDown += (_, _) => OnNotifyBubbleClick();
        KbdInfoBtn.MouseLeftButtonDown   += (_, _) => OpenKeyboardInfoWindow();

        _hideDataInput  = _settings.HideDataInput;
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

        WireButtons();
        InitWheelDrag();
        InitValueSlider();

        _combi.FlashTimer.Interval = TimeSpan.FromMilliseconds(420);
        _combi.FlashTimer.Tick += (sender, e) =>
        {
            if (!_combi.Active) { _combi.FlashTimer.Stop(); return; }
            _combi.FlashState = !_combi.FlashState;
            BTN_Program.IsActive = _combi.FlashState;
        };
    }

    // Records a user-requested mode and starts the confirmation timeout.
    // The button icon changes only when SetModeButton() is called by detection; if
    // detection never confirms within PendingModeTimeoutSec, RenderTick applies the fallback.
    void SetPendingMode(Mode mode)
    {
        _pendingMode         = mode;
        _pendingModeDeadline = DateTime.Now.AddSeconds(PendingModeTimeoutSec);
        _lastUserModeChange  = DateTime.Now;
        _sysExService.NotifyUserActivity();
    }

    void SendMode(Mode mode)
    {
        // Ignore mode changes until the board is verified booted. The Kronos front
        // panel ignores mode keys during boot anyway, so a press there does nothing
        // useful — but it would still stamp a pending mode whose timeout fallback
        // later lights the wrong button, and could perturb the boot sequence.
        // "Booted" = a real mode has been confirmed (_detectedModeEver) and the boot
        // overlay is not active. Both reset on (re)connect, so a fresh connection to
        // a still-booting board also blocks until its first mode is detected.
        if (!IsConnected || !_detectedModeEver || _boot.Phase)
        {
            AppLog.Debug($"[mode] SendMode({mode}) ignored — board not booted " +
                         $"(connected={IsConnected}, detectedMode={_detectedModeEver}, boot={_boot.Phase})");
            return;
        }

        if (mode.ButtonName().Length == 0) return;
        SetPendingMode(mode);
        Ctrl(DaemonCommand.Button(mode));
    }

    void WireButtons()
    {
        // Mode buttons — send the hardware packet and record pending mode.
        // Icon only lights up once detection confirms (or timeout fallback fires).
        BTN_Setlist.Click  += (sender, e) => SendMode(Mode.Setlist);
        BTN_Combi.Click    += (sender, e) => SendMode(Mode.Combi);
        BTN_Program.Click  += (sender, e) => SendMode(Mode.Program);
        BTN_Sequence.Click += (sender, e) => SendMode(Mode.Sequence);
        BTN_Sampling.Click += (sender, e) => SendMode(Mode.Sampling);
        BTN_Global.Click   += (sender, e) => SendMode(Mode.Global);
        BTN_Disk.Click     += (sender, e) => SendMode(Mode.Disk);

        // Toggle buttons
        BTN_Help.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Help));
        BTN_Compare.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Compare));

        // Number pad (no animation, but sends packet)
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

        // Exit / Enter
        BTN_Exit.Click  += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Exit));
        BTN_Enter.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Enter));

        // Value Inc / Dec
        BTN_Inc.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Inc));
        BTN_Dec.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Dec));

        // Sequencer transport — daemon maps each to a front-panel SEQUENCER key press.
        BTN_SeqLocate.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqLocate));
        BTN_SeqRew.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqRewind));
        BTN_SeqFf.Click     += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqForward));
        BTN_SeqPause.Click  += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqPause));
        BTN_SeqRec.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqRecord));
        BTN_SeqStart.Click  += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.SeqStart));

        // Right-click context menus on mode and toggle buttons
        foreach (var btn in new KronosButton[] { BTN_Setlist, BTN_Combi, BTN_Program, BTN_Sequence,
                                                  BTN_Sampling, BTN_Global, BTN_Disk, BTN_Help, BTN_Compare })
            AddButtonContextMenu(btn);
    }

    void AddButtonContextMenu(KronosButton btn)
    {
        var cm    = new ContextMenu();
        var miKey = new MenuItem { Header = "Map to _Key…" };
        miKey.Click += (_, _) => OpenSettingsDialog(SettingsTab.KeyBindings);
        var miMacro = new MenuItem { Header = "_Assign Macro…" };
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

    void OnWheelMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _wheel.DragActive = true;
        _wheel.DragStartY = e.GetPosition(Data_Wheel).Y;
        _wheel.DragSteps  = 0;
        Data_Wheel.CaptureMouse();
        e.Handled = true;
    }

    void OnWheelMouseMove(object s, MouseEventArgs e)
    {
        if (!_wheel.DragActive) return;
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
        _wheel.DragActive = false;
        Data_Wheel.ReleaseMouseCapture();
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
            return;                 // hold current state — no snap-back
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

    void OnVSliderMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
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
        UpdateVSliderFromMouse(e.GetPosition(ValueSliderCanvas).Y);
        e.Handled = true;
    }

    void OnVSliderMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _vsliderDragActive = false;
        ValueSliderCanvas.ReleaseMouseCapture();
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

    // ── SysEx-driven UI sync (runs on the UI thread — events are marshaled
    //    by SysExService before they reach here) ────────────────────────────────

    // Initial mode from the SysEx probe (func 0x42). Sets the mode but does NOT
    // prove realtime transmit, so screen detection stays active as a fallback.
    void OnSysExInitialMode(int mode) => SetModeButton((Mode)mode);

    // Live mode change (func 0x4E) — the authoritative source of truth. Stamp the
    // time so screen detection defers to it for the brief redraw-lag window, then
    // apply it (overriding any mode the screen may have just read during the change).
    void OnSysExModeChange(int mode)
    {
        _lastSysExMode   = (Mode)mode;
        _lastSysExModeAt = DateTime.Now;
        SetModeButton((Mode)mode);
    }

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
                mi.Click += (_, _) => { _host = host; _settings.KronosHost = host; Storage.SaveSettings(_settings); _ctrl = new CtrlClientAdapter(_host, _ctrlPort); TriggerReconnect(); };
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
            MNU_AspectLock.IsChecked   = _aspectLock;
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
        MNU_AspectLock.Click   += (sender, e) => { _aspectLock = MNU_AspectLock.IsChecked; RefreshFrameRect(); };
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

        // Layout presets
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
        MNU_InputTester.Click  += (sender, e) => new InputTesterWindow(_ctrl) { Owner = this }.Show();
        MNU_SysExTool.Click    += (sender, e) => OpenSysExToolWindow();
        MNU_SetListView.Click  += (sender, e) => OpenSetListWindow();
        MNU_SyncNames.Click    += (sender, e) => _ = SyncNamesAsync();
        MNU_SyncAll.Click      += (sender, e) => _ = SyncAllAsync();
        MNU_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        CTX_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        MNU_KbdWarp.Visibility = Visibility.Collapsed;

        // Bank Select — items built in code to avoid 21 x:Name declarations in XAML
        char[] bankLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G'];
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"I-{letter}" };
            mi.Click += (sender, e) => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, letter));
            MENU_BankSelect.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(new Separator());
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"U-{letter}" };
            mi.Click += (sender, e) => Ctrl(DaemonCommand.BankButton(BankGroup.User, letter));
            MENU_BankSelect.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(new Separator());
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"U-{letter}{letter}" };
            mi.Click += (sender, e) => Ctrl(DaemonCommand.DoubleUserBank(letter));
            MENU_BankSelect.Items.Add(mi);
        }

        // Mode Select
        MNU_Mode_Setlist.Click  += (sender, e) => SendMode(Mode.Setlist);
        MNU_Mode_Combi.Click    += (sender, e) => SendMode(Mode.Combi);
        MNU_Mode_Program.Click  += (sender, e) => SendMode(Mode.Program);
        MNU_Mode_Sequence.Click += (sender, e) => SendMode(Mode.Sequence);
        MNU_Mode_Sampling.Click += (sender, e) => SendMode(Mode.Sampling);
        MNU_Mode_Global.Click   += (sender, e) => SendMode(Mode.Global);
        MNU_Mode_Disk.Click     += (sender, e) => SendMode(Mode.Disk);

        // Calibration grid size
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
                "This will place you into the Kronos Test Mode. All unsaved changes will be lost, " +
                "and your Kronos will need to be restarted after complete. Also, this is potentially " +
                "a dangerous operation and should only be performed if you are aware of the risk.\n\n" +
                "Do you wish to continue?",
                "Kronos Test Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            Ctrl(DaemonCommand.Button(Mode.Program));
            await Task.Delay(500);
            Ctrl(DaemonCommand.EnterTestMode);
        };

        // Screenshot and frame operations
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

        // Frame context menu
        CTX_Screenshot.Click     += (sender, e) => SaveScreenshot();
        CTX_QuickSave.Click      += (sender, e) => QuickSaveScreenshot();
        CTX_CopyFrame.Click      += (sender, e) => CopyFrameToClipboard();
        CTX_OpenScreenshots.Click += (sender, e) => OpenScreenshotsFolder();
        CTX_ZoomIn.Click         += (sender, e) => { _zoomLevel = Math.Min(10.0, Math.Round(_zoomLevel + 0.5, 1)); _zoomOn = true; OverlayLayer.InvalidateVisual(); };
        CTX_ZoomOut.Click        += (sender, e) => { _zoomLevel = Math.Max(_settings.ZoomDefaultLevel, Math.Round(_zoomLevel - 0.5, 1)); OverlayLayer.InvalidateVisual(); };
        CTX_ZoomReset.Click      += (sender, e) => { _zoomLevel = _settings.ZoomDefaultLevel; _zoomOn = false; OverlayLayer.InvalidateVisual(); };
        CTX_AspectLock.Click     += (sender, e) => { _aspectLock = CTX_AspectLock.IsChecked; RefreshFrameRect(); };
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
            CTX_AspectLock.IsChecked = _aspectLock;
            CTX_Fullscreen.IsChecked = _fs.Active;
            CTX_Disconnect.IsEnabled = _connState != ConnState.Disconnected;
            CTX_ZoomOut.IsEnabled    = _zoomOn && _zoomLevel > _settings.ZoomDefaultLevel;
            CTX_ZoomReset.IsEnabled  = _zoomOn;
            CTX_ScaleSharp.IsChecked  = _settings.ImageScalingMode == ScalingQuality.Sharp;
            CTX_ScaleSmooth.IsChecked = _settings.ImageScalingMode == ScalingQuality.Smooth;
            CTX_ScaleHQ.IsChecked     = _settings.ImageScalingMode == ScalingQuality.HighQuality;
        };

        // Wheel context menu
        CTX_WheelSensitivity.Click += (sender, e) => OpenSettingsDialog(SettingsTab.View);
        CTX_WheelReset.Click       += (sender, e) => { SetWheelAngle(0); _wheel.AnimState = 0; };

        // Status bar context menus
        CTX_StatusReconnect.Click  += (sender, e) => UserInitiatedReconnect();
        CTX_StatusDisconnect.Click += (sender, e) => Disconnect();
        CTX_StatusCopyIP.Click      += (sender, e) => { if (!string.IsNullOrEmpty(_host)) Clipboard.SetText(_host); };
        CTX_KbdEnable.Click         += (sender, e) => { _kbdSendEnabled = true;  _instantKeys.Clear(); StopRepeat(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_KbdDisable.Click        += (sender, e) => { _kbdSendEnabled = false; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_SetMaxFps.Click         += (sender, e) => OpenSettingsDialog(SettingsTab.Streaming);
        CTX_Mode_Setlist.Click      += (sender, e) => SendMode(Mode.Setlist);
        CTX_Mode_Combi.Click        += (sender, e) => SendMode(Mode.Combi);
        CTX_Mode_Program.Click      += (sender, e) => SendMode(Mode.Program);
        CTX_Mode_Sequence.Click     += (sender, e) => SendMode(Mode.Sequence);
        CTX_Mode_Sampling.Click     += (sender, e) => SendMode(Mode.Sampling);
        CTX_Mode_Global.Click       += (sender, e) => SendMode(Mode.Global);
        CTX_Mode_Disk.Click         += (sender, e) => SendMode(Mode.Disk);
        CTX_OpenLogFile.Click       += (sender, e) => OnNotifyBubbleClick();
        CTX_ClearNotification.Click += (sender, e) => ClearNotification();

        MNU_HideDataInput.IsChecked  = _hideDataInput;
        MNU_HideValueInput.IsChecked = _hideValueInput;
    }

    // Change the upscale filter from the View / context menus.  Persist and apply immediately —
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
            MessageBox.Show("No frame available — connect to Kronos first.",
                "Screenshot", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show($"Failed to save screenshot:\n{ex.Message}",
                "Screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void QuickSaveScreenshot()
    {
        if (_wb == null) { SetNotification("No frame to save — connect first", isError: true); return; }
        try
        {
            var path = Path.Combine(EffectiveScreenshotDir, $"kronos_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveFramePng(_wb, path);
            SetNotification($"Saved {System.IO.Path.GetFileName(path)}", isError: false);
            Console.WriteLine($"[screenshot] quick-saved → {path}");
        }
        catch (Exception ex) { SetNotification($"Screenshot failed: {ex.Message}", isError: true); }
    }

    // Encode a frame as PNG and write it to path.
    static void SaveFramePng(BitmapSource frame, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var fs = File.OpenWrite(path);
        encoder.Save(fs);
    }

    void CopyFrameToClipboard()
    {
        if (_wb == null) { SetNotification("No frame to copy — connect first", isError: true); return; }
        try
        {
            Clipboard.SetImage(_wb);
            SetNotification("Frame copied to clipboard", isError: false);
        }
        catch (Exception ex) { SetNotification($"Copy failed: {ex.Message}", isError: true); }
    }

    void OpenScreenshotsFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EffectiveScreenshotDir) { UseShellExecute = true }); }
        catch (Exception ex) { SetNotification($"Could not open folder: {ex.Message}", isError: true); }
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
                case "--fps":   if (int.TryParse(args[++i], out int f))  _fps      = Math.Min(f, 15); break;
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
            showInputTester: () => new InputTesterWindow(_ctrl) { Owner = this }.Show(),
            initialTab: tab,
            onImagePreview: PreviewImageAdjust)
            { Owner = this };
        bool ok = ShowDialogPreservingGeometry(dlg);

        if (!ok)
        {
            // Undo the live preview — restore the values in effect before the dialog opened.
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

    // Applies a new settings object live — shared by the Settings dialog's OK path
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
        _ctrl = new CtrlClientAdapter(_host, _ctrlPort);
        Storage.SaveSettings(_settings);

        // Apply image-quality settings immediately: scaling filter, then re-bake the tone LUT and
        // re-render the current frame so brightness/contrast/gamma/saturation/sharpen take effect
        // now (even while disconnected, if a frame is still shown).
        ApplyScalingMode();
        RebuildLut();
        if (_wb != null && _rawFrame != null) ApplyLut();

        if (_rawFrame != null && _lut != null)
        {
            bool meetsThreshold = IsFrameMostlyBlack(_rawFrame, _lut, _settings.BootScreenThreshold / 100.0);
            if (_boot.Phase && !meetsThreshold)
            {
                _boot.Phase      = false;
                _detectedModeEver = true;
            }
            else if (!_boot.Phase && meetsThreshold && !_detectedModeEver && IsConnected)
            {
                _boot.Phase         = true;
                _boot.PreloadTimerStart = DateTime.Now;
                BuildPreloadSchedule();
            }
            Ctrl(DaemonCommand.RefreshDisplay);
        }
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
                "All settings have been reset to defaults.\n\nCalibration data will fully take effect on the next launch.",
                "Settings Reset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsConnected && streamChanged)
        {
            // Streaming parameters changed — reconnect so new mode/fps take effect now.
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
            MessageBox.Show(this, $"Settings exported to:\n{dlg.FileName}",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, "Settings imported and applied.",
                "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed:\n{ex.Message}",
                "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── FTP File Manager ─────────────────────────────────────────────────────

    void UserInitiatedReconnect()
    {
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
            MessageBox.Show("Not connected to Kronos.\n\nConnect to Kronos first, then open the File Manager.",
                "File Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureHasFtpCredentials()) return;

        _fileManagerWin = new FileManagerWindow(_host, _settings.FtpPort,
                                                _settings.FtpUsername, _settings.FtpPassword)
                          { Owner = this };
        _fileManagerWin.Closed += (_, _) => _fileManagerWin = null;
        _fileManagerWin.Show();
    }

    void ShowFtpCredentialsDialog()
    {
        var dlg = new LoginDialog(_host, _settings.FtpPort,
                                  _settings.FtpUsername, _settings.FtpPassword)
                  { Owner = this };
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
        var dlg = new LoginDialog(_host, _settings.FtpPort) { Owner = this };
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
        NotifyBubble.ToolTip  = msg + "\n— click to open log";
    }

    void ClearNotification()
    {
        NotifyBubblePath.Fill = new SolidColorBrush(NotifyColorIdle);
        NotifyBubble.ToolTip  = "Click to open log";
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
                               : $"Kronos ScreenRemote — {suffix}";
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    void SetConnectionStatus(ConnState state)
    {
        _connState = state;
        // Do NOT access IsLoaded here — FrameworkElement.IsLoaded calls VerifyAccess() in
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
                ConnState.Connected  => $"Connected — {_host}",
                ConnState.Connecting => $"Connecting to {_host}…",
                // When USB MIDI is live, say so — otherwise a disconnected screen reads
                // as "broken" even though the SysEx features are fully working over USB.
                _ when _midiCoord.UsingUsb => "USB MIDI — screen not connected",
                _                    => "Not connected"
            };
            ConnModeText.Text = state == ConnState.Connected
                ? (_pullMode ? "Pull" : "Change")
                : "";
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
                if (_combi.Active) { _combi.Active = false; _combi.FlashTimer.Stop(); }
                _combi.IndicatorGoneAt = DateTime.MinValue;
                _currentMode = Mode.Unknown;
                _prevMode    = Mode.Unknown;
                ClearModeButtons();
                OverlayLayer.InvalidateVisual();
            }
            // Start boot overlay immediately on connect so it shows while waiting for first frame
            if (state == ConnState.Connected && _boot.FirstFrame == DateTime.MinValue)
                _boot.FirstFrame = DateTime.Now;
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
        _helpWin = new HelpWindow(_settings) { Owner = this };
        _helpWin.Show();
    }

    void OpenAboutWindow()
    {
        string? host = string.IsNullOrEmpty(_host) ? null : _host;
        ShowDialogPreservingGeometry(new AboutWindow(host, _ctrlPort) { Owner = this });
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
            // line between "Not connected" and "USB MIDI — screen not connected". Update
            // just the text — not the full disconnected teardown, which would clear the
            // mode buttons USB is actively driving.
            if (!IsConnected)
                StatusText.Text = _midiCoord.UsingUsb
                    ? "USB MIDI — screen not connected"
                    : "Not connected";
        });
    }

    // Footer badge colours per link kind: USB green (native, fast), DIN amber (5-pin
    // interface, slow), TCP blue (network), None dim.
    static readonly System.Windows.Media.SolidColorBrush LinkUsbBrush  = FrozenBrush(0x7D, 0xC9, 0x7D);
    static readonly System.Windows.Media.SolidColorBrush LinkDinBrush  = FrozenBrush(0xCC, 0xAA, 0x33);
    static readonly System.Windows.Media.SolidColorBrush LinkTcpBrush  = FrozenBrush(0x88, 0xAA, 0xDD);
    static readonly System.Windows.Media.SolidColorBrush LinkNoneBrush = FrozenBrush(0x77, 0x77, 0x77);

    static System.Windows.Media.SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // Paint the footer TCP/USB/DIN badge from the coordinator's current link.
    void UpdateMidiLinkBadge()
    {
        var (text, brush) = _midiCoord.ActiveLink switch
        {
            MidiLinkKind.Usb => ("USB", LinkUsbBrush),
            MidiLinkKind.Din => ("DIN", LinkDinBrush),
            MidiLinkKind.Tcp => ("TCP", LinkTcpBrush),
            _                => ("—",   LinkNoneBrush),
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
        // need to poke it awake. One pending repaint at a time — a memory-speed event
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
        _sysExToolWin = new SysExToolWindow(_sysExService, _settings.MidiOutputChannel) { Owner = this };
        _sysExToolWin.Closed += (_, _) =>
        {
            _settings.MidiOutputChannel = _sysExToolWin.SelectedChannel;
            Storage.SaveSettings(_settings);
        };
        _sysExToolWin.SetActiveStream(_midiCoord.ActiveLinkLabel);   // seed with the current link
        _sysExToolWin.Show();
    }

    void OpenSetListWindow()
    {
        if (_setListWin != null && _setListWin.IsLoaded)
        {
            _setListWin.Activate();
            _setListWin.Focus();
            return;
        }
        _setListWin = new SetListWindow(_sysExService, _host) { Owner = this };
        _setListWin.Show();
    }

    bool _syncBusy;                        // guards Sync Names and Sync All — one at a time
    CancellationTokenSource? _syncAllCts;  // non-null while Sync All runs → a second click cancels

    async Task SyncNamesAsync()
    {
        if (_syncBusy) return;
        if (!_sysExService.CanDump)
        {
            SetNotification("Enable MIDI monitoring first (Settings → MIDI/SysEx)", isError: true);
            return;
        }

        var choice = MessageBox.Show(
            "Request all program & combi names from the Kronos and cache them locally.\n\n" +
            "Internal and GM banks sync reliably. Some user banks may not — those can be " +
            "captured by triggering Global → Dump on the Kronos itself (the app captures " +
            "names from that too). Briefly shows \"Transmitting MIDI Data…\" on the Kronos." +
            "\n\nStart now?",
            "Sync Names", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK) return;

        _syncBusy = true;
        int lastDone = 0, lastTotal = 0;
        var progress = new Progress<(int Done, int Total, int Names)>(p =>
        {
            lastDone = p.Done; lastTotal = p.Total;
            SetNotification($"Syncing names… {p.Done}/{p.Total} banks — {p.Names} names", isError: false);
        });
        try
        {
            int names = await _sysExService.SyncNamesAsync(progress, CancellationToken.None);
            if (lastTotal > 0 && lastDone < lastTotal)
                SetNotification($"Synced {lastDone}/{lastTotal} banks ({names} names cached). " +
                                "Any user banks that didn't sync: use Global → Dump on the Kronos.", isError: false);
            else
                SetNotification($"Name sync complete — {names} names cached", isError: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[sync-names] {ex.Message}");
            SetNotification($"Name sync failed: {ex.Message}", isError: true);
        }
        finally { _syncBusy = false; }
    }

    // "Sync All" — program/combi names (phase 1) then every set list (phase 2), each
    // cached locally. Reuses the service methods directly (not the Sync Names wrapper,
    // whose own dialog/guard we don't want here). Toggle-cancel: invoke again while it
    // runs to stop; whatever synced so far is already saved.
    async Task SyncAllAsync()
    {
        // Second click while running → cancel.
        if (_syncAllCts is { } running)
        {
            running.Cancel();
            SetNotification("Sync All: cancelling after the current item…", isError: false);
            return;
        }
        if (_syncBusy)
        {
            SetNotification("A sync is already running", isError: true);
            return;
        }
        if (!_sysExService.CanDump)
        {
            SetNotification("Enable MIDI monitoring first (Settings → MIDI/SysEx)", isError: true);
            return;
        }

        // On the (slow) TCP path with USB not in use? Point the user at the fast path.
        // Skip the tip if they've explicitly forced TCP in Settings.
        bool onSlowTcp = !_midiCoord.UsingUsb && _settings.MidiTransport != MidiTransportMode.Tcp;
        string usbTip = onSlowTcp
            ? "\n\nTip: you're syncing over the network (TCP), which is slow for large " +
              "dumps. Direct USB is usually much faster on the Kronos — connect it to " +
              "this PC with a USB cable to try (the app switches to USB automatically, " +
              "no other change needed). Or continue over the network now."
            : "";

        var choice = MessageBox.Show(
            "Sync everything from the Kronos and cache it locally:\n" +
            "  •  All program & combi names\n" +
            "  •  All 128 set lists (names, slot colors, notes)\n\n" +
            "This can take several minutes depending on how many set lists you have. " +
            "The Kronos briefly shows \"Transmitting MIDI Data…\". You can cancel anytime " +
            "(Tools → Cancel Sync All); progress is saved as it goes." +
            usbTip + "\n\nStart now?",
            "Sync All", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK) return;

        _syncBusy = true;
        var cts = new CancellationTokenSource();
        _syncAllCts = cts;
        MNU_SyncAll.Header = "Cancel Sync _All";
        try
        {
            // Phase 1 — program/combi names (skips banks already in the ledger).
            var nameProgress = new Progress<(int Done, int Total, int Names)>(p =>
                SetNotification($"Sync All — names: {p.Done}/{p.Total} banks, {p.Names} cached", isError: false));
            int names = await _sysExService.SyncNamesAsync(nameProgress, cts.Token);

            // Phase 2 — every set list (name + slot colors + notes).
            var listProgress = new Progress<(int Done, int Total, int Found)>(p =>
                SetNotification($"Sync All — set lists: {p.Done}/{p.Total}, {p.Found} with content", isError: false));
            var result = await _sysExService.DumpAllSetListsAsync(listProgress, cts.Token);

            // Merge into the on-disk set-list cache (keyed by host, same as the viewer):
            // content → store; confirmed-blank → drop a now-stale entry; no-response →
            // leave untouched (a transient miss must not delete good cached data).
            if (result.Found.Count > 0 || result.ConfirmedEmpty.Count > 0)
            {
                // Load + merge + persist the whole cache off the UI thread — each Set
                // List is ~79 KB of decoded data, so doing this inline froze the main
                // window at the end of a sync.
                await Task.Run(() =>
                {
                    var cache = Storage.LoadSetLists(_host);
                    foreach (var kv in result.Found) cache[kv.Key] = kv.Value;
                    foreach (var n  in result.ConfirmedEmpty) cache.Remove(n);
                    Storage.SaveSetLists(_host, cache);
                });
                if (_setListWin is { IsLoaded: true } win) await win.ReloadCacheAsync();
            }

            string via = _midiCoord.UsingUsb ? "USB" : "TCP";
            if (result.Cancelled)
                SetNotification($"Sync All cancelled — {names} names, {result.Found.Count} set lists saved so far (via {via})",
                                isError: false);
            else
                SetNotification($"Sync All complete — {names} names, {result.Found.Count} set lists cached (via {via})",
                                isError: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[sync-all] {ex.Message}");
            SetNotification($"Sync All failed: {ex.Message}", isError: true);
        }
        finally
        {
            _syncBusy   = false;
            _syncAllCts = null;
            cts.Dispose();
            MNU_SyncAll.Header = "Sync _All (Names + Set Lists)…";
        }
    }

    void OpenKeyboardInfoWindow()
    {
        if (_kbdInfoWin != null && _kbdInfoWin.IsLoaded)
        {
            _kbdInfoWin.Activate();
            _kbdInfoWin.Focus();
            return;
        }
        _kbdInfoWin = new KeyboardInfoWindow(_host, _ctrlPort, () => IsConnected) { Owner = this };
        _kbdInfoWin.Show();
    }

    // ── Command palette ───────────────────────────────────────────────────────

    void OpenCommandPalette()
    {
        AppLog.Info("[palette] opening");
        var pal = new CommandPaletteWindow(BuildCommandEntries()) { Owner = this };
        pal.Show();
    }

    List<CommandEntry> BuildCommandEntries()
    {
        string K(string action) => _settings.GetKeyName(action);
        return
        [
            // ── Connection
            new("Reconnect",                        "",              () => TriggerReconnect()),
            new("Refresh Display",                  "",              () => Ctrl(DaemonCommand.RefreshDisplay)),
            new("Disconnect",                       "",              () => Disconnect()),
            new("Settings…",                        "",              () => OpenSettingsDialog()),
            // ── View
            new("Toggle Fullscreen",                K("Fullscreen"),    () => ToggleFullscreen()),
            new("Toggle Aspect Lock",               K("AspectLock"),    () => { _aspectLock = !_aspectLock; RefreshFrameRect(); }),
            new("Toggle Zoom Window",               K("Zoom Window"),   () => { _zoomOn = !_zoomOn; OverlayLayer.InvalidateVisual(); }),
            new("Window Size: Small (75%)",         "Ctrl+1",           () => SetWindowSize(0.75)),
            new("Window Size: Normal (100%)",       "Ctrl+2",           () => SetWindowSize(1.0)),
            new("Window Size: Large (125%)",        "Ctrl+3",           () => SetWindowSize(1.25)),
            new("Window Size: Extra Large (150%)",  "Ctrl+4",           () => SetWindowSize(1.50)),
            new("Window Size: Huge (200%)",         "Ctrl+5",           () => SetWindowSize(2.00)),
            new("Hide/Show Data Input",              K("HideDataInput"),  () => ToggleHideDataInput()),
            new("Hide/Show Value Input",             K("HideValueInput"), () => ToggleHideValueInput()),
            new("Layout: Full",    "", () => ApplyLayoutPreset(LayoutPreset.Full)),
            new("Layout: Focused", "", () => ApplyLayoutPreset(LayoutPreset.Focused)),
            // ── Tools
            new("Keyboard Info",                    "",              () => OpenKeyboardInfoWindow()),
            new("Toggle VGA Mirror",                K("Mirror"),        () => { _mirrorState = !_mirrorState; Ctrl(DaemonCommand.VgaMirror(_mirrorState)); }),
            new("Toggle Calibration Mode",          K("Calibrate"),     () => { _cal.Mode = !_cal.Mode; if (_cal.Mode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); }),
            new("Save Screenshot…",                 "",              () => SaveScreenshot()),
            new("Toggle Keyboard Send",             "",              () => { _kbdSendEnabled = !_kbdSendEnabled; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); }),
            // ── Mode select
            new("Mode: Setlist",  K("Mode Setlist"),  () => SendMode(Mode.Setlist)),
            new("Mode: Combi",    K("Mode Combi"),    () => SendMode(Mode.Combi)),
            new("Mode: Program",  K("Mode Program"),  () => SendMode(Mode.Program)),
            new("Mode: Sequence", K("Mode Sequence"), () => SendMode(Mode.Sequence)),
            new("Mode: Sampling", K("Mode Sampling"), () => SendMode(Mode.Sampling)),
            new("Mode: Global",   K("Mode Global"),   () => SendMode(Mode.Global)),
            new("Mode: Disk",     K("Mode Disk"),     () => SendMode(Mode.Disk)),
            // ── Bank select
            new("Bank I-A",  K("Bank I-A"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'A'))),
            new("Bank I-B",  K("Bank I-B"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'B'))),
            new("Bank I-C",  K("Bank I-C"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'C'))),
            new("Bank I-D",  K("Bank I-D"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'D'))),
            new("Bank I-E",  K("Bank I-E"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'E'))),
            new("Bank I-F",  K("Bank I-F"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'F'))),
            new("Bank I-G",  K("Bank I-G"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.Internal, 'G'))),
            new("Bank U-A",  K("Bank U-A"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'A'))),
            new("Bank U-B",  K("Bank U-B"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'B'))),
            new("Bank U-C",  K("Bank U-C"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'C'))),
            new("Bank U-D",  K("Bank U-D"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'D'))),
            new("Bank U-E",  K("Bank U-E"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'E'))),
            new("Bank U-F",  K("Bank U-F"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'F'))),
            new("Bank U-G",  K("Bank U-G"),  () => Ctrl(DaemonCommand.BankButton(BankGroup.User, 'G'))),
            new("Bank U-AA", K("Bank U-AA"), () => Ctrl(DaemonCommand.DoubleUserBank('A'))),
            new("Bank U-BB", K("Bank U-BB"), () => Ctrl(DaemonCommand.DoubleUserBank('B'))),
            new("Bank U-CC", K("Bank U-CC"), () => Ctrl(DaemonCommand.DoubleUserBank('C'))),
            new("Bank U-DD", K("Bank U-DD"), () => Ctrl(DaemonCommand.DoubleUserBank('D'))),
            new("Bank U-EE", K("Bank U-EE"), () => Ctrl(DaemonCommand.DoubleUserBank('E'))),
            new("Bank U-FF", K("Bank U-FF"), () => Ctrl(DaemonCommand.DoubleUserBank('F'))),
            new("Bank U-GG", K("Bank U-GG"), () => Ctrl(DaemonCommand.DoubleUserBank('G'))),
            // ── Help
            new("Toggle Help Overlay", K("Help"), () => { _helpOpen = !_helpOpen; OverlayLayer.InvalidateVisual(); }),
            new("About",               "",        () => OpenAboutWindow()),
            new("Quit",                K("Quit"),  () => TryQuit()),
        ];
    }

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
    }

    void ResizeAndRefresh()
    {
        if (!IsLoaded) return;
        SyncMainAreaColumn();
        // Skip SetWindowSize when already maximized (non-fullscreen): the window fills
        // the screen and there is nothing to resize. SetWindowSize would force it back
        // to Normal, which is exactly the bug we are avoiding here.
        if (!_fs.Active && WindowState != WindowState.Maximized)
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
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

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
