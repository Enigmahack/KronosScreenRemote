namespace KronosScreenRemote;

// Off-hardware self-test for MidiTransportReplyExtensions.AwaitReplyAsync - the shared reply-
// await scaffold behind SysExDumpCollector / SysExService. These four methods had NO behavioral
// coverage (nothing constructs an IKronosMidiTransport under --librarian-selftest), which let a
// real regression slip in unseen: a `where T : notnull` version returned Task<int> for the int
// callers, so a TIMEOUT yielded reply-code 0 (= success) instead of null - a never-answered
// Store Bank would have reported success. This locks the timeout/failure semantics down for the
// value-type (int, ProgramBankTypes) AND reference-type (byte[]) instantiations. Wired into
// App.xaml.cs's --librarian-selftest.
static class MidiTransportReplySelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var t = new FakeTransport();

        // Every await uses ConfigureAwait(false): unlike the other self-tests (whose in-memory
        // fakes return already-completed Tasks and so never actually suspend), these genuinely
        // go async on Task.Delay, and App.xaml.cs blocks the UI thread on .GetResult() - so a
        // captured UI SynchronizationContext would deadlock the continuation.

        // The core regression: a timeout must return null for EVERY T - value or reference - not
        // default(T) (0 for int, a zeroed struct). `send` succeeds but no reply is ever raised.
        int? intTimeout = await t.AwaitReplyAsync<int>(() => Task.FromResult(true), _ => (int?)null, 1).ConfigureAwait(false);
        Check("int-timeout-is-null", intTimeout == null);

        ProgramBankTypes? structTimeout = await t.AwaitReplyAsync<ProgramBankTypes>(() => Task.FromResult(true), _ => null, 1).ConfigureAwait(false);
        Check("struct-timeout-is-null", structTimeout == null);

        byte[]? refTimeout = await t.AwaitReplyAsync<byte[]>(() => Task.FromResult(true), _ => null, 1).ConfigureAwait(false);
        Check("ref-timeout-is-null", refTimeout == null);

        // A send failure also returns null - even if `match` would have matched something.
        int? sendFailure = await t.AwaitReplyAsync<int>(() => Task.FromResult(false), _ => (int?)0, 1).ConfigureAwait(false);
        Check("int-send-failure-is-null", sendFailure == null);

        // Success path: a matching reply raised on the stream during the send resolves the await
        // with the parsed value (and 0 is a real success code, distinct from the timeout null).
        var reply = KronosSysEx.KorgMessage(0x24, 0x00);   // func 0x24 Reply, code 0
        var raising = new FakeTransport { RaiseOnSend = reply };
        int? code = await raising.AwaitReplyAsync<int>(() => raising.SendAsync(reply), KronosSysEx.ParseReply, 1000).ConfigureAwait(false);
        Check("reply-code-parsed", code == 0);

        // Through the real caller: a timed-out Store Bank must surface as null so its own callers'
        // `code ?? -1` reports FAILURE, never a phantom success. This is the exact data-safety
        // regression the notnull bug reintroduced one layer below where DataSafetySelfTests reach.
        var collector = new SysExDumpCollector(new FakeTransport());
        int? storeTimeout = await collector.SendAndAwaitReplyAsync(KronosSysEx.BuildStoreBankRequest(0, 0), 1).ConfigureAwait(false);
        Check("collector-store-timeout-is-null", storeTimeout == null);

        return fails;
    }

    // Minimal IKronosMidiTransport: SendAsync optionally raises one message on the stream (for the
    // success path) then reports SendResult; everything else is an inert stub.
    sealed class FakeTransport : IKronosMidiTransport
    {
        public byte[]? RaiseOnSend;
        public bool SendResult = true;

#pragma warning disable CS0067   // Traffic/SysExActivity are part of the interface but unused here
        public event Action<SysExTrafficEntry>? Traffic;
        public event Action? SysExActivity;
#pragma warning restore CS0067
        public event Action<byte[]>? SysExMessageReceived;

        public string Description => "fake";
        public string CacheKey => "fake";
        public bool CanStream => true;
        public SysExModeData? LastModeData => null;

        public void Start() { }
        public void Stop() { }
        public void SetStreamEnabled(bool enabled) { }
        public Task<bool> ProbeAsync(int timeoutMs = 8000) => Task.FromResult(true);
        public Task<byte[]?> QueryAsync(byte[] request, byte? expectReplyFunc = null, int timeoutMs = 3000) =>
            Task.FromResult<byte[]?>(null);

        public Task<bool> SendAsync(byte[] message)
        {
            if (RaiseOnSend != null) SysExMessageReceived?.Invoke(RaiseOnSend);
            return Task.FromResult(SendResult);
        }

        public Task<bool> SendLargeSysExAsync(byte[] sysex) => SendAsync(sysex);
        public void Dispose() { }
    }
}
