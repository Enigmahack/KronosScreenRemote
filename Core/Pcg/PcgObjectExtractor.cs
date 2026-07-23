namespace KronosScreenRemote;

using System.Text;

// One Program/Combi/Set List record recovered from a .pcg file. IsExi is only meaningful
// for Program (which bank tag — MBK1 vs PBK1 — it was extracted from); ignored for Combi/
// Set List, which have no such split. See ProgramFormatConverter for why this matters.
sealed record PcgObjectEntry(ObjLoc Loc, byte[] Body, string Name, bool IsExi = false);

// A candidate bank chunk (one of the four known tags, found literally in the file) whose
// header didn't validate, or whose bankId didn't resolve to a real bank — see Extract's
// `rejected` out-param.
sealed record PcgRejectedBank(string Tag, long Offset, int Count, int ItemSize, int BankIdRaw, string Reason);

// Extracts raw Program/Combi/Set List records directly from a .pcg file's bytes.
//
// Container format per Documentation/PCG Structure Kronos.txt (a third-party
// REVERSE-ENGINEERING DOC, not third-party code). "KORG" file header, then a chunk-tag(4
// ASCII)+length(4 BE) container nested at multiple levels. The doc documents two shallow
// directory passes before the real payload, and — for Set Lists specifically — TWO
// different encodings (an earlier SLS1/SLD1/SDB1 section and a later STL1/SBK1 section)
// whose relationship the doc's own author leaves unresolved.
//
// Rather than model that ambiguous outer structure exactly, this parser scans the file for
// the four sub-chunk tags that carry real object data with a self-describing header —
// MBK1/PBK1 (Program banks), CBK1 (Combi banks), SBK1 (Set List bank, the STL1 encoding) —
// and validates each candidate via its own declared count/item-size fields before trusting
// it, rather than trusting tag or position alone. A stray 4-byte sequence matching a tag
// inside unrelated binary parameter data fails validation and is just skipped.
//
// Header shape common to all four (24 bytes):
//   +0x00 tag (4 ASCII)     +0x04 chunk length (BE, unused here)   +0x08 reserved/meta (BE)
//   +0x0C count (BE)        +0x10 item size (BE)                  +0x14 bank id (BE, see below)
//   +0x18 first record (raw body, `count` of them, `item size` bytes each)
//
// VERIFIED against a real factory PRELOAD.PCG (Z:\RestoreDVD_SystemMNT\...\FACTORY\PRELOAD.PCG):
// header layout, itemSize (4960 Program / 7810 Combi / 69416 Set List), and the 24-byte
// name field at each record's offset 0 all checked out against real, readable factory
// program/combi/set-list names at the decoded locations.
//
// The `bankId` field (+0x14) is NOT a plain linear bank index — Korg's own on-disk encoding
// only assigns literal 0..4 to Program banks I-A..I-E; I-F gets a dedicated flag value
// (0x8000) instead of continuing the sequence; everything from there on (all 14 user banks)
// resumes as 0x20000+N.
//
// CRITICAL ASYMMETRY, confirmed against real files: Program has only 6 int banks (I-A..I-F)
// — there is NO Program "I-G". Combi genuinely has 7 int banks (I-A..I-G). This is NOT the
// same thing as this codebase's KronosBanks/ObjectTypeRegistry model, which gives Program 7
// "editable banks" (0x00-0x06) for the LIVE SysEx side — that's a separate concept (whatever
// ObjBank 0x06 addresses over MIDI, if anything) and does not correspond to a real bankId
// slot in the .pcg file's own numbering. An earlier version of this decoder routed Program
// through that 7-int-bank EditableBanks() list, which silently shifted every user bank's
// index down by one and dropped the LAST bank (U-GG) out of the valid range entirely —
// caught by loading a real user file with confirmed U-GG content: bankId 0x2000D was present
// in the file the whole time, just mis-decoded as U-FF. Confirmed via the reference PCG
// Tools codebase's own Kronos model (KronosProgramBanks.CreateBanks() in
// Z:\PCG Tools_enigmahack\PCG-Tools\KorgKronosTools\Model\KronosSpecific\Synth\
// KronosProgramBanks.cs), which defines exactly 6 int Program banks before user banks begin
// — and by the doc's own DIV1 bank-presence bitmap, which lists only I-A..I-F for Programs
// (vs I-A..I-G for Combis). Program is decoded directly to an ObjBank value (DecodeProgramObjBank,
// below) with no EditableBanks() indirection; Combi (7 int banks, matching its own
// EditableBanks() list) is unaffected and still decodes via that indirection.
//
// PROGRAM FORMAT: every .pcg Program record is a fixed 4960-byte slot, whether the bank's
// tag is MBK1 (EXi) or PBK1 (HD-1) — but that's the ON-DISK size, not necessarily the wire
// SysEx Object Dump size for that program. Verified against ~1000 real hardware-pulled
// Program bodies (this app's own local_library cache) cross-referenced by name against a
// real factory PRELOAD.PCG: EXi programs dump over wire at the full 4960 bytes, byte-
// identical to the .pcg slot (0 structural differences across 397 real same-named pairs);
// HD-1 programs dump over wire at only 3706 bytes, which is an exact truncation of the
// .pcg slot's first 3706 bytes (620/632 real same-named pairs matched exactly; the
// remainder differed by ordinary patch-content edits, not a byte-offset pattern). See
// ProgramFormatConverter for the actual PCG->wire conversion, and IsExi below for how each
// record's bank type is captured (from which tag — MBK1 or PBK1 — governed its bank).
static class PcgObjectExtractor
{
    const int HeaderSize = 24;

    // See the class-level comment: Korg's on-disk bank-id encoding for Program banks, decoded
    // directly to an ObjBank value (0x00-0x05 int, 0x40-0x4D user) — there is no Program
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

    static readonly Dictionary<string, int> BankChunkObjType = new()
    {
        ["MBK1"] = LibObj.Program,
        ["PBK1"] = LibObj.Program,
        ["CBK1"] = LibObj.Combi,
        ["SBK1"] = LibObj.SetList,
    };

    public static List<PcgObjectEntry> Extract(byte[] data) => Extract(data, out _);

    // The `rejected` list is a diagnostic: every position where one of the four tags
    // literally matched but its header didn't validate (or its bankId didn't resolve to a
    // real bank) — most of these are coincidental 4-byte matches inside unrelated binary
    // parameter data, but a bank silently missing from the extracted tree (e.g. a real
    // Program bank whose bankId encoding turns out to need another special case we haven't
    // seen yet) will show up here too, which a synthetic self-test never can. Surfaced by
    // PcgPaneViewModel so it's visible instead of just "the bank isn't in the tree."
    public static List<PcgObjectEntry> Extract(byte[] data, out List<PcgRejectedBank> rejected)
    {
        var results = new List<PcgObjectEntry>();
        rejected = new List<PcgRejectedBank>();
        int pos = 0;
        while (pos + HeaderSize <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            if (BankChunkObjType.TryGetValue(tag, out int objType))
            {
                if (TryReadBank(data, pos, objType, tag == "MBK1", results, out int consumed, out var reason))
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

    static bool TryReadBank(byte[] data, int offset, int objType, bool isExi, List<PcgObjectEntry> results, out int consumed, out PcgRejectedBank? rejected)
    {
        consumed = 0;
        rejected = null;
        string tag = Encoding.ASCII.GetString(data, offset, 4);
        int count = ReadBE32(data, offset + 0x0C);
        int itemSize = ReadBE32(data, offset + 0x10);
        int bankIdRaw = ReadBE32(data, offset + 0x14);

        if (count is < 1 or > 128)
        {
            rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw, $"count {count} out of range 1..128");
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
            objBank = 0;   // Set Lists have no per-object-type bank — same convention as the live path
        }
        else if (objType == LibObj.Program)
        {
            objBank = DecodeProgramObjBank(bankIdRaw);
            if (objBank < 0)
            {
                rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw,
                    $"bankId 0x{bankIdRaw:X} didn't decode to a valid Program bank");
                return false;   // bankIdRaw doesn't resolve — not a real bank header
            }
        }
        else   // Combi — genuinely has 7 int banks, matching its own EditableBanks() list
        {
            int bankIndex = DecodeCombiBankIndex(bankIdRaw);
            var editableBanks = ObjectTypeRegistry.Get(objType).EditableBanks().ToList();
            if (bankIndex < 0 || bankIndex >= editableBanks.Count)
            {
                rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw,
                    $"bankId 0x{bankIdRaw:X} decoded to index {bankIndex}, outside 0..{editableBanks.Count - 1}");
                return false;   // bankIdRaw doesn't resolve — not a real bank header
            }
            objBank = editableBanks[bankIndex];
        }

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

    static string ReadRecordName(int objType, byte[] body) => objType switch
    {
        LibObj.Program => ProgramBody.ReadName(body),
        LibObj.Combi   => CombiBody.ReadName(body),
        LibObj.SetList => SetListBody.FromRawBody(0, body)?.Name ?? "",
        _ => "",
    };

    static int ReadBE32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
