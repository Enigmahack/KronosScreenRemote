namespace KronosScreenRemote;

// Nautilus counterpart to ProgramFormatConverter.cs - converts a Program body between the
// .pcg on-disk storage format and the wire SysEx Object Dump format. Confirmed 2026-09-23 by
// pulling a real Nautilus-saved .pcg (111-Test.PCG) back from hardware: every Program record
// in that file - both HD-1 and EXi - is a uniform 4954-byte slot (NautilusPcgObjectExtractor's
// itemSize), exactly mirroring Kronos's own "fixed max-engine-size .pcg slot, engine-specific
// wire size" layout (see ProgramFormatConverter's class comment, PcgSlotSize=4960 there). Same
// truncation relationship assumed to hold here (EXi = byte-identical at 4954; HD-1 wire dump =
// an exact prefix truncation of the .pcg slot's first 3700 bytes) - not yet independently
// byte-diff-confirmed against a live HD-1 SysEx dump the way the Kronos-side class was, but
// consistent with every other Kronos<->Nautilus structural parity found this session (same
// container/chunk/checksum layer, same object-dump function codes, same offset-shift-not-
// reshuffle pattern for the wire body itself - see ProgramConverter.ToNautilusLayout).
static class NautilusProgramFormatConverter
{
    public const int PcgSlotSize = 4954;
    public const int WireSizeExi = 4954;
    public const int WireSizeHd1 = 3700;

    public static byte[] PcgToWire(byte[] pcgBody, bool isExi)
    {
        if (pcgBody.Length != PcgSlotSize)
            throw new ArgumentException($"expected a {PcgSlotSize}-byte Nautilus .pcg Program record, got {pcgBody.Length}");
        if (isExi) return pcgBody;
        var wire = new byte[WireSizeHd1];
        Array.Copy(pcgBody, wire, WireSizeHd1);
        return wire;
    }
}
