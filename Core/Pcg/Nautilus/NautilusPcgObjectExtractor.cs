namespace KronosScreenRemote;

using System.Text;

// One Program/DrumKit/WaveSequence record recovered from a Nautilus .pcg file. BankIdRaw is
// the file's own bank-id field, still exposed UNDECODED here (Phase 2's scope is read-only
// extraction by name + raw body, nothing more) - but the mapping IS now hardware-confirmed for
// all 3 v1 object types (2026-09-16, live SHA-1 Bank Digest cross-match against a real unit -
// see pcg_format_kronos_vs_nautilus.md's "Program BankIdRaw decoded" section): every type's
// single/first Int bank is `bankIdRaw == wireBank` (0), and every type's User banks are
// `wireBank = 0x40+n <-> bankIdRaw = 0x20000+n`. Program additionally has a 6th Int bank (F)
// at wire 0x05 <-> bankIdRaw 0x8000 (inferred by elimination, not hash-confirmed - Program's
// own content had changed since the reference backup; DrumKit/WaveSequence got a full,
// exact-hash-confirmed match on every single bank instead, since neither had been edited).
sealed record NautilusPcgObjectEntry(int ObjType, int BankIdRaw, int Index, byte[] Body, string Name, bool IsExi = false);

// Extracts raw Program/Drum Kit/Wave Sequence records directly from a Nautilus .pcg file's
// bytes. Mirrors PcgObjectExtractor's scan-for-known-tags approach exactly - the container/
// chunk/header/checksum layer is confirmed byte-identical between Kronos and Nautilus (see
// NautilusPcgFile) - but deliberately does NOT decode BankIdRaw into a semantic bank/slot the
// way PcgObjectExtractor does (see NautilusPcgObjectEntry).
//
// v1 scope only: MBK1/PBK1 (Program), DBK1 (Drum Kit), WBK1 (Wave Sequence). CBK1 (Combi) and
// SBK1 (Set List) are out of scope per the PCG converter plan (Q1); GLB1 and the new ABK1/ARP1
// (Nautilus's KARMA-replacement arp-pattern bank) aren't needed for Program/DrumKit/WaveSeq
// extraction and are simply skipped like any other unrecognized chunk - the scan naturally
// steps past them without special-casing, same as PcgObjectExtractor does for chunks it
// doesn't know either.
static class NautilusPcgObjectExtractor
{
    const int HeaderSize = 24;

    static readonly Dictionary<string, int> BankChunkObjType = new()
    {
        ["MBK1"] = LibObj.Program,
        ["PBK1"] = LibObj.Program,
        ["DBK1"] = LibObj.DrumKit,
        ["WBK1"] = LibObj.WaveSequence,
    };

    public static List<NautilusPcgObjectEntry> Extract(byte[] data) => Extract(data, out _, out _);

    public static List<NautilusPcgObjectEntry> Extract(byte[] data, out List<PcgRejectedBank> rejected, out List<PcgChecksumWarning> checksumWarnings)
    {
        var results = new List<NautilusPcgObjectEntry>();
        rejected = new List<PcgRejectedBank>();
        checksumWarnings = new List<PcgChecksumWarning>();
        int pos = 0;
        while (pos + HeaderSize <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            if (BankChunkObjType.TryGetValue(tag, out int objType))
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

    static bool TryReadBank(byte[] data, int offset, int objType, bool isExi, List<NautilusPcgObjectEntry> results, List<PcgChecksumWarning> checksumWarnings, out int consumed, out PcgRejectedBank? rejected)
    {
        consumed = 0;
        rejected = null;
        string tag = Encoding.ASCII.GetString(data, offset, 4);
        int count = ReadBE32(data, offset + 0x0C);
        int itemSize = ReadBE32(data, offset + 0x10);
        int bankIdRaw = ReadBE32(data, offset + 0x14);

        // Same sanity bounds as PcgObjectExtractor - a coincidental tag match still needs a
        // plausible count AND itemSize AND in-file records to slip through.
        if (count is < 1 or > 200)
        {
            rejected = new PcgRejectedBank(tag, offset, count, itemSize, bankIdRaw, $"count {count} out of range 1..200");
            return false;
        }
        if (itemSize is < 64 or > 200_000)
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

        // §... checksum byte at offset+11, same mod-256 sum-of-payload scheme as Kronos -
        // confirmed 32/32 and 90/90 banks matching across two real Nautilus files. Advisory
        // only, same rationale as PcgObjectExtractor's PcgChecksumWarning.
        int actualChecksum = data[offset + 11];
        int expectedChecksum = ComputeChecksum(data, offset + 12, recordsEnd);
        if (actualChecksum != expectedChecksum)
            checksumWarnings.Add(new PcgChecksumWarning(tag, offset, expectedChecksum, actualChecksum));

        var entries = new List<NautilusPcgObjectEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int recordOffset = offset + HeaderSize + i * itemSize;
            var body = new byte[itemSize];
            Array.Copy(data, recordOffset, body, 0, itemSize);
            entries.Add(new NautilusPcgObjectEntry(objType, bankIdRaw, i, body, ReadRecordName(objType, body), isExi));
        }

        results.AddRange(entries);
        consumed = HeaderSize + count * itemSize;
        return true;
    }

    // Name field (offset 0, 24 bytes, space/NUL padded) is confirmed unchanged on Nautilus -
    // safe to reuse the existing ObjectBody readers directly, they only ever touch bytes 0-23
    // regardless of the record's total length (which differs from Kronos per object type).
    static string ReadRecordName(int objType, byte[] body) => objType switch
    {
        LibObj.Program      => ProgramBody.ReadName(body),
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
