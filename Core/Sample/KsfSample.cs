namespace KronosScreenRemote;

using System.IO;
using System.Text;

// .KSF - single sample (binary, CKorgRiff-framed, big-endian, holds real PCM).
// Chunk order: SMP1 -> SNO1 -> NAME -> SMD1. Direct port of Tools/sample_editor/
// kronos_ksc_format.py's KsfSample, plus the loop-point fields that library predates
// (hardware-confirmed 2026-08-19, see kronosology/docs/interfaces/
// ksc_kmp_ksf_file_format.md §5.1).
sealed class KsfSample
{
    public string Name = "New Sample";
    public string Suffix = "";     // "", "-L", or "-R" - stereo channel marker
    public uint Sno1;
    public uint SampleRate = 44100;
    public byte Flags = 0x81;      // bit 0x80 = one-shot (loop disabled); every real
                                    // unlooped sample has it set
    public byte Channels = 1;      // not code-confirmed as read back anywhere; always 1 in practice
    public byte Bits = 16;         // the only value ever seen
    public byte[] Pcm = [];        // raw big-endian 16-bit signed samples - route through KsfPcm
    public string? Path;

    // SMP1 offsets 16/20/28 (payload), hardware-confirmed 2026-08-19 via a matched
    // LOOP/NOLOOP test pair built on a real Kronos: Sample Start=500, Loop Start=1000,
    // Loop End=5000 round-tripped exactly. Offset 24 (a 4th slot) is handled by
    // _preservedLoopDuplicate below.
    public uint SampleStart;
    public uint LoopStart;
    public uint LoopEnd;

    // The offset-24 duplicate slot: mirrors LoopStart in every one of 73/75 real files
    // examined, but NOT in 5 outlier files (unusual 27778 Hz sample rate, likely
    // legacy/converted content - e.g. LoopStart=0xfffbed64, dup=1, clearly not a
    // mirror). Preserved verbatim when read, same as _smf1, rather than blindly
    // overwritten - confirmed via byte diff that this is the ONLY field those 5 files
    // fail to round-trip on. Null means "not read from a file" (a brand-new sample),
    // in which case ToBytes falls back to mirroring LoopStart, matching the confirmed
    // convention. A caller that edits LoopStart on an existing sample should treat
    // this as stale and call ClearPreservedLoopDuplicate() to re-sync it.
    uint? _preservedLoopDuplicate;

    // Exposed so undo/redo (SampleFieldSnapshot) can snapshot and restore this alongside
    // SampleStart/LoopStart/LoopEnd/Flags - without this, undoing a field edit on one of
    // the 5 outlier files would silently drop its non-mirroring dup value (ClearPreserved-
    // LoopDuplicate falls back to mirroring LoopStart on next save, a real byte change on
    // a file that round-tripped byte-identical before any edit).
    public uint? PreservedLoopDuplicate => _preservedLoopDuplicate;

    public void ClearPreservedLoopDuplicate() => _preservedLoopDuplicate = null;
    public void RestorePreservedLoopDuplicate(uint? value) => _preservedLoopDuplicate = value;

    public bool IsLoopEnabled => (Flags & 0x80) == 0;
    public int FrameCount => Pcm.Length / 2;

    // A real, hardware-observed failure mode (doc §3.3): Eva's own Save can silently
    // write a 124-byte header-only .KSF (frame_count==0) for a sample loaded but never
    // fully read into memory. The single predicate every consumer (waveform view, FTP
    // push guard, export) checks before trusting Pcm.
    public bool IsHeaderOnly => FrameCount == 0;

    // The SMF1 chunk (doc §3.2): observed real chunk order is SMP1 -> SNO1 -> NAME ->
    // SMF1 -> SMD1. Payload meaning is still unconfirmed (a same-multisample zone
    // cross-reference, in every real instance seen so far) - preserved opaquely,
    // verbatim, when present, so round-tripping an existing file that carries one
    // (every real header-only-corrupted .KSF observed does) stays byte-identical.
    // Never written for a brand-new sample (doc's own recommendation: leave unset).
    byte[]? _smf1;

    // Returns null if `data` isn't a recognizable .KSF (first chunk isn't SMP1, or no
    // SMD1 chunk at all) rather than throwing - mirrors PcgFile.Open's contract for
    // "wrong kind of file" being expected file-picker input, not a bug. Requiring SMD1
    // matters beyond format validation: a genuinely truncated download (Phase 2's FTP
    // pull, cut off mid-transfer) must fail loudly here, not silently produce a
    // default-SampleRate/Flags/empty-Pcm object indistinguishable from a real
    // header-only-corrupted file (doc §3.3) - IsHeaderOnly is the predicate later
    // phases gate a push/export guard on, and it must mean "corrupted on the Kronos",
    // not "we only got 40 bytes over FTP."
    public static KsfSample? Open(byte[] data)
    {
        var chunks = KorgRiffChunk.ReadChunks(data);
        if (chunks.Count == 0 || chunks[0].Tag != "SMP1" || chunks[0].Payload.Length < 32) return null;
        if (!chunks.Any(c => c.Tag == "SMD1")) return null;

        var s = new KsfSample();
        foreach (var (tag, payload) in chunks)
        {
            if (tag == "SMP1" && payload.Length >= 32)
            {
                // Name/Suffix come from the 24-byte NAME chunk below, not this 16-byte
                // short field - the two are NOT simple re-truncations of each other.
                // A real fixture (SMPTEST/LOOP, name "ClaudeTestLoopOFF"+"-L") proved
                // this: the 16-byte field can only hold 14 base chars ("ClaudeTestLoop"),
                // while the 24-byte field holds the full 17-char base name. Deriving one
                // from the other (as this port originally did, mirroring the Python
                // reference) silently truncates real names on every save.
                s.SampleStart = KorgRiffChunk.ReadU32BE(payload, 16);
                s.LoopStart   = KorgRiffChunk.ReadU32BE(payload, 20);
                s._preservedLoopDuplicate = KorgRiffChunk.ReadU32BE(payload, 24);
                s.LoopEnd     = KorgRiffChunk.ReadU32BE(payload, 28);
            }
            else if (tag == "SNO1" && payload.Length >= 4)
            {
                s.Sno1 = KorgRiffChunk.ReadU32BE(payload, 0);
            }
            else if (tag == "NAME" && payload.Length >= 24)
            {
                var (name, suffix) = KorgRiffChunk.SplitNameSuffix(Encoding.ASCII.GetString(payload, 0, 24));
                s.Name = name;
                s.Suffix = suffix;
            }
            else if (tag == "SMF1")
            {
                s._smf1 = payload;
            }
            else if (tag == "SMD1" && payload.Length >= 12)
            {
                s.SampleRate = KorgRiffChunk.ReadU32BE(payload, 0);
                s.Flags = payload[4];
                s.Channels = payload[6];
                s.Bits = payload[7];
                uint frameCount = KorgRiffChunk.ReadU32BE(payload, 8);
                long pcmBytesWanted = (long)frameCount * 2;
                int pcmBytesAvailable = (int)Math.Max(0, Math.Min(pcmBytesWanted, payload.Length - 12));
                s.Pcm = payload.AsSpan(12, pcmBytesAvailable).ToArray();
            }
        }
        return s;
    }

    public byte[] ToBytes()
    {
        uint frameCountU = (uint)Math.Max(0, FrameCount);
        // LoopEnd is written exactly as stored - NOT auto-recomputed from FrameCount.
        // A real header-only-corrupted fixture (SMPTEST/NOLOOP/CLAUD001) proved that
        // matters: it has FrameCount==0 but its SMP1 tail still carries the ORIGINAL
        // sample's LoopEnd (235452, a stale pre-corruption value, not a fresh 0 or
        // frame_count-1 sentinel) - serialization must be lossless pass-through.
        // Callers that resize Pcm (WAV re-import, crop) own re-deriving LoopEnd/
        // LoopStart/SampleStart if they care about the "frame_count-1 when loop is
        // off" convention every intact real one-shot file happens to show - that's an
        // editing-time decision, not something ToBytes should impose.
        var tail = new byte[16];
        KorgRiffChunk.WriteU32BE(tail, 0, SampleStart);
        KorgRiffChunk.WriteU32BE(tail, 4, LoopStart);
        KorgRiffChunk.WriteU32BE(tail, 8, _preservedLoopDuplicate ?? LoopStart);
        KorgRiffChunk.WriteU32BE(tail, 12, LoopEnd);

        var smp1 = KorgRiffChunk.Concat(KorgRiffChunk.EncodeNameField(Name, Suffix, 16), tail);
        var sno1 = new byte[4];
        KorgRiffChunk.WriteU32BE(sno1, 0, Sno1);
        var nameChunk = KorgRiffChunk.EncodeNameField(Name, Suffix, 24);
        var sub = new byte[] { 0, 0, 0, 0, Flags, 0x00, Channels, Bits };
        KorgRiffChunk.WriteU32BE(sub, 0, SampleRate);
        var frameCountBytes = new byte[4];
        KorgRiffChunk.WriteU32BE(frameCountBytes, 0, frameCountU);
        var smd1 = KorgRiffChunk.Concat(sub, frameCountBytes, Pcm);

        return _smf1 == null
            ? KorgRiffChunk.Concat(
                KorgRiffChunk.BuildChunk("SMP1", smp1),
                KorgRiffChunk.BuildChunk("SNO1", sno1),
                KorgRiffChunk.BuildChunk("NAME", nameChunk),
                KorgRiffChunk.BuildChunk("SMD1", smd1))
            : KorgRiffChunk.Concat(
                KorgRiffChunk.BuildChunk("SMP1", smp1),
                KorgRiffChunk.BuildChunk("SNO1", sno1),
                KorgRiffChunk.BuildChunk("NAME", nameChunk),
                KorgRiffChunk.BuildChunk("SMF1", _smf1),
                KorgRiffChunk.BuildChunk("SMD1", smd1));
    }

    public short[] Samples() => KsfPcm.ToHostOrder(Pcm);

    public void SetSamples(short[] values) => Pcm = KsfPcm.ToBigEndianBytes(values);

    public void Save(string? path = null)
    {
        path ??= Path;
        if (path is null) throw new InvalidOperationException("no path given and none stored");
        File.WriteAllBytes(path, ToBytes());
        Path = path;
    }
}
