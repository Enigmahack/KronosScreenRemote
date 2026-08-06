namespace KronosScreenRemote;

using System.Text;

readonly record struct SysExTrafficEntry(DateTime Timestamp, bool IsSend, string Hex, bool IsMidi = false, byte[]? RawBytes = null);

// Korg SysEx Mode Data (func 0x42) - mode numbering from KRONOS_MIDI_SysEx.txt *5.
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

// A parsed func-0x73 Object Dump: header fields + the decoded (8→7) object body.
// The Body is a mutable copy the Librarian patches (reference bytes) before
// re-sending. Value equality is not used, so the byte[] reference is fine.
sealed record ObjectDump(int Obj, int Bank, int Index, byte Version, byte[] Body);

// A parsed func-0x38 Bank Digest: which bank, and its 20-byte SHA-1 storage digest.
readonly record struct BankDigest(int Obj, int Bank, byte[] Sha1);

// A parsed func-0x61 Program Bank Types reply: one flag per bit (true = EXi, false
// = HD-1), indexed exactly as the wire bitmap is (see ParseProgramBankTypes /
// KronosBanks.ProgramBankTypeBitIndex for what each index means).
readonly record struct ProgramBankTypes(bool[] IsExi);

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
//   3. Result cached per host - not re-run on reconnect to same host
sealed class KronosSysEx
{
    readonly string _host;
    readonly int _ctrlPort;
    readonly SemaphoreSlim _gate = new(1, 1);

    // Probe state - cached per host across reconnects
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
        var hex = MidiHex.ToHex(sysex);
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
    //   1. MIDI_STATUS - confirm MIDI_CAPTURE=1 (fast, no stream freeze)
    //   2. SYSEX Mode Request - if SysEx is disabled, daemon blocks ~5 s
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
                AppLog.Info("[sysex] MIDI capture unavailable - SysEx disabled");
                _capable = false;
                _probedHost = _host;
                return false;
            }

            AppLog.Info("[sysex] probing SysEx availability (may freeze stream up to 5 s if disabled on Kronos)...");
            var resp = await SendHexAsync("F0 42 30 68 12 F7", timeoutMs).ConfigureAwait(false);

            if (resp == null)
            {
                AppLog.Info("[sysex] probe failed - SysEx disabled or timeout");
                _capable = false;
                _probedHost = _host;
                return false;
            }

            _lastModeData = ParseModeData(resp);
            if (_lastModeData != null)
                AppLog.Info($"[sysex] available - mode={_lastModeData.Value.Mode} ({_lastModeData.Value.ModeName})");
            else
                AppLog.Info("[sysex] available - response received but not Mode Data");

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
    // Mode is always from Mode Data (func 0x42) - never from Performance Id
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

            var resp = await CtrlQuery.QueryMultiAsync(_host, _ctrlPort,
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
            return MidiHex.ToBytes(rxHex);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<bool> CheckMidiCaptureAsync()
    {
        var raw = await CtrlQuery.QueryMultiAsync(_host, _ctrlPort, DaemonCommand.QueryMidiStatus, timeoutMs: 2000)
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

    // True if a Korg SysEx frame header - F0 42 3g 68 - begins at index i (4 header bytes,
    // any function byte). Published so every SysEx producer/consumer shares ONE definition of
    // the header instead of re-spelling F0/42/3g/68 inline in a dozen places - the divergence
    // this centralizes is exactly the failure mode that bred the bank-table bug. Length-guarded
    // so it's self-safe; a scanning caller still bounds its own loop and keeps whatever extra
    // Length>=N guard the bytes it reads past i+3 require (this checks only i..i+3).
    public static bool HasKorgHeaderAt(byte[] b, int i) =>
        i + 3 < b.Length
        && b[i]              == 0xF0
        && b[i + 1]          == 0x42
        && (b[i + 2] & 0xF0) == 0x30
        && b[i + 3]          == 0x68;

    // As above, plus the function byte at i+4 equals `func` (a 5-byte match). The func-byte
    // length check is separate so the base header check can't read out of range for a 4-byte
    // buffer. Byte order matches the previous inline short-circuit checks.
    public static bool HasKorgHeaderAt(byte[] b, int i, byte func) =>
        HasKorgHeaderAt(b, i) && i + 4 < b.Length && b[i + 4] == func;

    // Parse Mode Data (func 0x42) from raw SysEx bytes.
    // Scans for F0 42 3x 68 42 header to tolerate leading real-time bytes.
    public static SysExModeData? ParseModeData(byte[] bytes)
    {
        if (bytes.Length < 10) return null;
        for (int i = 0; i <= bytes.Length - 10; i++)
        {
            if (!HasKorgHeaderAt(bytes, i, 0x42)) continue;

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
            if (!HasKorgHeaderAt(bytes, i, 0x33)) continue;

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
            AppLog.Debug($"[sysex] perf-id header found but fields out of range: {MidiHex.ToHex(bytes)}");
        return null;
    }

    // Range-check a parsed performance against the documented bank/number
    // ranges (func 33 spec). Rejects spurious header matches and unknown
    // payload layouts that would otherwise surface as "?<bank>:<number>".
    static bool IsValidPerformance(int type, int bank, int number) => type switch
    {
        0 or 1 => KronosBanks.Func33ToObjBank(type, bank) >= 0 && number <= 127,
        2      => number <= 199,                            // song (no bank)
        _      => false,
    };

    // Parse Current Object Dump (func 0x75) for a name-only object.
    // Name is 24 bytes of ASCII at offset 0 in the decoded binary data.
    public static string? ParseNameDump(byte[] bytes, int expectedObj)
    {
        for (int i = 0; i <= bytes.Length - 8; i++)
        {
            if (!HasKorgHeaderAt(bytes, i, 0x75)) continue;
            if (bytes[i + 5] != expectedObj) continue;

            int dataStart = i + 7;  // skip version byte
            int dataEnd = Array.IndexOf(bytes, (byte)0xF7, dataStart);
            if (dataEnd < 0) dataEnd = bytes.Length;

            int sysExLen = dataEnd - dataStart;
            if (sysExLen < 2) continue;   // spurious header match - keep scanning

            var decoded = Decode8to7(bytes, dataStart, sysExLen);
            if (decoded.Length < 24) continue;

            return Encoding.ASCII.GetString(decoded, 0, 24).TrimEnd('\0', ' ');
        }
        return null;
    }

    // Parse a Reply (func 0x24) message: F0 42 3g 68 24 cc F7. Returns the Reply
    // Code (0 = success, non-zero = failure - see KRONOS_MIDI_SysEx.txt *6), or
    // null if the message isn't a Reply.
    public static int? ParseReply(byte[] msg)
    {
        for (int i = 0; i + 5 < msg.Length; i++)
            if (HasKorgHeaderAt(msg, i, 0x24))
                return msg[i + 5] & 0x7F;
        return null;
    }

    // Build an Object Dump (func 0x73) WRITE message for a small, directly
    // addressed sub-object - e.g. Set List Slot Name (0x11, bank=set list,
    // index=slot) or Set List Slot Comments (0x10, same addressing). Not safe
    // for large objects: the daemon's MIDI_SEND caps at a 4096-byte payload
    // (screenremote.c CTRL_LINE_MAX), which a full ~79 KB Set List object (0x0D)
    // blows through by a wide margin.
    //   F0 42 3g 68 73 obj bank idH idL version <data 7→8> F7
    public static byte[] BuildObjectDumpMessage(int obj, int bank, int index, byte version, byte[] binaryData)
    {
        var encoded = Encode7to8(binaryData, 0, binaryData.Length);
        var payload = new byte[5 + encoded.Length];
        payload[0] = (byte)obj;
        payload[1] = (byte)bank;
        payload[2] = (byte)((index >> 7) & 0x7F);
        payload[3] = (byte)(index & 0x7F);
        payload[4] = version;
        Array.Copy(encoded, 0, payload, 5, encoded.Length);
        return KorgMessage(0x73, payload);
    }

    // Assemble a Korg SysEx message - F0 42 30 68 <func> <payload...> F7 - the single place the
    // 4-byte Korg preamble is written for outbound messages (every Build* here funnels through
    // it, so a framing change is a one-line edit). Channel byte 0x30 = global channel 1, the
    // channel every request this client sends targets.
    public static byte[] KorgMessage(byte func, params byte[] payload)
    {
        var msg = new byte[6 + payload.Length];
        msg[0] = 0xF0; msg[1] = 0x42; msg[2] = 0x30; msg[3] = 0x68; msg[4] = func;
        Array.Copy(payload, 0, msg, 5, payload.Length);
        msg[^1] = 0xF7;
        return msg;
    }

    // Build a Store Bank Request (func 0x76): commits previously-sent Object Dump
    // (func 0x73) data for the given object type/bank to non-volatile storage.
    //   F0 42 3g 68 76 obj bank F7
    public static byte[] BuildStoreBankRequest(int obj, int bank) =>
        KorgMessage(0x76, (byte)obj, (byte)bank);

    // Build a Change Program Bank Type (func 0x7C): sets the given program bank to HD-1
    // (type 0) or EXi (type 1). If the new type differs from the current one, the instrument
    // REFORMATS AND ERASES that bank before replying with a func 0x24 Reply - so this is only
    // ever sent as the first step of copying a WHOLE bank across (requirement 4).
    //   F0 42 3g 68 7C bank type F7
    public static byte[] BuildChangeProgramBankType(int bank, bool isExi) =>
        KorgMessage(0x7C, (byte)bank, (byte)(isExi ? 1 : 0));

    // ── Librarian additions: full-object parse, param-change, digest, mode ──────

    // Parse a received func-0x73 Object Dump into header fields + decoded body.
    //   F0 42 3g 68 73 obj bank idH idL version <data 8→7> F7
    public static ObjectDump? ParseObjectDump(byte[] msg)
    {
        if (msg.Length < 11) return null;
        if (!HasKorgHeaderAt(msg, 0, 0x73)) return null;
        int obj   = msg[5];
        int bank  = msg[6];
        int index = ((msg[7] & 0x7F) << 7) | (msg[8] & 0x7F);
        byte version = msg[9];
        int dataStart = 10;
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;
        var body = Decode8to7(msg, dataStart, dataEnd - dataStart);
        return new ObjectDump(obj, bank, index, version, body);
    }

    // Build a Parameter Change (func 0x43, integer): edits the CURRENT edit buffer
    // only (audible now, never persisted). typ/soc/sub/pid/idx are DECIMAL ids sent
    // verbatim (e.g. a set-list slot is pid=18, typ=37 - NOT 0x12/0x25). value is
    // 21-bit two's-complement across three 7-bit bytes.
    //   F0 42 3g 68 43 typ soc sub pid idx vH vM vL F7
    public static byte[] BuildParamChange(int typ, int soc, int sub, int pid, int idx, int value)
    {
        int v = value & 0x1FFFFF;
        return KorgMessage(0x43,
            (byte)(typ & 0x7F), (byte)(soc & 0x7F), (byte)(sub & 0x7F),
            (byte)(pid & 0x7F), (byte)(idx & 0x7F),
            (byte)((v >> 14) & 0x7F), (byte)((v >> 7) & 0x7F), (byte)(v & 0x7F));
    }

    // Build a Bank Digest Request (func 0x37): the instrument replies with a func
    // 0x38 storage digest for that bank.  F0 42 3g 68 37 obj bank F7
    public static byte[] BuildBankDigestRequest(int obj, int bank) =>
        KorgMessage(0x37, (byte)obj, (byte)bank);

    // Build a Mode Change (func 0x4E): 0 Combi, 2 Program, 4 Seq, 7 Global, 9 Set List.
    //   F0 42 3g 68 4E 0m F7
    public static byte[] BuildModeChange(int mode) =>
        KorgMessage(0x4E, (byte)(mode & 0x0F));

    // Parse a func-0x38 Bank Digest reply into (obj, bank, 20-byte SHA-1).
    //   F0 42 3g 68 38 obj bank <sha1 8→7 = 23 bytes> F7
    public static BankDigest? ParseBankDigest(byte[] msg)
    {
        for (int i = 0; i + 6 < msg.Length; i++)
        {
            if (!HasKorgHeaderAt(msg, i, 0x38)) continue;
            int obj = msg[i + 5];
            int bank = msg[i + 6];
            int dataStart = i + 7;
            int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
            if (dataEnd < 0) dataEnd = msg.Length;
            var sha1 = Decode8to7(msg, dataStart, dataEnd - dataStart);
            if (sha1.Length > 20) Array.Resize(ref sha1, 20);
            return new BankDigest(obj, bank, sha1);
        }
        return null;
    }

    // Build a Program Bank Types Request (func 0x60): the instrument replies with a
    // func 0x61 Program Bank Types bitmap (edit buffer + every typed program bank,
    // HD-1 vs EXi).  F0 42 3g 68 60 F7
    public static byte[] BuildProgramBankTypesRequest() =>
        KorgMessage(0x60);

    // Parse a func-0x61 Program Bank Types reply: F0 42 3g 68 61 numBits data[] F7.
    // data[] is 7-bit-packed (bit 0 of data[0] = overall bit 0, ... bit 6 of data[0]
    // = overall bit 6, bit 0 of data[1] = overall bit 7, etc.) - 1 = EXi, 0 = HD-1.
    public static ProgramBankTypes? ParseProgramBankTypes(byte[] msg)
    {
        for (int i = 0; i + 6 < msg.Length; i++)
        {
            if (!HasKorgHeaderAt(msg, i, 0x61)) continue;
            int numBits = msg[i + 5] & 0x7F;
            int dataStart = i + 6;
            int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
            if (dataEnd < 0) dataEnd = msg.Length;
            var flags = new bool[numBits];
            for (int bit = 0; bit < numBits; bit++)
            {
                int byteIdx = dataStart + bit / 7;
                if (byteIdx >= dataEnd) break;
                flags[bit] = (msg[byteIdx] & (1 << (bit % 7))) != 0;
            }
            return new ProgramBankTypes(flags);
        }
        return null;
    }

    // Parse an Object Dump (func 0x73) for a name-only object (0x12/0x13/...) into
    // (index, name). Layout: F0 42 3g 68 73 obj bank idH idL version <name 8→7> F7.
    // Returns (-1, "") on a non-matching message.
    public static (int Index, string Name) ParseNameObjectDump(byte[] msg)
    {
        if (msg.Length < 12) return (-1, "");
        if (!HasKorgHeaderAt(msg, 0, 0x73)) return (-1, "");

        int index = ((msg[7] & 0x7F) << 7) | (msg[8] & 0x7F);   // idH, idL
        int dataStart = 10;                                      // after version byte
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;
        if (dataEnd - dataStart < 2) return (-1, "");   // too short to hold a name at all

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

    // Encode: inverse of Decode8to7. Every 7 binary bytes produce 8 SysEx bytes -
    // an MSB byte (bit N = bit 7 of the Nth following byte) followed by up to 7
    // bytes each holding the low 7 bits of one binary byte. Matches
    // KRONOS_MIDI_SysEx.txt *3 exactly (sysExSize = binarySize + (binarySize+6)/7).
    public static byte[] Encode7to8(byte[] src, int offset, int binaryLen)
    {
        int sysExLen = binaryLen + (binaryLen + 6) / 7;
        var dst = new byte[sysExLen];
        int si = offset, di = 0;
        int end = offset + binaryLen;

        while (si < end)
        {
            int groupLen = Math.Min(7, end - si);
            int msbIndex = di++;
            byte msbs = 0;
            for (int bit = 0; bit < groupLen; bit++)
            {
                byte b = src[si + bit];
                if ((b & 0x80) != 0) msbs |= (byte)(1 << bit);
                dst[di++] = (byte)(b & 0x7F);
            }
            dst[msbIndex] = msbs;
            si += groupLen;
        }
        return dst;
    }

    // ── Bank label resolution ────────────────────────────────────────────────
    // type: 0=Combi, 1=Program, 2=Song. Public so the Set List decoder can
    // resolve slot performance banks with the same mapping.
    //
    // Delegates to KronosBanks.Func33ToObjBank + ProgramLabel/CombiLabel - the
    // hardware-validated linear↔objbank mapping the move engine itself uses - so a
    // func-33 bank index can never LABEL one bank while the Librarian's reference
    // math TARGETS another. This file previously carried its own label tables with
    // seven internal program banks; Program has no real I-G (see KronosBanks'
    // header), which shifted every program label from GM onward one bank off.
    // Combi genuinely has I-G at linear index 6 and is unaffected.
    public static string ResolveBankLabel(int type, int bank)
    {
        if (type == 2) return "";
        int objBank = KronosBanks.Func33ToObjBank(type, bank);
        if (objBank < 0) return $"?{bank}";
        return type == 1 ? KronosBanks.ProgramLabel(objBank) : KronosBanks.CombiLabel(objBank);
    }
}
