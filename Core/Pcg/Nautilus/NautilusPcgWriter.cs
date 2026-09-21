namespace KronosScreenRemote;

using System.Text;

// Purpose-built Nautilus .pcg file writer (Q13: exactly what conversion produces, not a
// general "export any object" tool) - writes ONE converted Program, Drum Kit, or Wave
// Sequence into a minimal, standalone, otherwise-empty .pcg file (never more than one object,
// never Combi/Set List - out of v1 scope per Q1). First writer of any kind in either
// KronosScreenRemote or the Kronos-side codebase this session found - no prior art to mirror
// beyond the READ-side container spec.
//
// Container format from Z:\kronosology\docs\interfaces\pcg_file_format.md §2 (hardware-
// verified against 32 real Kronos files) - the container/chunk/checksum LAYER is confirmed
// identical on Nautilus (NautilusPcgFile/NautilusPcgObjectExtractor already read real Nautilus
// files with this same layer unmodified), but writing it exposed real ambiguity the read side
// never had to resolve:
//
// - Two DIFFERENT chunk header shapes exist: top-level chunks (PCG1/DIV1/PRG1/etc, 12 bytes:
//   tag+size(BE)+dwX) vs the 24-byte sub-chunk header inside them (MBK1/PBK1/etc, already
//   documented in PcgObjectExtractor.cs). This writer produces both.
// - The outer PCG1 wrapper's declared `size` field: the doc's own formula ("file size − 0x10 −
//   4") doesn't cleanly resolve to a consistent header-shape interpretation on inspection. This
//   writer instead treats PCG1 as using the SAME 12-byte header convention as every other
//   top-level chunk (confirmed precisely, via the doc's own stepping formula "next = this + 12
//   + declared_payload_length", for DIV1/SLS1/PRG1/CMB1/DKT1/WSQ1/GLB1) for internal
//   consistency - i.e. `size` = bytes from PCG1's payload start (offset 0x1C) through EOF. This
//   is a DELIBERATE, DOCUMENTED DEVIATION from the doc's literal formula, not an oversight -
//   unverified against real hardware, flagged for Phase 5's spot-check.
// - The doc notes a `FF FF 00 00` 4-byte sequence observed right after DIV1's 44-byte payload
//   in real files, but the doc's own stepping formula has no extra gap term and "reproduces the
//   master chunk-order list exactly" without one - this writer follows the FORMULA (no extra
//   gap), treating the trailer note as an unconfirmed curiosity rather than a requirement.
// - This writer does NOT resize/truncate the Program body to Nautilus's own declared itemsize
//   (4954, confirmed 6 bytes smaller than Kronos's 4960) - it writes exactly whatever length
//   body the caller passes (itemSize = body.Length). ProgramConverter.KronosToNautilus doesn't
//   resize either, so a body converted from a full Kronos .pcg-file record will currently come
//   out 4960 bytes, 6 bytes LARGER than a real Nautilus record's declared size. What those
//   trailing ~6 bytes represent on Nautilus is unresolved by this session's work - left as an
//   explicit caveat rather than guessed at.
static class NautilusPcgWriter
{
    const byte ModelNautilus = 0xDD;

    // Fixed DIV1 slot-count constants per §2.3a - total possible slots for each object type,
    // NOT how many are actually written (confirmed unchanging between near-empty and near-full
    // real files).
    const int ProgramSlotCount = 21, CombiSlotCount = 14, DrumKitSlotCount = 15, WaveSeqSlotCount = 15;

    // Writes a single converted Program body (from ProgramConverter.KronosToNautilus) into a
    // minimal standalone .pcg file, in Int bank slot 0 (I-A, DIV1 bit 0).
    public static byte[] WriteSingleProgram(byte[] nautilusProgramBody, bool isExi) =>
        WriteSingleObject(Div1Block.Program, "PRG1", isExi ? "MBK1" : "PBK1", nautilusProgramBody);

    public static byte[] WriteSingleDrumKit(byte[] nautilusDrumKitBody) =>
        WriteSingleObject(Div1Block.DrumKit, "DKT1", "DBK1", nautilusDrumKitBody);

    public static byte[] WriteSingleWaveSequence(byte[] nautilusWaveSequenceBody) =>
        WriteSingleObject(Div1Block.WaveSequence, "WSQ1", "WBK1", nautilusWaveSequenceBody);

    enum Div1Block { Program, DrumKit, WaveSequence }

    // Shared by all 3 single-object writers above: one top-level wrapper chunk (PRG1/DKT1/
    // WSQ1) containing one sub-chunk (tag varies) containing one record, in Int bank slot 0
    // (DIV1 bit 0 of the relevant object-type block) - everything else in the file stays
    // "nothing present" shaped. Combi and Set List are never written by this writer at all
    // (out of v1 scope per Q1).
    static byte[] WriteSingleObject(Div1Block block, string topLevelTag, string subChunkTag, byte[] body)
    {
        byte[] subChunk = BuildSubChunk(subChunkTag, count: 1, itemSize: body.Length, bankIdRaw: 0, body);
        byte[] topLevel = BuildTopLevelChunk(topLevelTag, subChunk);
        byte[] div1 = BuildTopLevelChunk("DIV1", BuildDiv1Payload(block, wordA: 0x0001));

        byte[] pcg1Payload = Concat(div1, topLevel);
        byte[] pcg1 = BuildTopLevelChunk("PCG1", pcg1Payload);

        byte[] header = BuildFileHeader();
        return Concat(header, pcg1);
    }

    static byte[] BuildFileHeader()
    {
        var h = new byte[16];
        Encoding.ASCII.GetBytes("KORG").CopyTo(h, 0);
        h[4] = ModelNautilus;
        h[5] = 0x00;   // file type: PCG
        h[6] = 0x02;   // unaccounted byte, always 0x02 in every real file seen (Kronos and Nautilus alike)
        h[7] = 0x04;   // OS/checksum-gen byte - 4 matches the real Nautilus unit's own current-firmware export (Sept8_2026.PCG), the more recent of the two real values seen (PRELOAD.PCG's factory image reads 3)
        h[8] = 0x01;   // checksum-present flag - both real Nautilus files read 1
        return h;
    }

    // §2.3a: [00 01 00 00 00 00 00 00 fixed] then one 8-byte block per object type, in FIXED
    // ORDER Program/Combi/DrumKit/WaveSequence ([wordA BE][count BE][wordB BE][count BE]), then
    // a 4-byte Global/SetList/DPI flags dword. Only ONE block's wordA is ever real data (which
    // one depends on which single-object writer called this) - everything else is a correctly-
    // shaped "nothing else present" block. This writer never emits Combi/SetList/Global/
    // DrumTrackPattern top-level chunks at all, and only ever one of Program/DrumKit/
    // WaveSequence per file (the single-object scope Q13 calls for).
    static byte[] BuildDiv1Payload(Div1Block block, ushort wordA)
    {
        var p = new List<byte>(44);
        p.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        AppendBitmaskBlock(p, block == Div1Block.Program ? wordA : (ushort)0, ProgramSlotCount, wordB: 0);
        AppendBitmaskBlock(p, wordA: 0, CombiSlotCount, wordB: 0);
        AppendBitmaskBlock(p, block == Div1Block.DrumKit ? wordA : (ushort)0, DrumKitSlotCount, wordB: 0);
        AppendBitmaskBlock(p, block == Div1Block.WaveSequence ? wordA : (ushort)0, WaveSeqSlotCount, wordB: 0);
        p.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });   // has-DPI1=0, has-SLS1=0, byte2 unconfirmed=0, has-GLB1=0
        return p.ToArray();
    }

    static void AppendBitmaskBlock(List<byte> p, ushort wordA, int count, ushort wordB)
    {
        AppendBE16(p, wordA);
        AppendBE16(p, (ushort)count);
        AppendBE16(p, wordB);
        AppendBE16(p, (ushort)count);
    }

    // Top-level chunk: [4-byte tag][4-byte BE size][4-byte dwX, unknown - written 0][payload].
    // `size` = payload.Length, matching the doc's own confirmed stepping formula
    // "next_chunk_tag_offset = this_chunk_tag_offset + 12 + declared_payload_length".
    static byte[] BuildTopLevelChunk(string tag, byte[] payload)
    {
        var b = new byte[12 + payload.Length];
        Encoding.ASCII.GetBytes(tag).CopyTo(b, 0);
        WriteBE32(b, 4, payload.Length);
        // bytes 8-11 (dwX): left 0, unknown/unused by every reader that's read this container so far
        payload.CopyTo(b, 12);
        return b;
    }

    // Sub-chunk: 24-byte header (tag, length, reserved-dword-with-checksum-in-last-byte, count,
    // itemSize, bankId) + count*itemSize bytes of records - same shape
    // PcgObjectExtractor/NautilusPcgObjectExtractor already read. `length` at +4 is documented
    // by the reader as "unused" - written here as (24-8) + count*itemSize, i.e. everything
    // after the tag+length fields themselves, by analogy with the top-level chunk's own
    // confirmed size-field convention.
    static byte[] BuildSubChunk(string tag, int count, int itemSize, int bankIdRaw, byte[] singleRecordBody)
    {
        var b = new byte[24 + count * itemSize];
        Encoding.ASCII.GetBytes(tag).CopyTo(b, 0);
        WriteBE32(b, 4, 16 + count * itemSize);
        // bytes 8-11: reserved/meta dword - checksum goes in the last byte (+11), rest 0
        WriteBE32(b, 12, count);
        WriteBE32(b, 16, itemSize);
        WriteBE32(b, 20, bankIdRaw);
        singleRecordBody.CopyTo(b, 24);

        int checksum = ComputeChecksum(b, 12, 24 + count * itemSize);
        b[11] = (byte)checksum;
        return b;
    }

    static int ComputeChecksum(byte[] data, int startInclusive, int endExclusive)
    {
        int sum = 0;
        for (int i = startInclusive; i < endExclusive; i++) sum = (sum + data[i]) & 0xFF;
        return sum;
    }

    static void AppendBE16(List<byte> p, ushort v)
    {
        p.Add((byte)(v >> 8));
        p.Add((byte)v);
    }

    static void WriteBE32(byte[] b, int offset, int v)
    {
        b[offset]     = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var part in parts) { part.CopyTo(result, pos); pos += part.Length; }
        return result;
    }
}
