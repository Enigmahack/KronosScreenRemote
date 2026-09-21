using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Automation;
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
    int[]           _lut      = new int[256];    // Fmt.Index8: palette index -> Bgr32
    int[]           _lut16    = new int[65536];  // Fmt.Rgb565Le: raw LE word -> Bgr32

    // Native pixel format of the current/last session (StreamReceiver.Fmt/Bpp, set in
    // OnSessionConnected). Defaults to the Kronos INDEX8 assumption before any connection.
    StreamPixelFormat _fmt = StreamPixelFormat.Index8;
    int             _bpp = 8;

    WriteableBitmap? _wb;

    // Persisted per-index color overrides, baked into the display LUT (RebuildLut) and
    // applied by the zoom loupe. Loaded from disk on startup.
    Dictionary<int, PaletteEntry> _overrides = new();
    static readonly Dictionary<int, PaletteEntry> _noOverrides = new();

    // Retired (3.0.2). The palette editor and _overrides existed solely to let a user
    // hand-correct Nautilus colours under the old v2/3.0.1 stream, which carried the Nautilus
    // framebuffer through the same 8bpp-index+palette path as Kronos - a format that cannot
    // represent Nautilus's true RGB565 pixels, hence "wrong colours" (docs/api.md §15, 3.0.2:
    // "Nautilus colours are correct... There is no palette on Nautilus and no client-side
    // palette fix-up is needed or possible"). Protocol v3 streams Nautilus's native RGB565
    // pixels directly (see MainWindow.Streaming.ApplyLut's Fmt.Rgb565Le path) and never
    // reports a palette for it (StreamReceiver.Palette is empty when Fmt != Index8), so the
    // per-index override dictionary has nothing left to correct on either device family.
    // Always returns the empty dictionary; _overrides/_noOverrides stay in place only because
    // Storage still loads/saves a previously-written overrides file.
    Dictionary<int, PaletteEntry> EffectiveOverrides => _noOverrides;

    // Applies every UI difference driven by _deviceFamily.
    //
    // The palette editor is hidden for every family now - see EffectiveOverrides above for why
    // it no longer has a valid use on Nautilus (the one family it was ever shown for).
    //
    // This is also the intended seam for a future Nautilus-specific front-panel skin: both
    // families currently render through the same MainWindow.xaml (laid out to match the
    // Kronos panel). When a Nautilus skin exists, swapping which layout/UserControl the
    // control column shows belongs here too, keyed off the same _deviceFamily. The button
    // label/layout differences to eventually reflect are tracked in
    // Documentation/nautilus_button_mapping.md (unverified - needs hardware testing).
    void ApplyDeviceFamilyUi()
    {
        MNU_PaletteEd.Visibility = Visibility.Collapsed;
        if (_edOpen)
        {
            _edOpen = false;
            OverlayLayer.InvalidateVisual();
        }

        // The status-bar Inc/Dec stand-in is retired - Nautilus's own Data Inc/Dec buttons now
        // live on NautilusRightPanel (wired to HELP/COMPARE, same as Kronos's thin toggle
        // buttons - see WireButtons). _kronosLeftPanel.BTN_Inc/BTN_Dec/ValueSliderCanvas keep
        // working as-is for Kronos.

        // The Kronos left value-slider skin (KronosLeftSide2EMPTY.png, _kronosLeftPanel.BTN_Inc/_kronosLeftPanel.BTN_Dec,
        // _kronosLeftPanel.ValueSliderCanvas, in KronosLeftPanel) isn't used on Nautilus at all - grill-me
        // decision 2026-09-21. LeftPanelHost.Content is null there (no Nautilus equivalent
        // exists) and the column collapses to 0 width so the live screen area (column 1) gets
        // the freed space, same as if the column were never in the grid.
        bool isNautilus = _deviceFamily == DeviceFamily.Nautilus;
        LeftPanelColumn.Width = isNautilus ? new GridLength(0) : new GridLength(282, GridUnitType.Star);
        LeftPanelHost.Content = isNautilus ? null : _kronosLeftPanel;

        // Right-side data panel: Nautilus gets its own background (NautilusRightPanel,
        // NautilusFront1.jpg) in place of the Kronos data-entry surface (KronosRightPanel) -
        // stage 1 of the Nautilus front-panel skin, no buttons yet (see NautilusRightPanel.xaml's
        // header comment). Both UserControl instances are built once in the ctor and kept alive
        // regardless of which is attached here, so re-entering a family costs nothing beyond the
        // ContentControl reparent (see the ctor's own comment on why they're built eagerly).
        RightPanelHost.Content = isNautilus ? _nautilusRightPanel : (object)_kronosRightPanel;

        // Drives Record()'s SEQ_REC vs MS2 choice - see SeqTransportViewModel's own comment.
        _seqTransport.DeviceFamily = _deviceFamily;

        // Bank Select: still visibly disabled on Nautilus - see BuildCommandRegistry's matching
        // guard (which stops the underlying commands; this is what makes that visible rather
        // than a silent no-op). The status bar's right-click Mode menu (CTX_Mode_*) has no
        // single top-level item to disable, so its 7 entries are disabled individually; it stays
        // Kronos-only for now (not given the Nautilus item-swap MENU_ModeSelect gets below - a
        // smaller, secondary surface, revisit if it turns out to matter).
        bool kronosOnly = _deviceFamily != DeviceFamily.Nautilus;
        MENU_BankSelect.IsEnabled = kronosOnly;
        CTX_Mode_Setlist.IsEnabled  = kronosOnly;
        CTX_Mode_Combi.IsEnabled    = kronosOnly;
        CTX_Mode_Program.IsEnabled  = kronosOnly;
        CTX_Mode_Sequence.IsEnabled = kronosOnly;
        CTX_Mode_Sampling.IsEnabled = kronosOnly;
        CTX_Mode_Global.IsEnabled   = kronosOnly;
        CTX_Mode_Disk.IsEnabled     = kronosOnly;

        // Mode Select: dynamic per family (your direction, 2026-09-21) - always enabled, but
        // shows a different item set. Kronos's 7 named modes don't exist as such on Nautilus;
        // MNU_NautilusMode_* mirrors the front panel's own Mode/Page/A-F instead.
        var kronosVis   = kronosOnly ? Visibility.Visible : Visibility.Collapsed;
        var nautilusVis = kronosOnly ? Visibility.Collapsed : Visibility.Visible;
        MNU_Mode_Setlist.Visibility  = kronosVis;
        MNU_Mode_Combi.Visibility    = kronosVis;
        MNU_Mode_Program.Visibility  = kronosVis;
        MNU_Mode_Sequence.Visibility = kronosVis;
        MNU_Mode_Sampling.Visibility = kronosVis;
        MNU_Mode_Global.Visibility   = kronosVis;
        MNU_Mode_Disk.Visibility     = kronosVis;
        MNU_NautilusMode_Mode.Visibility = nautilusVis;
        MNU_NautilusMode_Page.Visibility = nautilusVis;
        MNU_NautilusMode_A.Visibility    = nautilusVis;
        MNU_NautilusMode_B.Visibility    = nautilusVis;
        MNU_NautilusMode_C.Visibility    = nautilusVis;
        MNU_NautilusMode_D.Visibility    = nautilusVis;
        MNU_NautilusMode_E.Visibility    = nautilusVis;
        MNU_NautilusMode_F.Visibility    = nautilusVis;

        // Footer Fast-Forward/Rewind: faded + non-clickable on Nautilus, kept in place rather
        // than collapsed (your 2026-09-22 direction). SeqTransportBtn's ControlTemplate has no
        // disabled-state trigger of its own (the icon is a plain Path, not standard WPF chrome
        // that dims itself), so Opacity does the actual fading; IsEnabled does the non-clickable
        // part. Unconditional for Nautilus - not just gated by Sequence mode like the rest of
        // the transport row (IsTransportEnabled).
        BTN_SeqFf.IsEnabled  = BTN_SeqRew.IsEnabled  = kronosOnly;
        BTN_SeqFf.Opacity    = BTN_SeqRew.Opacity    = kronosOnly ? 1.0 : 0.35;

        // Play/Record lighting removed on Nautilus (your 2026-09-22 direction) - IsPlaying/
        // IsRecording are only ever the client's own optimistic guess (no daemon confirmation
        // exists for them on Nautilus, same reasoning as Mode/Page above), so the checked/lit
        // visual is actively misleading there. Still fully clickable either way - only the
        // Style (and so the IsChecked-triggered background) changes; the underlying
        // IsChecked="{Binding IsPlaying/IsRecording}" bindings and their bookkeeping are
        // untouched (SeqTransportViewModel still needs IsPlaying to auto-clear IsRecording, etc.).
        // FindResource must be called ON a descendant of AppStatusBar (these styles live in
        // AppStatusBar.Resources, not MainWindow's own) - FindResource only searches UPWARD
        // through logical-tree ancestors, so calling it on `this` (the Window, an ANCESTOR of
        // AppStatusBar) can never see them and throws ResourceReferenceKeyNotFoundException.
        BTN_SeqStart.Style = (Style)(kronosOnly ? BTN_SeqStart.FindResource("SeqTransportToggleBtn") : BTN_SeqStart.FindResource("SeqTransportToggleBtnNoLight"));
        BTN_SeqRec.Style   = (Style)(kronosOnly ? BTN_SeqRec.FindResource("SeqRecordToggleBtn")       : BTN_SeqRec.FindResource("SeqTransportToggleBtnNoLight"));
    }

    // Built once in the ctor (before WireButtons() wires up their children - see the ctor's own
    // comment) and attached/detached via LeftPanelHost.Content/RightPanelHost.Content in
    // ApplyDeviceFamilyUi. Never null after the ctor runs - non-nullable so every reference below
    // (moved here from bare field access when this content lived directly in this window's XAML)
    // reads exactly as it did before the split, just qualified through these.
    KronosLeftPanel    _kronosLeftPanel    = null!;
    KronosRightPanel   _kronosRightPanel   = null!;
    NautilusRightPanel _nautilusRightPanel = null!;

    // Device family/model reported by the daemon's MODEL command, queried once per
    // connection (ScreenSession.ConnectAsync). Null until connected, or if the query
    // failed/timed out.
    DeviceModelInfo? _deviceModel = null;

    // Parsed from _deviceModel.Family. Unknown (never connected, or query failed) is
    // treated the same as Kronos everywhere this gates behavior - see DeviceFamily.cs.
    // The Kronos palette is fixed/hardware-correct and must never be touched; only a
    // confirmed Nautilus gets the palette editor and palette overrides applied at all.
    DeviceFamily _deviceFamily = DeviceFamily.Unknown;

    // ── Palette editor state ─────────────────────────────────────────────────
    bool   _edOpen   = false;
    int    _edSel    = 0;
    int    _edCh     = 0;         // 0=R 1=G 2=B
    string? _edTyped = null;
    int?   _hoverIdx = null;
    HashSet<int> _locked = new();

    List<HistEntry> _history = new();
    int  _histPos   = -1;
    PaletteEntry? _clipboard = null;

    // True once _overrides has changed since the last successful Storage.SaveOverrides
    // (Key.S) - drives the panel's [SAVED]/[UNSAVED] tag, since pressing S previously
    // gave zero visible feedback either way (Console.WriteLine alone in a windowed app).
    bool _paletteDirty = false;

    // ── Palette overlay (reference-image color assignment) ──────────────────────
    // See Views/MainWindow.PaletteOverlay.cs. A pasted clipboard image, dragged/resized over
    // the live frame so its pixels can be compared against the frame's raw palette indices
    // and auto-assigned - the interactive version of nautilus_palette_selector_brief.md's
    // manual before/after-screenshot method.
    BitmapSource? _ovlBitmapSrc;
    byte[]?       _ovlPixels;         // Bgra32, stride = _ovlStride
    int           _ovlPixelW, _ovlPixelH, _ovlStride;
    Rect          _ovlRect;           // current on-screen position/size, RootGrid space
    OverlayDragKind? _ovlActiveDrag;   // null = idle - see OverlayDragKind/OverlayHitTest
    Point         _ovlDragStart;
    Rect          _ovlDragStartRect;

    // Panel geometry — updated each draw
    Rect   _panelRect;
    Point  _gridOrigin;
    double _sliderTop;

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

    // Opts out of ThemedWindow's generic placement memory: this window already has its own
    // WindowLeft/Top/Width/Height/Maximized settings, restored in MainWindow.Input.cs alongside
    // the minimum-width pin, and its geometry is further driven by the tray and fullscreen paths
    // (MainWindow.xaml.cs's _fs save/restore). Two mechanisms writing the same window's position
    // would fight over it.
    protected override bool RemembersPlacement => false;

    public MainWindow()
    {
        InitializeComponent();

        // Constructed eagerly (both families, regardless of which is ever detected) so every
        // existing WireButtons()/keybind/etc. reference below has a non-null target to wire up
        // to, exactly as when this content lived directly in this window's own XAML - only
        // WHICH one is actually attached to LeftPanelHost/RightPanelHost changes, in
        // ApplyDeviceFamilyUi. See KronosLeftPanel.xaml's header comment for why this split
        // exists. NautilusRightPanel has no wiring yet (no buttons placed on it yet), so it
        // doesn't need to be built before WireButtons() - built here anyway for symmetry/because
        // it's cheap (one Image).
        _kronosLeftPanel    = new KronosLeftPanel();
        _kronosRightPanel   = new KronosRightPanel();
        _nautilusRightPanel = new NautilusRightPanel();

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

        // Kick off the Keyboard Library's one-time referrer-catalog build (LocalLibraryCache.
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
        LibrarianStatusBtn.MouseLeftButtonDown    += (_, _) => OpenLibrarianShellWindow();
        FileTransferStatusBtn.MouseLeftButtonDown += (_, _) => OpenFileManagerWindow();
        SampleEditorStatusBtn.MouseLeftButtonDown += (_, _) => OpenSampleEditorWindow();
        MidiMonitorStatusBtn.MouseLeftButtonDown  += (_, _) => OpenSysExToolWindow();

        _hideDataInput  = _settings.HideDataInput;
        _scrollDirection = _settings.ReverseScrolling;
        _hideValueInput = _settings.HideValueInput;
        _overrides    = Storage.LoadOverrides();
        _locked       = Storage.LoadLocks();
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
            if ((ReferenceEquals(captured, _kronosRightPanel.Data_Wheel) || ReferenceEquals(captured, _nautilusRightPanel.Data_Wheel))
                && !_wheel.DragActive) Mouse.Capture(null);
            if (ReferenceEquals(captured, _kronosLeftPanel.ValueSliderCanvas) && !_vsliderDragActive) Mouse.Capture(null);
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
            _kronosRightPanel.BTN_Program.IsActive = _editCtx.FlashState;
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
        WireCommand(_kronosRightPanel.BTN_Setlist,  "Mode Setlist");
        WireCommand(_kronosRightPanel.BTN_Combi,    "Mode Combi");
        WireCommand(_kronosRightPanel.BTN_Program,  "Mode Program");
        WireCommand(_kronosRightPanel.BTN_Sequence, "Mode Sequence");
        WireCommand(_kronosRightPanel.BTN_Sampling, "Mode Sampling");
        WireCommand(_kronosRightPanel.BTN_Global,   "Mode Global");
        WireCommand(_kronosRightPanel.BTN_Disk,     "Mode Disk");

        _kronosRightPanel.BTN_Help.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Help));
        _kronosRightPanel.BTN_Compare.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Compare));

        _kronosRightPanel.BTN_data_dash.Click   += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.NumDash));
        _kronosRightPanel.BTN_data0.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(0));
        _kronosRightPanel.BTN_data_period.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.NumDot));
        _kronosRightPanel.BTN_data1.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(1));
        _kronosRightPanel.BTN_data2.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(2));
        _kronosRightPanel.BTN_data3.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(3));
        _kronosRightPanel.BTN_data4.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(4));
        _kronosRightPanel.BTN_data5.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(5));
        _kronosRightPanel.BTN_data6.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(6));
        _kronosRightPanel.BTN_data7.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(7));
        _kronosRightPanel.BTN_data8.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(8));
        _kronosRightPanel.BTN_data9.Click       += (sender, e) => Ctrl(DaemonCommand.NumberButton(9));

        _kronosRightPanel.BTN_Exit.Click  += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Exit));
        _kronosRightPanel.BTN_Enter.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Enter));

        _kronosLeftPanel.BTN_Inc.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Inc));
        _kronosLeftPanel.BTN_Dec.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Dec));

        // Nautilus front panel (NautilusRightPanel) - your button-code table, 2026-09-21.
        // Mode/Page share the existing "Mode Combi"/"Mode Program" registry commands, so they
        // get the same SendMode/pending-mode/boot-gate path as Kronos's own mode buttons; the
        // lighting side (which button reflects the confirmed mode) is ButtonForMode's Nautilus
        // branch below.
        WireCommand(_nautilusRightPanel.BTN_Mode, "Mode Combi");
        WireCommand(_nautilusRightPanel.BTN_Page, "Mode Program");

        // A-F: momentary quick-access shortcuts, sent directly rather than through SendMode -
        // these are HoldLit (lit only while held) and don't reflect/track a confirmed mode, so
        // they skip SetPendingMode's bookkeeping entirely (see NautilusRightPanel.xaml comment).
        // Wire tokens are the fixed NKS4 scan codes for these QA positions (unchanged since
        // 2026-09-21); the tooltips describe what your unit's QA slots are actually programmed
        // to do, which can differ from the token name - see NautilusRightPanel.xaml's comment.
        _nautilusRightPanel.BTN_A.Click += (sender, e) => Ctrl(DaemonCommand.Button(Mode.Sequence));
        _nautilusRightPanel.BTN_B.Click += (sender, e) => Ctrl(DaemonCommand.Button(Mode.Sampling));
        _nautilusRightPanel.BTN_C.Click += (sender, e) => Ctrl(DaemonCommand.Button(Mode.Global));
        _nautilusRightPanel.BTN_D.Click += (sender, e) => Ctrl(DaemonCommand.Button(Mode.Disk));
        _nautilusRightPanel.BTN_E.Click += (sender, e) => Ctrl(DaemonCommand.Button(Mode.Setlist));
        // F = the new Save/Write shortcut (your QA slot F) - same wire token as the status bar's
        // Save button on Nautilus (SeqTransportViewModel.Save).
        _nautilusRightPanel.BTN_F.Click += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Exit));

        // Exit/Enter, Data Inc/Dec - no lit version, direct sends (NautilusRightPanel.xaml
        // comment). Exit=NUM1, Enter=KARMA_ONOFF (your 2026-09-22 correction - these are NOT
        // Octave -/+, despite reusing Kronos's Exit/Enter button graphics as placeholder art).
        _nautilusRightPanel.BTN_Exit.Click  += (sender, e) => Ctrl(DaemonCommand.NumberButton(1));
        _nautilusRightPanel.BTN_Enter.Click += (sender, e) => Ctrl(DaemonCommand.Button("KARMA_ONOFF"));
        _nautilusRightPanel.BTN_DataInc.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Help));
        _nautilusRightPanel.BTN_DataDec.Click    += (sender, e) => Ctrl(DaemonCommand.Button(PanelButton.Compare));

        // Sequencer transport - daemon maps each to a front-panel SEQUENCER key press.
        // Record/Start are handled by SeqTransportBarItem's DataContext (SeqTransportViewModel)
        // via Command/IsChecked bindings in XAML instead of code-behind - see _seqTransport.
        WireCommand(BTN_SeqLocate, "Seq Locate");
        WireCommand(BTN_SeqRew,    "Seq Rewind");
        WireCommand(BTN_SeqFf,     "Seq Forward");
        WireCommand(BTN_SeqPause,  "Seq Pause");
        WireCommand(BTN_TapTempo,  "Tap Tempo");   // global (not seq-mode gated) - see command registry

        foreach (var btn in new KronosButton[] { _kronosRightPanel.BTN_Setlist, _kronosRightPanel.BTN_Combi, _kronosRightPanel.BTN_Program, _kronosRightPanel.BTN_Sequence,
                                                  _kronosRightPanel.BTN_Sampling, _kronosRightPanel.BTN_Global, _kronosRightPanel.BTN_Disk, _kronosRightPanel.BTN_Help, _kronosRightPanel.BTN_Compare })
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
        0 => _kronosRightPanel.BTN_data0, 1 => _kronosRightPanel.BTN_data1, 2 => _kronosRightPanel.BTN_data2, 3 => _kronosRightPanel.BTN_data3,
        4 => _kronosRightPanel.BTN_data4, 5 => _kronosRightPanel.BTN_data5, 6 => _kronosRightPanel.BTN_data6, 7 => _kronosRightPanel.BTN_data7,
        8 => _kronosRightPanel.BTN_data8, 9 => _kronosRightPanel.BTN_data9, _ => null
    };

    // Kronos's and Nautilus's Data_Wheel (KronosRightPanel/NautilusRightPanel) share these exact
    // handlers - only one panel is ever attached to RightPanelHost at a time, so wiring both
    // costs nothing and means neither family needs special-casing here or on a family switch.
    // The handlers below resolve which wheel actually fired via the event's own sender rather
    // than _deviceFamily, so they're correct even in the (currently impossible, but cheap to be
    // robust against) case of a mid-drag family change.
    void InitWheelDrag()
    {
        foreach (var wheel in new[] { _kronosRightPanel.Data_Wheel, _nautilusRightPanel.Data_Wheel })
        {
            wheel.MouseDown        += OnWheelMouseDown;
            wheel.MouseMove        += OnWheelMouseMove;
            wheel.MouseUp          += OnWheelMouseUp;
            wheel.LostMouseCapture += (sender, e) => _wheel.DragActive = false;
            wheel.PreviewKeyDown   += OnWheelPreviewKeyDown;
        }

        _wheel.AnimTimer.Interval = TimeSpan.FromMilliseconds(WheelState.AnimIntervalMs);
        _wheel.AnimTimer.Tick    += (sender, e) => AdvanceWheelAnim();
    }

    // The currently-attached family's wheel/rotation - for call sites with no event sender to
    // read the concrete Image from (EndWheelDrag, SetWheelAngle).
    Image ActiveDataWheel => _deviceFamily == DeviceFamily.Nautilus ? _nautilusRightPanel.Data_Wheel : _kronosRightPanel.Data_Wheel;
    RotateTransform ActiveWheelRotate => _deviceFamily == DeviceFamily.Nautilus ? _nautilusRightPanel.WheelRotate : _kronosRightPanel.WheelRotate;

    // Keyboard counterpart to the mouse-drag path above - only reachable at all once the active
    // Data_Wheel's Focusable is armed (remote typing off; see UpdateKbdStatus). Space is
    // explicitly swallowed rather than left unhandled: the wheel has no "press" action, and
    // an unhandled Space would otherwise bubble up to whatever routed command treats it as one.
    void OnWheelPreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Up)        { Ctrl(DaemonCommand.Wheel(true));  TriggerWheelAnim(1);  e.Handled = true; }
        else if (e.Key == Key.Down) { Ctrl(DaemonCommand.Wheel(false)); TriggerWheelAnim(-1); e.Handled = true; }
        else if (e.Key == Key.Space) e.Handled = true;
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
        if (ActiveDataWheel.IsMouseCaptured) ActiveDataWheel.ReleaseMouseCapture();
    }

    void OnWheelMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { EndWheelDrag(); return; }
        var wheel = (Image)s;
        _wheel.DragActive = true;
        _wheel.DragStartY = e.GetPosition(wheel).Y;
        _wheel.DragSteps  = 0;
        wheel.CaptureMouse();
        e.Handled = true;
    }

    void OnWheelMouseMove(object s, MouseEventArgs e)
    {
        if (!_wheel.DragActive) return;
        // Primary recovery point: the first mouse move after a missed MouseUp ends the drag
        // instead of spinning the wheel for every pixel of ordinary cursor movement.
        if (e.LeftButton != MouseButtonState.Pressed) { EndWheelDrag(); return; }
        double dy    = _wheel.DragStartY - e.GetPosition((Image)s).Y; // +ve = up = CW
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
        ActiveWheelRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ActiveWheelRotate.Angle = angle;
    }

    void InitValueSlider()
    {
        _kronosLeftPanel.ValueSliderCanvas.MouseDown        += OnVSliderMouseDown;
        _kronosLeftPanel.ValueSliderCanvas.MouseMove        += OnVSliderMouseMove;
        _kronosLeftPanel.ValueSliderCanvas.MouseUp          += OnVSliderMouseUp;
        _kronosLeftPanel.ValueSliderCanvas.LostMouseCapture += (_, _) => _vsliderDragActive = false;
        _kronosLeftPanel.ValueSliderCanvas.PreviewKeyDown   += OnVSliderPreviewKeyDown;
        AutomationProperties.SetItemStatus(_kronosLeftPanel.ValueSliderCanvas, _vsliderValue.ToString());
    }

    // Keyboard counterpart to the mouse-drag path - only reachable once _kronosLeftPanel.ValueSliderCanvas.
    // Focusable is armed (remote typing off; see UpdateKbdStatus). Space is explicitly
    // swallowed, same reasoning as OnWheelPreviewKeyDown.
    void OnVSliderPreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Up)        { StepVSlider(1);  e.Handled = true; }
        else if (e.Key == Key.Down) { StepVSlider(-1); e.Handled = true; }
        else if (e.Key == Key.Space) e.Handled = true;
    }

    void StepVSlider(int delta)
    {
        int newVal = Math.Clamp(_vsliderValue + delta, 0, 127);
        if (newVal == _vsliderValue) return;
        SetVSliderValue(newVal);
        Ctrl(DaemonCommand.ValueSlider(_vsliderValue));
    }

    // Same capture-plus-Handled hazard as the data wheel - see EndWheelDrag.
    void EndVSliderDrag()
    {
        _vsliderDragActive = false;
        if (_kronosLeftPanel.ValueSliderCanvas.IsMouseCaptured) _kronosLeftPanel.ValueSliderCanvas.ReleaseMouseCapture();
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
        _kronosLeftPanel.ValueSliderCanvas.CaptureMouse();
        UpdateVSliderFromMouse(e.GetPosition(_kronosLeftPanel.ValueSliderCanvas).Y);
        e.Handled = true;
    }

    void OnVSliderMouseMove(object s, MouseEventArgs e)
    {
        if (!_vsliderDragActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndVSliderDrag(); return; }
        UpdateVSliderFromMouse(e.GetPosition(_kronosLeftPanel.ValueSliderCanvas).Y);
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
        System.Windows.Controls.Canvas.SetTop(_kronosLeftPanel.ValueSliderThumb, thumbTop);
        int newVal = (int)Math.Round(127.0 * (VSliderTravel - thumbTop) / VSliderTravel);
        if (newVal != _vsliderValue)
        {
            _vsliderValue = newVal;
            Ctrl(DaemonCommand.ValueSlider(_vsliderValue));
            AutomationProperties.SetItemStatus(_kronosLeftPanel.ValueSliderCanvas, _vsliderValue.ToString());
        }
    }

    // Move the thumb to a 0-127 value WITHOUT sending VSLIDER back to the Kronos.
    // Inverse of the mouse mapping above.
    void SetVSliderValue(int val)
    {
        val = Math.Clamp(val, 0, 127);
        double thumbTop = VSliderTravel * (127 - val) / 127.0;
        System.Windows.Controls.Canvas.SetTop(_kronosLeftPanel.ValueSliderThumb, thumbTop);
        _vsliderValue = val;
        AutomationProperties.SetItemStatus(_kronosLeftPanel.ValueSliderCanvas, val.ToString());
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
            MNU_PaletteEd.IsChecked  = _edOpen;
        };
        MNU_CalMode.Click   += (sender, e) => { _cal.Mode = MNU_CalMode.IsChecked; if (_cal.Mode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); };
        // Retired (3.0.2) - menu item is always hidden (ApplyDeviceFamilyUi); see EffectiveOverrides
        // for why. Handler kept only so an already-open command-palette invocation (below) doesn't
        // need a second code path.
        MNU_PaletteEd.Click += (sender, e) =>
        {
            _edOpen = MNU_PaletteEd.IsChecked;
            OverlayLayer.InvalidateVisual();
        };
        ApplyDeviceFamilyUi();   // hides the palette editor menu item unconditionally

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
        MNU_InputTester.Click    += (sender, e) => new InputTesterWindow(_ctrl).OwnedBy(this).Show();
        MNU_ButtonInjector.Click += (sender, e) => new ButtonInjectorWindow(_ctrl).OwnedBy(this).Show();
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

        // Nautilus item set - same underlying commands as the front panel's own Mode/Page/A-F
        // buttons (WireButtons); Mode/Page reuse the Kronos mode commands directly since the
        // wire tokens are identical, only the label/position differs (see BuildCommandRegistry's
        // Bank-only disable guard comment).
        WireCommand(MNU_NautilusMode_Mode, "Mode Combi");
        WireCommand(MNU_NautilusMode_Page, "Mode Program");
        WireCommand(MNU_NautilusMode_A,    "Mode Sequence");
        WireCommand(MNU_NautilusMode_B,    "Mode Sampling");
        WireCommand(MNU_NautilusMode_C,    "Mode Global");
        WireCommand(MNU_NautilusMode_D,    "Mode Disk");
        WireCommand(MNU_NautilusMode_E,    "Mode Setlist");
        WireCommand(MNU_NautilusMode_F,    "NautilusModeF");

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

        _kronosRightPanel.CTX_WheelSensitivity.Click   += (sender, e) => OpenSettingsDialog(SettingsTab.View);
        _kronosRightPanel.CTX_WheelReset.Click         += (sender, e) => { SetWheelAngle(0); _wheel.AnimState = 0; };
        _nautilusRightPanel.CTX_WheelSensitivity.Click += (sender, e) => OpenSettingsDialog(SettingsTab.View);
        _nautilusRightPanel.CTX_WheelReset.Click       += (sender, e) => { SetWheelAngle(0); _wheel.AnimState = 0; };

        CTX_StatusReconnect.Click  += (sender, e) => UserInitiatedReconnect();
        CTX_StatusDisconnect.Click += (sender, e) => Disconnect();
        CTX_StatusCopyIP.Click      += (sender, e) => { if (!string.IsNullOrEmpty(_host)) Clipboard.SetText(_host); };
        CTX_KbdEnable.Click         += (sender, e) => { _kbdSendEnabled = true;  _instantKeys.Clear(); StopRepeat(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_KbdDisable.Click        += (sender, e) => { _kbdSendEnabled = false; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); };
        CTX_SetMaxFps.Click         += (sender, e) => OpenSettingsDialog(SettingsTab.Streaming);

        CTX_PerfPullMenu.Opened    += (_, _) => UpdatePullMenuState();
        CTX_PullThisProgram.Click += async (_, _) => await RunPullAsync(PullScope.Program);
        CTX_PullThisBank.Click    += async (_, _) => await RunPullAsync(PullScope.Bank);
        CTX_PullThisMode.Click    += async (_, _) => await RunPullAsync(PullScope.Mode);
        CTX_PullAll.Click         += async (_, _) => await RunPullAsync(PullScope.All);

        WireCommand(CTX_Mode_Setlist,  "Mode Setlist");
        WireCommand(CTX_Mode_Combi,    "Mode Combi");
        WireCommand(CTX_Mode_Program,  "Mode Program");
        WireCommand(CTX_Mode_Sequence, "Mode Sequence");
        WireCommand(CTX_Mode_Sampling, "Mode Sampling");
        WireCommand(CTX_Mode_Global,   "Mode Global");
        WireCommand(CTX_Mode_Disk,     "Mode Disk");
        CTX_OpenLogFile.Click       += (sender, e) => OnNotifyBubbleClick();
        CTX_ClearLogs.Click         += (sender, e) => AppLog.Clear();
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

        // An already-open Librarian holds the AppSettings instance we are about to replace - see
        // LibrarianShellViewModel.ApplySettings for the three ways that goes wrong unnoticed. It
        // also owns the live MergeCache, so it handles the merge-behavior switch itself.
        if (_librarianShellWin is { IsLoaded: true } lib) lib.ViewModel.ApplySettings(newSettings);
        else if (_settings.MergeBehavior == MergeCacheBehavior.LocalStorage &&
                 newSettings.MergeBehavior != MergeCacheBehavior.LocalStorage)
        {
            // No Librarian open, so nothing will ever call SetPersistence for this transition.
            // Left on disk the snapshot outlives the switch, and a later switch BACK silently
            // re-adopts a staging batch from whatever session last used Local Storage. Keyed off
            // the TRANSITION, not the new value alone: the Librarian's own open path only sees the
            // settings it is handed, so a caller passing plain defaults (the UI theme smoke test
            // does exactly that) must not be able to delete a real user's staged batch.
            new FileMergeCachePersistence(LibrarianShellViewModel.MergeCachePath).Clear();
        }

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
            // Push VGA mirror + screensaver to daemon immediately if connected.
            // MIRROR_ON is unsupported on Nautilus (ERR MIRROR_UNSUPPORTED) - see OnSessionConnected.
            _mirrorState = _settings.VgaMirrorEnabled;
            if (_deviceFamily != DeviceFamily.Nautilus)
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

    void Ctrl(string cmd) => Ctrl(cmd, _ctrl);

    // Explicit-target overload for a multi-step sequence (macro, clipboard paste) that must
    // stay pinned to the SAME endpoint for its whole run - see RunUserMacroAsync/
    // PasteClipboardToKronos, which capture `_ctrl` once and send every step through it rather
    // than re-resolving `_ctrl` (and so implicitly following a host/settings change) per step.
    void Ctrl(string cmd, ICtrlClient ctrl)
    {
        AppLog.Debug($"[ctrl] {cmd}");
        _sysExService.NotifyUserActivity();
        ctrl.Send(cmd);
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
            tip   = "Remote typing disabled";
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

        // Main-panel keyboard accessibility (Tab/Space on the KronosButtons, arrow keys on
        // the Value slider/Data wheel) only participates while remote typing is OFF - Tab and
        // Space belong to the Kronos while it's armed. See KronosButton's own ctor comment.
        KronosButton.RemoteTypingArmed = _kbdSendEnabled;
        // Canvas/Image aren't Control, so they have no IsTabStop of their own - the
        // KeyboardNavigation attached property is the WPF-wide equivalent for any element.
        bool panelFocusable = !_kbdSendEnabled;
        _kronosLeftPanel.ValueSliderCanvas.Focusable = panelFocusable;
        KeyboardNavigation.SetIsTabStop(_kronosLeftPanel.ValueSliderCanvas, panelFocusable);
        _kronosRightPanel.Data_Wheel.Focusable = panelFocusable;
        KeyboardNavigation.SetIsTabStop(_kronosRightPanel.Data_Wheel, panelFocusable);
        _nautilusRightPanel.Data_Wheel.Focusable = panelFocusable;
        KeyboardNavigation.SetIsTabStop(_nautilusRightPanel.Data_Wheel, panelFocusable);
        foreach (var b in new System.Windows.Controls.Primitives.ButtonBase[]
                 { BTN_SeqLocate, BTN_SeqRew, BTN_SeqFf, BTN_SeqPause,
                   BTN_SeqRec, BTN_SeqStart, BTN_TapTempo, BTN_SeqSave })
            b.IsTabStop = panelFocusable;
        KbdStatusGrid.ToolTip    = tip;
    }

    // ── Notification bubble ───────────────────────────────────────────────────

    static readonly Color NotifyColorIdle  = Color.FromRgb(0xB4, 0xB4, 0xB4);
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
                ConnState.Connected  => AppMessages.Connection.Connected(_host, _deviceFamily),
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

    // Status bar > current performance > right-click > Pull from Kronos. This/Bank/Mode need
    // a resolved CurrentBankId (Program or Combi only - see ISysExService.CurrentBankId's own
    // comment); All always works, it's just the existing full-library pull.
    enum PullScope { Program, Bank, Mode, All }

    void UpdatePullMenuState()
    {
        var id = _sysExService.CurrentBankId;
        bool have = id.HasValue;
        CTX_PullThisProgram.Header    = id?.Type == 1 ? "This _Program" : "This _Combi";
        CTX_PullThisProgram.IsEnabled = have;
        CTX_PullThisBank.IsEnabled    = have;
        CTX_PullThisMode.IsEnabled    = have;
    }

    async Task RunPullAsync(PullScope scope)
    {
        var id = _sysExService.CurrentBankId;
        if (scope != PullScope.All && id == null) return;   // menu already gates this; defensive only

        int objType = id is { } bid && bid.Type == 1 ? LibObj.Program : LibObj.Combi;
        int objBank = id?.ObjBank ?? 0;
        int number  = id?.Number ?? 0;
        try
        {
            var result = scope switch
            {
                PullScope.Program => await LibraryPullPipeline.PullObjectAsync(_sysExService, _localLibraryCache, objType, objBank, number),
                PullScope.Bank    => await LibraryPullPipeline.PullBankAsync(_sysExService, _localLibraryCache, objType, objBank),
                PullScope.Mode    => await LibraryPullPipeline.PullModeAsync(_sysExService, _localLibraryCache, objType),
                _                 => await LibraryPullPipeline.PullAsync(_sysExService, _localLibraryCache, full: false),
            };
            SetNotification(AppMessages.Notify.PullComplete(result.ObjectsFetched, result.Conflicts), isError: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[pull] {scope} pull failed: {ex.Message}");
            SetNotification(AppMessages.Notify.PullFailed(ex.Message), isError: true);
        }
    }

    void ApplyMidiMonitorMenuState()
    {
        bool enabled = _settings.MidiMonitorEnabled;
        MNU_SysExTool.IsEnabled = enabled;
        MNU_SysExTool.Opacity   = enabled ? 1.0 : 0.4;
        MidiMonitorStatusBtn.IsEnabled = enabled;
        MidiMonitorStatusBtn.Opacity   = enabled ? 1.0 : 0.4;
    }

    void OpenSysExToolWindow()
    {
        if (_sysExToolWin != null && _sysExToolWin.IsLoaded)
        {
            _sysExToolWin.Activate();
            _sysExToolWin.Focus();
            return;
        }
        // IsAvailable reflects the Kronos's own SysEx probe (KronosSysEx.ProbeAsync, run once
        // per host on connect) - false here means the instrument's own SysEx/MIDI setting is
        // off, not this app's "Monitor MIDI" toggle (that already gates whether this window can
        // be reached at all, see ApplyMidiMonitorMenuState). Non-blocking: the window still
        // opens immediately, since traffic can start flowing the moment the user fixes it on
        // the Kronos - and RecheckSysExAvailabilityAsync re-probes rather than trusting that
        // one-time connect-time reading, so a user who enables SysEx AFTER connecting and then
        // opens this window doesn't see a permanently-stale red bubble (the probe cache has no
        // expiry otherwise - see KronosSysEx.ProbeAsync).
        if (!_sysExService.IsAvailable)
            _ = RecheckSysExAvailabilityAsync();
        _sysExToolWin = new SysExToolWindow(_sysExService, _settings.MidiOutputChannel).OwnedBy(this);
        _sysExToolWin.Closed += (_, _) =>
        {
            _settings.MidiOutputChannel = _sysExToolWin.SelectedChannel;
            Storage.SaveSettings(_settings);
        };
        _sysExToolWin.SetActiveStream(_midiCoord.ActiveLinkLabel);   // seed with the current link
        _sysExToolWin.Show();
    }

    // Re-probes SysEx availability (up to ~8 s - see KronosSysEx.ProbeAsync) and only shows
    // the red bubble if that confirms it's still unavailable, instead of trusting a possibly-
    // stale reading from the one-time connect probe. Fire-and-forget from OpenSysExToolWindow.
    async Task RecheckSysExAvailabilityAsync()
    {
        bool capable = await _sysExService.RecheckAvailabilityAsync().ConfigureAwait(false);
        if (!capable)
            SetNotification(AppMessages.Notify.SysExUnavailable, isError: true);
    }

    // Fire-and-forget from the constructor, mirroring LibrarianShellViewModel.WarmCatalogAsync's
    // own exception handling - without an explicit catch/log here, a blob-IO failure (e.g. the
    // library share going away) would be an unobserved task exception, invisible until the
    // Librarian window is opened and pays the same cost again. LocalPane.IsIndexing (which hides
    // the Keyboard Library tree mid-build) is owned by LibrarianShellViewModel, not this window, so
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
        //
        // Deliberately NOT .OwnedBy(this) - same reasoning as OpenSampleEditorWindow's own
        // comment: a WPF/Win32 owned window is permanently kept ABOVE its owner in z-order for
        // as long as both are visible, which was exactly the complaint (clicking back to
        // MainWindow while the Librarian stays open didn't bring MainWindow to front). Centering
        // and "closes when MainWindow closes" are reproduced manually - the Left/Top below and
        // OnClosing's own explicit _librarianShellWin.Close() call.
        var win = new LibrarianShellWindow(_sysExService, _localLibraryCache, _settings, _host)
            { WindowStartupLocation = WindowStartupLocation.Manual };
        win.Left = Left + (ActualWidth - win.Width) / 2;
        win.Top = Top + (ActualHeight - win.Height) / 2;
        _librarianShellWin = win;
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
        List<CommandEntry> entries =
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
            new("Sample Editor",   "Sample Editor",           K("Sample Editor"), () => OpenSampleEditorWindow()),
            new("Librarian",       "Librarian",               K("Librarian"),     () => OpenLibrarianShellWindow()),
            new("Mirror",          "Toggle VGA Mirror",       K("Mirror"),        () => { if (_deviceFamily == DeviceFamily.Nautilus) return; _mirrorState = !_mirrorState; Ctrl(DaemonCommand.VgaMirror(_mirrorState)); }),
            new("Calibrate",       "Toggle Calibration Mode", K("Calibrate"),     () => { _cal.Mode = !_cal.Mode; if (_cal.Mode) EnterCalMode(); else ExitCalMode(); OverlayLayer.InvalidateVisual(); }),
            new("PaletteEditor",   "Toggle Palette Editor",  K("PaletteEditor"), () => { _edOpen = !_edOpen; OverlayLayer.InvalidateVisual(); }),
            new("SaveScreenshot",  "Save Screenshot...",        "",              () => SaveScreenshot()),
            new("ToggleKeyboardSend", "Toggle Remote Typing", "",              () => { _kbdSendEnabled = !_kbdSendEnabled; _instantKeys.Clear(); StopRepeat(); ReleaseActiveRawKeys(); UpdateKbdStatus(); OverlayLayer.InvalidateVisual(); }),
            // ── Mode select (Id = action name; also wired to mode buttons + menu + context menu + keybinds)
            new("Mode Setlist",  "Mode: Setlist",  K("Mode Setlist"),  () => SendMode(Mode.Setlist)),
            new("Mode Combi",    "Mode: Combi",    K("Mode Combi"),    () => SendMode(Mode.Combi)),
            new("Mode Program",  "Mode: Program",  K("Mode Program"),  () => SendMode(Mode.Program)),
            new("Mode Sequence", "Mode: Sequence", K("Mode Sequence"), () => SendMode(Mode.Sequence)),
            new("Mode Sampling", "Mode: Sampling", K("Mode Sampling"), () => SendMode(Mode.Sampling)),
            new("Mode Global",   "Mode: Global",   K("Mode Global"),   () => SendMode(Mode.Global)),
            new("Mode Disk",     "Mode: Disk",     K("Mode Disk"),     () => SendMode(Mode.Disk)),
            // Nautilus's front-panel "F" (Save) - not a Mode, so it isn't part of the Kronos
            // "Mode "-prefixed set above; only reachable via MENU_ModeSelect's Nautilus item set
            // and the front panel itself (NautilusRightPanel.BTN_F), not a keybind.
            new("NautilusModeF", "F: Save",        "",                 () => Ctrl(DaemonCommand.Button(PanelButton.Exit))),
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
            // Nautilus tokens only (Kronos's own SEQ_LOCATE/SEQ_REW/SEQ_FF/SEQ_PAUSE are
            // untouched) - Locate and Rewind swapped, per your 2026-09-22 correction: the
            // BUTTON stays where it is (BTN_SeqLocate still means "Locate" in the UI), only
            // which Nautilus scan code it actually sends changed. Forward (MP8) is unaffected.
            new("Seq Locate",  "Seq: Locate",       K("Seq Locate"),  () => Ctrl(DaemonCommand.Button(NautilusToken(PanelButton.SeqLocate, "MS1")))),
            new("Seq Rewind",  "Seq: Rewind",       K("Seq Rewind"),  () => Ctrl(DaemonCommand.Button(NautilusToken(PanelButton.SeqRewind, "MP7")))),
            new("Seq Forward", "Seq: Fast-Forward", K("Seq Forward"), () => Ctrl(DaemonCommand.Button(NautilusToken(PanelButton.SeqForward, "MP8")))),
            // Pause/Start(PlayStop) likewise swapped (Nautilus only) - see SeqTransportViewModel
            // for the Start/Stop half of this swap.
            new("Seq Pause",   "Seq: Pause",        K("Seq Pause"),   () => Ctrl(DaemonCommand.Button(NautilusToken(PanelButton.SeqPause, "MS3")))),
            new("Seq Record",  "Seq: Record",       K("Seq Record"),  () => _seqTransport.RecordCommand.Execute(null)),
            new("Seq Start",   "Seq: Start/Stop",   K("Seq Start"),   () => _seqTransport.StartStopCommand.Execute(null)),
            new("Seq Save",    "Write / Save",      K("Seq Save"),    () => _seqTransport.SaveCommand.Execute(null)),
            // Tap tempo: one BUTTON TAP_TEMPO press per invocation; the Kronos averages
            // successive taps itself. Global - enabled whenever connected, not seq-gated.
            new("Tap Tempo",   "Tap Tempo",         K("Tap Tempo"),   TapTempoOnce),
            // ── Help
            new("Help",  "Show Help", K("Help"), () => OpenHelpWindow()),
            new("About", "About",     "",        () => OpenAboutWindow()),
            new("Quit",  "Quit",      K("Quit"),  () => TryQuit()),
        ];
        // Retired (3.0.2) - always removed, same as the Tools menu item; see
        // ApplyDeviceFamilyUi's comment for why.
        entries.RemoveAll(e => e.Id == "PaletteEditor");

        // Bank Select sends Kronos front-panel scan codes with no confirmed Nautilus equivalent
        // yet (only 3 of its ~28 tokens are known - BANK_IA/BANK_UF/BANK_UG). Disabled rather
        // than guessed. Mode Select WAS disabled here too (grill-me follow-up, 2026-09-21) but
        // is now confirmed and re-enabled: Mode.ButtonName()'s wire tokens (COMBI/PROGRAM/...)
        // are literally unchanged on Nautilus (your button-code table, same date) - only the
        // physical button LABEL/position differs (Mode/Page instead of Combi/Program printed
        // directly), which MENU_ModeSelect's own Nautilus item set now reflects (see
        // ApplyDeviceFamilyUi). Wrapping Execute here (rather than guarding RunCommand) covers
        // every consumer of this list uniformly - menu/context/keybind (which all reach the
        // action through RunCommand/_commands, built from this same list) AND the command
        // palette, which builds its OWN fresh copy of this list rather than going through
        // RunCommand (see this method's own header comment). ApplyDeviceFamilyUi additionally
        // greys out MENU_BankSelect so this is visible, not just inert.
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.Id.StartsWith("Bank ")) continue;
            entries[i] = e with
            {
                Execute = () =>
                {
                    if (_deviceFamily == DeviceFamily.Nautilus)
                    {
                        AppLog.Debug($"[cmd] '{e.Id}' disabled on Nautilus - no confirmed wire token yet");
                        return;
                    }
                    e.Execute();
                }
            };
        }
        return entries;
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
    // Resolves a panel-function wire token to Nautilus's own scan code when connected to one,
    // else the Kronos token unchanged. Source: button_labels.json (ButtonInjectorWindow-tested
    // against real Nautilus hardware) - see SeqTransportViewModel's own comment on why this data
    // supersedes the older Documentation/nautilus_button_mapping.md.
    string NautilusToken(PanelButton kronos, string nautilus) =>
        _deviceFamily == DeviceFamily.Nautilus ? nautilus : kronos.Token();

    void TapTempoOnce()
    {
        FlashTapTempo();
        // button_labels.json: Nautilus's own TAP_TEMPO scan code lands on numeric key "8", not a
        // tempo function - the real Tap Tempo button is NUM9 there (unambiguous, confirmed).
        Ctrl(DaemonCommand.Button(NautilusToken(PanelButton.TapTempo, "NUM9")));
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
                _kronosRightPanel.ControlRail.Visibility    = Visibility.Collapsed;
                _kronosLeftPanel.ValueRail.Visibility      = Visibility.Collapsed;
                _kronosRightPanel.ControlViewbox.Visibility = Visibility.Visible;
                ControlsColumn.Width = _hideDataInput
                    ? new GridLength(0, GridUnitType.Star)
                    : new GridLength(FrameDesignWidth, GridUnitType.Star);
                ShowLeftPanel(!_hideValueInput);
                break;

            case LayoutPreset.Focused:
                _focusedDataExpanded  = _settings.FocusedDataExpanded;
                _focusedValueExpanded = _settings.FocusedValueExpanded;
                _kronosRightPanel.ControlRail.Visibility    = Visibility.Visible;
                _kronosLeftPanel.ValueRail.Visibility      = Visibility.Visible;
                _kronosRightPanel.ControlViewbox.Visibility = _focusedDataExpanded ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)_kronosRightPanel.BtnRailExpand.Content).Text = _focusedDataExpanded ? "‹" : "›";
                _kronosRightPanel.BtnRailExpand.ToolTip = _focusedDataExpanded ? "Collapse data input" : "Expand data input";
                ((TextBlock)_kronosLeftPanel.BtnValueRailExpand.Content).Text = _focusedValueExpanded ? "›" : "‹";
                _kronosLeftPanel.BtnValueRailExpand.ToolTip = _focusedValueExpanded ? "Collapse value input" : "Expand value input";
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
        AdjustWindowWidthForPanelChange((_focusedDataExpanded ? 1 : -1) * (FrameDesignWidth - 28.0));
        _kronosRightPanel.ControlViewbox.Visibility = _focusedDataExpanded ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)_kronosRightPanel.BtnRailExpand.Content).Text = _focusedDataExpanded ? "‹" : "›";
        _kronosRightPanel.BtnRailExpand.ToolTip = _focusedDataExpanded ? "Collapse data input" : "Expand data input";
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
        AdjustWindowWidthForPanelChange((_focusedValueExpanded ? 1 : -1) * (282.0 - 28.0));
        ((TextBlock)_kronosLeftPanel.BtnValueRailExpand.Content).Text = _focusedValueExpanded ? "›" : "‹";
        _kronosLeftPanel.BtnValueRailExpand.ToolTip = _focusedValueExpanded ? "Collapse value input" : "Expand value input";
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
        // _frameH (not the FrameDesignHeight constant) so the window actually fits the
        // CONNECTED stream's aspect ratio - 800x600 Kronos, 800x480 Nautilus (RefreshFrameRect
        // already letterboxes correctly either way; this is what makes that letterboxing
        // unnecessary by sizing the window to need none). Defaults to FrameDesignHeight (600)
        // before any connection, since _frameH is seeded with it.
        Height = _frameH * scale + menuH + chromeH;
        _scaledW = Width; _scaledH = Height;
    }

    // Settings > View > "Default Window Size" - applied once at launch (OnLoaded) in place
    // of the restored WindowWidth/Height. Maximized bypasses SetWindowSize (which requires
    // WindowState.Normal); the rest reuse the same scale factors as the Ctrl+1..5 shortcuts.
    void ApplyDefaultWindowSize(DefaultWindowSizeMode mode)
    {
        if (mode == DefaultWindowSizeMode.Maximized) { WindowState = WindowState.Maximized; return; }
        SetWindowSize(mode switch
        {
            DefaultWindowSizeMode.Small => 0.75,
            DefaultWindowSizeMode.Large => 1.25,
            _                           => 1.0,
        });
    }

    // Grows/shrinks the actual window Width by exactly the design-width a panel's
    // show/hide just added or removed, scaled the same way SetWindowSize's own targetW
    // is - unlike SetWindowSize (which recomputes an ABSOLUTE target and only applies
    // when the window is still untouched, see ResizeAndRefresh's own userResized check),
    // this keeps whatever size the user has already dragged the window to. Without it,
    // hiding a panel collapsed its Grid column to 0 but left the window's own physical
    // Width unchanged - the vacated Star-sizing space was redistributed into the
    // Viewbox-wrapped screen/panel content instead of actually shrinking the window,
    // which reads as blank grey letterboxing rather than the panel's space disappearing.
    // Keeps _scaledW in lockstep so ResizeAndRefresh's own "has the user resized since
    // the last programmatic size change" check stays meaningful after this runs.
    void AdjustWindowWidthForPanelChange(double deltaDesignWidth)
    {
        if (!IsLoaded || _fs.Active || WindowState == WindowState.Maximized) return;
        double delta = deltaDesignWidth * _currentScale;
        Width = Math.Max(MinWidth, Width + delta);
        if (!double.IsNaN(_scaledW)) _scaledW += delta;
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
        // The Kronos left value-slider skin doesn't exist on Nautilus at all (ApplyDeviceFamilyUi)
        // - centralized here too so a Focused-preset panel toggle (ToggleFocusedValueExpand etc.)
        // can never re-show it out from under that, regardless of call order.
        if (_deviceFamily == DeviceFamily.Nautilus) show = showRail = false;
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
        _kronosLeftPanel.LeftPanelViewbox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
        AdjustWindowWidthForPanelChange(_hideDataInput ? -FrameDesignWidth : FrameDesignWidth);
        ApplyHideInputPanels();
        MNU_HideDataInput.IsChecked = _hideDataInput;
    }

    void ToggleHideValueInput()
    {
        _hideValueInput = !_hideValueInput;
        _settings.HideValueInput = _hideValueInput;
        Storage.SaveSettings(_settings);
        AdjustWindowWidthForPanelChange(_hideValueInput ? -282.0 : 282.0);
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
