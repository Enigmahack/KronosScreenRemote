namespace KronosScreenRemote;

// Nautilus wire framing - CONFIRMED against real hardware 2026-09-16 via a passive USB SysEx
// capture (Tools/NautilusSysExCapture.cs) of a Program Object Dump from a real Nautilus unit:
//   F0 42 30 00 01 5D 73 00 00 00 00 06  <data...>  F7
//   \_______header, 6B_______/ fn obj bk idH idL ver
// This is a genuinely different (2 bytes longer) shape than Kronos's `KronosSysEx` header
// (F0 42 3g 68, 4 bytes, function at index 4) - confirmed NOT identical despite an earlier
// session's doc claim to the contrary (see Nautilus Research/MAIN/07_pcg_and_sampling/
// sysex_protocol_kronos_vs_nautilus.md). Kept as a separate class rather than parameterizing
// KronosSysEx's header length in place, to avoid touching that class's many existing hardcoded
// call sites under real hardware pressure - function codes/semantics/8-to-7 encoding are
// identical (reuses KronosSysEx.Decode8to7/Encode7to8 directly), only the header bytes differ.
static class NautilusSysEx
{
    // True if a Nautilus Excl Header - F0 42 3g 00 01 5D - begins at index i (6 header bytes).
    public static bool HasHeaderAt(byte[] b, int i) =>
        i + 5 < b.Length
        && b[i]              == 0xF0
        && b[i + 1]          == 0x42
        && (b[i + 2] & 0xF0) == 0x30
        && b[i + 3]          == 0x00
        && b[i + 4]          == 0x01
        && b[i + 5]          == 0x5D;

    // As above, plus the function byte at i+6 equals `func`.
    public static bool HasHeaderAt(byte[] b, int i, byte func) =>
        HasHeaderAt(b, i) && i + 6 < b.Length && b[i + 6] == func;

    // Assemble a Nautilus SysEx message - F0 42 30 00 01 5D <func> <payload...> F7.
    public static byte[] KorgMessage(byte func, params byte[] payload)
    {
        var msg = new byte[8 + payload.Length];
        msg[0] = 0xF0; msg[1] = 0x42; msg[2] = 0x30;
        msg[3] = 0x00; msg[4] = 0x01; msg[5] = 0x5D;
        msg[6] = func;
        Array.Copy(payload, 0, msg, 7, payload.Length);
        msg[^1] = 0xF7;
        return msg;
    }

    // Build an Object Dump Request (func 0x72): F0 42 3g 00 01 5D 72 obj bank idH idL F7.
    public static byte[] BuildObjectDumpRequest(int obj, int bank, int index) =>
        KorgMessage(0x72, (byte)obj, (byte)bank, (byte)((index >> 7) & 0x7F), (byte)(index & 0x7F));

    // Build a Dump Bank Request (func 0x77): F0 42 3g 00 01 5D 77 obj bank F7.
    public static byte[] BuildDumpBankRequest(int obj, int bank) =>
        KorgMessage(0x77, (byte)obj, (byte)bank);

    // Build a Store Bank Request (func 0x76): commits previously-sent Object Dump (func 0x73)
    // data for the given object type/bank to non-volatile storage. DESTRUCTIVE on real hardware.
    //   F0 42 3g 00 01 5D 76 obj bank F7
    public static byte[] BuildStoreBankRequest(int obj, int bank) =>
        KorgMessage(0x76, (byte)obj, (byte)bank);

    // Build an Object Dump (func 0x73) WRITE message - same wire shape as KronosSysEx's, just
    // the 6-byte Nautilus header instead of Kronos's 4-byte one.
    //   F0 42 3g 00 01 5D 73 obj bank idH idL version <data 8->7> F7
    public static byte[] BuildObjectDumpMessage(int obj, int bank, int index, byte version, byte[] binaryData)
    {
        var encoded = KronosSysEx.Encode7to8(binaryData, 0, binaryData.Length);
        var payload = new byte[5 + encoded.Length];
        payload[0] = (byte)obj;
        payload[1] = (byte)bank;
        payload[2] = (byte)((index >> 7) & 0x7F);
        payload[3] = (byte)(index & 0x7F);
        payload[4] = version;
        Array.Copy(encoded, 0, payload, 5, encoded.Length);
        return KorgMessage(0x73, payload);
    }

    // Parse a received func-0x73 Object Dump into header fields + decoded body.
    //   F0 42 3g 00 01 5D 73 obj bank idH idL version <data 8->7> F7
    public static ObjectDump? ParseObjectDump(byte[] msg)
    {
        if (msg.Length < 13) return null;
        if (!HasHeaderAt(msg, 0, 0x73)) return null;
        int obj   = msg[7];
        int bank  = msg[8];
        int index = ((msg[9] & 0x7F) << 7) | (msg[10] & 0x7F);
        byte version = msg[11];
        int dataStart = 12;
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;
        var body = KronosSysEx.Decode8to7(msg, dataStart, dataEnd - dataStart);
        return new ObjectDump(obj, bank, index, version, body);
    }

    // Parse a Reply (func 0x24): F0 42 3g 00 01 5D 24 cc F7. Returns the Reply Code, or null.
    public static int? ParseReply(byte[] msg)
    {
        for (int i = 0; i + 7 < msg.Length; i++)
            if (HasHeaderAt(msg, i, 0x24))
                return msg[i + 7] & 0x7F;
        return null;
    }

    // Build a Bank Digest Request (func 0x37): F0 42 3g 00 01 5D 37 obj bank F7. Read-only -
    // asks the instrument to report a func 0x38 Bank Digest for the given object type/bank.
    public static byte[] BuildBankDigestRequest(int obj, int bank) =>
        KorgMessage(0x37, (byte)obj, (byte)bank);

    // Parse a received func-0x38 Bank Digest: F0 42 3g 00 01 5D 38 obj bank <digest 8->7> F7.
    // Digest is a 20-byte SHA-1 per NAUTILUS_MIDI_SysEx.txt *38*.
    public static BankDigest? ParseBankDigest(byte[] msg)
    {
        if (msg.Length < 10) return null;
        if (!HasHeaderAt(msg, 0, 0x38)) return null;
        int obj  = msg[7];
        int bank = msg[8];
        int dataStart = 9;
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;
        var digest = KronosSysEx.Decode8to7(msg, dataStart, dataEnd - dataStart);
        return new BankDigest(obj, bank, digest);   // reuses KronosSysEx.cs's BankDigest shape
    }
}
