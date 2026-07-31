namespace KronosScreenRemote;

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
// NOTE: the Set List slot Type field is 0=COMBI, 1=PROGRAM, 2=song - the SAME
// convention as func 0x33 (KronosSysEx.ResolveBankLabel), NOT the "prog/combi/song"
// order the SetList.txt doc lists. Hardware-confirmed against a Set List dump +
// func-33 log: slot type-field 0 → func-33 COMBI (e.g. I-G:007 ACCORDION), slot
// type-field 1 → func-33 PROGRAM (e.g. I-B:043 "3 Way Stereo Grand"). Each type
// uses its OWN bank numbering (combi: I-A...I-G,U-A...U-G; program: I-A...I-F,GM/g,U-A...).
readonly record struct SetListSlot(
    int Number, string Name, int Type, int Bank, int Index,
    int Color, int HoldTime, int Volume, string Comments)
{
    [JsonIgnore]
    public string TypeLabel => Type switch { 0 => "Combi", 1 => "Prog", 2 => "Song", _ => "?" };

    // Type IS the ResolveBankLabel convention (0=combi, 1=program), so resolve
    // directly - no cross-mapping. Combis carry I-G at bank 6; programs have none.
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

    // Decode a func 0x73 Object Dump for obj 0x0D into a SetListData.
    // Message: F0 42 3g 68 73 0D bank idH idL version <data 8→7> F7.
    // Returns null if the message isn't a Set List dump or is too short.
    // Field-level decode lives in Core/ObjectBody/SetListBody.cs (FromRawBody), so a
    // .pcg-sourced raw body (byte-identical layout, no 8-to-7 step needed) reuses the
    // exact same decoder instead of a second copy - this method only handles the
    // wire-message-specific parts (header validation, 8-to-7 decode).
    public static SetListData? FromObjectDump(byte[] msg)
    {
        // Header (10 bytes) + at least an F7.
        if (msg.Length < 12) return null;
        if (!KronosSysEx.HasKorgHeaderAt(msg, 0, 0x73) || msg[5] != 0x0D) return null;

        int number = ((msg[7] & 0x7F) << 7) | (msg[8] & 0x7F);   // idH,idL
        int dataStart = 10;                                       // after version byte
        int dataEnd = Array.IndexOf(msg, (byte)0xF7, dataStart);
        if (dataEnd < 0) dataEnd = msg.Length;

        var bin = KronosSysEx.Decode8to7(msg, dataStart, dataEnd - dataStart);
        return SetListBody.FromRawBody(number, bin);
    }

    // A set list with no filled slots has nothing the viewer can show - treat it as
    // empty regardless of its (possibly default) name, so a full-sweep "Sync All"
    // doesn't cache 100+ blank objects. Keyed on slots alone on purpose: whether an
    // untouched set list carries a blank name or a default label is unverified.
    [JsonIgnore]
    public bool IsEmpty => Slots.Count == 0 || Slots.All(s => s.IsEmpty);

    // The Kronos's factory-default name for set-list slot N - verified against a full hardware
    // dump, where every untouched slot comes back as "Set List 000".."Set List 127" (zero-padded
    // to three digits). Used to name a slot reverted-to-blank on a committed delete (requirement 2:
    // "revert to the init configuration but with the name of the slot it occupies"), so an erased
    // Set List reads as its own slot instead of inheriting the shared blank template's name - that
    // template is captured ONCE from Set List 127, so reusing it verbatim would stamp "Set List 127"
    // onto every erased slot (the exact corruption this method prevents).
    public static string DefaultName(int number) => $"Set List {number:D3}";
}

// Result of a full Set List sweep (ISysExService.DumpAllSetListsAsync / "Sync All").
// Three-way per set list so the caller can update its cache accurately:
//   Found          - set lists that returned content → store these.
//   ConfirmedEmpty - set lists that dumped blank → drop any now-stale cache entry.
//   (neither)      - set lists that never responded (glitch / transmit off) → the
//                    caller leaves the cache untouched, so a transient miss can't
//                    delete good cached data.
sealed record SetListSyncResult(
    IReadOnlyDictionary<int, SetListData> Found,
    IReadOnlyCollection<int> ConfirmedEmpty,
    int Attempted, bool Cancelled);
