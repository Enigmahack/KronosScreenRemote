namespace KronosScreenRemote;

using System.Text;

// Raw-body decode/mutate for a Set List object (obj 0x0D) - decoded body only, no
// wire-format/8-to-7 knowledge. FromRawBody holds the field-walk logic that used to
// live inline in SetListData.FromObjectDump; that method now just validates the
// wire header, runs KronosSysEx.Decode8to7, and delegates here - so a .pcg-sourced
// raw body (byte-identical layout, confirmed against Documentation/PCG Structure
// Kronos.txt) reuses the exact same decoder as a live dump, never a second copy.
static class SetListBody
{
    public const int NameLen    = 24;
    public const int SlotBase   = 24;
    public const int SlotSize   = 542;
    public const int CommentLen = 512;

    // Moved verbatim (behavior-preserving) out of the old SetListData.FromObjectDump.
    public static SetListData? FromRawBody(int number, byte[] bin)
    {
        if (bin.Length < SlotBase) return null;

        string name = Ascii(bin, 0, NameLen);
        var slots = new List<SetListSlot>(SetListData.SlotCount);
        for (int n = 0; n < SetListData.SlotCount; n++)
        {
            int b = SlotBase + n * SlotSize;
            if (b + 30 > bin.Length) break;   // truncated dump - keep what decoded

            string slotName = Ascii(bin, b, NameLen);
            int packed  = bin[b + 24];
            int type    = packed & 0x03;
            int color   = (packed >> 2) & 0x0F;
            int bank    = bin[b + 25] & 0x1F;
            int index   = bin[b + 26];
            int hold    = bin[b + 27];
            int volume  = bin[b + 28];
            int comLen  = Math.Min(CommentLen, bin.Length - (b + 30));
            string comments = comLen > 0 ? Ascii(bin, b + 30, comLen) : "";

            slots.Add(new SetListSlot(n, slotName, type, bank, index, color, hold, volume, comments));
        }

        return new SetListData(number, name, slots);
    }

    // ── Raw-body mutators (byte-level, bit-preserving where fields are packed) ──

    public static byte[] WriteName(byte[] bin, string name) => Librarian.BuildRenamedBody(bin, name);

    public static byte[] WriteSlotName(byte[] bin, int slot, string name)
    {
        var body = (byte[])bin.Clone();
        int b = SlotBase + slot * SlotSize;
        var padded = Librarian.PadAscii(name, NameLen);
        int n = Math.Min(NameLen, Math.Max(0, body.Length - b));
        if (n > 0) Array.Copy(padded, 0, body, b, n);
        return body;
    }

    // Color shares its byte with Type (bits 1-0) and font-LSB (bits 7-6) - mask,
    // don't overwrite, same discipline as LibRefs.SetSetListSlotRef.
    public static byte[] WriteSlotColor(byte[] bin, int slot, int color)
    {
        var body = (byte[])bin.Clone();
        int b = SlotBase + slot * SlotSize;
        int ofs = b + 24;
        if (ofs < body.Length)
            body[ofs] = (byte)((body[ofs] & ~0b0011_1100) | ((color & 0x0F) << 2));
        return body;
    }

    public static byte[] WriteSlotComments(byte[] bin, int slot, string comments)
    {
        var body = (byte[])bin.Clone();
        int b = SlotBase + slot * SlotSize;
        var padded = Librarian.PadAscii(comments, CommentLen);
        int n = Math.Min(CommentLen, Math.Max(0, body.Length - (b + 30)));
        if (n > 0) Array.Copy(padded, 0, body, b + 30, n);
        return body;
    }

    internal static string Ascii(byte[] data, int offset, int len)
    {
        int end = Math.Min(offset + len, data.Length);
        if (end <= offset) return "";
        // Kronos names are space/nul padded and may embed control bytes; keep
        // printable ASCII only, then trim trailing padding.
        var sb = new StringBuilder(end - offset);
        for (int i = offset; i < end; i++)
        {
            byte c = data[i];
            sb.Append(c is >= 0x20 and < 0x7F ? (char)c : c == 0 ? '\0' : ' ');
        }
        return sb.ToString().TrimEnd('\0', ' ');
    }
}
