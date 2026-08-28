namespace KronosScreenRemote;

// Walks a Program/Drum Kit/Wave Sequence body's own outgoing SAMPLE references (Sampling
// Mode/RAM, EXs, User Sample Banks) - separate from ObjectReferenceWalker/DependencyScanner
// on purpose. A sample reference is (16-byte Bank Identity, numeric ID), NOT an
// ObjLoc(ObjType,Bank,Number): it points OUTSIDE the catalogued library at instrument/EXs/
// .KSC filesystem content that can never be content-hashed, pulled, placed, or repointed.
// Feeding it into ObjectReferenceWalker.Walk/WalkResolvable would make every Program/
// DrumKit/WaveSequence touching a non-ROM sample permanently "unresolved" - the exact
// failure class ObjectReferenceWalker.IsAlwaysAvailable already exists to prevent for GM
// Program/DrumKit refs (see DependencyScanner.cs), just for a reference shape that
// additionally can't ever resolve locally even in principle. This walker is DISPLAY ONLY -
// its only consumers are the Object Dependencies panel paths in LibrarianShellViewModel.
//
// We only track Sampling Mode (RAM), EXs, and User/3rd-party Sample Banks - never ROM
// ("always there", not a removable dependency - the user's own framing for this feature).
//
// Byte layout hardware-confirmed 2026-08-27 against real Kronos-saved test programs
// (ZTEST-SMPDEP.PCG / v2 / v3) - full derivation and evidence in
// scratch_debug/pcg-sample-dependency-investigation.md. Summary:
//   - HD-1 Program oscillator zone, Drum Kit zone, and Wave Sequence step all share ONE
//     scheme: [type/mode byte][16-byte Bank UUID][1 reserved byte][2-byte LE Number]. See
//     LibRefs.IterProgramSampleZoneRefs / DrumKitBody.IterSampleZoneRefs /
//     WaveSequenceBody.IterSampleStepRefs for the per-site byte offsets.
//   - EXi (MOD-7, STR-1 only - the sole 2 of 9 EXi engines with any PCM-referencing
//     component, confirmed via Documentation/MIDI implementation/Prog_EXi.txt) use a
//     completely different, EXi-internal binary encoding at a fixed absolute offset per
//     engine, gated on the real engine-type byte (offset 2857, "Algorithm Type" per
//     Prog_EXi_Common.txt) - see LibRefs.IterExiPcmHighSlotCandidates for the full
//     derivation, including the corpus measurements that found this gate necessary in the
//     first place. Only the "High" PCM OSC slot is decoded - MidHigh/MidLow/Low were never
//     independently confirmed.
static class SampleReferenceWalker
{
    public enum BankBucket { SamplingModeRam, Exs, UserOrThirdParty, ExiExternal }

    // EXi's own "unpopulated slot" / ROM sentinel for the PCM OSC High slot - hardware-
    // confirmed identical for both MOD-7's and STR-1's own High slot (see the investigation
    // capsule). A slot whose blob matches this exactly is ROM or genuinely nothing assigned -
    // either way, skipped, same as a legacy-scheme ROM (0) reference.
    static readonly byte[] ExiPcmHighDefaultBlob =
        { 0xf4, 0x24, 0x75, 0x04, 0, 0, 0, 0, 0, 0, 0, 0xd0, 0x34, 0x05, 0x00, 0xb0 };

    // Key is the dedup identity (bucket + bank identity, no count suffix) - callers that want
    // to dedupe the SAME bank across multiple selected/visited objects in one panel
    // population (mirroring how ObjectReferenceWalker's refLoc-keyed `seen` set works) key
    // off this, not Description (which embeds a per-object "(Nx)" count suffix). Bucket is
    // exposed so a caller can color-code the row instead of treating every sample dependency
    // as one undifferentiated kind (Views/LibrarianShellWindow.xaml's Object Dependencies list).
    public readonly record struct SampleDependencyRow(string Description, string Key, int Count, BankBucket Bucket);

    static readonly byte[] KorgMsPrefix =
        { 0x4B, 0x4F, 0x52, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0x4D, 0x53, 0x00 };   // 15 bytes

    static bool IsLegacyForm(byte[] uuid) => uuid.Length == 16 && uuid.AsSpan(0, 15).SequenceEqual(KorgMsPrefix);
    static bool IsAllZero(byte[] uuid) => Array.TrueForAll(uuid, b => b == 0);

    // Byte 15 bit 0 is a mono/stereo flag on every form of this UUID, not part of bank
    // identity - masked out before using the UUID as a dedup/display key.
    static string DedupKey(byte[] uuid)
    {
        var masked = (byte[])uuid.Clone();
        masked[15] &= 0xFE;
        return Convert.ToHexString(masked);
    }

    // Classifies a legacy-scheme UUID (HD-1 zone / Drum Kit zone / Wave Sequence step).
    // Returns null for ROM (0) - callers skip it, matching the user's own "don't care about
    // ROM" framing for this feature.
    static (BankBucket Bucket, string Label, string Key)? ClassifyLegacyUuid(byte[] uuid)
    {
        if (IsLegacyForm(uuid))
        {
            int legacyBankNumber = uuid[15] >> 1;   // §7: 0=ROM, 1=Sampling Mode (RAM), N+1=EXs<N>
            if (legacyBankNumber == 0) return null;
            if (legacyBankNumber == 1) return (BankBucket.SamplingModeRam, "Sampling Mode (RAM)", "ram");
            int exsNumber = legacyBankNumber - 1;
            return (BankBucket.Exs, $"EXs{exsNumber}", $"exs{exsNumber}");
        }
        string key = DedupKey(uuid);
        return (BankBucket.UserOrThirdParty, $"User/3rd-Party Sample Bank ({key[..12]}…)", key);
    }

    public static IReadOnlyList<SampleDependencyRow> Walk(int objType, byte[] body)
    {
        // Mirrors ObjectReferenceWalker.Walk's own INIT early-out - an untouched slot
        // references nothing meaningful (see InitObjects's own header comment).
        if (InitObjects.IsInit(objType, body)) return Array.Empty<SampleDependencyRow>();

        var groups = new Dictionary<string, (string Label, int Count, BankBucket Bucket)>();
        void Add(BankBucket bucket, string label, string key)
        {
            string groupKey = $"{bucket}|{key}";
            groups[groupKey] = groups.TryGetValue(groupKey, out var g)
                ? (label, g.Count + 1, bucket)
                : (label, 1, bucket);
        }
        void AddLegacy(byte[] uuid)
        {
            if (IsAllZero(uuid)) return;   // nothing assigned - distinct from a real ROM (0) legacy UUID
            if (ClassifyLegacyUuid(uuid) is { } c) Add(c.Bucket, c.Label, c.Key);
        }

        if (objType == LibObj.Program)
        {
            if (body.Length == ProgramFormatConverter.WireSizeHd1)
            {
                foreach (var (_, _, uuid, _) in LibRefs.IterProgramSampleZoneRefs(body)) AddLegacy(uuid);
            }
            else if (body.Length == ProgramFormatConverter.WireSizeExi)
            {
                foreach (var (engine, blob, raw) in LibRefs.IterExiPcmHighSlotCandidates(body))
                {
                    if (blob.AsSpan().SequenceEqual(ExiPcmHighDefaultBlob)) continue;   // ROM/unassigned
                    int number = raw >> 4;
                    Add(BankBucket.ExiExternal,
                        $"{engine} PCM OSC: External Sample Bank #{number} (bank identity not decodable from PCG bytes alone)",
                        $"{engine}|{Convert.ToHexString(blob)}");
                }
            }
        }
        else if (objType == LibObj.DrumKit)
        {
            foreach (var (_, _, uuid, _) in DrumKitBody.IterSampleZoneRefs(body)) AddLegacy(uuid);
        }
        else if (objType == LibObj.WaveSequence)
        {
            foreach (var (_, uuid, _) in WaveSequenceBody.IterSampleStepRefs(body)) AddLegacy(uuid);
        }

        var rows = new List<SampleDependencyRow>();
        foreach (var (groupKey, (label, count, bucket)) in groups)
            rows.Add(new SampleDependencyRow(count > 1 ? $"{label} ({count}x)" : label, groupKey, count, bucket));
        return rows;
    }
}
