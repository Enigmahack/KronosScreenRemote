using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace KronosScreenRemote;

sealed class SysExService : ISysExService
{
    const double SysExDeferralSec = 5.0;

    readonly Dispatcher _dispatcher;

    // Coalescing window for perf-metadata refresh after a Program/Bank change.
    // A Bank Select is CC0 + CC32 + PC in a burst; debounce collapses them into
    // one query and makes external program changes feel instant.
    const int PerfRefreshDebounceMs = 300;

    KronosSysEx? _transport;
    MidiStreamMonitor? _midiMonitor;
    CancellationTokenSource? _cts;
    CancellationTokenSource? _perfPollDelayCts;
    CancellationTokenSource? _refreshDebounceCts;
    DateTime _lastUserActivity = DateTime.MinValue;
    string _host = "";
    int    _ctrlPort = CtrlClient.CtrlPort;

    public int ValueSliderCc { get; set; } = 18;

    bool _midiMonitorEnabled = true;
    bool _proactivePoll       = false;
    int  _pollIntervalSec     = 60;
    bool _pollOnChanges       = true;

    string _performanceDisplay = "";
    bool _isAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? InitialModeDetected;
    public event Action<int>? ModeChanged;
    public event Action<int>? ValueSliderChanged;
    public event Action<SysExTrafficEntry>? SysExTraffic;

    public string PerformanceDisplay
    {
        get => _performanceDisplay;
        private set => SetProperty(ref _performanceDisplay, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public SysExService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start(string host, int ctrlPort)
    {
        _cts?.Cancel();
        _cts?.Dispose();

        _host     = host;
        _ctrlPort = ctrlPort;
        PerformanceDisplay = "";
        IsAvailable = false;

        if (_transport != null)
            _transport.Traffic -= OnTransportTraffic;

        _transport = new KronosSysEx(host, ctrlPort);
        _transport.Traffic += OnTransportTraffic;

        if (_midiMonitor != null)
            _midiMonitor.Traffic -= OnTransportTraffic;
        _midiMonitor?.Stop();
        _midiMonitor = null;
        if (_midiMonitorEnabled)
        {
            _midiMonitor = new MidiStreamMonitor(host);
            _midiMonitor.Traffic += OnTransportTraffic;
            _midiMonitor.Start();
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Task.Run(() => ProbeAsync(ct));
        _ = PerfMetadataLoop(ct);
    }

    public void Reset()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _refreshDebounceCts?.Cancel(); _refreshDebounceCts?.Dispose(); } catch { }
        _refreshDebounceCts = null;
        if (_transport != null)
            _transport.Traffic -= OnTransportTraffic;
        _transport = null;
        if (_midiMonitor != null)
            _midiMonitor.Traffic -= OnTransportTraffic;
        _midiMonitor?.Stop();
        _midiMonitor = null;
        IsAvailable = false;
        PerformanceDisplay = "";
    }

    void OnTransportTraffic(SysExTrafficEntry entry)
    {
        SysExTraffic?.Invoke(entry);

        // Only interpret messages the Kronos actually transmitted on the live
        // stream (IsMidi + not our own send + raw bytes present).
        if (entry.IsSend || !entry.IsMidi || entry.RawBytes is not { Length: > 0 } raw)
            return;

        ParseIncoming(raw);
    }

    // Decode signals the Kronos pushes unsolicited on the MIDI-out stream.
    // Every recognised message is logged so an absent signal (transmit disabled
    // in the Kronos Global/MIDI settings) is distinguishable from a parse bug.
    void ParseIncoming(byte[] raw)
    {
        byte status = raw[0];

        // Mode Change (SysEx func 0x4E): F0 42 3g 68 4E 0m F7 — authoritative,
        // event-driven mode follow. See KRONOS_MIDI_SysEx.txt func 4E.
        if (status == 0xF0 && raw.Length >= 7 &&
            raw[1] == 0x42 && (raw[2] & 0xF0) == 0x30 && raw[3] == 0x68 && raw[4] == 0x4E)
        {
            var md = new SysExModeData(raw[5] & 0x0F, 0, 0, 0);
            int stateMode = md.ToStateMode();
            if (stateMode > 0)
            {
                AppLog.Info($"[sysex] mode-change 0x4E -> {md.ModeName} (state={stateMode})");
                _dispatcher.InvokeAsync(() => ModeChanged?.Invoke(stateMode));
            }
            return;
        }

        // Channel messages.
        int hi = status & 0xF0;

        // Control Change: value-slider follow (CC# = ValueSliderCc) and
        // Program/Bank-change perf refresh (Bank Select CC0/CC32).
        if (hi == 0xB0 && raw.Length >= 3)
        {
            int cc  = raw[1] & 0x7F;
            int val = raw[2] & 0x7F;

            // Bank Select (MSB/LSB) always takes priority — a misconfigured
            // ValueSliderCc must never shadow program-change follow.
            if (cc == 0 || cc == 32)
            {
                if (_pollOnChanges) _ = DeferredRefreshAsync();
                return;
            }

            if (cc == ValueSliderCc)
            {
                AppLog.Debug($"[sysex] value-slider CC#{cc} = {val}");   // Debug: fires rapidly on a sweep
                _dispatcher.InvokeAsync(() => ValueSliderChanged?.Invoke(val));
            }
            return;
        }

        // Program Change: refresh current performance metadata.
        if (hi == 0xC0 && _pollOnChanges)
            _ = DeferredRefreshAsync();
    }

    public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges)
    {
        _proactivePoll   = proactivePoll;
        _pollIntervalSec = pollIntervalSec;
        _pollOnChanges   = pollOnChanges;

        bool monitorChanged = _midiMonitorEnabled != midiMonitorEnabled;
        _midiMonitorEnabled = midiMonitorEnabled;

        if (monitorChanged && _transport != null)
        {
            if (midiMonitorEnabled)
            {
                _midiMonitor = new MidiStreamMonitor(_host);
                _midiMonitor.Traffic += OnTransportTraffic;
                _midiMonitor.Start();
            }
            else
            {
                if (_midiMonitor != null)
                    _midiMonitor.Traffic -= OnTransportTraffic;
                _midiMonitor?.Stop();
                _midiMonitor = null;
            }
        }

        // Wake up the polling loop if proactive was just enabled
        if (proactivePoll)
        {
            try { _perfPollDelayCts?.Cancel(); } catch { }
        }
    }

    public void RefreshNow()
    {
        _ = DeferredRefreshAsync();
    }

    // Coalescing debounce: each Program/Bank message restarts a short timer, so
    // a CC0+CC32+PC burst fires a single refresh ~PerfRefreshDebounceMs later.
    // The user-activity guard still skips refreshes during active app-driven
    // interaction (which would otherwise freeze the video stream mid-drag);
    // external changes on the Kronos have no app activity, so they follow fast.
    async Task DeferredRefreshAsync()
    {
        var cts  = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _refreshDebounceCts, cts);
        try { prev?.Cancel(); prev?.Dispose(); } catch { }

        try { await Task.Delay(PerfRefreshDebounceMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        Interlocked.CompareExchange(ref _refreshDebounceCts, null, cts);
        cts.Dispose();

        if ((DateTime.Now - _lastUserActivity).TotalSeconds < SysExDeferralSec)
            return;
        try { _perfPollDelayCts?.Cancel(); } catch { }
    }

    public void NotifyUserActivity()
    {
        _lastUserActivity = DateTime.Now;
    }

    async Task ProbeAsync(CancellationToken ct)
    {
        var transport = _transport;
        if (transport == null) return;

        try
        {
            bool capable = await transport.ProbeAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            if (capable)
            {
                var md = transport.LastModeData;
                if (md.HasValue)
                {
                    int stateMode = md.Value.ToStateMode();
                    if (stateMode > 0)
                        await _dispatcher.InvokeAsync(() => InitialModeDetected?.Invoke(stateMode))
                            .Task.ConfigureAwait(false);
                }
            }

            IsAvailable = capable;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[sysex-service] probe exception: {ex.Message}");
            IsAvailable = false;
        }
    }

    async Task PerfMetadataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isAvailable)
        {
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        bool firstPoll = true;

        while (!ct.IsCancellationRequested)
        {
            bool shouldPoll = firstPoll || (DateTime.Now - _lastUserActivity).TotalSeconds >= SysExDeferralSec;
            firstPoll = false;

            if (shouldPoll)
            {
                var transport = _transport;
                if (transport != null && _isAvailable)
                {
                    try
                    {
                        var info = await transport.RequestPerformanceIdAsync(timeoutMs: 1200)
                            .ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;

                        if (info != null)
                        {
                            var name = await transport.RequestCurrentNameAsync(info.Value.Type, timeoutMs: 1200)
                                .ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;

                            var perf = name != null ? info.Value with { Name = name } : info.Value;
                            var display = perf.ToDisplayString();
                            if (display != PerformanceDisplay)
                                AppLog.Info($"[sysex] current performance: {display}");
                            PerformanceDisplay = display;
                        }
                        else
                        {
                            PerformanceDisplay = "";
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug($"[sysex-service] perf metadata poll error: {ex.Message}");
                    }
                }
            }

            // Proactive: repeat on fixed interval; otherwise park until DeferredRefreshAsync wakes us.
            int delayMs = _proactivePoll ? _pollIntervalSec * 1000 : Timeout.Infinite;
            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _perfPollDelayCts = delayCts;
                await Task.Delay(delayMs, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
            }
            finally
            {
                _perfPollDelayCts = null;
            }
        }
    }

    public async Task<bool> SendMidiAsync(string hexBytes)
    {
        var decoded = MidiStreamMonitor.DecodeHex(hexBytes);
        SysExTraffic?.Invoke(new SysExTrafficEntry(DateTime.Now, true, decoded, IsMidi: true));

        var resp = await CtrlClient.QueryAsync(_host, _ctrlPort, $"MIDI_SEND {hexBytes}", timeoutMs: 2000)
            .ConfigureAwait(false);

        bool ok = resp?.TrimEnd() == "OK";
        if (!ok)
            SysExTraffic?.Invoke(new SysExTrafficEntry(DateTime.Now, false, resp?.Trim() ?? "ERR", IsMidi: true));
        return ok;
    }

    void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _dispatcher.InvokeAsync(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
