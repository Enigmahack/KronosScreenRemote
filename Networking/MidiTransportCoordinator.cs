namespace KronosScreenRemote;

using System.Windows.Threading;

// Decides which MIDI backend SysExService runs on and (re)starts it as inputs
// change: the transport-mode setting, TCP screen connect/disconnect, and USB
// device hot-plug. Keeps the MIDI path independent of the video/daemon
// connection, so USB works standalone (no screen), and honours Auto-prefer-USB.
//
// Decision (per MidiTransportMode):
//   Auto — a Kronos USB device if present, else the TCP daemon (when connected).
//   Usb  — the Kronos USB device if present, else nothing.
//   Tcp  — the TCP daemon when the screen is connected, else nothing.
//
// All mutation funnels through Reevaluate() under a lock, so the hot-plug timer
// (UI thread) and the connect task (background thread) can't race on the current
// selection.
sealed class MidiTransportCoordinator : IDisposable
{
    readonly ISysExService _sysEx;
    readonly DispatcherTimer _hotplug;
    readonly object _gate = new();

    MidiTransportMode _mode = MidiTransportMode.Auto;
    string _usbMatch = KronosMidiDevices.DefaultMatch;
    string _host = "";
    int    _ctrlPort = CtrlClient.CtrlPort;
    bool   _screenConnected;

    string? _usbDevice;                     // resolved Kronos USB name, or null
    string? _usbOpenFailed;                 // device name whose USB port wouldn't open (busy)
    int     _usbRetryTick;                  // backoff counter for retrying a failed-open device
    (string Kind, string Id)? _current;     // what SysExService currently runs on

    // Hot-plug ticks between retries of a present-but-failed-open USB device (3 s
    // timer × 5 ≈ 15 s). Slow enough not to churn on a device a DAW is holding.
    const int UsbRetryEveryTicks = 5;

    // Fired when the active transport changes; argument is its description, or null
    // when no transport is active. Handlers should marshal to the UI thread.
    public event Action<string?>? ActiveTransportChanged;

    public string? ActiveDescription { get; private set; }

    // The concrete link now carrying MIDI (for the footer badge + SysEx monitor).
    // ActiveLink is the short kind; ActiveLinkLabel is "USB — KRONOS" / "DIN — <dev>"
    // / "TCP — host:port" / "—". Written under _gate in Reevaluate; read on the UI
    // thread (enum/reference reads are atomic).
    public MidiLinkKind ActiveLink      { get; private set; } = MidiLinkKind.None;
    public string       ActiveLinkLabel { get; private set; } = "—";

    // True when the active MIDI transport is direct USB. Lets the UI skip the daemon
    // auto-connect (screen + FTP) on launch — USB carries the MIDI features with no
    // network/auth, so the screen becomes opt-in via an explicit Connect.
    public bool UsingUsb { get { lock (_gate) return _current?.Kind == "usb"; } }

    public MidiTransportCoordinator(ISysExService sysEx)
    {
        _sysEx   = sysEx;
        _hotplug = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _hotplug.Tick += (_, _) => PollUsb();
    }

    // Apply the transport-mode + USB-match settings (startup and on settings change).
    public void ApplySettings(MidiTransportMode mode, string usbMatch)
    {
        string desc; bool changed;
        lock (_gate)
        {
            _mode     = mode;
            _usbMatch = string.IsNullOrWhiteSpace(usbMatch) ? KronosMidiDevices.DefaultMatch : usbMatch.Trim();
            changed   = Reevaluate(out desc);
        }
        if (changed) ActiveTransportChanged?.Invoke(ActiveDescription);
    }

    // TCP screen connect/disconnect. USB is unaffected (it's independent), but this
    // supplies the host/port a TCP transport needs and drives Auto's fallback.
    public void SetScreenConnection(bool connected, string host, int ctrlPort)
    {
        bool changed;
        lock (_gate)
        {
            _screenConnected = connected;
            if (connected) { _host = host; _ctrlPort = ctrlPort; }
            changed = Reevaluate(out _);
        }
        if (changed) ActiveTransportChanged?.Invoke(ActiveDescription);
    }

    // Begin hot-plug monitoring and evaluate the initial transport (may start USB
    // standalone before any screen connection).
    public void Start()
    {
        PollUsb();
        _hotplug.Start();
    }

    public void Dispose() => _hotplug.Stop();

    void PollUsb()
    {
        bool changed = false;
        lock (_gate)
        {
            var found = KronosMidiDevices.Find(_usbMatch);

            if (found != _usbDevice)
            {
                // Presence changed (plugged in or removed) — the normal hot-plug path.
                AppLog.Info($"[midi-coord] USB Kronos {(found != null ? $"detected: '{found}'" : "removed")}");
                _usbDevice     = found;
                _usbOpenFailed = null;   // a re-plug (presence change) gets a fresh open attempt
                _usbRetryTick  = 0;
                changed = Reevaluate(out _);
            }
            else if (found != null && _usbOpenFailed == found && _current?.Kind != "usb"
                     && ++_usbRetryTick >= UsbRetryEveryTicks)
            {
                // Device still present but a PRIOR open failed (busy/initialising) and we
                // fell back to TCP/none. Without this, that latch would strand us off USB
                // until a physical replug. Re-test on a backoff and only switch when a
                // lightweight open now succeeds, so a still-busy device never tears down
                // the working transport.
                _usbRetryTick = 0;
                if (KronosMidiDevices.CanOpen(_usbMatch))
                {
                    AppLog.Info($"[midi-coord] USB '{found}' openable again — upgrading from fallback");
                    _usbOpenFailed = null;
                    changed = Reevaluate(out _);
                }
            }
        }
        if (changed) ActiveTransportChanged?.Invoke(ActiveDescription);
    }

    // USB is a candidate only if a device is present AND it didn't just fail to open
    // (busy — e.g. held by a DAW). A failed device is excluded until it's re-plugged
    // or the settings change, so Auto falls through to TCP instead of dying silently.
    bool UsbUsable => _usbDevice != null && _usbDevice != _usbOpenFailed;

    (string Kind, string Id)? Decide() => _mode switch
    {
        MidiTransportMode.Usb => UsbUsable ? ("usb", _usbMatch) : null,
        MidiTransportMode.Tcp => _screenConnected ? ("tcp", $"{_host}:{_ctrlPort}") : null,
        _ /* Auto */          => UsbUsable ? ("usb", _usbMatch)
                                           : (_screenConnected ? ("tcp", $"{_host}:{_ctrlPort}") : null),
    };

    // Returns true (and sets ActiveDescription) if the active transport changed.
    // Must be called under _gate.
    bool Reevaluate(out string description)
    {
        description = "";
        var desired = Decide();
        if (Nullable.Equals(desired, _current)) return false;

        if (desired == null)
        {
            _sysEx.Reset();
            _current = null;
            ActiveDescription = null;
            SetLink(MidiLinkKind.None, "—");
            AppLog.Info("[midi-coord] no MIDI transport active");
            return true;
        }

        if (desired.Value.Kind == "usb")
        {
            var usb = new UsbMidiTransport(_usbMatch);
            _sysEx.Start(usb);                    // opens the device synchronously
            if (usb.CanStream)                    // opened OK
            {
                _current = desired;
                ActiveDescription = usb.Description;
                description = usb.Description;
                // Native Kronos USB (name has "KRONOS") = USB (fast); any other
                // MIDI device = a 5-pin DIN interface bridging the Kronos = DIN (slow).
                var kind = ClassifyUsb(_usbDevice);
                SetLink(kind, $"{LinkWord(kind)} — {_usbDevice ?? _usbMatch}");
                AppLog.Info($"[midi-coord] MIDI transport → {usb.Description} (link={kind})");
                return true;
            }

            // winmm input is exclusive — the port is present but busy (DAW/other app).
            // Mark it unusable and fall back to TCP if a screen is connected, else
            // leave nothing running (a re-plug clears the mark and retries).
            AppLog.Warn($"[midi-coord] USB '{_usbMatch}' present but did not open (port busy?) — falling back");
            _usbOpenFailed = _usbDevice;
            if (_screenConnected)
            {
                var tcp = new TcpMidiTransport(_host, _ctrlPort);
                _sysEx.Start(tcp);                // disposes the dead USB, starts TCP
                _current = ("tcp", $"{_host}:{_ctrlPort}");
                ActiveDescription = tcp.Description;
                description = tcp.Description;
                SetLink(MidiLinkKind.Tcp, $"TCP — {_host}:{_ctrlPort}");
                AppLog.Info($"[midi-coord] fell back to {tcp.Description} (USB busy)");
            }
            else
            {
                _sysEx.Reset();                   // dispose the dead USB; nothing else available
                _current = null;
                ActiveDescription = null;
                SetLink(MidiLinkKind.None, "—");
                AppLog.Info("[midi-coord] no MIDI transport (USB busy, no TCP screen)");
            }
            return true;
        }

        var t = new TcpMidiTransport(_host, _ctrlPort);
        _sysEx.Start(t);
        _current = desired;
        ActiveDescription = t.Description;
        description = t.Description;
        SetLink(MidiLinkKind.Tcp, $"TCP — {_host}:{_ctrlPort}");
        AppLog.Info($"[midi-coord] MIDI transport → {t.Description}");
        return true;
    }

    void SetLink(MidiLinkKind kind, string label) { ActiveLink = kind; ActiveLinkLabel = label; }

    // The Kronos's own USB port reports a product name containing "KRONOS"; any other
    // MIDI device carrying Kronos traffic is a generic interface on the 5-pin DIN link.
    static MidiLinkKind ClassifyUsb(string? deviceName) =>
        deviceName != null && deviceName.Contains("KRONOS", StringComparison.OrdinalIgnoreCase)
            ? MidiLinkKind.Usb : MidiLinkKind.Din;

    static string LinkWord(MidiLinkKind k) => k switch
    {
        MidiLinkKind.Tcp => "TCP",
        MidiLinkKind.Usb => "USB",
        MidiLinkKind.Din => "DIN",
        _                => "—",
    };
}
