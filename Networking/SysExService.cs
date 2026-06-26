using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace KronosScreenRemote;

sealed class SysExService : ISysExService
{
    const double SysExDeferralSec = 5.0;

    readonly Dispatcher _dispatcher;

    KronosSysEx? _transport;
    MidiStreamMonitor? _midiMonitor;
    CancellationTokenSource? _cts;
    CancellationTokenSource? _perfPollDelayCts;
    DateTime _lastUserActivity = DateTime.MinValue;
    string _host = "";
    int    _ctrlPort = CtrlClient.CtrlPort;

    string _performanceDisplay = "";
    bool _isAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? InitialModeDetected;
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
        _midiMonitor = new MidiStreamMonitor(host);
        _midiMonitor.Traffic += OnTransportTraffic;
        _midiMonitor.Start();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Task.Run(() => ProbeAsync(ct));
        _ = PerfMetadataLoop(ct);
    }

    public void Reset()
    {
        _cts?.Cancel();
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

    void OnTransportTraffic(SysExTrafficEntry entry) => SysExTraffic?.Invoke(entry);

    public void RefreshNow()
    {
        _ = DeferredRefreshAsync();
    }

    async Task DeferredRefreshAsync()
    {
        await Task.Delay(3000).ConfigureAwait(false);
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

        while (!ct.IsCancellationRequested)
        {
            if ((DateTime.Now - _lastUserActivity).TotalSeconds >= SysExDeferralSec)
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
                            PerformanceDisplay = perf.ToDisplayString();
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

            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _perfPollDelayCts = delayCts;
                await Task.Delay(60_000, delayCts.Token).ConfigureAwait(false);
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
