namespace KronosScreenRemote;

internal readonly record struct ScreenConnection(
    string Host, int Port, bool PullMode, int Fps, string FtpUsername, string FtpPassword);

internal readonly record struct ScreenSessionInfo(
    long Id, ScreenConnection Connection, int Width, int Height, PaletteEntry[] Palette);

internal readonly record struct ScreenDaemonState(long Id, Mode Mode, EditContext EditContext, bool Booting);

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

    public void Start(ScreenConnection connection, Func<CancellationToken, Task<bool>> ensureFtpLogin)
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
        _ = Task.Run(() => ConnectAsync(id, connection, ensureFtpLogin, cts));
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

    async Task ConnectAsync(long id, ScreenConnection connection,
                            Func<CancellationToken, Task<bool>> ensureFtpLogin,
                            CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (!await ensureFtpLogin(ct).ConfigureAwait(false))
            {
                if (!ct.IsCancellationRequested) Fail(id, cts, null);
                return;
            }
            if (ct.IsCancellationRequested) return;

            var receiver = _receiverFactory(connection);
            receiver.Disconnected += () => ReceiverDisconnected(id, receiver);
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
                published = _id == id && ReferenceEquals(_connectCts, cts) && !ct.IsCancellationRequested;
                if (published) _receiver = receiver;
            }
            if (!published)
            {
                receiver.Dispose();
                return;
            }

            Connected?.Invoke(new ScreenSessionInfo(
                id, connection, receiver.Width, receiver.Height, receiver.Palette));
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
                    StateReceived?.Invoke(new ScreenDaemonState(id, s.Mode, s.EditContext, s.Booting));

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

    internal static (Mode Mode, EditContext EditContext, bool Booting)? ParseDaemonState(string? response)
    {
        if (response == null) return null;

        int mode = -1, editContext = 0, boot = 1;
        foreach (var token in response.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            if (equals <= 0) continue;
            var value = token[(equals + 1)..];
            if (token.StartsWith("MODE=", StringComparison.Ordinal))
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
        return mode >= 0 ? ((Mode)mode, (EditContext)editContext, boot != 0) : null;
    }

    public void Dispose() => Disconnect();
}
