using System.Net.Sockets;
using System.Threading.Channels;

namespace KronosScreenRemote;

// Connects to the daemon's internal midi_tcp bridge (port 9875) and reads the
// continuous MIDI output stream from the Kronos.  Parses channel messages, SysEx,
// and selected system real-time bytes; suppresses MIDI clock and active sensing.
//
// Fires SysExTrafficEntry(IsMidi=true, IsSend=false) for each received message.
// Auto-reconnects with exponential backoff on disconnect.
sealed class MidiStreamMonitor : IDisposable
{
    const int MidiPort = 9875;

    readonly string _host;

    // Guards the _cts swap. The RUN LOOP owns disposal of its own CTS (in RunLoopAsync's
    // finally), never Start/Stop — disposing here while the loop still holds the token let a
    // just-ended loop hit `Task.Delay(retryMs, ct)` on a disposed source and throw an
    // ObjectDisposedException the loop doesn't catch (unobserved task exception). Start/Stop
    // only Cancel + repoint the field under this lock; the loop nulls the field (if it still
    // points at its own CTS) and disposes it as it unwinds.
    readonly object _lifecycleLock = new();
    CancellationTokenSource? _cts;

    // The 9875 bridge is BIDIRECTIONAL: whatever a client writes, the daemon
    // recv()s and write()s straight to /proc/.midi_in (midi_tcp.c) with no per-line
    // cap — so a full-object (0x73) write goes over this same socket as one
    // continuous stream (the path Python uses), no ctrl-port MIDI_SEND chunking. The
    // read loop and this write use opposite directions of the one NetworkStream,
    // which is safe to use concurrently; _writeGate serialises writers only.
    readonly SemaphoreSlim _writeGate = new(1, 1);
    volatile NetworkStream? _writeStream;

    // Inject raw MIDI bytes (typically one large SysEx) into the Kronos over the
    // live 9875 socket. Returns false if the stream isn't connected or the write
    // failed (caller may fall back to the capped ctrl MIDI_SEND path).
    public async Task<bool> SendAsync(byte[] data)
    {
        var stream = _writeStream;
        if (stream == null) return false;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[midi-mon] 9875 inject write failed: {ex.Message}");
            return false;
        }
        finally { _writeGate.Release(); }
    }

    public event Action<SysExTrafficEntry>? Traffic;

    // Raw complete SysEx messages (F0…F7) as they arrive on the stream. Used by
    // the dump-collector to gather multi-message bank dumps and large objects
    // that the daemon's single-message SYSEX capture can't return.
    public event Action<byte[]>? SysExMessageReceived;

    // Pulses on SysEx start and periodically during accumulation. Lets the dump-
    // collector tell "the Kronos is slowly transmitting a large object" from "no
    // response / stalled", since SysExMessageReceived only fires once a full
    // F0…F7 completes.
    public event Action? SysExActivity;

    public MidiStreamMonitor(string host) => _host = host;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            _cts?.Cancel();          // signal the previous loop; it disposes its OWN cts as it exits
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = RunLoopAsync(cts);   // hand the cts to the loop, which owns its disposal
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _cts?.Cancel();
            _cts = null;             // never Dispose here — the running loop owns that (see field note)
        }
    }

    public void Dispose()
    {
        Stop();
        _writeGate.Dispose();
    }

    async Task RunLoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            int retryMs = 2000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient { NoDelay = true };
                    await tcp.ConnectAsync(_host, MidiPort, ct).ConfigureAwait(false);
                    AppLog.Info($"[midi-mon] connected to {_host}:{MidiPort}");
                    retryMs = 2000;
                    await ReadAsync(tcp.GetStream(), ct).ConfigureAwait(false);
                    AppLog.Debug("[midi-mon] stream ended");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Debug($"[midi-mon] {ex.Message}");
                }

                try { await Task.Delay(retryMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                retryMs = Math.Min(retryMs * 2, 30_000);
            }
        }
        finally
        {
            // This loop owns this cts. Clear the field only if it still points at us (a newer
            // Start may have already repointed it at its own cts), then dispose — safe now
            // because Start/Stop never dispose, so no other path can race this disposal.
            lock (_lifecycleLock)
                if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    async Task ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        var parser = new MidiStreamParser();
        _writeStream = stream;   // expose this connection's write direction for injection

        // Daemon v1.9.1 streams bulk dumps at USB memory speed (~800 KB/s, ~280×
        // the old DIN rate). The read loop MUST return to draining the socket the
        // instant a message completes: the daemon's per-client send is MSG_DONTWAIT
        // best-effort, so if this thread stalls (building a hex string, marshalling
        // to the UI) its socket buffer fills and the daemon drops a chunk — often an
        // F7 — and the dump never reassembles. So the read thread does only cheap,
        // lossless work inline (parse + feed the dump collector) and hands each
        // finished message to a consumer task for the heavy part (hex decode, UI
        // traffic, ParseIncoming). DropOldest means an extreme event flood degrades
        // the cosmetic traffic log, never the socket drain or a bulk dump.
        var handoff = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        void OnMessage(byte[] msg)
        {
            // Inline on the read thread — cheap and lossless. The bulk-dump collector
            // must see every completed SysEx promptly regardless of UI/log backlog.
            if (msg.Length > 0 && msg[0] == 0xF0)
                SysExMessageReceived?.Invoke(msg);
            // Offload the rest (hex decode + Traffic → UI + ParseIncoming).
            handoff.Writer.TryWrite(msg);
        }

        parser.MessageReceived += OnMessage;
        parser.SysExActivity   += OnSysExActivity;
        parser.SysExAborted    += OnSysExAborted;

        var consumer = Task.Run(() => ConsumeAsync(handoff.Reader, ct), CancellationToken.None);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                parser.Feed(buf, 0, n);
            }
        }
        finally
        {
            _writeStream = null;   // this connection is going away; no more injects on it
            parser.MessageReceived -= OnMessage;
            parser.SysExActivity   -= OnSysExActivity;
            parser.SysExAborted    -= OnSysExAborted;
            handoff.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); } catch { }
        }
    }

    // Drains finished messages off the read thread and does the heavy per-message
    // work (hex decode + Traffic fan-out to the UI log and ParseIncoming). Never
    // touches the socket, so a slow UI can't back up into the daemon's send buffer.
    async Task ConsumeAsync(ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in reader.ReadAllAsync(ct).ConfigureAwait(false))
                Surface(msg);
        }
        catch (OperationCanceledException) { }
    }

    void Surface(byte[] msg)
    {
        // Defer the human-readable decode to the display layer (SysExMessageItem):
        // building it here allocated a hex string for every firehose message even
        // when nothing is viewing the traffic log — a bank dump alone is a ~½ MB
        // transient string. RawBytes carries everything the decode and the parser
        // need; the description is produced on demand only when actually shown.
        var entry = new SysExTrafficEntry(DateTime.Now, false, "", IsMidi: true, RawBytes: msg);
        Traffic?.Invoke(entry);
        // Log every completed SysEx with its size + Korg func/obj so a dump that DID
        // complete (but failed to decode) is distinguishable from one that never
        // arrived. Large messages are the bulk dumps we care about.
        if (msg.Length >= 6 && msg[0] == 0xF0 && msg[1] == 0x42)
            AppLog.Debug($"[midi-mon] SysEx complete {msg.Length} B func=0x{msg[4]:X2} obj=0x{msg[5]:X2}");
    }

    void OnSysExActivity() => SysExActivity?.Invoke();

    // Diagnostic: a SysEx accumulation was discarded because a status byte arrived
    // before its F7. For a bulk dump this is the smoking gun that some other message
    // (e.g. a perf-id reply) interleaved into the 0x73 stream and killed reassembly.
    void OnSysExAborted(int bytes, byte interrupt) =>
        AppLog.Debug($"[midi-mon] SysEx ABORTED after {bytes} B by status 0x{interrupt:X2} " +
                     "(reassembly discarded — an interleaved message broke the stream)");

    // ── MIDI message decoder ─────────────────────────────────────────────────

    // Decode a hex string (e.g. "90 3C 64") to a human-readable MIDI description.
    internal static string DecodeHex(string hex)
    {
        var bytes = MidiHex.ToBytes(hex);
        return bytes == null ? hex : DecodeMidi(bytes);
    }

    // maxHexBytes caps how many bytes are rendered into the embedded hex preview.
    // A bulk SysEx object (a Set List is ~79 KB) would otherwise build a ~200k-char
    // string here — expensive to allocate and catastrophic to lay out in a wrapping
    // TextBlock. The [{len}B] size tag already conveys the magnitude; the traffic log
    // passes a small cap while callers that want the full hex leave it unbounded.
    internal static string DecodeMidi(byte[] msg, int maxHexBytes = int.MaxValue)
    {
        if (msg.Length == 0) return "";
        byte status = msg[0];

        if (status == 0xF0)
        {
            string raw = MidiHex.ToHex(msg, maxHexBytes);
            // Korg SysEx: F0 42 3g 68 <func>
            if (KronosSysEx.HasKorgHeaderAt(msg, 0) && msg.Length >= 5)
                return $"SysEx Korg func={msg[4]:X2} [{msg.Length}B]  [{raw}]";
            return $"SysEx [{msg.Length}B]  [{raw}]";
        }

        string hex = $"[{MidiHex.ToHex(msg)}]";

        // System real-time
        return status switch
        {
            0xFA => $"Start              {hex}",
            0xFB => $"Continue           {hex}",
            0xFC => $"Stop               {hex}",
            0xFF => $"Reset              {hex}",
            _ when (status & 0x80) == 0 => hex,
            _ => DecodeChannel(status, msg, hex)
        };
    }

    static string DecodeChannel(byte status, byte[] msg, string hex)
    {
        int ch = (status & 0x0F) + 1;
        return (status & 0xF0) switch
        {
            0x90 when msg.Length >= 3 && msg[2] > 0
                => $"NoteOn  Ch{ch,-2} {NoteName(msg[1])} vel={msg[2],-3}  {hex}",
            0x90 when msg.Length >= 3
                => $"NoteOff Ch{ch,-2} {NoteName(msg[1])}          {hex}",
            0x80 when msg.Length >= 3
                => $"NoteOff Ch{ch,-2} {NoteName(msg[1])}          {hex}",
            0xB0 when msg.Length >= 3
                => $"CC#{msg[1],-3} Ch{ch,-2} val={msg[2],-3}    {hex}",
            0xC0 when msg.Length >= 2
                => $"PC      Ch{ch,-2} #{msg[1],-3}          {hex}",
            0xE0 when msg.Length >= 3
                => $"Bend    Ch{ch,-2} {PitchBend(msg[1], msg[2]),+6}      {hex}",
            0xD0 when msg.Length >= 2
                => $"ChPres  Ch{ch,-2} val={msg[1],-3}    {hex}",
            0xA0 when msg.Length >= 3
                => $"PolyPres Ch{ch,-2} {NoteName(msg[1])} val={msg[2],-3}  {hex}",
            _ => hex
        };
    }

    static string NoteName(byte midi)
    {
        ReadOnlySpan<string> names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }

    static int PitchBend(byte lsb, byte msb) => ((msb << 7) | lsb) - 8192;
}

// Stateful MIDI byte stream parser with running status support.
// Handles channel messages, SysEx, and system common/real-time.
// Suppresses MIDI clock (F8), active sensing (FE), and undefined bytes (F9, FD).
sealed class MidiStreamParser
{
    enum State { Idle, NeedData, SysEx }

    State _state;
    int   _status;
    int   _dataNeeded;
    readonly byte[] _dataBuf = new byte[2];
    int   _dataCount;
    readonly List<byte> _sysex = [];

    public event Action<byte[]>? MessageReceived;
    // Pulses on SysEx start and every ~512 bytes while a SysEx accumulates, so a
    // collector can tell an in-progress large transfer from a stalled one.
    public event Action?         SysExActivity;
    // Fires when an in-progress SysEx is discarded because a status byte arrived
    // before F7 (bytes accumulated so far, the interrupting status). Diagnostic:
    // exposes a bulk dump being killed by an interleaved message.
    public event Action<int, byte>? SysExAborted;

    public void Feed(byte[] buf, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
            Process(buf[i]);
    }

    // Discard any in-progress reassembly (partial SysEx / pending data byte) and
    // return to Idle. Used when a stream source is stopped/restarted so a fragment
    // from the old session can't corrupt the first message of the new one.
    public void Reset()
    {
        _state = State.Idle;
        _status = 0;
        _dataNeeded = 0;
        _dataCount = 0;
        _sysex.Clear();
    }

    void Process(byte b)
    {
        // Real-time messages: single byte, can appear anywhere in the stream
        if (b >= 0xF8)
        {
            if (b is 0xFA or 0xFB or 0xFC or 0xFF)
                MessageReceived?.Invoke([b]);
            // Suppress: 0xF8 (clock), 0xF9 (undefined), 0xFD (undefined), 0xFE (active sensing)
            return;
        }

        if (_state == State.SysEx)
        {
            if (b == 0xF7)
            {
                _sysex.Add(0xF7);
                MessageReceived?.Invoke([.. _sysex]);
                _sysex.Clear();
                _state = State.Idle;
            }
            else if ((b & 0x80) != 0)
            {
                // Status byte interrupts SysEx (broken message) — reset and process new status
                SysExAborted?.Invoke(_sysex.Count, b);
                _sysex.Clear();
                _state = State.Idle;
                ProcessStatus(b);
            }
            else
            {
                _sysex.Add(b);
                if ((_sysex.Count & 0x1FF) == 0) SysExActivity?.Invoke();   // pulse every 512 bytes
            }
            return;
        }

        if (b == 0xF0)
        {
            _sysex.Clear();
            _sysex.Add(0xF0);
            _state = State.SysEx;
            SysExActivity?.Invoke();
            return;
        }

        if ((b & 0x80) != 0)
        {
            ProcessStatus(b);
            return;
        }

        // Data byte — requires active status
        if (_state == State.Idle) return;

        _dataBuf[_dataCount++] = b;
        if (_dataCount < _dataNeeded) return;

        var msg = new byte[1 + _dataNeeded];
        msg[0] = (byte)_status;
        for (int j = 0; j < _dataNeeded; j++)
            msg[1 + j] = _dataBuf[j];

        _dataCount = 0;
        // Running status: stay in NeedData with the same status/dataNeeded
        MessageReceived?.Invoke(msg);
    }

    void ProcessStatus(byte b)
    {
        _status    = b;
        _dataCount = 0;
        _dataNeeded = MidiHex.DataBytesFor(b);

        if (_dataNeeded == 0)
        {
            MessageReceived?.Invoke([b]);
            // System common clears running status; channel messages keep it
            if (b >= 0xF0) _state = State.Idle;
        }
        else
        {
            _state = State.NeedData;
        }
    }
}
