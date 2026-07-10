namespace KronosScreenRemote;

using System.Text;

readonly record struct SysExTrafficEntry(DateTime Timestamp, bool IsSend, string Hex, bool IsMidi = false, byte[]? RawBytes = null);

// Korg SysEx Mode Data (func 0x42) — mode numbering from KRONOS_MIDI_SysEx.txt *5.
readonly record struct SysExModeData(int Mode, int Option, int Setup1, int Setup2)
{
    public string ModeName => Mode switch
    {
        0 => "Combi",
        2 => "Program",
        4 => "Sequencer",
        6 => "Sampling",
        7 => "Global",
        8 => "Disk",
        9 => "Setlist",
        _ => $"Unknown ({Mode})"
    };

    // Map SysEx mode numbers to the daemon's STATE mode values (1-7).
    public int ToStateMode() => Mode switch
    {
        0 => 2, // Combi
        2 => 3, // Program
        4 => 4, // Sequencer
        6 => 5, // Sampling
        7 => 6, // Global
        8 => 7, // Disk
        9 => 1, // Setlist
        _ => 0
    };
}

// Metadata about the current performance, obtained via SysEx.
readonly record struct PerformanceInfo(
    int Type,
    int Bank,
    int Number,
    string BankLabel,
    string TypeLabel,
    string Name = "")
{
    public int ToStateMode() => Type switch
    {
        0 => 2, // Combi
        1 => 3, // Program
        2 => 4, // Song/Sequencer
        _ => 0
    };

    public string ToDisplayString()
    {
        var id = Type == 2
            ? $"Song {Number:D3}"
            : $"{BankLabel}:{Number:D3}";
        return string.IsNullOrWhiteSpace(Name) ? id : $"{id} {Name}";
    }
}

// General-purpose Korg Kronos SysEx client.
//
// Handles all SysEx communication through the screenremote daemon's SYSEX
// command on the ctrl port.  Provides both raw send/receive for arbitrary
// messages and typed convenience methods for the queries actually used:
// mode, performance ID, and current name.
//
// Thread safety: all sends are serialized through a SemaphoreSlim so
// concurrent callers (probe, poll loop, mode-change background task)
// don't stack back-to-back stream freezes.
//
// Probe lifecycle:
//   1. MIDI_STATUS pre-check (fast, no stream freeze)
//   2. Mode Request (func 0x12) with 8 s timeout
//   3. Result cached per host — not re-run on reconnect to same host
sealed class KronosSysEx
{
    readonly string _host;
    readonly int _ctrlPort;
    readonly SemaphoreSlim _gate = new(1, 1);

    // Probe state — cached per host across reconnects
    bool? _capable;
    string? _probedHost;
    int _probing;   // Interlocked guard for concurrent probe coalescing

    // Last successful query results
    SysExModeData? _lastModeData;
    PerformanceInfo? _lastPerformance;
    int _lastStateMode;

    public bool? IsCapable => _capable;
    public SysExModeData? LastModeData => _lastModeData;
    public PerformanceInfo? LastPerformance => _lastPerformance;
    public int LastStateMode => _lastStateMode;

    public event Action<SysExTrafficEntry>? Traffic;

    public KronosSysEx(string host, int ctrlPort)
    {
        _host = host;
        _ctrlPort = ctrlPort;
    }

    // ── Raw send/receive ─────────────────────────────────────────────────────

    // Send arbitrary SysEx bytes and return the raw response.
    // Returns null on timeout, error, or if SysEx is unavailable.
    public async Task<byte[]?> SendAsync(byte[] sysex, int timeoutMs = 3000)
    {
        var hex = BytesToHex(sysex);
        return await SendHexAsync(hex, timeoutMs).ConfigureAwait(false);
    }

    // Send arbitrary SysEx as a hex string (e.g. "F0 42 30 68 12 F7").
    public async Task<byte[]?> SendAsync(string sysexHex, int timeoutMs = 3000)
    {
        return await SendHexAsync(sysexHex, timeoutMs).ConfigureAwait(false);
    }

    // ── Probe ────────────────────────────────────────────────────────────────

    // Determine whether SysEx is functional.  Runs at most once per host.
    // Safe to call from any thread; concurrent calls coalesce.
    //
    // Steps:
    //   1. MIDI_STATUS — confirm MIDI_CAPTURE=1 (fast, no stream freeze)
    //   2. SYSEX Mode Request — if SysEx is disabled, daemon blocks ~5 s
    //      then returns ERR TIMEOUT.  8 s client timeout covers this.
    //   3. Parse Mode Data response and cache it.
    public async Task<bool> ProbeAsync(int timeoutMs = 8000)
    {
        if (_capable.HasValue && _probedHost == _host)
            return _capable.Value;

        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
        {
            while (Volatile.Read(ref _probing) != 0)
                await Task.Delay(100).ConfigureAwait(false);
            return _capable ?? false;
        }

        try
        {
            _lastModeData = null;

            if (!await CheckMidiCaptureAsync().ConfigureAwait(false))
            {
                AppLog.Info("[sysex] MIDI capture unavailable — SysEx disabled");
                _capable = false;
                _probedHost = _host;
                return false;
            }

            AppLog.Info("[sysex] probing SysEx availability (may freeze stream up to 5 s if disabled on Kronos)...");
            var resp = await SendHexAsync("F0 42 30 68 12 F7", timeoutMs).ConfigureAwait(false);

            if (resp == null)
            {
                AppLog.Info("[sysex] probe failed — SysEx disabled or timeout");
                _capable = false;
                _probedHost = _host;
                return false;
            }

            _lastModeData = ParseModeData(resp);
            if (_lastModeData != null)
                AppLog.Info($"[sysex] available — mode={_lastModeData.Value.Mode} ({_lastModeData.Value.ModeName})");
            else
                AppLog.Info("[sysex] available — response received but not Mode Data");

            _capable = true;
            _probedHost = _host;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[sysex] probe exception: {ex.Message}");
            _capable = false;
            _probedHost = _host;
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    // ── Typed queries ────────────────────────────────────────────────────────

    // Send Mode Request (func 0x12) and parse Mode Data (func 0x42).
    public async Task<SysExModeData?> RequestModeAsync(int timeoutMs = 3000)
    {
        var resp = await SendHexAsync("F0 42 30 68 12 F7", timeoutMs).ConfigureAwait(false);
        if (resp == null) return null;
        var md = ParseModeData(resp);
        if (md != null) _lastModeData = md;
        return md;
    }

    // Send Current Performance Id Request (func 0x32) and parse response (func 0x33).
    public async Task<PerformanceInfo?> RequestPerformanceIdAsync(int timeoutMs = 3000)
    {
        var resp = await SendHexAsync("F0 42 30 68 32 F7", timeoutMs).ConfigureAwait(false);
        if (resp == null) return null;
        return ParsePerformanceId(resp);
    }

    // Send Current Object Dump Request (func 0x74) for a name-only object and
    // parse the 24-byte name from the response (func 0x75).
    //   perfType: 0=Combi, 1=Program, 2=Song
    public async Task<string?> RequestCurrentNameAsync(int perfType, int timeoutMs = 3000)
    {
        int obj = perfType switch { 0 => 0x12, 1 => 0x13, 2 => 0x14, _ => -1 };
        if (obj < 0) return null;

        var resp = await SendHexAsync($"F0 42 30 68 74 {obj:X2} F7", timeoutMs).ConfigureAwait(false);
        if (resp == null) return null;
        return ParseNameDump(resp, obj);
    }

    // Combined query: authoritative mode via func 0x12, then performance
    // metadata (bank/number/name) via func 0x32 + 0x74 when applicable.
    //
    // Returns the STATE-equivalent mode (1-7), or 0 on failure.
    // Populates LastPerformance when in a performance-bearing mode.
    //
    // Mode is always from Mode Data (func 0x42) — never from Performance Id
    // type, because Setlist mode returns the underlying combi/program type.
    public async Task<int> QueryModeAndPerformanceAsync(int timeoutMs = 3000)
    {
        var md = await RequestModeAsync(timeoutMs).ConfigureAwait(false);
        if (md == null) return 0;

        _lastStateMode = md.Value.ToStateMode();
        if (_lastStateMode <= 0) return 0;

        AppLog.Info($"[sysex] mode: {md.Value.ModeName} (state={_lastStateMode})");

        _lastPerformance = null;
        int sysExMode = md.Value.Mode;
        if (sysExMode is 0 or 2 or 4)
        {
            var info = await RequestPerformanceIdAsync(timeoutMs).ConfigureAwait(false);
            if (info != null)
            {
                AppLog.Info($"[sysex] performance: {info.Value.TypeLabel} {info.Value.BankLabel}:{info.Value.Number:D3}");
                var name = await RequestCurrentNameAsync(info.Value.Type, timeoutMs).ConfigureAwait(false);
                _lastPerformance = name != null ? info.Value with { Name = name } : info.Value;
                AppLog.Info($"[sysex] {_lastPerformance.Value.ToDisplayString()}");
            }
        }

        return _lastStateMode;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    async Task<byte[]?> SendHexAsync(string sysexHex, int timeoutMs)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Traffic?.Invoke(new SysExTrafficEntry(DateTime.Now, true, sysexHex));

            var resp = await CtrlClient.QueryMultiAsync(_host, _ctrlPort,
                $"SYSEX {sysexHex}", timeoutMs).ConfigureAwait(false);

            if (resp == null) return null;
            resp = resp.Trim();

            if (resp.StartsWith("ERR", StringComparison.Ordinal))
            {
                AppLog.Debug($"[sysex] daemon error: {resp}");
                Traffic?.Invoke(new SysExTrafficEntry(DateTime.Now, false, $"ERR {resp[3..].Trim()}"));
                return null;
            }

            if (!resp.StartsWith("SYSEX_RESP ", StringComparison.Ordinal))
                return null;

            var rxHex = resp["SYSEX_RESP ".Length..].Trim();
            Traffic?.Invoke(new SysExTrafficEntry(DateTime.Now, false, rxHex));
            return HexToBytes(rxHex);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<bool> CheckMidiCaptureAsync()
    {
        var raw = await CtrlClient.QueryMultiAsync(_host, _ctrlPort, "MIDI_STATUS", timeoutMs: 2000)
            .ConfigureAwait(false);
        if (raw == null) return false;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed == "MIDI_CAPTURE=1") return true;
            if (trimmed == "MIDI_CAPTURE=0") return false;
        }
        return false;
    }

    // ── Response parsers (static) ────────────────────────────────────────────

    // Parse Mode Data (func 0x42) from raw SysEx bytes.
    // Scans for F0 42 3x 68 42 header to tolerate leading real-time bytes.
    public static SysExModeData? ParseModeData(byte[] bytes)
    {
        if (bytes.Length < 10) return null;
        for (int i = 0; i <= bytes.Length - 10; i++)
        {
            if (bytes[i]     != 0xF0) continue;
            if (bytes[i + 1] != 0x42) continue;
            if ((bytes[i + 2] & 0xF0) != 0x30) continue;
            if (bytes[i + 3] != 0x68) continue;
            if (bytes[i + 4] != 0x42) continue;

            return new SysExModeData(
                bytes[i + 5] & 0x0F,
                bytes[i + 6] & 0x7F,
                bytes[i + 7] & 0x7F,
                bytes[i + 8] & 0x7F);
        }
        return null;
    }

    // Parse Current Performance Id (func 0x33).
    //
    // Two on-wire payload layouts are seen in the field. In BOTH, the first
    // payload byte is the performance type and the final three payload bytes
    // are bank, number(MSB), number(LSB):
    //
    //   Documented (KRONOS_MIDI_SysEx.txt func 33):
    //       F0 42 3g 68 33  type bank numMSB numLSB  F7                       (4-byte payload)
    //
    //   Extended (Kronos OS 3.x hardware, verified 2026-07):
    //       F0 42 3g 68 33  type 68 33 type bank numMSB numLSB bank numMSB numLSB  F7
    //                                                                         (10-byte payload)
    //     e.g. Program I-B:043 → F0 42 30 68 33 01 68 33 01 01 00 2B 01 00 2B F7.
    //     The old code read bank from payload[1] (0x68=104) and number from
    //     payload[2..3] (0x33,type → 6529), rendering garbage like "?104:6529".
    //
    // Parsed fields are range-checked (IsValidPerformance) so a spurious header
    // match or an unknown layout yields null (display hidden) rather than junk.
    public static PerformanceInfo? ParsePerformanceId(byte[] bytes)
    {
        bool sawRejectedHeader = false;
        for (int i = 0; i + 4 < bytes.Length; i++)
        {
            if (bytes[i]     != 0xF0) continue;
            if (bytes[i + 1] != 0x42) continue;
            if ((bytes[i + 2] & 0xF0) != 0x30) continue;
            if (bytes[i + 3] != 0x68) continue;
            if (bytes[i + 4] != 0x33) continue;

            // Payload spans i+5 up to the next F7 (or end of buffer).
            int end = Array.IndexOf(bytes, (byte)0xF7, i + 5);
            if (end < 0) end = bytes.Length;
            if (end - (i + 5) < 4) { sawRejectedHeader = true; continue; }

            int type   =  bytes[i + 5]   & 0x7F;
            int bank   =  bytes[end - 3] & 0x7F;
            int number = ((bytes[end - 2] & 0x7F) << 7) | (bytes[end - 1] & 0x7F);

            if (!IsValidPerformance(type, bank, number)) { sawRejectedHeader = true; continue; }

            return new PerformanceInfo(
                type, bank, number,
                ResolveBankLabel(type, bank),
                type switch { 0 => "Combi", 1 => "Program", 2 => "Song", _ => "Unknown" });
        }

        if (sawRejectedHeader)
            AppLog.Debug($"[sysex] perf-id header found but fields out of range: {BytesToHex(bytes)}");
        return null;
    }

    // Range-check a parsed performance against the documented bank/number
    // ranges (func 33 spec). Rejects spurious header matches and unknown
    // payload layouts that would otherwise surface as "?<bank>:<number>".
    static bool IsValidPerformance(int type, int bank, int number) => type switch
    {
        0 => bank < CombiBanks.Length   && number <= 127,   // combi
        1 => bank < ProgramBanks.Length && number <= 127,   // program
        2 => number <= 199,                                 // song (no bank)
        _ => false,
    };

    // Parse Current Object Dump (func 0x75) for a name-only object.
    // Name is 24 bytes of ASCII at offset 0 in the decoded binary data.
    public static string? ParseNameDump(byte[] bytes, int expectedObj)
    {
        for (int i = 0; i <= bytes.Length - 8; i++)
        {
            if (bytes[i]     != 0xF0) continue;
            if (bytes[i + 1] != 0x42) continue;
            if ((bytes[i + 2] & 0xF0) != 0x30) continue;
            if (bytes[i + 3] != 0x68) continue;
            if (bytes[i + 4] != 0x75) continue;
            if (bytes[i + 5] != expectedObj) continue;

            int dataStart = i + 7;  // skip version byte
            int dataEnd = Array.IndexOf(bytes, (byte)0xF7, dataStart);
            if (dataEnd < 0) dataEnd = bytes.Length;

            int sysExLen = dataEnd - dataStart;
            if (sysExLen < 2) return null;

            var decoded = Decode8to7(bytes, dataStart, sysExLen);
            if (decoded.Length < 24) return null;

            return Encoding.ASCII.GetString(decoded, 0, 24).TrimEnd('\0', ' ');
        }
        return null;
    }

    // Parse an Object Dump (func 0x73) for a name-only object (0x12/0x13/…) into
    // (index, name). Layout: F0 42 3g 68 73 obj bank idH idL version <name 8→7> F7.
    // Returns (-1, "") on a non-matching message.
    public static (int Index, string Name) ParseNameObjectDump(byte[] msg)
    {
        if (msg.Length < 12) return (-1, "");
        if (msg[0] != 0xF0 || msg[1] != 0x42 || (msg[2] & 0xF0) != 0x30 ||
            msg[3] != 0x68 || msg[4] != 0x73)
            return (-1, "");

        int index = ((msg[7] & 0x7F) << 7) | (msg[8] & 0x7F);   // idH, idL
        int dataStart = 10;                                      // after version byte
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;
        if (dataEnd - dataStart < 2) return (index, "");

        // Name is the first 24 bytes of every object (name-only or full). Decode
        // just enough (32 sys/ex bytes → ≥24 binary) so a full 4 KB program dump
        // doesn't decode in its entirety for a 24-byte name.
        int decodeLen = Math.Min(dataEnd - dataStart, 32);
        var decoded = Decode8to7(msg, dataStart, decodeLen);
        int n = Math.Min(24, decoded.Length);
        return (index, Encoding.ASCII.GetString(decoded, 0, n).TrimEnd('\0', ' '));
    }

    // ── Korg 8-to-7-bit SysEx codec ─────────────────────────────────────────

    // Decode: every 8 SysEx bytes encode 7 binary bytes.
    // First SysEx byte carries MSBs of the next 7 bytes in bits 0-6.
    public static byte[] Decode8to7(byte[] src, int offset, int sysExLen)
    {
        int binaryLen = (sysExLen / 8) * 7 + (sysExLen % 8 > 0 ? sysExLen % 8 - 1 : 0);
        var dst = new byte[binaryLen];
        int si = offset, di = 0;

        while (si < offset + sysExLen && di < binaryLen)
        {
            byte msbs = src[si++];
            for (int bit = 0; bit < 7 && si < offset + sysExLen && di < binaryLen; bit++)
                dst[di++] = (byte)(src[si++] | (((msbs >> bit) & 1) << 7));
        }
        return dst;
    }

    // Encode: every 7 binary bytes become 8 SysEx bytes.
    public static byte[] Encode7to8(byte[] src, int offset, int binaryLen)
    {
        int sysExLen = binaryLen + (binaryLen + 6) / 7;
        var dst = new byte[sysExLen];
        int si = offset, di = 0;

        while (si < offset + binaryLen)
        {
            int groupLen = Math.Min(7, offset + binaryLen - si);
            byte msbs = 0;
            for (int bit = 0; bit < groupLen; bit++)
                msbs |= (byte)(((src[si + bit] >> 7) & 1) << bit);
            dst[di++] = msbs;
            for (int bit = 0; bit < groupLen; bit++)
                dst[di++] = (byte)(src[si++] & 0x7F);
        }
        return dst;
    }

    // ── Bank label tables ────────────────────────────────────────────────────
    // Numbering from KRONOS_MIDI_SysEx.txt / SysExParams/SetList.txt.

    // Combis have SEVEN internal banks (I-A…I-G) — one more than programs (I-A…I-F).
    // KRONOS_MIDI_SysEx.txt *2: combi bank 0-6 = INT-A…G, then USER-A…G. The linear
    // index here (used by func-0x33 perf-id AND the Set List slot decoder) must
    // include I-G at index 6, or every combi from I-G onward is off by one — e.g. a
    // slot referencing I-G:007 renders as U-A:007. (Programs skip straight to GM.)
    static readonly string[] CombiBanks =
        ["I-A", "I-B", "I-C", "I-D", "I-E", "I-F", "I-G",
         "U-A", "U-B", "U-C", "U-D", "U-E", "U-F", "U-G"];

    // Linear PROGRAM-bank numbering (func-33 / Set List program slots). This Kronos
    // has SEVEN internal program banks (I-A…I-G, per the user's bank list), then the
    // read-only GM/g banks, then USER (U-A…U-G, U-AA…U-GG). Index 6 = I-G.
    static readonly string[] ProgramBanks =
        ["I-A", "I-B", "I-C", "I-D", "I-E", "I-F", "I-G",
         "GM", "g(1)", "g(2)", "g(3)", "g(4)", "g(5)", "g(6)", "g(7)",
         "g(8)", "g(9)", "g(d)",
         "U-A", "U-B", "U-C", "U-D", "U-E", "U-F", "U-G",
         "U-AA", "U-BB", "U-CC", "U-DD", "U-EE", "U-FF", "U-GG"];

    // type: 0=Combi, 1=Program, 2=Song. Public so the Set List decoder can
    // resolve slot performance banks with the same tables.
    public static string ResolveBankLabel(int type, int bank) => type switch
    {
        0 => bank < CombiBanks.Length ? CombiBanks[bank] : $"?{bank}",
        1 => bank < ProgramBanks.Length ? ProgramBanks[bank] : $"?{bank}",
        2 => "",
        _ => $"?{bank}"
    };

    // ── Hex utilities ────────────────────────────────────────────────────────

    static byte[]? HexToBytes(string hex)
    {
        var clean = hex.Replace(" ", "");
        if (clean.Length % 2 != 0) return null;
        try
        {
            var bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }
        catch { return null; }
    }

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
