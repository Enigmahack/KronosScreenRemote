namespace KronosScreenRemote;

// Collects SysEx Object Dump (func 0x73) replies off the live MIDI stream.
//
// The daemon's SYSEX command captures only a single F0...F7 block and caps at
// 64 KB, so it can't return a multi-object bank dump or a large object (a full
// Set List is ~79 KB of SysEx). Instead we send the request fire-and-forget
// (IKronosMidiTransport.SendAsync) and gather the 0x73 replies off the
// transport's live stream (SysExMessageReceived), which broadcasts every message
// with no size limit - identically over the TCP (port-9875) and USB backends.
//
// Requires SysEx receive ("Enable Exclusive") on the Kronos - same prerequisite
// as every other SysEx feature here. Dumps are serialized through a gate so two
// callers can't interleave their 0x73 streams.
sealed class SysExDumpCollector
{
    readonly IKronosMidiTransport _transport;
    readonly SemaphoreSlim _gate = new(1, 1);

    public SysExDumpCollector(IKronosMidiTransport transport)
    {
        _transport = transport;
    }

    // Send a dump request (func 0x72 single object, or 0x77 whole bank) and
    // collect the matching func 0x73 replies.
    //   expectObj     - object-type byte to match in the 0x73 replies
    //   expectedCount - stop as soon as this many replies arrive (null = idle-only)
    //   idleMs        - after ≥1 reply, stop once this long passes with no new one
    //   noResponseMs  - give up if no SysEx activity at all within this long
    //   stallMs       - give up if activity started then stalled this long (mid-xfer)
    //   overallMs     - hard cap on the whole collection
    //
    // "Activity" (SysExActivity) pulses on SysEx start and every ~512 bytes, so a
    // slow multi-second object keeps the collector alive; only a real stall or
    // total silence ends it early.
    public async Task<List<byte[]>> CollectAsync(
        string requestHex, byte expectObj, int? expectedCount,
        int idleMs = 600, int noResponseMs = 4000, int stallMs = 4000, int overallMs = 60000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var  results       = new List<byte[]>();
        long lastMatchTicks = 0;
        long lastActTicks   = 0;
        int  activity       = 0;
        int  rejectCode     = -1;   // last func 0x24 Reply code seen (−1 = none)

        void OnMsg(byte[] m)
        {
            if (KronosSysEx.HasKorgHeaderAt(m, 0, 0x73) && m.Length >= 6 && m[5] == expectObj)
            {
                lock (results) results.Add(m);
                Volatile.Write(ref lastMatchTicks, DateTime.Now.Ticks);
                Volatile.Write(ref lastActTicks,   DateTime.Now.Ticks);
            }
            // Reply (func 0x24): the Kronos rejects a dump request with a code -
            // 4 = "target object not found" (empty/absent bank). Capturing it makes
            // "rejected" distinct from "silent" in the sweep log.
            else if (KronosSysEx.HasKorgHeaderAt(m, 0, 0x24) && m.Length >= 6)
                Volatile.Write(ref rejectCode, m[5] & 0x7F);
        }
        void OnActivity()
        {
            Volatile.Write(ref activity, 1);
            Volatile.Write(ref lastActTicks, DateTime.Now.Ticks);
        }

        _transport.SysExMessageReceived += OnMsg;
        _transport.SysExActivity        += OnActivity;
        string exit = "overall";   // which loop condition ended the collection
        try
        {
            var reqBytes = MidiHex.ToBytes(requestHex);
            bool sent = reqBytes != null && await _transport.SendAsync(reqBytes).ConfigureAwait(false);
            if (!sent)
            {
                AppLog.Warn($"[sysex-dump] send failed for request: {requestHex}");
                return results;
            }

            var start = DateTime.Now;
            while (true)
            {
                await Task.Delay(50).ConfigureAwait(false);
                var now = DateTime.Now;
                int c; lock (results) c = results.Count;

                if (expectedCount.HasValue && c >= expectedCount.Value) { exit = "count"; break; }
                if (c > 0 && Elapsed(now, Volatile.Read(ref lastMatchTicks)) > idleMs) { exit = "idle"; break; }
                // A Reply (func 0x24) with no matching objects = the Kronos declined
                // this request (e.g. code 4 after the func-0x77 dump path exhausts).
                // End immediately instead of waiting out stallMs - the caller retries
                // after a rest. NOTE: callers that rely on this fast exit MUST insert
                // their own recovery pause; the old slow stall used to be the rest.
                if (c == 0 && Volatile.Read(ref rejectCode) >= 0) { exit = "reject"; break; }
                if (Volatile.Read(ref activity) == 0)
                {
                    if ((now - start).TotalMilliseconds > noResponseMs) { exit = "silence"; break; }   // total silence
                }
                else if (c == 0 && Elapsed(now, Volatile.Read(ref lastActTicks)) > stallMs) { exit = "stall"; break; }  // started then stalled
                if ((now - start).TotalMilliseconds > overallMs) { exit = "overall"; break; }          // hard cap
            }
        }
        finally
        {
            _transport.SysExMessageReceived -= OnMsg;
            _transport.SysExActivity        -= OnActivity;
            _gate.Release();
        }

        int got; lock (results) got = results.Count;
        int rej = Volatile.Read(ref rejectCode);
        // exit=idle/count → data arrived; silence → nothing came back at all;
        // stall → activity but no matching object; reject=4 → Kronos said "not found".
        AppLog.Info($"[sysex-dump] obj={expectObj:X2} collected {got} object(s) " +
                    $"(exit={exit} reject={(rej < 0 ? "none" : rej.ToString())})");
        lock (results) return new List<byte[]>(results);
    }

    static double Elapsed(DateTime now, long ticks) =>
        (now - new DateTime(ticks)).TotalMilliseconds;

    // Pull a whole bank's names one object at a time via func 0x72 (single Object
    // Dump Request), for WRITABLE banks whose func-0x77 whole-bank name ENUM the
    // firmware rejects. That enum is preset-only (INT/GM); it returns Reply code 4
    // for every user bank. But a per-object func-0x72 name fetch works for EVERY
    // bank - confirmed on hardware at a full 128/128 for USER-A, with no per-object
    // session throttle (the old "~13 banks/session" ceiling was the preset-only
    // enum rejecting user banks, not a real cap).
    //
    // PACED, never bursted: firing requests back-to-back overruns the Kronos MIDI-in
    // - it drops every reply AND can corrupt a request (losing its F7), popping a
    // user-facing "MIDI Receiving Error". A ~10 ms send spacing streams a clean
    // 128/128. Names are cached by SysExService.ParseIncoming off the same stream;
    // this returns the indices that replied plus whether the pull CONVERGED (a full
    // pass added no new name), so the caller marks a bank done only when complete.
    // Convergence is a safe "done" signal precisely because there is no throttle to
    // masquerade as "nothing new".
    public async Task<(HashSet<int> Replied, bool Converged)> CollectPerObjectNamesAsync(
        byte obj, int bank, int count,
        IProgress<int>? progress, CancellationToken ct,
        int batchSize = 32, int batchIdleMs = 350, int batchMaxMs = 2500, int maxPasses = 3)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var  replied        = new HashSet<int>();
        long lastReplyTicks = 0;

        void OnMsg(byte[] m)
        {
            // Object Dump reply: F0 42 3g 68 73 <obj> <bank> <idH> <idL> ...
            if (KronosSysEx.HasKorgHeaderAt(m, 0, 0x73) && m.Length >= 9 && m[5] == obj && m[6] == bank)
            {
                int idx = (m[7] << 7) | (m[8] & 0x7F);
                bool added;
                lock (replied) added = replied.Add(idx);
                Volatile.Write(ref lastReplyTicks, DateTime.Now.Ticks);
                if (added) { int c; lock (replied) c = replied.Count; progress?.Report(c); }
            }
        }

        _transport.SysExMessageReceived += OnMsg;
        bool converged = false;
        try
        {
            for (int pass = 0; pass < maxPasses && !ct.IsCancellationRequested; pass++)
            {
                // Indices still missing at the start of this pass (re-request only those).
                var missing = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    bool have; lock (replied) have = replied.Contains(i);
                    if (!have) missing.Add(i);
                }
                if (missing.Count == 0) { converged = true; break; }

                int before; lock (replied) before = replied.Count;

                // Send in BATCHES (many func-0x72 requests concatenated into one
                // MIDI_SEND) - ~32 keeps a batch under the daemon's 2 KB ctrl line and
                // holds connection churn to ~4/bank instead of one TCP connect per
                // object (which hammers the tiny on-Kronos daemon). Drain each batch
                // before the next: a back-to-back flood overruns the Kronos MIDI-in,
                // drops every reply, and can corrupt a request into a "MIDI Receiving
                // Error" dialog. Batched-then-drained is HW-verified at a clean 128/128.
                for (int start = 0; start < missing.Count && !ct.IsCancellationRequested; start += batchSize)
                {
                    int end = Math.Min(start + batchSize, missing.Count);
                    var sb = new System.Text.StringBuilder();
                    for (int j = start; j < end; j++)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(ObjectDumpRequest(obj, bank, missing[j]));
                    }
                    var batchBytes = MidiHex.ToBytes(sb.ToString());
                    if (batchBytes != null)
                        await _transport.SendAsync(batchBytes).ConfigureAwait(false);

                    // Wait for this batch's replies to arrive and go idle (idle = batch
                    // done), capped by batchMaxMs. A populated bank ends each batch at
                    // idle in ~0.3 s; only a bank returning nothing waits the full cap.
                    Volatile.Write(ref lastReplyTicks, 0);
                    var t0 = DateTime.Now;
                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(25, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        long lr = Volatile.Read(ref lastReplyTicks);
                        if (lr != 0 && Elapsed(DateTime.Now, lr) > batchIdleMs) break;  // came then idled
                        if ((DateTime.Now - t0).TotalMilliseconds > batchMaxMs) break;  // slow/absent cap
                    }

                    // Absent-bank early out AFTER a full generous batchMaxMs wait - so a
                    // real bank's first-reply latency can never be mistaken for "absent"
                    // (a warm monitor answers in ~tens of ms, far under the cap). Zero
                    // replies to a whole first batch = nothing here; stop pacing 128.
                    if (pass == 0 && start == 0)
                    {
                        int got; lock (replied) got = replied.Count;
                        if (got == 0) { converged = true; break; }
                    }
                }
                if (converged) break;

                int after; lock (replied) after = replied.Count;
                if (after == before) { converged = true; break; }   // a full pass added nothing → done
            }
        }
        finally
        {
            _transport.SysExMessageReceived -= OnMsg;
            _gate.Release();
        }

        int total; lock (replied) total = replied.Count;
        AppLog.Info($"[sysex-dump] per-object obj={obj:X2} bank={bank:X2} got {total}/{count} " +
                    $"name(s) (converged={converged})");
        lock (replied) return (new HashSet<int>(replied), converged);
    }

    // ── Writes (Object Dump send + Store Bank Request) ──────────────────────────
    //
    // MIDI_SEND is fire-and-forget on the daemon's ctrl port - there's no
    // synchronous response to a write the way KronosSysEx's SYSEX command has for
    // reads. The Kronos's func 0x24 Reply comes back asynchronously on the live
    // stream instead, so these await it there, the same way CollectAsync watches
    // for 0x73 replies. Only small, directly-addressed sub-objects (e.g. Set List
    // Slot Name/Comments) go through here - see BuildObjectDumpMessage's caveat
    // about the daemon's 4096-byte MIDI_SEND cap.

    // Send a SysEx message and wait for the next func 0x24 Reply on the live
    // stream. Returns the Reply Code (0 = success), or null on send failure or
    // timeout (no Reply arrived - e.g. SysEx receive disabled on the Kronos).
    public async Task<int?> SendAndAwaitReplyAsync(byte[] message, int timeoutMs = 4000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _transport.AwaitReplyAsync<int>(
                () => _transport.SendAsync(message),
                KronosSysEx.ParseReply,
                timeoutMs).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Object Dump write (func 0x73) for a small, directly-addressed sub-object.
    public Task<int?> SendObjectDumpAsync(int obj, int bank, int index, byte version, byte[] binaryData, int timeoutMs = 4000) =>
        SendAndAwaitReplyAsync(KronosSysEx.BuildObjectDumpMessage(obj, bank, index, version, binaryData), timeoutMs);

    // Send a LARGE Object Dump (func 0x73) - a full Combi (~8.9 KB) or Set List
    // (~79 KB) that exceeds the daemon's per-MIDI_SEND cap - and await the func 0x24
    // Reply on the live stream. Uses the transport's backend-aware large send
    // (TCP chunks across MIDI_SEND; USB one long message). Longer default timeout:
    // a big object plus (over TCP) many chunk round-trips take a few seconds before
    // the Kronos replies. Returns the Reply Code (0 = success) or null on timeout.
    public async Task<int?> SendLargeObjectDumpAndAwaitReplyAsync(byte[] message, int timeoutMs = 8000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _transport.AwaitReplyAsync<int>(
                () => _transport.SendLargeSysExAsync(message),
                KronosSysEx.ParseReply,
                timeoutMs).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Store Bank Request (func 0x76) - commits previously-sent Object Dump data.
    public Task<int?> SendStoreBankRequestAsync(int obj, int bank, int timeoutMs = 4000) =>
        SendAndAwaitReplyAsync(KronosSysEx.BuildStoreBankRequest(obj, bank), timeoutMs);

    // Change Program Bank Type (func 0x7C) - reformats+ERASES the bank to HD-1/EXi. Longer
    // default timeout: reformatting a bank on the instrument takes noticeably longer than a
    // plain Store before the func 0x24 Reply comes back.
    public Task<int?> SendChangeProgramBankTypeAsync(int bank, bool isExi, int timeoutMs = 20000) =>
        SendAndAwaitReplyAsync(KronosSysEx.BuildChangeProgramBankType(bank, isExi), timeoutMs);

    // ── Request builders (Korg header F0 42 30 68, matching existing convention) ──

    // Object Dump Request (func 0x72): one specific object.
    public static string ObjectDumpRequest(int obj, int bank, int index) =>
        $"F0 42 30 68 72 {obj:X2} {bank:X2} {(index >> 7) & 0x7F:X2} {index & 0x7F:X2} F7";

    // Dump Bank Request (func 0x77): every object of a type in a bank.
    public static string DumpBankRequest(int obj, int bank) =>
        $"F0 42 30 68 77 {obj:X2} {bank:X2} F7";
}
