namespace KronosScreenRemote;

// Wraps the ONE confirmed difference between Kronos's and Nautilus's live SysEx wire protocol -
// the Exclusive Header length (Kronos 4 bytes, Nautilus 6 - see Nautilus Research/MAIN/
// 07_pcg_and_sampling/sysex_protocol_kronos_vs_nautilus.md, hardware-confirmed 2026-09-16) - so
// SysExService/SysExDumpCollector can build/parse the object-dump/store-bank/bank-digest
// primitives without caring which family they're talking to. Deliberately narrow: only the
// primitives that are hardware round-trip-verified on Nautilus (KronosSysEx.HasKorgHeaderAt/
// NautilusSysEx.HasHeaderAt's family, the 0x72/0x73/0x76/0x77/0x37/0x38/0x24 functions) are
// here. Everything else that still only exists as a Kronos static method (ParseModeData,
// ParsePerformanceId, ParseNameDump, ParseProgramBankTypes, BuildChangeProgramBankType -
// destructive, function-code not yet confirmed on Nautilus) stays Kronos-only and is called
// directly, guarded by DeviceFamily where it matters - see SysExService's own comments at each
// call site. This mirrors the grill-me decision to keep the interface surface to the
// hardware-verified slice rather than a broad, unverified device-profile abstraction.
interface IKorgSysExCodec
{
    bool HasHeaderAt(byte[] b, int i);
    bool HasHeaderAt(byte[] b, int i, byte func);
    byte[] KorgMessage(byte func, params byte[] payload);
    byte[] BuildObjectDumpRequest(int obj, int bank, int index);
    byte[] BuildDumpBankRequest(int obj, int bank);
    byte[] BuildStoreBankRequest(int obj, int bank);
    byte[] BuildObjectDumpMessage(int obj, int bank, int index, byte version, byte[] binaryData);
    ObjectDump? ParseObjectDump(byte[] msg);
    int? ParseReply(byte[] msg);
    byte[] BuildBankDigestRequest(int obj, int bank);
    BankDigest? ParseBankDigest(byte[] msg);
}

// Kronos wire framing (F0 42 3g 68, function at index 4) - thin wrapper over KronosSysEx's
// existing static methods, unchanged behavior.
sealed class KronosSysExCodec : IKorgSysExCodec
{
    public static readonly KronosSysExCodec Instance = new();

    public bool HasHeaderAt(byte[] b, int i) => KronosSysEx.HasKorgHeaderAt(b, i);
    public bool HasHeaderAt(byte[] b, int i, byte func) => KronosSysEx.HasKorgHeaderAt(b, i, func);
    public byte[] KorgMessage(byte func, params byte[] payload) => KronosSysEx.KorgMessage(func, payload);
    public byte[] BuildObjectDumpRequest(int obj, int bank, int index) =>
        MidiHex.ToBytes(SysExDumpCollector.KronosObjectDumpRequestHex(obj, bank, index))!;
    public byte[] BuildDumpBankRequest(int obj, int bank) =>
        MidiHex.ToBytes(SysExDumpCollector.KronosDumpBankRequestHex(obj, bank))!;
    public byte[] BuildStoreBankRequest(int obj, int bank) => KronosSysEx.BuildStoreBankRequest(obj, bank);
    public byte[] BuildObjectDumpMessage(int obj, int bank, int index, byte version, byte[] binaryData) =>
        KronosSysEx.BuildObjectDumpMessage(obj, bank, index, version, binaryData);
    public ObjectDump? ParseObjectDump(byte[] msg) => KronosSysEx.ParseObjectDump(msg);
    public int? ParseReply(byte[] msg) => KronosSysEx.ParseReply(msg);
    public byte[] BuildBankDigestRequest(int obj, int bank) => KronosSysEx.BuildBankDigestRequest(obj, bank);
    public BankDigest? ParseBankDigest(byte[] msg) => KronosSysEx.ParseBankDigest(msg);
}

// Nautilus wire framing (F0 42 3g 00 01 5D, function at index 6) - thin wrapper over
// NautilusSysEx's existing static methods. Every method here is hardware round-trip-verified
// (2026-09-16: passive capture, active 0x72/0x73 request/reply, and a full write+0x76 Store+
// re-read byte-diff, all against a real Nautilus - see NautilusSysEx.cs's own header comment).
sealed class NautilusSysExCodec : IKorgSysExCodec
{
    public static readonly NautilusSysExCodec Instance = new();

    public bool HasHeaderAt(byte[] b, int i) => NautilusSysEx.HasHeaderAt(b, i);
    public bool HasHeaderAt(byte[] b, int i, byte func) => NautilusSysEx.HasHeaderAt(b, i, func);
    public byte[] KorgMessage(byte func, params byte[] payload) => NautilusSysEx.KorgMessage(func, payload);
    public byte[] BuildObjectDumpRequest(int obj, int bank, int index) =>
        NautilusSysEx.BuildObjectDumpRequest(obj, bank, index);
    public byte[] BuildDumpBankRequest(int obj, int bank) => NautilusSysEx.BuildDumpBankRequest(obj, bank);
    public byte[] BuildStoreBankRequest(int obj, int bank) => NautilusSysEx.BuildStoreBankRequest(obj, bank);
    public byte[] BuildObjectDumpMessage(int obj, int bank, int index, byte version, byte[] binaryData) =>
        NautilusSysEx.BuildObjectDumpMessage(obj, bank, index, version, binaryData);
    public ObjectDump? ParseObjectDump(byte[] msg) => NautilusSysEx.ParseObjectDump(msg);
    public int? ParseReply(byte[] msg) => NautilusSysEx.ParseReply(msg);
    public byte[] BuildBankDigestRequest(int obj, int bank) => NautilusSysEx.BuildBankDigestRequest(obj, bank);
    public BankDigest? ParseBankDigest(byte[] msg) => NautilusSysEx.ParseBankDigest(msg);
}

static class KorgSysExCodecs
{
    // Unknown treated as Kronos, matching DeviceFamily.cs's own documented convention
    // (everywhere else in this app that gates on family).
    public static IKorgSysExCodec For(DeviceFamily family) => family switch
    {
        DeviceFamily.Nautilus => NautilusSysExCodec.Instance,
        _                     => KronosSysExCodec.Instance,
    };
}
