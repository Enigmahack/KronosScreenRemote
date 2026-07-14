namespace KronosScreenRemote;

// MIDI transport over the screenremote daemon (the original, TCP path).
//
//   • Round-trip + probe  → KronosSysEx (daemon SYSEX ctrl command).
//   • Fire-and-forget send → CtrlClient MIDI_SEND ctrl command.
//   • Live inbound stream  → MidiStreamMonitor (port-9875 MIDI-out firehose).
//
// Behaviour is byte-for-byte the same as before the transport abstraction was
// extracted: this class just re-homes the three collaborators SysExService used
// to new up itself, and merges their Traffic streams into one event.
sealed class TcpMidiTransport : IKronosMidiTransport
{
    readonly string _host;
    readonly int    _ctrlPort;
    readonly KronosSysEx _sysEx;
    MidiStreamMonitor? _monitor;
    bool _streamEnabled = true;

    public event Action<SysExTrafficEntry>? Traffic;
    public event Action<byte[]>? SysExMessageReceived;
    public event Action? SysExActivity;

    public string Description => $"TCP {_host}:{_ctrlPort}";
    public string CacheKey    => _host;   // preserve existing per-host cache keys
    public bool   CanStream   => _monitor != null;
    public SysExModeData? LastModeData => _sysEx.LastModeData;

    public TcpMidiTransport(string host, int ctrlPort)
    {
        _host     = host;
        _ctrlPort = ctrlPort;
        _sysEx    = new KronosSysEx(host, ctrlPort);
        _sysEx.Traffic += OnChildTraffic;   // round-trip TX/RX from the SYSEX command
    }

    public void Start()
    {
        if (_streamEnabled) EnsureMonitor();
    }

    public void Stop()
    {
        DisposeMonitor();
    }

    public void Dispose() => Stop();

    public void SetStreamEnabled(bool enabled)
    {
        _streamEnabled = enabled;
        if (enabled) EnsureMonitor();
        else         DisposeMonitor();
    }

    void EnsureMonitor()
    {
        if (_monitor != null) return;
        var m = new MidiStreamMonitor(_host);
        m.Traffic              += OnChildTraffic;
        m.SysExMessageReceived += OnMonitorSysEx;
        m.SysExActivity        += OnMonitorActivity;
        _monitor = m;
        m.Start();
    }

    void DisposeMonitor()
    {
        var m = _monitor;
        _monitor = null;
        if (m == null) return;
        m.Traffic              -= OnChildTraffic;
        m.SysExMessageReceived -= OnMonitorSysEx;
        m.SysExActivity        -= OnMonitorActivity;
        m.Stop();
    }

    void OnChildTraffic(SysExTrafficEntry e) => Traffic?.Invoke(e);
    void OnMonitorSysEx(byte[] m)            => SysExMessageReceived?.Invoke(m);
    void OnMonitorActivity()                 => SysExActivity?.Invoke();

    public Task<bool> ProbeAsync(int timeoutMs = 8000) => _sysEx.ProbeAsync(timeoutMs);

    // The daemon's SYSEX command captures the single reply itself, so the func
    // hint is unused here (correlation is implicit in the request/response ctrl
    // round-trip). KronosSysEx already logs TX + RX to Traffic.
    public Task<byte[]?> QueryAsync(byte[] request, byte? expectReplyFunc = null, int timeoutMs = 3000)
        => _sysEx.SendAsync(request, timeoutMs);

    public async Task<bool> SendAsync(byte[] message)
    {
        var resp = await CtrlClient.QueryAsync(_host, _ctrlPort, DaemonCommand.MidiSend(MidiHex.ToHex(message)), 2000)
            .ConfigureAwait(false);
        return resp?.TrimEnd() == "OK";
    }
}
