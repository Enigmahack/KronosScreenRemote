namespace KronosScreenRemote;

using System.Text;
using System.Text.Json.Serialization;

// One Set List slot, decoded from a Set List object (obj 0x0D) dump.
// Layout per SysExInfo/MIDI implementation/SysExParams/SetList.txt:
//   slot N base = 24 + N*542, fields relative to base:
//     +0    name (24 ASCII)
//     +24   packed: type(bits1-0) color(bits5-2) fontLSB(bits7-6)
//     +25   bank(bits4-0) + transpose-MSB(bits7-5)
//     +26   performance index (0-199)
//     +27   hold time (0-22 → 0-60 s)
//     +28   volume (0-127)
//     +29   keyboard track(bits3-0) fontMSB(bit4) transpose-LSB(bits7-5)
//     +30   comments (512 ASCII)
//
// NOTE: the Set List slot Type field is 0=COMBI, 1=PROGRAM, 2=song — the SAME
// convention as func 0x33 (KronosSysEx.ResolveBankLabel), NOT the "prog/combi/song"
// order the SetList.txt doc lists. Hardware-confirmed against a Set List dump +
// func-33 log: slot type-field 0 → func-33 COMBI (e.g. I-G:007 ACCORDION), slot
// type-field 1 → func-33 PROGRAM (e.g. I-B:043 "3 Way Stereo Grand"). Each type
// uses its OWN bank numbering (combi: I-A…I-G,U-A…U-G; program: I-A…I-F,GM/g,U-A…).
readonly record struct SetListSlot(
    int Number, string Name, int Type, int Bank, int Index,
    int Color, int HoldTime, int Volume, string Comments)
{
    [JsonIgnore]
    public string TypeLabel => Type switch { 0 => "Combi", 1 => "Prog", 2 => "Song", _ => "?" };

    // Type IS the ResolveBankLabel convention (0=combi → CombiBanks, 1=program →
    // ProgramBanks), so resolve directly — no cross-mapping. Combis carry I-G at bank 6.
    [JsonIgnore]
    public string PerformanceLabel =>
        Type == 2 ? $"Song {Index:D3}"
                  : $"{KronosSysEx.ResolveBankLabel(Type, Bank)}:{Index:D3}";

    // An unused slot has a blank name and points at prog/combi 0 with no name.
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name);
}

// A full Set List (name + 128 slots), decoded from one obj 0x0D Object Dump.
sealed record SetListData(int Number, string Name, IReadOnlyList<SetListSlot> Slots)
{
    public const int SlotCount   = 128;   // slots per set list
    public const int MaxCount    = 128;   // number of set lists on the Kronos (0..127)
    const int NameLen   = 24;
    const int SlotBase  = 24;
    const int SlotSize  = 542;
    const int CommentLen = 512;

    // Decode a func 0x73 Object Dump for obj 0x0D into a SetListData.
    // Message: F0 42 3g 68 73 0D bank idH idL version <data 8→7> F7.
    // Returns null if the message isn't a Set List dump or is too short.
    public static SetListData? FromObjectDump(byte[] msg)
    {
        // Header (10 bytes) + at least an F7.
        if (msg.Length < 12) return null;
        if (msg[0] != 0xF0 || msg[1] != 0x42 || (msg[2] & 0xF0) != 0x30 ||
            msg[3] != 0x68 || msg[4] != 0x73 || msg[5] != 0x0D)
            return null;

        int number = ((msg[7] & 0x7F) << 7) | (msg[8] & 0x7F);   // idH,idL
        int dataStart = 10;                                       // after version byte
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;

        var bin = KronosSysEx.Decode8to7(msg, dataStart, dataEnd - dataStart);
        if (bin.Length < SlotBase) return null;

        string name = Ascii(bin, 0, NameLen);
        var slots = new List<SetListSlot>(SlotCount);
        for (int n = 0; n < SlotCount; n++)
        {
            int b = SlotBase + n * SlotSize;
            if (b + 30 > bin.Length) break;   // truncated dump — keep what decoded

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

    // A set list with no filled slots has nothing the viewer can show — treat it as
    // empty regardless of its (possibly default) name, so a full-sweep "Sync All"
    // doesn't cache 100+ blank objects. Keyed on slots alone on purpose: whether an
    // untouched set list carries a blank name or a default label is unverified.
    [JsonIgnore]
    public bool IsEmpty => Slots.Count == 0 || Slots.All(s => s.IsEmpty);

    static string Ascii(byte[] data, int offset, int len)
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

// Result of a full Set List sweep (ISysExService.DumpAllSetListsAsync / "Sync All").
// Three-way per set list so the caller can update its cache accurately:
//   Found          — set lists that returned content → store these.
//   ConfirmedEmpty — set lists that dumped blank → drop any now-stale cache entry.
//   (neither)      — set lists that never responded (glitch / transmit off) → the
//                    caller leaves the cache untouched, so a transient miss can't
//                    delete good cached data.
sealed record SetListSyncResult(
    IReadOnlyDictionary<int, SetListData> Found,
    IReadOnlyCollection<int> ConfirmedEmpty,
    int Attempted, bool Cancelled);

// Result of ISysExService.WriteSetListSlotAsync. Error is a user-facing reason,
// set only when Success is false.
readonly record struct SetListSlotWriteResult(bool Success, string? Error)
{
    public static SetListSlotWriteResult Ok() => new(true, null);
    public static SetListSlotWriteResult Fail(string error) => new(false, error);
}
