using System.Net.Sockets;
using System.Text;

namespace KronosScreenRemote;

// Connects to the daemon's internal midi_tcp bridge (port 9875) and reads the
// continuous MIDI output stream from the Kronos.  Parses channel messages, SysEx,
// and selected system real-time bytes; suppresses MIDI clock and active sensing.
//
// Fires SysExTrafficEntry(IsMidi=true, IsSend=false) for each received message.
// Auto-reconnects with exponential backoff on disconnect.
sealed class MidiStreamMonitor
{
    const int MidiPort = 9875;

    readonly string _host;
    CancellationTokenSource? _cts;

    public event Action<SysExTrafficEntry>? Traffic;

    public MidiStreamMonitor(string host) => _host = host;

    public void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    async Task RunLoopAsync(CancellationToken ct)
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

    async Task ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        var parser = new MidiStreamParser();
        parser.MessageReceived += OnMessage;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) return;
                parser.Feed(buf, 0, n);
            }
        }
        finally
        {
            parser.MessageReceived -= OnMessage;
        }
    }

    void OnMessage(byte[] msg)
    {
        var entry = new SysExTrafficEntry(DateTime.Now, false, DecodeMidi(msg), IsMidi: true, RawBytes: msg);
        Traffic?.Invoke(entry);
    }

    // ── MIDI message decoder ─────────────────────────────────────────────────

    // Decode a hex string (e.g. "90 3C 64") to a human-readable MIDI description.
    internal static string DecodeHex(string hex)
    {
        var clean = hex.Replace(" ", "");
        if (clean.Length % 2 != 0 || clean.Length == 0) return hex;
        try
        {
            var bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return DecodeMidi(bytes);
        }
        catch { return hex; }
    }

    internal static string DecodeMidi(byte[] msg)
    {
        if (msg.Length == 0) return "";
        byte status = msg[0];

        if (status == 0xF0)
        {
            string raw = BytesToHex(msg);
            // Korg SysEx: F0 42 3g 68 <func>
            if (msg.Length >= 5 && msg[1] == 0x42 && (msg[2] & 0xF0) == 0x30 && msg[3] == 0x68)
                return $"SysEx Korg func={msg[4]:X2} [{msg.Length}B]  [{raw}]";
            return $"SysEx [{msg.Length}B]  [{raw}]";
        }

        string hex = $"[{BytesToHex(msg)}]";

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

    static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
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

    public void Feed(byte[] buf, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
            Process(buf[i]);
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
                _sysex.Clear();
                _state = State.Idle;
                ProcessStatus(b);
            }
            else
            {
                _sysex.Add(b);
            }
            return;
        }

        if (b == 0xF0)
        {
            _sysex.Clear();
            _sysex.Add(0xF0);
            _state = State.SysEx;
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
        _dataNeeded = DataBytesFor(b);

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

    static int DataBytesFor(int status) => (status & 0xF0) switch
    {
        0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 2,
        0xC0 or 0xD0 => 1,
        _ => status switch
        {
            0xF1 or 0xF3 => 1,   // MTC quarter-frame, song select
            0xF2         => 2,   // song position pointer
            _            => 0
        }
    };
}
