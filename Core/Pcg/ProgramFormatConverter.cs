namespace KronosScreenRemote;

// Converts a Program body between the .pcg on-disk storage format and the wire SysEx
// Object Dump format actually used by the local library cache / push pipeline. See
// PcgObjectExtractor's class comment for the empirical evidence: EXi programs are
// byte-identical in both formats (4960 bytes); HD-1 programs' wire dump (3706 bytes) is an
// exact truncation of the .pcg slot's first 3706 bytes. Combi and Set List need no
// conversion at all (their .pcg and wire sizes already match) — this class is Program-only.
static class ProgramFormatConverter
{
    public const int PcgSlotSize = 4960;
    public const int WireSizeExi = 4960;
    public const int WireSizeHd1 = 3706;

    // isExi identifies which chunk tag (MBK1/PBK1) the .pcg bank this record came from used
    // — see PcgObjectEntry.IsExi.
    public static byte[] PcgToWire(byte[] pcgBody, bool isExi)
    {
        if (pcgBody.Length != PcgSlotSize)
            throw new ArgumentException($"expected a {PcgSlotSize}-byte .pcg Program record, got {pcgBody.Length}");
        if (isExi) return pcgBody;
        var wire = new byte[WireSizeHd1];
        Array.Copy(pcgBody, wire, WireSizeHd1);
        return wire;
    }

    // Programs need the PcgToWire conversion above; Combi and Set List records already match
    // the wire format exactly (see class comment). Returns null (rather than throwing) for a
    // malformed .pcg Program slot — every caller treats "can't place this" as a skip, not a
    // crash. Shared by every PCG->local pull path (direct PlaceFromPcg/BatchPlaceFromPcg, and
    // MergeCache's own PCG->Merge pull) so the conversion and its malformed-input handling
    // live in exactly one place.
    public static byte[]? WireBodyFromPcgEntry(int objType, PcgObjectEntry entry)
    {
        if (objType != LibObj.Program) return entry.Body;
        try { return PcgToWire(entry.Body, entry.IsExi); }
        catch (ArgumentException) { return null; }
    }
}
