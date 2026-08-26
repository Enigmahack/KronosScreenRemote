namespace KronosScreenRemote;

using NAudio.Midi;
using System.Threading.Channels;

// MIDI transport over a directly-connected Kronos USB-MIDI device (NAudio/winmm).
// No daemon required - this is the standalone path that lets every SysEx feature
// work with the Kronos plugged in over USB and no network/video connection.
//
// Inbound design: winmm delivers short messages (MIM_DATA) and SysEx chunks
// (MIM_LONGDATA) on its callback thread. Both are funnelled, in arrival order,
// into the SAME MidiStreamParser the TCP monitor uses - so SysEx that spans
// multiple driver buffers (a ~79 KB Set List dump) is reassembled to one F0...F7
// message and the ~512 B activity pulses fire identically to the TCP backend.
// NAudio 2.2.1 re-adds each SysEx buffer after MIM_LONGDATA, so long dumps aren't
// capped at the buffer count.
//
// Prerequisite on the Kronos: "Enable Exclusive" (SysEx Rx) plus MIDI transmit -
// the same requirement as the TCP backend. If the Kronos doesn't route unsolicited
// messages to USB, request/reply still works but some follow-features degrade to
// poll-only; ProbeAsync surfaces that (no reply → not capable).
sealed class UsbMidiTransport : IKronosMidiTransport
{
    // Buffers large enough that a Set List dump needs only a few, with headroom so
    // the driver always has a queued buffer while one is being processed/re-added.
    const int SysexBufferSize  = 16 * 1024;
    const int SysexBufferCount = 4;

    // Small spacing between messages of a batched send (the dump collector
    // concatenates up to ~32 requests). Back-to-back SysEx can overrun the Kronos
    // MIDI-in; this paces them. Tune against hardware - USB is faster than the
    // daemon path these constants were originally set for.
    const int SendSpacingMs = 3;

    readonly string _match;                 // device-name substring, e.g. "KRONOS"
    readonly object _sendLock  = new();      // guards _out use vs Stop() disposal
    readonly object _replyLock = new();      // guards the correlation state below
    readonly SemaphoreSlim _queryGate = new(1, 1);
    readonly MidiStreamParser _parser = new();

    MidiIn?  _in;
    MidiOut? _out;
    string   _resolvedName = "";
    volatile bool _open;

    // Off-thread hand-off for the heavy per-message work. winmm delivers inbound on
    // its callback thread (under the parser lock); building a hex string / firing UI
    // traffic there holds the driver callback and, at USB memory-dump speed, drops
    // SysEx buffers mid-dump. The callback does only cheap, prompt work inline
    // (dump-collector feed + round-trip correlation) and drops each finished message
    // here for the consumer task to decode and surface.
    Channel<byte[]>? _handoff;

    // In-flight round-trip correlation (one query at a time via _queryGate). Read on
    // the winmm callback thread, written on the caller thread - all access under
    // _replyLock so the callback never sees a torn/stale (reply, func) pair.
    TaskCompletionSource<byte[]?>? _pendingReply;
    byte? _pendingFunc;

    public event Action<SysExTrafficEntry>? Traffic;
    public event Action<byte[]>? SysExMessageReceived;
    public event Action? SysExActivity;

    public string Description => _open ? $"USB: {_resolvedName}" : $"USB: {_match} (offline)";
    public string CacheKey    => $"usb:{_match}";
    public bool   CanStream   => _open;
    public SysExModeData? LastModeData { get; private set; }

    public UsbMidiTransport(string match)
    {
        _match = string.IsNullOrWhiteSpace(match) ? KronosMidiDevices.DefaultMatch : match;
        _parser.MessageReceived += OnParsedMessage;
        _parser.SysExActivity   += () => SysExActivity?.Invoke();
        _parser.SysExAborted    += (n, b) =>
            AppLog.Debug($"[usb-midi] SysEx ABORTED after {n} B by status 0x{b:X2} (reassembly discarded)");
    }

    public void Start()
    {
        if (_open) return;

        lock (_parser) _parser.Reset();   // clear any partial SysEx left from a prior session

        int inIdx  = KronosMidiDevices.FindInputIndex(_match);
        int outIdx = KronosMidiDevices.FindOutputIndex(_match);
        if (inIdx < 0 || outIdx < 0)
        {
            AppLog.Warn($"[usb-midi] Kronos '{_match}' not found (in={inIdx} out={outIdx}) - not opening");
            return;
        }

        // Start the consumer before the driver so it's ready for the first callback.
        var handoff = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        _handoff = handoff;
        _ = Task.Run(() => ConsumeAsync(handoff.Reader));

        try
        {
            _out = new MidiOut(outIdx);
            _in  = new MidiIn(inIdx);
            _in.MessageReceived      += OnShortMessage;
            _in.SysexMessageReceived += OnSysexMessage;
            _in.ErrorReceived        += OnError;
            _in.CreateSysexBuffers(SysexBufferSize, SysexBufferCount);
            _in.Start();

            _resolvedName = SafeName(() => MidiIn.DeviceInfo(inIdx).ProductName, _match);
            _open = true;
            AppLog.Info($"[usb-midi] opened  IN[{inIdx}]='{_resolvedName}'  " +
                        $"OUT[{outIdx}]='{SafeName(() => MidiOut.DeviceInfo(outIdx).ProductName, _match)}'");
        }
        catch (Exception ex)
        {
            AppLog.Error($"[usb-midi] open failed: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _open = false;

        // Complete the hand-off channel so the consumer drains any queued messages
        // and exits. (Completion, not cancellation, so nothing in flight is lost.)
        var handoff = _handoff;
        _handoff = null;
        handoff?.Writer.TryComplete();

        // Claim the handles under _sendLock so an in-flight SendAsync (which reads
        // _out under the same lock) can't call into a device we're disposing.
        MidiIn?  mi;
        MidiOut? mo;
        lock (_sendLock) { mi = _in; _in = null; mo = _out; _out = null; }

        if (mi != null)
        {
            mi.MessageReceived      -= OnShortMessage;
            mi.SysexMessageReceived -= OnSysexMessage;
            mi.ErrorReceived        -= OnError;
            try { mi.Stop();  } catch { }
            try { mi.Reset(); } catch { }
            try { mi.Dispose(); } catch { }
        }
        if (mo != null)
        {
            try { mo.Reset();   } catch { }
            try { mo.Dispose(); } catch { }
        }

        // Fail any in-flight query so its awaiter unblocks.
        lock (_replyLock)
        {
            _pendingReply?.TrySetResult(null);
            _pendingReply = null;
            _pendingFunc  = null;
        }
    }

    public void Dispose() => Stop();

    // The USB device connection inherently carries the inbound stream (and the
    // round-trip replies ride it too), so it can't be toggled off independently.
    public void SetStreamEnabled(bool enabled) { /* no-op for USB */ }

    // ── Inbound ──────────────────────────────────────────────────────────────

    void OnShortMessage(object? sender, MidiInMessageEventArgs e)
    {
        int raw = e.RawMessage;
        byte status = (byte)(raw & 0xFF);
        // The Kronos streams MIDI clock (F8) continuously over USB, plus active
        // sensing (FE) / undefined (F9,FD); the parser suppresses all of these, so
        // drop them here to avoid an allocation + lock on every clock tick.
        if (status == 0xF8) { TempoProbe.Pulse("usb"); return; }   // PROBE (throwaway) - clock tick, then drop
        if (status is 0xF9 or 0xFD or 0xFE) return;
        int need = MidiHex.DataBytesFor(status);
        Span<byte> msg = stackalloc byte[1 + need];
        msg[0] = status;
        if (need >= 1) msg[1] = (byte)((raw >> 8)  & 0x7F);
        if (need >= 2) msg[2] = (byte)((raw >> 16) & 0x7F);
        FeedParser(msg);
    }

    void OnSysexMessage(object? sender, MidiInSysexMessageEventArgs e)
    {
        var bytes = e.SysexBytes;
        if (bytes is { Length: > 0 }) FeedParser(bytes);
    }

    void OnError(object? sender, MidiInMessageEventArgs e) =>
        AppLog.Debug($"[usb-midi] MIM_ERROR raw=0x{e.RawMessage:X8}");

    // All inbound bytes go through one parser instance. winmm callbacks are
    // single-threaded, but Stop() can race, so serialise feeds.
    void FeedParser(ReadOnlySpan<byte> bytes)
    {
        var arr = bytes.ToArray();
        lock (_parser) _parser.Feed(arr, 0, arr.Length);
    }

    void OnParsedMessage(byte[] msg)
    {
        // Inline, prompt, cheap - this runs on the winmm callback thread while the
        // parser lock is held, so it must not build strings or marshal to the UI.
        // The dump collector and round-trip correlation both need every SysEx the
        // instant it completes.
        if (msg.Length > 0 && msg[0] == 0xF0)
        {
            SysExMessageReceived?.Invoke(msg);

            // Correlate a pending round-trip: a Korg reply (F0 42 3g 68 <func>)
            // whose func matches the request's expected reply func. Clearing the
            // pending state on the first accepted reply means a late/duplicate reply
            // is dropped rather than completing a subsequent same-func query. (A
            // reply that outlives its query's timeout can still, in principle, match
            // the next same-func query - but the app's two round-trips use distinct
            // funcs, 0x42 probe vs 0x33 perf-id, so that residual can't cross them.)
            if (IsKorgSysEx(msg))
            {
                lock (_replyLock)
                {
                    var tcs = _pendingReply;
                    if (tcs != null &&
                        (_pendingFunc == null || (msg.Length >= 5 && msg[4] == _pendingFunc)))
                    {
                        _pendingReply = null;
                        _pendingFunc  = null;
                        tcs.TrySetResult(msg);
                    }
                }
            }
        }

        // Offload the heavy part (hex decode + Traffic → UI log + ParseIncoming)
        // to the consumer task so it never stalls the driver callback.
        _handoff?.Writer.TryWrite(msg);
    }

    // Drains finished messages off the winmm callback thread and does the heavy
    // per-message work. Ends when Stop() completes the channel.
    async Task ConsumeAsync(ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var msg in reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Defer the human-readable decode to the display layer - see
                // MidiStreamMonitor.Surface. RawBytes carries the message; the
                // description is built on demand only when the traffic log shows it.
                var entry = new SysExTrafficEntry(DateTime.Now, false, "", IsMidi: true, RawBytes: msg);
                Traffic?.Invoke(entry);
            }
        }
        catch (OperationCanceledException) { }
    }

    static bool IsKorgSysEx(byte[] m) =>
        KronosSysEx.HasKorgHeaderAt(m, 0) && m.Length >= 5;

    // ── Probe / round-trip ─────────────────────────────────────────────────────

    // Availability = the device is open AND it answers a Mode Request (func 0x12)
    // with Mode Data (func 0x42). No reply → SysEx transmit/receive is off on the
    // Kronos, exactly the not-capable case the TCP probe reports.
    public async Task<bool> ProbeAsync(int timeoutMs = 8000)
    {
        if (!_open) return false;
        var req  = KronosSysEx.KorgMessage(0x12);
        var resp = await QueryAsync(req, 0x42, timeoutMs).ConfigureAwait(false);
        if (resp == null)
        {
            AppLog.Info("[usb-midi] probe: no Mode Data reply - SysEx disabled on Kronos or not routed to USB");
            return false;
        }
        var md = KronosSysEx.ParseModeData(resp);
        if (md != null)
        {
            LastModeData = md;
            AppLog.Info($"[usb-midi] available - mode={md.Value.Mode} ({md.Value.ModeName})");
        }
        else
        {
            AppLog.Info("[usb-midi] available - reply received but not Mode Data");
        }
        return true;
    }

    public async Task<byte[]?> QueryAsync(byte[] request, byte? expectReplyFunc = null, int timeoutMs = 3000)
    {
        if (!_open) return null;

        await _queryGate.WaitAsync().ConfigureAwait(false);
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_replyLock) { _pendingFunc = expectReplyFunc; _pendingReply = tcs; }
        try
        {
            // Log the request like the daemon SYSEX path does (RX is logged when the
            // reply arrives through the stream, so it isn't double-logged here).
            Traffic?.Invoke(new SysExTrafficEntry(DateTime.Now, true, MidiHex.ToHex(request)));

            if (!await SendAsync(request).ConfigureAwait(false)) return null;

            using var cts = new CancellationTokenSource(timeoutMs);
            using (cts.Token.Register(() => tcs.TrySetResult(null)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // Only clear if still ours - a completed reply already cleared it.
            lock (_replyLock)
                if (ReferenceEquals(_pendingReply, tcs)) { _pendingReply = null; _pendingFunc = null; }
            _queryGate.Release();
        }
    }

    // ── Outbound ────────────────────────────────────────────────────────────────

    public async Task<bool> SendAsync(byte[] message)
    {
        if (!_open) return false;

        var msgs = MidiHex.SplitMessages(message);
        try
        {
            for (int i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                if (m.Length == 0) continue;

                // Re-read _out under the lock each time: a Stop() between the paced
                // sends nulls it (also under _sendLock, before disposing), so we
                // never send on a disposed winmm handle.
                lock (_sendLock)
                {
                    var outp = _out;
                    if (!_open || outp == null) return false;
                    if (m[0] == 0xF0)
                    {
                        outp.SendBuffer(m);
                    }
                    else
                    {
                        int packed = m[0]
                                   | (m.Length > 1 ? m[1] << 8  : 0)
                                   | (m.Length > 2 ? m[2] << 16 : 0);
                        outp.Send(packed);
                    }
                }

                if (i < msgs.Count - 1 && SendSpacingMs > 0)
                    await Task.Delay(SendSpacingMs).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[usb-midi] send failed: {ex.Message}");
            return false;
        }
    }

    // USB has no per-message size cap - winmm's long-message send (SendBuffer)
    // transmits the whole SysEx in one call, and SplitMessages keeps a complete
    // F0...F7 as a single message. So a large object write is just SendAsync.
    public Task<bool> SendLargeSysExAsync(byte[] sysex) => SendAsync(sysex);

    static string SafeName(Func<string> get, string fallback)
    {
        try { return get(); } catch { return fallback; }
    }
}
