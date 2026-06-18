using System.Collections.Concurrent;
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
    int             _frameW = 800;
    int             _frameH = 600;
    PaletteEntry[]  _basePal  = new PaletteEntry[256];
    byte[]?         _rawFrame = null;
    int[]           _lut      = new int[256];

    WriteableBitmap? _wb;
    readonly ConcurrentQueue<byte[]> _frameQ = new();

    // ── Editor state ──────────────────────────────────────────────────────────
    bool   _edOpen   = false;
    int    _edSel    = 0;
    int    _edCh     = 0;         // 0=R 1=G 2=B
    string? _edTyped = null;
    int?   _hoverIdx = null;
    Dictionary<int, PaletteEntry> _overrides = new();
    HashSet<int> _locked = new();

    List<HistEntry> _history = new();
    int  _histPos   = -1;
    PaletteEntry? _clipboard = null;

    // Panel geometry — updated each draw
    Rect   _panelRect;
    Point  _gridOrigin;
    double _sliderTop;

    // ── Display state ─────────────────────────────────────────────────────────
    bool   _aspectLock   = true;
    bool   _mirrorState  = false;
    bool   _helpOpen     = false;
    bool   _zoomOn       = false;
    double _zoomLevel    = 2.5;
    bool   _hideControls    = false;
    bool   _focusedExpanded = false;
    double _currentScale    = 1.0;
    Rect   _frameRect;           // screen rect of displayed frame

    // ── Data wheel drag / animation ──────────────────────────────────────────
    bool            _wheelDragActive    = false;
    double          _wheelDragStartY    = 0;
    int             _wheelDragSteps     = 0;
    const double    WheelPxPerStep      = 12;       // design-space px per step

    readonly DispatcherTimer _wheelAnimTimer = new();
    int                      _wheelAnimState = 0;
    int                      _wheelAnimDir   = 1;
    DateTime                 _wheelLastActivity = DateTime.MinValue;
    const int WheelAnimIntervalMs = 100;
    const int WheelAnimIdleMs     = 400;
    static readonly double[] WheelAngles = { 0.0, 10.0, -10.0 };

    // ── Value slider state ────────────────────────────────────────────────────
    bool   _vsliderDragActive = false;
    int    _vsliderValue      = 0;
    const double VSliderTravel    = 228.0;
    const double VSliderThumbHalf = 21.0;

    // ── Drag / touch state ────────────────────────────────────────────────────
    const int DragStartThresh = 8;
    const int DragMoveThresh  = 3;
    bool  _dragPending    = false;
    (int x, int y) _dragPendingPos;
    bool  _dragActive     = false;
    (int x, int y) _dragLast;

    // ── Calibration state ─────────────────────────────────────────────────────
    bool   _calMode    = false;   // C: unified calibration — drag nodes, touch pass-through, keyboard stays local
    bool   _calDirty   = false;   // mesh has changes not yet written to disk
    CalMesh _calMesh   = new();
    List<CalBiasDot> _calBiasDots = new();
    (int col, int row)? _calDraggingNode = null;
    (int col, int row)? _calHoverNode    = null;

    const double CalNodeHitRadius = 18.0;
    const double CalDotHitRadius  = 12.0;

    // ── Touch marker ──────────────────────────────────────────────────────────
    (Point pos, DateTime t)? _touchMarker = null;

    // ── Mode polling ──────────────────────────────────────────────────────────
    CancellationTokenSource? _modePollCts;
    DateTime _lastUserModeChange  = DateTime.MinValue;
    bool     _helpActive          = false;
    int      _pendingMode         = 0;          // 1-7 while awaiting detection confirmation
    DateTime _pendingModeDeadline = DateTime.MinValue;
    const double PendingModeTimeoutSec = 3.0;

    // ── Mode history (for transition detection) ───────────────────────────────
    int _currentMode = 0;   // last mode applied by SetModeButton
    int _prevMode    = 0;   // mode before the current one; survives across frames

    // ── Combi program-edit state ──────────────────────────────────────────────
    bool     _combiProgramEditActive   = false;
    bool     _combiProgramFlashState   = false;
    DateTime _combiEditIndicatorGoneAt = DateTime.MinValue; // for holdoff on indicator-absence exit
    readonly DispatcherTimer _combiProgramFlashTimer = new();
    const double CombiEditExitDelaySec = 1.5; // indicator must be absent this long before exiting

    // ── Cal undo ──────────────────────────────────────────────────────────────
    List<CalHistEntry> _calHistory = new();
    int  _calHistPos = -1;
    (int offX, int offY) _calDragStartOffset;

    // ── Help window ──────────────────────────────────────────────────────────
    HelpWindow?          _helpWin;
    KeyboardInfoWindow?  _kbdInfoWin;

    // ── Misc ──────────────────────────────────────────────────────────────────
    System.Windows.Forms.NotifyIcon? _trayIcon;

    int              _connecting  = 0;   // Interlocked guard — 1 while ConnectAsync runs
    IStreamReceiver? _receiver;
    ICtrlClient      _ctrl        = null!;
    double           _pixPerDip   = 1.0;
    bool             _shiftHeld   = false;
    bool             _isFullscreen = false;
    bool             _kbdCapture      = false;
    bool             _extendedKey     = false;   // set by LLKeyboardProc; true = numpad/extended key
    LowLevelKbProc?  _llKbProc        = null;    // field keeps delegate alive so GC won't collect it
    IntPtr           _llKbHook        = IntPtr.Zero;
    bool             _kbdSendEnabled  = true;   // false = capture active but nothing forwarded to Kronos
    HashSet<Key>     _instantKeys     = new();  // shifted overrides sent as press+release pair
    HashSet<Key>     _capsShiftedKeys = new();  // letters whose KEY 42 was injected for CapsLock mode

    // ── Key repeat ────────────────────────────────────────────────────────────
    readonly DispatcherTimer _repeatTimer = new();
    bool _repeatPhase = false;
    int  _repeatCode  = 0;
    WindowState      _savedState   = WindowState.Normal;
    WindowStyle      _savedStyle   = WindowStyle.SingleBorderWindow;
    ResizeMode       _savedResize  = ResizeMode.CanResize;
    AppSettings      _settings     = new();

    enum ConnState { Disconnected, Connecting, Connected }
    ConnState _connState     = ConnState.Disconnected;
    int       _fpsFrameCount = 0;
    DateTime  _fpsLastCheck  = DateTime.MinValue;
    double    _measuredFps   = 0;

    // ── Boot splash state ────────────────────────────────────────────────────
    BitmapSource?            _bootSplash          = null;
    bool                     _bootPhase           = false;
    bool                     _detectedModeEver    = false;
    bool                     _frameIsMostlyBlack      = false;
    bool                     _frameIsLikelyBootScreen = false;  // ≥60% black — gates splash display
    DateTime                 _bootFirstFrame      = DateTime.MinValue;
    DateTime                 _bootPhaseStart      = DateTime.MinValue;

    // Load-phase tracking — updated by BootPhaseDetector during _bootPhase
    BootPhaseDetector.Phase  _bootLoadPhase       = BootPhaseDetector.Phase.None;
    DateTime                 _preloadTimerStart   = DateTime.MinValue; // latched at boot entry
    DateTime                 _bankDataDetectedAt  = DateTime.MinValue;
    double                   _finishingFillFrac   = BootBarF_StaticEnd; // snapshotted on Finishing detect

    // Preload schedule: (wallEnd, progressEnd) pairs for each active/pause segment.
    // Active segments advance progress linearly; pause segments hold progress constant.
    // Built once at boot phase entry; null until then.
    (double WallEnd, double ProgressEnd)[]? _preloadSchedule;

    // Show overlay only after 0.5 s with no mode detected; exits the instant a mode is confirmed.
    // The buffer prevents a flash during quick reconnects/stream-mode changes where the mode
    // banner is already visible and detection fires within a frame or two.
    const double BootEntryDelaySec = 0.5;

    // Bar fill fractions (0..1, left=BootBarFx0, right=BootBarFx1) — resolution-independent
    const double BootBarF_StaticEnd  = 724.0  / 1302;  // px 864  in 1600-wide image
    const double BootBarF_PreloadEnd = 1190.0 / 1302;  // px 1330
    const double BootBarF_BankStart  = 1190.0 / 1302;  // px 1330
    const double BootBarF_BankEnd    = 1.0;             // px 1442 = right edge
    const double BootBarF_End        = 1.0;             // px 1442 = right edge

    // ── Layout preset ─────────────────────────────────────────────────────────
    LayoutPreset           _layoutPreset      = LayoutPreset.Full;
    ControlPaletteWindow?  _controlPaletteWin = null;
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

        // Log daemon-side ERR responses and surface them in the notification bubble.
        // Fires on a background thread; SetNotification handles its own dispatch.
        CtrlClient.OnCtrlError += msg =>
        {
            AppLog.Warn($"[ctrl] daemon error: {msg}");
            SetNotification(msg, isError: true);
        };

        NotifyBubble.MouseLeftButtonDown += (_, _) => OnNotifyBubbleClick();
        KbdInfoBtn.MouseLeftButtonDown   += (_, _) => OpenKeyboardInfoWindow();

        _hideControls = _settings.HideControls;
        _overrides    = Storage.LoadOverrides();
        _locked       = Storage.LoadLocks();
        (_calMesh, _calBiasDots) = Storage.LoadCal();
        if (!_calMesh.IsIdentity() || _calBiasDots.Count > 0)
            Console.WriteLine($"[cal] mesh loaded, {_calBiasDots.Count} bias dot(s)");

        Loaded      += OnLoaded;
        Closing     += OnClosing;
        Deactivated += (sender, e) =>
        {
            _kbdCapture = false;
            _instantKeys.Clear();
            StopRepeat();
            if (_capsShiftedKeys.Count > 0) { Ctrl("KEY 42 0"); _capsShiftedKeys.Clear(); }
            _calDraggingNode = null;
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

        _combiProgramFlashTimer.Interval = TimeSpan.FromMilliseconds(420);
        _combiProgramFlashTimer.Tick += (sender, e) =>
        {
            if (!_combiProgramEditActive) { _combiProgramFlashTimer.Stop(); return; }
            _combiProgramFlashState = !_combiProgramFlashState;
            BTN_Program.IsActive = _combiProgramFlashState;
        };
    }

    // Records a user-requested mode and starts the confirmation timeout.
    // The button icon changes only when SetModeButton() is called by detection; if
    // detection never confirms within PendingModeTimeoutSec, RenderTick applies the fallback.
    void SetPendingMode(int mode)
    {
        _pendingMode         = mode;
        _pendingModeDeadline = DateTime.Now.AddSeconds(PendingModeTimeoutSec);
        _lastUserModeChange  = DateTime.Now;
    }

    void SendMode(int mode)
    {
        string name = mode switch {
            1 => "SETLIST", 2 => "COMBI",    3 => "PROGRAM",
            4 => "SEQUENCE",5 => "SAMPLING", 6 => "GLOBAL",
            7 => "DISK",    _ => ""
        };
        SetPendingMode(mode);
        Ctrl($"BUTTON {name}");
    }

    void WireButtons()
    {
        // Mode buttons — send the hardware packet and record pending mode.
        // Icon only lights up once detection confirms (or timeout fallback fires).
        BTN_Setlist.Click  += (sender, e) => SendMode(1);
        BTN_Combi.Click    += (sender, e) => SendMode(2);
        BTN_Program.Click  += (sender, e) => SendMode(3);
        BTN_Sequence.Click += (sender, e) => SendMode(4);
        BTN_Sampling.Click += (sender, e) => SendMode(5);
        BTN_Global.Click   += (sender, e) => SendMode(6);
        BTN_Disk.Click     += (sender, e) => SendMode(7);

        // Toggle buttons
        BTN_Help.Click    += (sender, e) => Ctrl("BUTTON HELP");
        BTN_Compare.Click += (sender, e) => Ctrl("BUTTON COMPARE");

        // Number pad (no animation, but sends packet)
        BTN_data_dash.Click   += (sender, e) => Ctrl("BUTTON NUM_DASH");
        BTN_data0.Click       += (sender, e) => Ctrl("BUTTON NUM0");
        BTN_data_period.Click += (sender, e) => Ctrl("BUTTON NUM_DOT");
        BTN_data1.Click       += (sender, e) => Ctrl("BUTTON NUM1");
        BTN_data2.Click       += (sender, e) => Ctrl("BUTTON NUM2");
        BTN_data3.Click       += (sender, e) => Ctrl("BUTTON NUM3");
        BTN_data4.Click       += (sender, e) => Ctrl("BUTTON NUM4");
        BTN_data5.Click       += (sender, e) => Ctrl("BUTTON NUM5");
        BTN_data6.Click       += (sender, e) => Ctrl("BUTTON NUM6");
        BTN_data7.Click       += (sender, e) => Ctrl("BUTTON NUM7");
        BTN_data8.Click       += (sender, e) => Ctrl("BUTTON NUM8");
        BTN_data9.Click       += (sender, e) => Ctrl("BUTTON NUM9");

        // Exit / Enter
        BTN_Exit.Click  += (sender, e) => Ctrl("BUTTON EXIT");
        BTN_Enter.Click += (sender, e) => Ctrl("BUTTON ENTER");

        // Value Inc / Dec
        BTN_Inc.Click += (sender, e) => Ctrl("BUTTON INC");
        BTN_Dec.Click += (sender, e) => Ctrl("BUTTON DEC");

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
        Data_Wheel.LostMouseCapture += (sender, e) => _wheelDragActive = false;

        _wheelAnimTimer.Interval = TimeSpan.FromMilliseconds(WheelAnimIntervalMs);
        _wheelAnimTimer.Tick    += (sender, e) => AdvanceWheelAnim();
    }

    void OnWheelMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _wheelDragActive = true;
        _wheelDragStartY = e.GetPosition(Data_Wheel).Y;
        _wheelDragSteps  = 0;
        Data_Wheel.CaptureMouse();
        e.Handled = true;
    }

    void OnWheelMouseMove(object s, MouseEventArgs e)
    {
        if (!_wheelDragActive) return;
        double dy    = _wheelDragStartY - e.GetPosition(Data_Wheel).Y; // +ve = up = CW
        int    steps = (int)(dy / WheelPxPerStep);
        int    diff  = steps - _wheelDragSteps;

        if (diff > 0)
            for (int i = 0; i < diff;  i++) { Ctrl("WHEEL CW");  TriggerWheelAnim(1);  }
        else if (diff < 0)
            for (int i = 0; i < -diff; i++) { Ctrl("WHEEL CCW"); TriggerWheelAnim(-1); }

        if (diff != 0) _wheelDragSteps = steps;
        e.Handled = true;
    }

    void OnWheelMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _wheelDragActive = false;
        Data_Wheel.ReleaseMouseCapture();
        e.Handled = true;
    }

    void TriggerWheelAnim(int dir)
    {
        _wheelAnimDir      = dir;
        _wheelLastActivity = DateTime.Now;
        if (!_wheelAnimTimer.IsEnabled)
        {
            _wheelAnimTimer.Start();
            AdvanceWheelAnim();     // jump to next state immediately on first trigger
        }
    }

    void AdvanceWheelAnim()
    {
        if ((DateTime.Now - _wheelLastActivity).TotalMilliseconds > WheelAnimIdleMs)
        {
            _wheelAnimTimer.Stop();
            return;                 // hold current state — no snap-back
        }
        _wheelAnimState = (_wheelAnimState + _wheelAnimDir + 3) % 3;
        SetWheelAngle(WheelAngles[_wheelAnimState]);
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
            Ctrl($"VSLIDER {_vsliderValue}");
        }
    }

    void WireMenu()
    {
        MENU_Connection.SubmenuOpened += (sender, e) =>
        {
            MNU_Disconnect.IsEnabled     = _receiver != null;
            MNU_RefreshDisplay.IsEnabled = _receiver != null;
        };
        MNU_Reconnect.Click  += (sender, e) => UserInitiatedReconnect();
        MNU_RefreshDisplay.Click += (sender, e) => Ctrl("REFRESH");
        MNU_Disconnect.Click += (sender, e) =>
        {
            ResetBootState();
            _receiver?.Dispose();
            _receiver = null;
            _ctrl.Reset();
            SetConnectionStatus(ConnState.Disconnected);
            UpdateTitle("Not Connected");
        };

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
            MNU_HideControls.IsChecked = _hideControls;
            MNU_AlwaysOnTop.IsChecked  = _settings.AlwaysOnTop;
        };
        MNU_AlwaysOnTop.Click += (sender, e) =>
        {
            _settings.AlwaysOnTop = MNU_AlwaysOnTop.IsChecked;
            Topmost = _settings.AlwaysOnTop;
            Storage.SaveSettings(_settings);
        };
        MNU_AspectLock.Click   += (sender, e) => { _aspectLock = MNU_AspectLock.IsChecked; RefreshFrameRect(); };
        MNU_Zoom.Click         += (sender, e) => { _zoomOn = MNU_Zoom.IsChecked; OverlayLayer.InvalidateVisual(); };
        MNU_Fullscreen.Click   += (sender, e) => ToggleFullscreen();
        MNU_HideControls.Click += (sender, e) => ToggleHideControls();

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
            MNU_CalMode.IsChecked    = _calMode;
            MNU_DisableKbd.IsChecked = !_kbdSendEnabled;
        };
        // Palette editor is disabled — collapse the menu item so it is not accessible
        MNU_PaletteEd.Visibility = Visibility.Collapsed;
        MNU_CalMode.Click   += (sender, e) => { _calMode = MNU_CalMode.IsChecked; if (_calMode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); };

        MNU_SettingsDlg.Click += (sender, e) => OpenSettingsDialog();

        MNU_FileManager.Click    += (_, _) => OpenFileManagerWindow();

        MNU_ShowHelp.Click       += (sender, e) => OpenHelpWindow();
        MNU_CommandPalette.Click += (sender, e) => OpenCommandPalette();
        MNU_About.Click          += (sender, e) => OpenAboutWindow();

        // Layout presets
        MENU_LayoutPreset.SubmenuOpened += (sender, e) =>
        {
            MNU_PresetFull.IsChecked    = _layoutPreset == LayoutPreset.Full;
            MNU_PresetFocused.IsChecked = _layoutPreset == LayoutPreset.Focused;
            MNU_HideControls.IsEnabled  = _layoutPreset == LayoutPreset.Full;
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
        MNU_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        CTX_KeyboardInfo.Click += (sender, e) => OpenKeyboardInfoWindow();
        MNU_KbdWarp.Visibility = Visibility.Collapsed;

        // Bank Select — items built in code to avoid 21 x:Name declarations in XAML
        char[] bankLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G'];
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"I-{letter}" };
            mi.Click += (sender, e) => Ctrl($"BUTTON BANK_I{letter}");
            MENU_BankSelect.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(new Separator());
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"U-{letter}" };
            mi.Click += (sender, e) => Ctrl($"BUTTON BANK_U{letter}");
            MENU_BankSelect.Items.Add(mi);
        }
        MENU_BankSelect.Items.Add(new Separator());
        foreach (var letter in bankLetters)
        {
            var mi = new MenuItem { Header = $"U-{letter}{letter}" };
            mi.Click += (sender, e) => Ctrl($"CHORD BANK_U{letter} BANK_I{letter}");
            MENU_BankSelect.Items.Add(mi);
        }

        // Mode Select
        MNU_Mode_Setlist.Click  += (sender, e) => SendMode(1);
        MNU_Mode_Combi.Click    += (sender, e) => SendMode(2);
        MNU_Mode_Program.Click  += (sender, e) => SendMode(3);
        MNU_Mode_Sequence.Click += (sender, e) => SendMode(4);
        MNU_Mode_Sampling.Click += (sender, e) => SendMode(5);
        MNU_Mode_Global.Click   += (sender, e) => SendMode(6);
        MNU_Mode_Disk.Click     += (sender, e) => SendMode(7);

        // Calibration grid size
        MENU_CalGrid.SubmenuOpened += (sender, e) =>
        {
            MNU_CalGrid3.IsChecked = _calMesh.Cols == 3;
            MNU_CalGrid4.IsChecked = _calMesh.Cols == 4;
            MNU_CalGrid5.IsChecked = _calMesh.Cols == 5;
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
            Ctrl("BUTTON PROGRAM");
            await Task.Delay(500);
            Ctrl("CHORD 500 MIX_KNOBS RESET ENTER NUM5");
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
        CTX_Reconnect.Click      += (sender, e) => UserInitiatedReconnect();
        CTX_Disconnect.Click     += (sender, e) =>
        {
            ResetBootState();
            _receiver?.Dispose(); _receiver = null;
            _ctrl.Reset();
            SetConnectionStatus(ConnState.Disconnected);
            UpdateTitle("Not Connected");
        };
        FrameImage.ContextMenuOpening += (s, e) =>
        {
            if (_calMode) { e.Handled = true; return; }
            CTX_AspectLock.IsChecked = _aspectLock;
            CTX_Fullscreen.IsChecked = _isFullscreen;
            CTX_Disconnect.IsEnabled = _receiver != null;
            CTX_ZoomOut.IsEnabled    = _zoomOn && _zoomLevel > _settings.ZoomDefaultLevel;
            CTX_ZoomReset.IsEnabled  = _zoomOn;
        };

        // Wheel context menu
        CTX_WheelSensitivity.Click += (sender, e) => OpenSettingsDialog(SettingsTab.View);
        CTX_WheelReset.Click       += (sender, e) => { SetWheelAngle(0); _wheelAnimState = 0; };

        // Status bar context menus
        CTX_StatusReconnect.Click  += (sender, e) => UserInitiatedReconnect();
        CTX_StatusDisconnect.Click += (sender, e) =>
        {
            ResetBootState();
            _receiver?.Dispose(); _receiver = null;
            _ctrl.Reset();
            SetConnectionStatus(ConnState.Disconnected);
            UpdateTitle("Not Connected");
        };
        CTX_StatusCopyIP.Click      += (sender, e) => { if (!string.IsNullOrEmpty(_host)) Clipboard.SetText(_host); };
        CTX_KbdEnable.Click         += (sender, e) => { _kbdSendEnabled = true;  _instantKeys.Clear(); StopRepeat(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_KbdDisable.Click        += (sender, e) => { _kbdSendEnabled = false; _instantKeys.Clear(); StopRepeat(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_SetMaxFps.Click         += (sender, e) => OpenSettingsDialog(SettingsTab.Streaming);
        CTX_Mode_Setlist.Click      += (sender, e) => SendMode(1);
        CTX_Mode_Combi.Click        += (sender, e) => SendMode(2);
        CTX_Mode_Program.Click      += (sender, e) => SendMode(3);
        CTX_Mode_Sequence.Click     += (sender, e) => SendMode(4);
        CTX_Mode_Sampling.Click     += (sender, e) => SendMode(5);
        CTX_Mode_Global.Click       += (sender, e) => SendMode(6);
        CTX_Mode_Disk.Click         += (sender, e) => SendMode(7);
        CTX_OpenLogFile.Click       += (sender, e) => OnNotifyBubbleClick();
        CTX_ClearNotification.Click += (sender, e) => ClearNotification();

        MNU_HideControls.IsChecked = _hideControls;
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
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_wb));
            using var fs = File.OpenWrite(dlg.FileName);
            encoder.Save(fs);
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
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_wb));
            using var fs = File.OpenWrite(path);
            encoder.Save(fs);
            SetNotification($"Saved {System.IO.Path.GetFileName(path)}", isError: false);
            Console.WriteLine($"[screenshot] quick-saved → {path}");
        }
        catch (Exception ex) { SetNotification($"Screenshot failed: {ex.Message}", isError: true); }
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

    void OpenSettingsDialog(SettingsTab tab = SettingsTab.General)
    {
        var dlg = new SettingsWindow(_settings, m => _ = RunUserMacroAsync(m),
            showInputTester: () => new InputTesterWindow(_ctrl) { Owner = this }.Show(),
            initialTab: tab)
            { Owner = this };
        bool ok = dlg.ShowDialog() == true;

        // WPF/OS can reset the owner's WindowState from Maximized to Normal while a modal
        // dialog is open over a WindowStyle.None fullscreen window. Re-apply if needed so
        // closing settings never silently drops fullscreen — regardless of OK or Cancel.
        if (_isFullscreen && WindowState != WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
        }

        if (!ok) return;

        bool streamChanged = _settings.PullMode    != dlg.Result.PullMode  ||
                             _settings.MaxFps      != dlg.Result.MaxFps    ||
                             _settings.KronosHost  != dlg.Result.KronosHost||
                             _settings.StreamPort  != dlg.Result.StreamPort;

        _settings = dlg.Result;
        AppLog.DebugEnabled = _settings.DebugLogging;
        _host     = _settings.KronosHost;
        _port     = _settings.StreamPort;
        _ctrlPort = _settings.CtrlPort;
        _pullMode = _settings.PullMode;
        _fps      = _settings.MaxFps;
        _hideControls = _settings.HideControls;
        _ctrl = new CtrlClientAdapter(_host, _ctrlPort);
        Storage.SaveSettings(_settings);
        ApplyHideControls();
        MNU_HideControls.IsChecked = _hideControls;

        if (dlg.WasReset)
        {
            if (_receiver != null) TriggerReconnect();
            MessageBox.Show(
                "All settings have been reset to defaults.\n\nCalibration data will fully take effect on the next launch.",
                "Settings Reset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_receiver != null && streamChanged)
        {
            // Streaming parameters changed — reconnect so new mode/fps take effect now.
            TriggerReconnect();
        }
        else if (_receiver != null)
        {
            // Push VGA mirror + screensaver to daemon immediately if connected
            _mirrorState = _settings.VgaMirrorEnabled;
            Ctrl(_mirrorState ? "MIRROR_ON" : "MIRROR_OFF");
            Ctrl($"SS_TIMEOUT {_settings.ScreensaverTimeout}");
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
        if (_connState != ConnState.Connected)
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
        if (dlg.ShowDialog() == true)
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
        if (dlg.ShowDialog() != true) return false;
        _settings.FtpUsername = dlg.Username;
        _settings.FtpPassword = dlg.Password;
        if (dlg.SavePassword) Storage.SaveSettings(_settings);
        return true;
    }

    // ── Control port helper ───────────────────────────────────────────────────

    void Ctrl(string cmd)
    {
        AppLog.Debug($"[ctrl] {cmd}");
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
                _                    => "Not connected"
            };
            ConnModeText.Text = state == ConnState.Connected
                ? (_pullMode ? "Pull" : "Change")
                : "";
            if (state != ConnState.Connected) { FpsText.Text = ""; PingText.Text = ""; _fpsLastCheck = DateTime.MinValue; _fpsFrameCount = 0; }
            if (state == ConnState.Connected) StartPing(); else StopPing();
            if (state == ConnState.Connected) StartAudioCapture(); else StopAudioCapture();
            if (state != ConnState.Connected)
            {
                _rawFrame = null;
                while (_frameQ.TryDequeue(out _)) {}
                _wb = null;
                FrameImage.Source = null;
            }
            if (state == ConnState.Disconnected)
            {
                if (_combiProgramEditActive) { _combiProgramEditActive = false; _combiProgramFlashTimer.Stop(); }
                _combiEditIndicatorGoneAt = DateTime.MinValue;
                _currentMode = 0;
                _prevMode    = 0;
                ClearModeButtons();
                OverlayLayer.InvalidateVisual();
            }
            // Start boot overlay immediately on connect so it shows while waiting for first frame
            if (state == ConnState.Connected && _bootFirstFrame == DateTime.MinValue)
                _bootFirstFrame = DateTime.Now;
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
        new AboutWindow(host, _ctrlPort) { Owner = this }.ShowDialog();
    }

    void OpenKeyboardInfoWindow()
    {
        if (_kbdInfoWin != null && _kbdInfoWin.IsLoaded)
        {
            _kbdInfoWin.Activate();
            _kbdInfoWin.Focus();
            return;
        }
        _kbdInfoWin = new KeyboardInfoWindow(_host, _ctrlPort) { Owner = this };
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
            new("Refresh Display",                  "",              () => Ctrl("REFRESH")),
            new("Disconnect",                       "",              () => { _receiver?.Dispose(); _receiver = null; _ctrl.Reset(); SetConnectionStatus(ConnState.Disconnected); UpdateTitle("Not Connected"); }),
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
            new("Hide/Show Controls",               K("HideControls"),  () => ToggleHideControls()),
            new("Layout: Full",    "", () => ApplyLayoutPreset(LayoutPreset.Full)),
            new("Layout: Focused", "", () => ApplyLayoutPreset(LayoutPreset.Focused)),
            // ── Tools
            new("Keyboard Info",                    "",              () => OpenKeyboardInfoWindow()),
            new("Toggle VGA Mirror",                K("Mirror"),        () => { _mirrorState = !_mirrorState; Ctrl(_mirrorState ? "MIRROR_ON" : "MIRROR_OFF"); }),
            new("Toggle Calibration Mode",          K("Calibrate"),     () => { _calMode = !_calMode; if (_calMode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); }),
            new("Save Screenshot…",                 "",              () => SaveScreenshot()),
            new("Toggle Keyboard Send",             "",              () => { _kbdSendEnabled = !_kbdSendEnabled; _instantKeys.Clear(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); }),
            // ── Mode select
            new("Mode: Setlist",  K("Mode Setlist"),  () => SendMode(1)),
            new("Mode: Combi",    K("Mode Combi"),    () => SendMode(2)),
            new("Mode: Program",  K("Mode Program"),  () => SendMode(3)),
            new("Mode: Sequence", K("Mode Sequence"), () => SendMode(4)),
            new("Mode: Sampling", K("Mode Sampling"), () => SendMode(5)),
            new("Mode: Global",   K("Mode Global"),   () => SendMode(6)),
            new("Mode: Disk",     K("Mode Disk"),     () => SendMode(7)),
            // ── Bank select
            new("Bank I-A",  K("Bank I-A"),  () => Ctrl("BUTTON BANK_IA")),
            new("Bank I-B",  K("Bank I-B"),  () => Ctrl("BUTTON BANK_IB")),
            new("Bank I-C",  K("Bank I-C"),  () => Ctrl("BUTTON BANK_IC")),
            new("Bank I-D",  K("Bank I-D"),  () => Ctrl("BUTTON BANK_ID")),
            new("Bank I-E",  K("Bank I-E"),  () => Ctrl("BUTTON BANK_IE")),
            new("Bank I-F",  K("Bank I-F"),  () => Ctrl("BUTTON BANK_IF")),
            new("Bank I-G",  K("Bank I-G"),  () => Ctrl("BUTTON BANK_IG")),
            new("Bank U-A",  K("Bank U-A"),  () => Ctrl("BUTTON BANK_UA")),
            new("Bank U-B",  K("Bank U-B"),  () => Ctrl("BUTTON BANK_UB")),
            new("Bank U-C",  K("Bank U-C"),  () => Ctrl("BUTTON BANK_UC")),
            new("Bank U-D",  K("Bank U-D"),  () => Ctrl("BUTTON BANK_UD")),
            new("Bank U-E",  K("Bank U-E"),  () => Ctrl("BUTTON BANK_UE")),
            new("Bank U-F",  K("Bank U-F"),  () => Ctrl("BUTTON BANK_UF")),
            new("Bank U-G",  K("Bank U-G"),  () => Ctrl("BUTTON BANK_UG")),
            new("Bank U-AA", K("Bank U-AA"), () => Ctrl("CHORD BANK_UA BANK_IA")),
            new("Bank U-BB", K("Bank U-BB"), () => Ctrl("CHORD BANK_UB BANK_IB")),
            new("Bank U-CC", K("Bank U-CC"), () => Ctrl("CHORD BANK_UC BANK_IC")),
            new("Bank U-DD", K("Bank U-DD"), () => Ctrl("CHORD BANK_UD BANK_ID")),
            new("Bank U-EE", K("Bank U-EE"), () => Ctrl("CHORD BANK_UE BANK_IE")),
            new("Bank U-FF", K("Bank U-FF"), () => Ctrl("CHORD BANK_UF BANK_IF")),
            new("Bank U-GG", K("Bank U-GG"), () => Ctrl("CHORD BANK_UG BANK_IG")),
            // ── Help
            new("Toggle Help Overlay", K("Help"), () => { _helpOpen = !_helpOpen; OverlayLayer.InvalidateVisual(); }),
            new("About",               "",        () => OpenAboutWindow()),
            new("Quit",                K("Quit"),  () => TryQuit()),
        ];
    }

    // ── Layout presets ────────────────────────────────────────────────────────

    void ApplyLayoutPreset(LayoutPreset preset, bool saveSettings = true)
    {
        // Tear down any existing detached window first
        if (_controlPaletteWin != null)
        {
            _controlPaletteWin.Closed -= OnControlPaletteWindowClosed;
            _controlPaletteWin.Close();
            _controlPaletteWin = null;
        }

        _layoutPreset = preset;
        if (saveSettings)
        {
            _settings.LayoutPreset = preset;
            Storage.SaveSettings(_settings);
        }

        switch (preset)
        {
            case LayoutPreset.Full:
                _focusedExpanded          = false;
                ControlRail.Visibility    = Visibility.Collapsed;
                ControlViewbox.Visibility = Visibility.Visible;
                ControlsColumn.Width = _hideControls
                    ? new GridLength(0, GridUnitType.Star)
                    : new GridLength(800, GridUnitType.Star);
                ShowLeftPanel(!_hideControls);
                break;

            case LayoutPreset.Focused:
                _focusedExpanded          = false;
                ControlRail.Visibility    = Visibility.Visible;
                ((TextBlock)BtnRailExpand.Content).Text = "›";
                BtnRailExpand.ToolTip     = "Expand controls";
                ControlViewbox.Visibility = Visibility.Collapsed;
                ControlsColumn.Width = new GridLength(28);
                ShowLeftPanel(false);
                break;

        }

        ResizeAndRefresh();

        MNU_PresetFull.IsChecked    = preset == LayoutPreset.Full;
        MNU_PresetFocused.IsChecked = preset == LayoutPreset.Focused;
        MNU_HideControls.IsEnabled  = preset == LayoutPreset.Full;
    }

    void ToggleFocusedExpand()
    {
        if (_layoutPreset != LayoutPreset.Focused) return;
        _focusedExpanded = !_focusedExpanded;
        ControlViewbox.Visibility = _focusedExpanded ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)BtnRailExpand.Content).Text = _focusedExpanded ? "‹" : "›";
        BtnRailExpand.ToolTip = _focusedExpanded ? "Collapse controls" : "Expand controls";
        ControlsColumn.Width = _focusedExpanded
            ? new GridLength(800, GridUnitType.Star)
            : new GridLength(28);
        ShowLeftPanel(_focusedExpanded);
        ResizeAndRefresh();
    }

    void OnControlPaletteWindowClosed(object? s, EventArgs e)
    {
        _controlPaletteWin = null;
        if (_layoutPreset == LayoutPreset.Detached)
            ApplyLayoutPreset(LayoutPreset.Full);
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────

    void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle   = _savedStyle;
            WindowState   = _savedState;
            ResizeMode    = _savedResize;
            MainMenu.Visibility = Visibility.Visible;
            _isFullscreen = false;
        }
        else
        {
            _savedState   = WindowState;
            _savedStyle   = WindowStyle;
            _savedResize  = ResizeMode;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = ResizeMode.NoResize;
            // Must pass through Normal so WPF recalculates maximized bounds for the
            // borderless style; skipping this leaves the old chrome-inclusive bounds
            // in place and the window overflows the screen by ~8 px on each edge.
            WindowState   = WindowState.Normal;
            WindowState   = WindowState.Maximized;
            MainMenu.Visibility = Visibility.Collapsed;
            _isFullscreen = true;
        }
        // SizeChanged fires for the WindowState change but not for the menu
        // visibility change, so explicitly refresh after layout settles.
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    void SetWindowSize(double scale)
    {
        if (!IsLoaded) return;
        if (_isFullscreen) return;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        _currentScale  = scale;
        var dp         = (FrameworkElement)Content;
        double chromeW = Width  - dp.ActualWidth;
        double chromeH = Height - dp.ActualHeight;
        double menuH   = dp.ActualHeight - RootGrid.ActualHeight;
        double targetW = _layoutPreset switch
        {
            LayoutPreset.Focused  => _focusedExpanded ? 1882.0 : 828.0,
            LayoutPreset.Detached => 800.0,
            _                     => _hideControls ? 800.0 : 1882.0
        };
        Width  = targetW * scale + chromeW;
        Height = 600.0   * scale + menuH + chromeH;
    }

    void ResizeAndRefresh()
    {
        if (!IsLoaded) return;
        SetWindowSize(_currentScale);
        // In fullscreen, SetWindowSize is a no-op (window already maximized),
        // so SizeChanged never fires. Always defer a layout refresh explicitly.
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
    }

    void ShowLeftPanel(bool show)
    {
        if (show)
        {
            LeftPanelColumn.Width    = new GridLength(282, GridUnitType.Star);
            LeftPanelColumn.MaxWidth = double.PositiveInfinity;
        }
        else
        {
            LeftPanelColumn.Width    = new GridLength(0);
            LeftPanelColumn.MaxWidth = 0;
        }
        LeftPanelViewbox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyHideControls()
    {
        if (_layoutPreset == LayoutPreset.Full)
        {
            ControlsColumn.Width = _hideControls
                ? new GridLength(0, GridUnitType.Star)
                : new GridLength(800, GridUnitType.Star);
            ShowLeftPanel(!_hideControls);
        }
        ResizeAndRefresh();
    }

    void ToggleHideControls()
    {
        _hideControls = !_hideControls;
        _settings.HideControls = _hideControls;
        Storage.SaveSettings(_settings);
        ApplyHideControls();
        MNU_HideControls.IsChecked = _hideControls;
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
        menu.Items.Add("Disconnect", null, (_, _) =>
        {
            _receiver?.Dispose(); _receiver = null;
            _ctrl.Reset();
            SetConnectionStatus(ConnState.Disconnected);
            UpdateTitle("Not Connected");
        });
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
            WindowState = _savedState == WindowState.Minimized ? WindowState.Normal : _savedState;
        Activate();
    }
}
