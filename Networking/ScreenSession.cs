namespace KronosScreenRemote;

internal readonly record struct ScreenConnection(
    string Host, int Port, bool PullMode, int Fps, string FtpUsername, string FtpPassword);

// Calibration is the unit's stored touch calibration text (CAL_GET): "" when the unit has none,
// null when the daemon can't store one (older than 3.1.2) or the query failed.
internal readonly record struct ScreenSessionInfo(
    long Id, ScreenConnection Connection, int Width, int Height,
    StreamPixelFormat Fmt, int Bpp, PaletteEntry[] Palette,
    DeviceModelInfo? Model, string? Calibration);

// FAMILY is the trusted signal (derived from fb1 bpp, hardware-confirmed both ways);
// Model is the finer-grained /proc/cpuinfo-derived code and can be a "*_UNKNOWN" value
// or (rarely, per the daemon's own MODEL command docs) disagree with Family on a
// MODEL CONFLICT - callers that need to branch on device type should key off Family.
internal readonly record struct DeviceModelInfo(string Family, string Model, int FbBpp);

// ModeLit/PageLit are the Nautilus MODE/PAGE front-panel LEDs (daemon 3.1.0+). null means the
// daemon didn't report them at all - a Kronos, or a Nautilus whose mode_page_hook.ko failed to
// load - which is not the same as "reported off" and must not light anything.
internal readonly record struct ScreenDaemonState(
    long Id, Mode Mode, EditContext EditContext, bool Booting, bool? ModeLit, bool? PageLit);

internal readonly record struct ScreenSessionFailure(long Id, Exception? Error);

// Owns the daemon stream lifetime and STATE polling; UI work stays with MainWindow.
internal sealed class ScreenSession : IDisposable
{
    const int ModePollIntervalMs = 500;

    readonly object _gate = new();
    readonly Func<ScreenConnection, IStreamReceiver> _receiverFactory;
    ICtrlClient _ctrl;
    CancellationTokenSource? _connectCts;
    CancellationTokenSource? _pollCts;
    IStreamReceiver? _receiver;
    long _id;

    public event Action<ScreenSessionInfo>? Connected;
    public event Action<long>? Disconnected;
    public event Action<ScreenDaemonState>? StateReceived;
    public event Action<ScreenSessionFailure>? ConnectionFailed;

    public ScreenSession(ICtrlClient ctrl, Func<ScreenConnection, IStreamReceiver>? receiverFactory = null)
    {
        _ctrl = ctrl;
        _receiverFactory = receiverFactory ?? (connection => new StreamReceiver(
            connection.Host, connection.Port, connection.PullMode, connection.Fps,
            connection.FtpUsername, connection.FtpPassword));
    }

    public void SetCtrlClient(ICtrlClient ctrl)
    {
        lock (_gate) _ctrl = ctrl;
    }

    public void Start(ScreenConnection connection)
    {
        var (connect, poll, receiver, ctrl) = StopCurrent();
        CancelAndDispose(connect, poll, receiver, ctrl);

        CancellationTokenSource cts;
        long id;
        lock (_gate)
        {
            cts = new CancellationTokenSource();
            _connectCts = cts;
            id = _id;
        }
        _ = Task.Run(() => ConnectAsync(id, connection, cts));
    }

    public void Disconnect()
    {
        var (connect, poll, receiver, ctrl) = StopCurrent();
        CancelAndDispose(connect, poll, receiver, ctrl);
    }

    public bool TryCopyLatestFrame(byte[] destination)
    {
        IStreamReceiver? receiver;
        lock (_gate) receiver = _receiver;
        return receiver?.TryCopyLatestFrame(destination) == true;
    }

    public bool IsCurrent(long id)
    {
        lock (_gate) return _id == id && _receiver != null;
    }

    public bool IsLatest(long id)
    {
        lock (_gate) return _id == id;
    }

    async Task ConnectAsync(long id, ScreenConnection connection, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var receiver = _receiverFactory(connection);
            // StreamReceiver's receive thread starts at the tail of its own handshake, BEFORE
            // ConnectAsync's await below returns control here - so an immediate post-handshake
            // disconnect can fire this event while `receiver` isn't `_receiver` yet.
            // ReceiverDisconnected bails out in that case (nothing to compare against), so
            // latch it here instead: the publish step below checks it and, if set, treats this
            // receiver as already-dead rather than publishing it and reporting Connected.
            int earlyDisconnect = 0;
            receiver.Disconnected += () =>
            {
                Interlocked.Exchange(ref earlyDisconnect, 1);
                ReceiverDisconnected(id, receiver);
            };
            try
            {
                await receiver.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                receiver.Dispose();
                return;
            }
            catch (Exception ex)
            {
                receiver.Dispose();
                if (!ct.IsCancellationRequested) Fail(id, cts, ex);
                return;
            }

            bool published;
            lock (_gate)
            {
                published = _id == id && ReferenceEquals(_connectCts, cts) && !ct.IsCancellationRequested
                            && Volatile.Read(ref earlyDisconnect) == 0;
                if (published) _receiver = receiver;
            }
            if (!published)
            {
                receiver.Dispose();
                // Replay the early disconnect as a real connect failure - it fired before this
                // receiver was published, so ReceiverDisconnected's own bail-out means nothing
                // else will ever tell the caller this attempt didn't actually succeed.  Fail()
                // itself already no-ops if a newer attempt has since superseded this one.
                if (Volatile.Read(ref earlyDisconnect) != 0 && !ct.IsCancellationRequested)
                    Fail(id, cts, null);
                return;
            }

            ICtrlClient ctrlForModel;
            lock (_gate) ctrlForModel = _ctrl;
            if (!connection.PullMode) ctrlForModel.Send(DaemonCommand.RefreshDisplay);

            // MODEL is on the daemon's read-only allowlist and answered from any IP even
            // before ownership is established, but this connection just became the owner
            // regardless - safe to query once here, right after the stream handshake, per
            // the daemon docs ("computed once at startup... safe to poll cheaply, or just
            // read once per connection").
            var model = ParseModelInfo(
                await ctrlForModel.QueryAsync(DaemonCommand.QueryModel).ConfigureAwait(false));
            var calibration = ParseCalibrationReply(
                await ctrlForModel.QueryAsync(DaemonCommand.QueryCalibration).ConfigureAwait(false));

            Connected?.Invoke(new ScreenSessionInfo(
                id, connection, receiver.Width, receiver.Height,
                receiver.Fmt, receiver.Bpp, receiver.Palette, model, calibration));
            StartPolling(id, receiver);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    void StartPolling(long id, IStreamReceiver receiver)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_id != id || !ReferenceEquals(_receiver, receiver)) return;
            cts = new CancellationTokenSource();
            _pollCts = cts;
        }
        _ = PollStateAsync(id, receiver, cts);
    }

    async Task PollStateAsync(long id, IStreamReceiver receiver, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                ICtrlClient ctrl;
                lock (_gate)
                {
                    if (_id != id || !ReferenceEquals(_receiver, receiver)) return;
                    ctrl = _ctrl;
                }

                var state = ParseDaemonState(await ctrl.QueryAsync(DaemonCommand.QueryState).ConfigureAwait(false));
                lock (_gate)
                {
                    if (_id != id || !ReferenceEquals(_receiver, receiver) || !ReferenceEquals(_ctrl, ctrl))
                        return;
                }
                if (state is { } s)
                    StateReceived?.Invoke(new ScreenDaemonState(
                        id, s.Mode, s.EditContext, s.Booting, s.ModeLit, s.PageLit));

                await Task.Delay(ModePollIntervalMs, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    }

    void ReceiverDisconnected(long id, IStreamReceiver receiver)
    {
        CancellationTokenSource? connect;
        CancellationTokenSource? poll;
        ICtrlClient? ctrl;
        IStreamReceiver? disconnected;
        lock (_gate)
        {
            if (_id != id || !ReferenceEquals(_receiver, receiver)) return;
            disconnected = _receiver;
            _receiver = null;
            connect = _connectCts;
            _connectCts = null;
            poll = _pollCts;
            _pollCts = null;
            ctrl = _ctrl;
        }
        CancelAndDispose(connect, poll, disconnected, ctrl);
        Disconnected?.Invoke(id);
    }

    void Fail(long id, CancellationTokenSource cts, Exception? error)
    {
        lock (_gate)
        {
            if (_id != id || !ReferenceEquals(_connectCts, cts)) return;
            _connectCts = null;
        }
        cts.Dispose();
        ConnectionFailed?.Invoke(new ScreenSessionFailure(id, error));
    }

    (CancellationTokenSource? Connect, CancellationTokenSource? Poll,
     IStreamReceiver? Receiver, ICtrlClient Ctrl) StopCurrent()
    {
        lock (_gate)
        {
            _id++;
            var current = (_connectCts, _pollCts, _receiver, _ctrl);
            _connectCts = null;
            _pollCts = null;
            _receiver = null;
            return current;
        }
    }

    static void CancelAndDispose(CancellationTokenSource? connect, CancellationTokenSource? poll,
                                 IStreamReceiver? receiver, ICtrlClient ctrl)
    {
        connect?.Cancel();
        poll?.Cancel();
        receiver?.Dispose();
        ctrl.Reset();
        connect?.Dispose();
        poll?.Dispose();
    }

    internal static (Mode Mode, EditContext EditContext, bool Booting, bool? ModeLit, bool? PageLit)?
        ParseDaemonState(string? response)
    {
        if (response == null) return null;

        int mode = -1, editContext = 0, boot = 1;
        bool? modeLit = null, pageLit = null;
        foreach (var token in response.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            if (equals <= 0) continue;
            var value = token[(equals + 1)..];
            // MODE_LIT/PAGE_LIT before MODE= - not strictly required ("MODE_LIT=" doesn't start
            // with "MODE="), but it keeps the two from ever depending on that coincidence.
            if (token.StartsWith("MODE_LIT=", StringComparison.Ordinal))
            {
                modeLit = int.TryParse(value, out int lit) ? lit != 0 : null;
            }
            else if (token.StartsWith("PAGE_LIT=", StringComparison.Ordinal))
            {
                pageLit = int.TryParse(value, out int lit) ? lit != 0 : null;
            }
            else if (token.StartsWith("MODE=", StringComparison.Ordinal))
            {
                if (!int.TryParse(value, out mode)) mode = -1;
            }
            else if (token.StartsWith("EDITCTX=", StringComparison.Ordinal))
            {
                if (!int.TryParse(value, out editContext)) editContext = 0;
            }
            else if (token.StartsWith("BOOT=", StringComparison.Ordinal))
            {
                if (!int.TryParse(value, out boot)) boot = 1;
            }
        }
        return mode >= 0
            ? ((Mode)mode, (EditContext)editContext, boot != 0, modeLit, pageLit)
            : null;
    }

    internal static DeviceModelInfo? ParseModelInfo(string? response)
    {
        if (response == null) return null;

        string? family = null, model = null;
        int fbBpp = 0;
        foreach (var token in response.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            if (equals <= 0) continue;
            var value = token[(equals + 1)..];
            if (token.StartsWith("FAMILY=", StringComparison.Ordinal)) family = value;
            else if (token.StartsWith("MODEL=", StringComparison.Ordinal)) model = value;
            else if (token.StartsWith("FB_BPP=", StringComparison.Ordinal))
            {
                if (!int.TryParse(value, out fbBpp)) fbBpp = 0;
            }
        }

        return family != null ? new DeviceModelInfo(family, model ?? "", fbBpp) : null;
    }

    internal static string? ParseCalibrationReply(string? response)
    {
        var line = response?.Trim();
        if (line == "CAL NONE") return "";
        return line != null && line.StartsWith("CAL ", StringComparison.Ordinal) ? line[4..] : null;
    }

    public void Dispose() => Disconnect();
}
