namespace KronosScreenRemote;

using System.IO;
using System.Text;

// .KSF - single sample (binary, CKorgRiff-framed, big-endian, holds real PCM).
// Chunk order: SMP1 -> SNO1 -> NAME -> SMD1. Direct port of Tools/sample_editor/
// kronos_ksc_format.py's KsfSample, plus the loop-point fields that library predates
// (see kronosology/docs/interfaces/ksc_kmp_ksf_file_format.md §5.1).
sealed class KsfSample
{
    public string Name = "New Sample";
    public string Suffix = "";     // "", "-L", or "-R" - stereo channel marker
    public uint Sno1;
    public uint SampleRate = 44100;
    // SMD1 sub-header offset 4 (kronosology doc §3.1a):
    // bit 0x80 = one-shot (loop disabled, every real unlooped sample has it set),
    // bit 0x40 = Reverse (playback-direction flag - does NOT touch PCM data, unlike
    // ApplyReverse's destructive in-place Array.Reverse effect below; a totally
    // different feature that happens to share an English name), bit 0x01 = +12dB
    // gain boost (default-on, matching every real fresh/unedited sample observed).
    // Bits 1-5 unobserved in any real sample checked so far.
    public byte Flags = 0x81;
    public byte Channels = 1;      // not code-confirmed as read back anywhere; always 1 in practice -
                                    // a genuine stereo source's two channels still each land in a
                                    // separate mono .KSF with this byte = 1, and a 24-bit source still
                                    // shows Bits = 16 below - the Kronos has no on-disk representation
                                    // for anything else.
    public byte Bits = 16;         // the only value ever seen

    // SMD1 sub-header offset 5 (kronosology doc §3.1a): Loop Tune, a signed byte, raw
    // tune value written directly (+2 -> 0x02). The setter clamps to the front-panel UI's
    // own hard limit (-99..+99) even though the byte's own range is wider (-128..127) -
    // values outside that clamp were never producible to test on real hardware, so
    // writing them is not known-safe.
    // Reading from a file bypasses the clamp (direct field write in Open()) so an
    // out-of-clamp value already on disk round-trips instead of being silently altered.
    sbyte _loopTune;
    public sbyte LoopTune
    {
        get => _loopTune;
        set => _loopTune = (sbyte)Math.Clamp(value, (sbyte)-99, (sbyte)99);
    }

    // Bypasses the clamp above - same reason RestorePreservedLoopDuplicate exists:
    // SampleFieldSnapshot.ApplyTo restores undo/redo state, and going through the
    // clamping setter there means the first Ctrl+Z on one of the rare
    // out-of-clamp-but-on-disk files (see this field's own comment above) would
    // permanently rewrite a byte that had round-tripped byte-identical until then.
    public void RestoreLoopTune(sbyte value) => _loopTune = value;

    // Bit-level helpers for the two Flags bits above - mutate through these rather than
    // hand-rolling `Flags |= 0x40` at each call site (that pattern already exists for
    // the one-shot bit across 3 separate call sites in SampleEditorViewModel; adding two
    // more bits the same inline way risks a copy-paste mistake picking the wrong mask).
    public bool IsReversed
    {
        get => (Flags & 0x40) != 0;
        set => Flags = value ? (byte)(Flags | 0x40) : (byte)(Flags & ~0x40);
    }
    public bool Is12dbBoostEnabled
    {
        get => (Flags & 0x01) != 0;
        set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
    }
    public byte[] Pcm = [];        // raw big-endian 16-bit signed samples - route through KsfPcm
    public string? Path;

    // SMP1 offsets 16/20/28 (payload). Offset 24 (a 4th slot) is handled by
    // _preservedLoopDuplicate below.
    public uint SampleStart;
    public uint LoopStart;
    public uint LoopEnd;

    // The offset-24 duplicate slot: mirrors LoopStart in the overwhelming majority of
    // real files, but not in files with a distinct dup value (unusual sample rate,
    // likely legacy/converted content - e.g. LoopStart=0xfffbed64, dup=1, clearly not a
    // mirror). Preserved verbatim when read, same as _smf1, rather than blindly
    // overwritten. Null means "not read from a file" (a brand-new sample), in which
    // case ToBytes falls back to mirroring LoopStart. A caller that edits LoopStart on
    // an existing sample should treat this as stale and call
    // ClearPreservedLoopDuplicate() to re-sync it.
    uint? _preservedLoopDuplicate;

    // Exposed so undo/redo (SampleFieldSnapshot) can snapshot and restore this alongside
    // SampleStart/LoopStart/LoopEnd/Flags - without this, undoing a field edit on an
    // outlier file would silently drop its non-mirroring dup value (ClearPreserved-
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

    // Public read of _smf1, decoded as the filename it holds when present - lets a
    // caller tell "this .KSF is a stub, its real audio lives in another file named X"
    // apart from "this is a resident file with its own real audio", without exposing
    // the raw chunk bytes. Used by ImportSampleIntoZone/AssignExistingKsfToZone's own
    // stub-safety check (see their comments) - overwriting a stub's target name in
    // place would silently redirect every OTHER zone whose own stub still names it.
    public string? StubTargetFilename => _smf1 == null ? null
        : Encoding.ASCII.GetString(_smf1).TrimEnd('\0', ' ');

    // Returns null if `data` isn't a recognizable .KSF (first chunk isn't SMP1, or no
    // SMD1 chunk at all) rather than throwing - mirrors PcgFile.Open's contract for
    // "wrong kind of file" being expected file-picker input, not a bug. Requiring SMD1
    // matters beyond format validation: a genuinely truncated download (an FTP pull cut
    // off mid-transfer) must fail loudly here, not silently produce a
    // default-SampleRate/Flags/empty-Pcm object indistinguishable from a real
    // header-only-corrupted file (doc §3.3) - IsHeaderOnly is the predicate a
    // push/export guard checks, and it must mean "corrupted on the Kronos", not "we
    // only got 40 bytes over FTP."
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
                // short field - the two are NOT simple re-truncations of each other: the
                // 16-byte field can only hold 14 base chars, while the 24-byte field
                // holds the full name. Deriving one from the other silently truncates
                // real names on every save.
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
                s._loopTune = unchecked((sbyte)payload[5]);
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
        // LoopEnd is written exactly as stored - NOT auto-recomputed from FrameCount. A
        // header-only-corrupted file can have FrameCount==0 while its SMP1 tail still
        // carries the ORIGINAL sample's LoopEnd (a stale pre-corruption value, not a
        // fresh 0 or frame_count-1 sentinel) - serialization must be lossless
        // pass-through. Callers that resize Pcm (WAV re-import, crop) own re-deriving LoopEnd/
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
        var sub = new byte[] { 0, 0, 0, 0, Flags, unchecked((byte)_loopTune), Channels, Bits };
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
