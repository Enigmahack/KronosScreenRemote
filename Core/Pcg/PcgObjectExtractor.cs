namespace KronosScreenRemote;

using System.Text;

// One Program/Combi/Set List record recovered from a .pcg file. IsExi is only meaningful
// for Program (which bank tag - MBK1 vs PBK1 - it was extracted from); ignored for Combi/
// Set List, which have no such split. See ProgramFormatConverter for why this matters.
sealed record PcgObjectEntry(ObjLoc Loc, byte[] Body, string Name, bool IsExi = false);

// A candidate bank chunk (one of the four known tags, found literally in the file) whose
// header didn't validate, or whose bankId didn't resolve to a real bank - see Extract's
// `rejected` out-param.
sealed record PcgRejectedBank(string Tag, long Offset, int Count, int ItemSize, int BankIdRaw, string Reason);

// A bank chunk that DID validate and DID get extracted, but whose stored checksum byte
// (chunk.Offset+11, per kronosology/docs/interfaces/pcg_file_format.md §12) doesn't match
// the bytes actually on disk. This is advisory, not a rejection: the records are still
// extracted and usable (Korg's own algorithm is a simple mod-256 sum, easy to get "right by
// luck" on garbage, and conversely a real, structurally valid bank can carry a stale
// checksum from a tool - noted in §12 - that wrote payload bytes without recomputing it).
// What this catches is the case that matters most for a "Load PCG..." file picker: a
// truncated/corrupted download or a hand-edited file whose bytes are no longer what the
// Kronos itself wrote - the file still parses, but silently trusting it risks pushing
// garbage into Local Library. Surfaced by PcgPaneViewModel.Load exactly like RejectedBanks.
sealed record PcgChecksumWarning(string Tag, long Offset, int Expected, int Actual);

// Extracts raw Program/Combi/Set List/Drum Kit/Wave Sequence/Global records directly from a
// .pcg file's bytes.
//
// Container format per kronosology/docs/interfaces/pcg_file_format.md §2.2/§2.3.
// "KORG" file header, a fixed-offset DIV1 chunk, then a flat top-level chunk walk (DIV1 ->
// SLS1 -> PRG1 -> CMB1 -> DKT1 -> WSQ1 -> GLB1 -> DPI1) where each top-level chunk has its
// own hand-written descent logic for its sub-chunks - PRG1 nests MBK1/PBK1, CMB1 nests CBK1,
// SLS1 nests SLD1 then STL1 (STL1 nests SBK1).
//
// Rather than walk that outer structure level by level, this parser scans the file directly
// for the sub-chunk tags that carry real object data with a self-describing header -
// MBK1/PBK1 (Program banks), CBK1 (Combi banks), SBK1 (Set List bank), DBK1 (Drum Kit banks),
// WBK1 (Wave Sequence banks), GLB1 (Global, a singleton - see its own branch below) - and
// validates each candidate via its own declared count/item-size fields and its stored
// checksum (see PcgChecksumWarning) before trusting it, rather than trusting tag or position
// alone. A stray 4-byte sequence matching a tag inside unrelated binary parameter data fails
// validation and is just skipped. This is a deliberate simplification, not a workaround for
// an unresolved structure: DIV1 is itself "a redundant table-of-contents the loader doesn't
// need" (§2.3) - the real Kronos firmware discovers banks the same way, by which sub-chunks
// it actually finds while descending, not by trusting DIV1's bitmap.
//
// Header shape common to all of these (24 bytes):
//   +0x00 tag (4 ASCII)     +0x04 chunk length (BE, unused here)   +0x08 reserved/meta (BE)
//   +0x0C count (BE)        +0x10 item size (BE)                  +0x14 bank id (BE, see below)
//   +0x18 first record (raw body, `count` of them, `item size` bytes each)
//
// The `bankId` field (+0x14) is NOT a plain linear bank index - Korg's own on-disk encoding
// only assigns literal 0..4 to Program banks I-A..I-E; I-F gets a dedicated flag value
// (0x8000) instead of continuing the sequence; everything from there on (all 14 user banks)
// resumes as 0x20000+N.
//
// CRITICAL ASYMMETRY: Program has only 6 int banks (I-A..I-F) - there is NO Program "I-G".
// Combi genuinely has 7 int banks (I-A..I-G). This is NOT the same thing as this codebase's
// KronosBanks/ObjectTypeRegistry model, which gives Program 7 "editable banks" (0x00-0x06)
// for the LIVE SysEx side - that's a separate concept (whatever ObjBank 0x06 addresses over
// MIDI, if anything) and does not correspond to a real bankId slot in the .pcg file's own
// numbering. Routing Program through that 7-int-bank EditableBanks() list here would
// silently shift every user bank's index down by one and drop the LAST bank (U-GG) out of
// the valid range entirely. Program is decoded directly to an ObjBank value
// (DecodeProgramObjBank, below) with no EditableBanks() indirection; Combi (7 int banks,
// matching its own EditableBanks() list) is unaffected and still decodes via that
// indirection.
//
// PROGRAM FORMAT: every .pcg Program record is a fixed 4960-byte slot, whether the bank's
// tag is MBK1 (EXi) or PBK1 (HD-1) - but that's the ON-DISK size, not necessarily the wire
// SysEx Object Dump size for that program: EXi programs dump over wire at the full 4960
// bytes, byte-identical to the .pcg slot; HD-1 programs dump over wire at only 3706 bytes,
// an exact truncation of the .pcg slot's first 3706 bytes. See ProgramFormatConverter for
// the actual PCG->wire conversion, and IsExi below for how each record's bank type is
// captured (from which tag - MBK1 or PBK1 - governed its bank).
static class PcgObjectExtractor
{
    const int HeaderSize = 24;

    // See the class-level comment: Korg's on-disk bank-id encoding for Program banks, decoded
    // directly to an ObjBank value (0x00-0x05 int, 0x40-0x4D user) - there is no Program
    // "I-G", so no EditableBanks() indirection here. Literal 0..4 for I-A..I-E; 0x8000 is a
    // dedicated flag for I-F; 0x20000+N (N=0..13) maps directly to U-A..U-GG.
    static int DecodeProgramObjBank(int bankIdRaw)
    {
        if (bankIdRaw == 0x8000) return 0x05;       // I-F
        if (bankIdRaw < 0x8000) return bankIdRaw;   // I-A..I-E (literal 0x00..0x04)
        int n = bankIdRaw - 0x20000;
        return n is >= 0 and <= 13 ? 0x40 + n : -1; // U-A..U-GG
    }

    // Combi has no I-F-style split (7 int banks, all encoded as literal 0..6); 0x20000+N
    // resumes from the first user bank (N=0).
    static int DecodeCombiBankIndex(int bankIdRaw) =>
        bankIdRaw < 0x20000 ? bankIdRaw : bankIdRaw - 0x20000 + 7;

    // Drum Kit and Wave Sequence share one bank-id scheme, distinct from both Program's and
    // Combi's above: a single Int bank (raw 0) plus 14 User banks at 0x20000+N - see
    // pcg_file_format.md §2.4. Decoded straight to the live obj-dump bank number
    // (0=Int, 0x40+N=User) so ObjLoc matches what ObjectTypeRegistry's descriptors expect.
    static int DecodeDrumKitOrWaveSeqBank(int bankIdRaw)
    {
        if (bankIdRaw == 0) return 0;
        int n = bankIdRaw - 0x20000;
        return n is >= 0 and <= 13 ? 0x40 + n : -1;
    }

    // GLB1 is NOT a bank chunk - see TryReadGlobal for its own (shorter, singleton) header
    // shape, hex-verified against real hardware-written files.
    static readonly Dictionary<string, int> BankChunkObjType = new()
    {
        ["MBK1"] = LibObj.Program,
        ["PBK1"] = LibObj.Program,
        ["CBK1"] = LibObj.Combi,
        ["SBK1"] = LibObj.SetList,
        ["DBK1"] = LibObj.DrumKit,
        ["WBK1"] = LibObj.WaveSequence,
    };

    public static List<PcgObjectEntry> Extract(byte[] data) => Extract(data, out _, out _);

    public static List<PcgObjectEntry> Extract(byte[] data, out List<PcgRejectedBank> rejected) =>
        Extract(data, out rejected, out _);

    // The `rejected` list is a diagnostic: every position where one of the four tags
    // literally matched but its header didn't validate (or its bankId didn't resolve to a
    // real bank) - most of these are coincidental 4-byte matches inside unrelated binary
    // parameter data, but a bank silently missing from the extracted tree (e.g. a real
    // Program bank whose bankId encoding turns out to need another special case we haven't
    // seen yet) will show up here too, which a synthetic self-test never can. Surfaced by
    // PcgPaneViewModel so it's visible instead of just "the bank isn't in the tree."
    //
    // `checksumWarnings` is a second, non-rejecting diagnostic - see PcgChecksumWarning.
    public static List<PcgObjectEntry> Extract(byte[] data, out List<PcgRejectedBank> rejected, out List<PcgChecksumWarning> checksumWarnings)
    {
        var results = new List<PcgObjectEntry>();
        rejected = new List<PcgRejectedBank>();
        checksumWarnings = new List<PcgChecksumWarning>();
        int pos = 0;
        while (pos + HeaderSize <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            if (tag == "GLB1")
            {
                if (TryReadGlobal(data, pos, results, checksumWarnings, out int consumed, out var reason))
                {
                    pos += consumed;
                    continue;
                }
                if (reason != null) rejected.Add(reason);
            }
            else if (BankChunkObjType.TryGetValue(tag, out int objType))
            {
                if (TryReadBank(data, pos, objType, tag == "MBK1", results, checksumWarnings, out int consumed, out var reason))
                {
                    pos += consumed;
                    continue;
                }
                if (reason != null) rejected.Add(reason);
            }
            pos++;
        }
        return results;
    }

    static bool TryReadBank(byte[] data, int offset, int objType, bool isExi, List<PcgObjectEntry> results, List<PcgChecksumWarning> checksumWarnings, out int consumed, out PcgRejectedBank? rejected)
    {
        consumed = 0;
        rejected = null;
        string tag = Encoding.ASCII.GetString(data, offset, 4);
        int count = ReadBE32(data, offset + 0x0C);
        int itemSize = ReadBE32(data, offset + 0x10);
        int bankIdRaw = ReadBE32(data, offset + 0x14);

        // Upper bound covers the largest real bank seen (WBK1 Int = 150) - still tight enough
        // that a coincidental tag match needs a plausible count AND itemSize AND in-file
        // records to slip through (see recordsEnd/itemSize checks below).
        if (count is < 1 or > 200)
        {
            rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw, $"count {count} out of range 1..200");
            return false;
        }
        if (itemSize is < 64 or > 200_000)   // sane range for a Kronos object body
        {
            rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw, $"itemSize {itemSize} out of range 64..200000");
            return false;
        }
        long recordsEnd = (long)offset + HeaderSize + (long)count * itemSize;
        if (recordsEnd > data.Length)
        {
            rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw, "records would run past end of file");
            return false;
        }

        int objBank;
        if (objType == LibObj.SetList)
        {
            objBank = 0;   // Set Lists have no per-object-type bank - same convention as the live path
        }
        else if (objType == LibObj.Program)
        {
            objBank = DecodeProgramObjBank(bankIdRaw);
            if (objBank < 0)
            {
                rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw,
                    $"bankId 0x{bankIdRaw:X} didn't decode to a valid Program bank");
                return false;   // bankIdRaw doesn't resolve - not a real bank header
            }
        }
        else if (objType == LibObj.DrumKit || objType == LibObj.WaveSequence)
        {
            objBank = DecodeDrumKitOrWaveSeqBank(bankIdRaw);
            if (objBank < 0)
            {
                rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw,
                    $"bankId 0x{bankIdRaw:X} didn't decode to a valid {(objType == LibObj.DrumKit ? "Drum Kit" : "Wave Sequence")} bank");
                return false;
            }
        }
        else   // Combi - genuinely has 7 int banks, matching its own EditableBanks() list
        {
            int bankIndex = DecodeCombiBankIndex(bankIdRaw);
            var editableBanks = ObjectTypeRegistry.Get(objType).EditableBanks().ToList();
            if (bankIndex < 0 || bankIndex >= editableBanks.Count)
            {
                rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw,
                    $"bankId 0x{bankIdRaw:X} decoded to index {bankIndex}, outside 0..{editableBanks.Count - 1}");
                return false;   // bankIdRaw doesn't resolve - not a real bank header
            }
            objBank = editableBanks[bankIndex];
        }

        // §12: checksum byte at offset+11 (last byte of the 12-byte chunk header) should equal
        // sum(payload from offset+12 through recordsEnd) mod 256 - i.e. the count/itemSize/
        // bankId sub-header plus every record. Advisory only - see PcgChecksumWarning for why
        // this doesn't reject the bank.
        int actualChecksum = data[offset + 11];
        int expectedChecksum = ComputeChecksum(data, offset + 12, recordsEnd);
        if (actualChecksum != expectedChecksum)
            checksumWarnings.Add(new PcgChecksumWarning(tag, offset, expectedChecksum, actualChecksum));

        var entries = new List<PcgObjectEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int recordOffset = offset + HeaderSize + i * itemSize;
            var body = new byte[itemSize];
            Array.Copy(data, recordOffset, body, 0, itemSize);
            entries.Add(new PcgObjectEntry(new ObjLoc(objType, objBank, i), body, ReadRecordName(objType, body), isExi));
        }

        results.AddRange(entries);
        consumed = HeaderSize + count * itemSize;
        return true;
    }

    // GLB1 is not a bank chunk (see TryReadBank's 24-byte header) - it's a shorter 12-byte
    // header (tag + declared size (BE) + reserved/checksum dword) immediately followed by the
    // single Global payload, `size` bytes long. No count/itemSize/bankId sub-fields - Global
    // is always exactly one record. Hex-verified against a real hardware-written file: bytes
    // at offset+12 read `00 00 08 02...`, matching pcg_file_format.md §8's own quote of
    // Global's payload start, which resolves that doc's "payload+0 vs +12 vs +16" open
    // question - it's +12.
    const int GlobalHeaderSize = 12;

    static bool TryReadGlobal(byte[] data, int offset, List<PcgObjectEntry> results, List<PcgChecksumWarning> checksumWarnings, out int consumed, out PcgRejectedBank? rejected)
    {
        consumed = 0;
        rejected = null;
        int size = ReadBE32(data, offset + 4);

        if (size is < 64 or > 200_000)
        {
            rejected = new PcgRejectedBank("GLB1", offset, 1, size, 0, $"declared size {size} out of range 64..200000");
            return false;
        }
        long recordsEnd = (long)offset + GlobalHeaderSize + size;
        if (recordsEnd > data.Length)
        {
            rejected = new PcgRejectedBank("GLB1", offset, 1, size, 0, "payload would run past end of file");
            return false;
        }

        int actualChecksum = data[offset + 11];
        int expectedChecksum = ComputeChecksum(data, offset + 12, recordsEnd);
        if (actualChecksum != expectedChecksum)
            checksumWarnings.Add(new PcgChecksumWarning("GLB1", offset, expectedChecksum, actualChecksum));

        var body = new byte[size];
        Array.Copy(data, offset + GlobalHeaderSize, body, 0, size);
        results.Add(new PcgObjectEntry(new ObjLoc(LibObj.Global, 0, 0), body, "", false));

        consumed = GlobalHeaderSize + size;
        return true;
    }

    static string ReadRecordName(int objType, byte[] body) => objType switch
    {
        LibObj.Program      => ProgramBody.ReadName(body),
        LibObj.Combi        => CombiBody.ReadName(body),
        LibObj.SetList      => SetListBody.FromRawBody(0, body)?.Name ?? "",
        LibObj.DrumKit      => DrumKitBody.ReadName(body),
        LibObj.WaveSequence => WaveSequenceBody.ReadName(body),
        _ => "",
    };

    static int ReadBE32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    static int ComputeChecksum(byte[] data, long startInclusive, long endExclusive)
    {
        int sum = 0;
        for (long i = startInclusive; i < endExclusive; i++) sum = (sum + data[i]) & 0xFF;
        return sum;
    }
}
