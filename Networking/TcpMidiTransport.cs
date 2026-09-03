namespace KronosScreenRemote;

// MIDI transport over the screenremote daemon (the original, TCP path).
//
//   • Round-trip + probe  → KronosSysEx (daemon SYSEX ctrl command).
//   • Fire-and-forget send → CtrlClient MIDI_SEND ctrl command.
//   • Live inbound stream  → MidiStreamMonitor (port-9875 MIDI-out firehose).
//
// This class re-homes those three collaborators under one IKronosMidiTransport and
// merges their Traffic streams into one event.
sealed class TcpMidiTransport : IKronosMidiTransport
{
    readonly string _host;
    readonly int    _ctrlPort;
    readonly KronosSysEx _sysEx;
    // Guards the _monitor check-then-act. Start/Stop/SetStreamEnabled can be driven from
    // different threads (coordinator vs. an ApplyMidiSettings on the UI thread), so an
    // unguarded "if (_monitor != null) return; ... _monitor = m" let two callers each create and
    // start a monitor, orphaning one live socket + read loop. Snapshot reads elsewhere
    // (CanStream, SendLargeSysExAsync) stay lock-free - a reference read is atomic.
    readonly object _monitorLock = new();
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
        // Whole check-then-act under the lock - including m.Start() - so a concurrent
        // DisposeMonitor can't null/dispose the instance between assignment and start.
        lock (_monitorLock)
        {
            if (_monitor != null) return;
            var m = new MidiStreamMonitor(_host);
            m.Traffic              += OnChildTraffic;
            m.SysExMessageReceived += OnMonitorSysEx;
            m.SysExActivity        += OnMonitorActivity;
            _monitor = m;
            m.Start();
        }
    }

    void DisposeMonitor()
    {
        MidiStreamMonitor? m;
        lock (_monitorLock)
        {
            m = _monitor;
            _monitor = null;
        }
        if (m == null) return;
        // Unsubscribe + dispose outside the lock (Dispose cancels the read loop): a concurrent
        // EnsureMonitor may create a fresh, independent monitor meanwhile - harmless.
        m.Traffic              -= OnChildTraffic;
        m.SysExMessageReceived -= OnMonitorSysEx;
        m.SysExActivity        -= OnMonitorActivity;
        m.Dispose();
    }

    void OnChildTraffic(SysExTrafficEntry e) => Traffic?.Invoke(e);
    void OnMonitorSysEx(byte[] m)            => SysExMessageReceived?.Invoke(m);
    void OnMonitorActivity()                 => SysExActivity?.Invoke();

    public Task<bool> ProbeAsync(int timeoutMs = 8000, bool forceRefresh = false) => _sysEx.ProbeAsync(timeoutMs, forceRefresh);

    // The daemon's SYSEX command captures the single reply itself, so the func
    // hint is unused here (correlation is implicit in the request/response ctrl
    // round-trip). KronosSysEx already logs TX + RX to Traffic.
    public Task<byte[]?> QueryAsync(byte[] request, byte? expectReplyFunc = null, int timeoutMs = 3000)
        => _sysEx.SendAsync(request, timeoutMs);

    public async Task<bool> SendAsync(byte[] message)
    {
        // This is the ONE choke point every dump/digest/write request's outbound send funnels
        // through during a Sync (SysExDumpCollector.CollectAsync, AwaitReplyAsync's `send`) - a
        // Force Full Sync can fire hundreds to thousands of these back-to-back, each its own
        // short-lived ctrl-port TCP connection (CtrlQuery's own comment). 2000ms was tuned for a
        // single idle request; under that much connection churn against the daemon's tiny ctrl
        // server, occasional replies land past it, and CtrlQuery's cts-driven timeout genuinely
        // throws (caught, but visible in a debugger as a repeating TaskCanceledException - see
        // CtrlQuery's catch). Generous headroom here costs nothing in the common case (the daemon
        // answers in well under a second) and only matters exactly when it's needed.
        var resp = await CtrlQuery.QueryAsync(_host, _ctrlPort, DaemonCommand.MidiSend(MidiHex.ToHex(message)), 4000)
            .ConfigureAwait(false);
        return resp?.TrimEnd() == "OK";
    }

    public async Task<bool> SendLargeSysExAsync(byte[] sysex)
    {
        // PREFERRED: inject over the 9875 stream socket. It's the daemon's raw
        // bidirectional MIDI pipe - inbound bytes are recv()'d and write()'n straight
        // to /proc/.midi_in with NO per-line cap (midi_tcp.c), the same fast path
        // Python uses. One socket write carries the whole object; TCP handles the
        // size and the daemon reassembles the byte stream to the Kronos.
        var monitor = _monitor;
        if (monitor != null && await monitor.SendAsync(sysex).ConfigureAwait(false))
            return true;

        // FALLBACK only (monitor/stream not available): the ctrl-port MIDI_SEND path,
        // whose mb[4096] decode buffer + CTRL_LINE_MAX cap force splitting a big object
        // across several sends the daemon injects contiguously.
        AppLog.Warn("[midi-tcp] 9875 injector unavailable - falling back to chunked MIDI_SEND");
        return await SendViaChunkedMidiSendAsync(sysex).ConfigureAwait(false);
    }

    // Max MIDI bytes per MIDI_SEND. MidiHex.ToHex is space-separated (~3 chars/byte),
    // so the daemon's CTRL_LINE_MAX (8320) and its mb[4096] decode buffer both bound a
    // single send; 2048 stays well under either with headroom for the "MIDI_SEND "
    // prefix. Splitting mid-SysEx is safe: the daemon write()s each chunk's raw bytes
    // to /proc/.midi_in in order, so the Kronos sees one contiguous F0...F7.
    const int MaxMidiSendBytes = 2048;

    async Task<bool> SendViaChunkedMidiSendAsync(byte[] sysex)
    {
        if (sysex.Length <= MaxMidiSendBytes)
            return await SendAsync(sysex).ConfigureAwait(false);

        for (int off = 0; off < sysex.Length; off += MaxMidiSendBytes)
        {
            int len = Math.Min(MaxMidiSendBytes, sysex.Length - off);
            var chunk = sysex[off..(off + len)];
            var resp = await CtrlQuery.QueryAsync(_host, _ctrlPort,
                DaemonCommand.MidiSend(MidiHex.ToHex(chunk)), 5000).ConfigureAwait(false);
            if (resp?.TrimEnd() != "OK")
            {
                AppLog.Warn($"[midi-tcp] large SysEx chunk at {off}/{sysex.Length} failed");
                return false;
            }
        }
        return true;
    }
}
