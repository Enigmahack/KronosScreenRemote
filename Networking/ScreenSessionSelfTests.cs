namespace KronosScreenRemote;

static class ScreenSessionSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool condition) { if (!condition) fails.Add(name); }

        var valid = ScreenSession.ParseDaemonState("MODE=3 EDITCTX=1 BOOT=0");
        Check("state-valid", valid is { Mode: Mode.Program, EditContext: EditContext.ProgramFromCombi, Booting: false });
        Check("state-missing-mode", ScreenSession.ParseDaemonState("EDITCTX=1 BOOT=0") == null);
        Check("state-malformed-mode", ScreenSession.ParseDaemonState("MODE=x BOOT=0") == null);

        var safe = ScreenSession.ParseDaemonState("MODE=3 EDITCTX=x");
        Check("state-safe-defaults", safe is { EditContext: EditContext.None, Booting: true });

        var first = new FakeReceiver();
        var second = new FakeReceiver();
        var receivers = new Queue<FakeReceiver>(new[] { first, second });
        var stateReceived = new TaskCompletionSource<ScreenDaemonState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstConnected = new TaskCompletionSource<ScreenSessionInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource<ScreenSessionInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int connectionCount = 0, disconnectCount = 0;

        using var session = new ScreenSession(
            new FakeCtrlClient("MODE=3 EDITCTX=1 BOOT=0"), _ => receivers.Dequeue());
        session.Connected += info =>
        {
            if (Interlocked.Increment(ref connectionCount) == 1) firstConnected.TrySetResult(info);
            else secondConnected.TrySetResult(info);
        };
        session.StateReceived += state => stateReceived.TrySetResult(state);
        session.Disconnected += _ => Interlocked.Increment(ref disconnectCount);

        var connection = new ScreenConnection("fake", 7373, false, 15, "user", "pass");
        session.Start(connection, _ => Task.FromResult(true));
        try
        {
            await firstConnected.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var frame = new byte[4];
            Check("session-frame-access", session.TryCopyLatestFrame(frame) &&
                                          frame.SequenceEqual(new byte[] { 1, 2, 3, 4 }));

            var state = await stateReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Check("session-state-event", state is
                { Mode: Mode.Program, EditContext: EditContext.ProgramFromCombi, Booting: false });

            session.Start(connection, _ => Task.FromResult(true));
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Check("replaced-receiver-disposed", first.Disposed);
            first.TriggerDisconnected();
            Check("stale-receiver-after-replace", Volatile.Read(ref disconnectCount) == 0);

            session.Disconnect();
            Check("disconnected-receiver-disposed", second.Disposed);
            second.TriggerDisconnected();
            Check("stale-receiver-after-disconnect", Volatile.Read(ref disconnectCount) == 0);
        }
        catch (TimeoutException)
        {
            fails.Add("session-lifecycle-timeout");
        }

        return fails;
    }

    sealed class FakeReceiver : IStreamReceiver
    {
        bool _hasFrame = true;
        public bool Disposed { get; private set; }

        public event Action? Disconnected;
        public int Width => 2;
        public int Height => 2;
        public PaletteEntry[] Palette { get; } = new PaletteEntry[256];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public bool TryCopyLatestFrame(byte[] destination)
        {
            if (!_hasFrame || destination.Length < 4) return false;
            new byte[] { 1, 2, 3, 4 }.CopyTo(destination, 0);
            _hasFrame = false;
            return true;
        }

        public void TriggerDisconnected() => Disconnected?.Invoke();
        public void Dispose() => Disposed = true;
    }

    sealed class FakeCtrlClient(string response) : ICtrlClient
    {
        public event Action<string>? CtrlError { add { } remove { } }
        public void Send(string cmd) { }
        public void Reset() { }
        public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000) => Task.FromResult<string?>(response);
    }
}
