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

        // MODE_LIT/PAGE_LIT (Nautilus only) - absent must stay null, not false: "the daemon
        // never reported these" is not "the LEDs are off", and only the former leaves the
        // front-panel lamps alone. MODE_LIT= must also never be read as MODE=.
        Check("state-lit-absent", valid is { ModeLit: null, PageLit: null });
        var lit = ScreenSession.ParseDaemonState("MODE=2 EDITCTX=0 BOOT=0 MODE_LIT=1 PAGE_LIT=0");
        Check("state-lit-present", lit is { Mode: Mode.Combi, ModeLit: true, PageLit: false });
        Check("state-lit-malformed",
            ScreenSession.ParseDaemonState("MODE=2 MODE_LIT=x PAGE_LIT=1") is { ModeLit: null, PageLit: true });

        var kronos = ScreenSession.ParseModelInfo("FAMILY=KRONOS MODEL=KRONOS2 FB_BPP=8");
        Check("model-kronos", kronos is { Family: "KRONOS", Model: "KRONOS2", FbBpp: 8 });
        var nautilus = ScreenSession.ParseModelInfo("FAMILY=NAUTILUS MODEL=NAUTILUS FB_BPP=16");
        Check("model-nautilus", nautilus is { Family: "NAUTILUS", Model: "NAUTILUS", FbBpp: 16 });
        Check("model-null-response", ScreenSession.ParseModelInfo(null) == null);
        Check("model-missing-family", ScreenSession.ParseModelInfo("MODEL=KRONOS2 FB_BPP=8") == null);

        Check("cal-reply", ScreenSession.ParseCalibrationReply("CAL G=5 M=1,2,-3,4 D=\n") == "G=5 M=1,2,-3,4 D=");
        Check("cal-reply-none", ScreenSession.ParseCalibrationReply("CAL NONE\n") == "");
        Check("cal-reply-old-daemon", ScreenSession.ParseCalibrationReply("ERR\n") == null);
        Check("cal-reply-null", ScreenSession.ParseCalibrationReply(null) == null);

        var mesh = new CalMesh(4, 4);
        mesh.SetOffset(1, 2, -3, 4);
        mesh.SetOffset(3, 3, 7, -1);
        var text = CalText.Serialize(mesh, [new CalBiasDot(10, 20), new CalBiasDot(-5, 799)]);
        Check("cal-text-format", text == "G=4 M=1,2,-3,4;3,3,7,-1 D=10,20;-5,799");
        var back = CalText.Parse(text);
        Check("cal-text-roundtrip", back is { } rt && rt.mesh.Cols == 4
            && rt.mesh.GetOffset(1, 2) == (-3, 4) && rt.mesh.GetOffset(3, 3) == (7, -1)
            && rt.mesh.GetOffset(0, 0) == (0, 0) && rt.dots.Count == 2 && rt.dots[1] == new CalBiasDot(-5, 799));
        Check("cal-text-identity", CalText.Parse(CalText.Serialize(new CalMesh(), [])) is { } id
            && id.mesh.IsIdentity() && id.dots.Count == 0);
        Check("cal-text-bad-grid", CalText.Parse("G=9 M= D=") == null);
        Check("cal-text-node-out-of-range", CalText.Parse("G=3 M=3,0,1,1 D=") == null);
        Check("cal-text-garbage", CalText.Parse("G=5 M=1,2 D=") == null);

        // MIDI Monitor decode must read the func byte from the right offset for each family's
        // Exclusive Header length (4 bytes Kronos, 6 Nautilus) - see MidiStreamMonitor.DecodeMidi.
        var kronosMsg   = MidiHex.ToBytes("F0 42 30 68 42 00 00 00 F7")!;
        var nautilusMsg = MidiHex.ToBytes("F0 42 30 00 01 5D 73 00 00 00 00 06 F7")!;
        Check("decode-kronos-func",
            MidiStreamMonitor.DecodeMidi(kronosMsg, DeviceFamily.Kronos).Contains("func=42"));
        Check("decode-nautilus-func",
            MidiStreamMonitor.DecodeMidi(nautilusMsg, DeviceFamily.Nautilus).Contains("func=73"));
        Check("decode-nautilus-as-kronos-no-false-func",
            !MidiStreamMonitor.DecodeMidi(nautilusMsg, DeviceFamily.Kronos).Contains("func="));

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

        var ctrl = new FakeCtrlClient("MODE=3 EDITCTX=1 BOOT=0");
        using var session = new ScreenSession(ctrl, _ => receivers.Dequeue());
        session.Connected += info =>
        {
            if (Interlocked.Increment(ref connectionCount) == 1) firstConnected.TrySetResult(info);
            else secondConnected.TrySetResult(info);
        };
        session.StateReceived += state => stateReceived.TrySetResult(state);
        session.Disconnected += _ => Interlocked.Increment(ref disconnectCount);

        var connection = new ScreenConnection("fake", 7373, false, 15, "user", "pass");
        session.Start(connection);
        try
        {
            await firstConnected.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Check("session-change-mode-initial-refresh",
                ctrl.SentCommands.SequenceEqual(new[] { DaemonCommand.RefreshDisplay }));
            var frame = new byte[4];
            Check("session-frame-access", session.TryCopyLatestFrame(frame) &&
                                          frame.SequenceEqual(new byte[] { 1, 2, 3, 4 }));

            var state = await stateReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Check("session-state-event", state is
                { Mode: Mode.Program, EditContext: EditContext.ProgramFromCombi, Booting: false });

            session.Start(connection);
            await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Check("session-change-mode-reconnect-refresh",
                ctrl.SentCommands.SequenceEqual(new[]
                {
                    DaemonCommand.RefreshDisplay,
                    DaemonCommand.RefreshDisplay,
                }));
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
        public StreamPixelFormat Fmt => StreamPixelFormat.Index8;
        public int Bpp => 8;
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
        public List<string> SentCommands { get; } = new();
        public event Action<string>? CtrlError { add { } remove { } }
        public void Send(string cmd) => SentCommands.Add(cmd);
        public void Reset() { }
        public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000) => Task.FromResult<string?>(response);
    }
}
