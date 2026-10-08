namespace KronosScreenRemote;

// Remaps ROM waveform references inside a Program/Drum Kit/Wave Sequence body from Kronos
// indices to Nautilus indices, using RomWaveformMap. Reuses the same hardware-confirmed
// (mode/UUID/Number) reference sites SampleReferenceWalker already knows about
// (Core/LibrarianModel.cs's LibRefs.IterProgramSampleZoneRefs, DrumKitBody.IterSampleZoneRefs,
// WaveSequenceBody.IterSampleStepRefs) rather than re-deriving offsets - see the PCG converter
// v1 plan's Phase 3a.
//
// v1 scope: Program HD-1 oscillator zones, Drum Kit zones, Wave Sequence steps. EXi Program
// zones (MOD-7/STR-1 PCM OSC) are NOT covered - their sample reference is an EXi-internal
// 16-byte binary blob with no decoded bank/number scheme (confirmed both wire and .pcg-file
// forms are 16 bytes wide, per Prog_EXi.txt's own bit-span for "MS Bank UUID" - NOT a simple
// enum despite the doc's "ROM Mono~EXs998 Ste" range description). Decoding it needs new real
// hardware test data (several EXi programs spanning different EXs bank numbers, ideally both
// sides of the <=126/>=127 boundary below) that doesn't exist yet - out of scope until that
// data exists.
//
// EXs bank references (legacy small-int form, bank<=126 - see LegacyExs below - AND raw-UUID
// form, bank>=127) are a hard REFUSE by default, same as an unmapped ROM index, per Q12 - NOT
// a pass-through. This was very nearly implemented backwards this session: real evidence (a
// Sxxx option file inside an EXs pack .zip) proves EXs bank identity is a Korg-assigned GLOBAL
// PRODUCT id (small int <=126, or a real RFC-4122 UUID >=127 - confirmed via F:\KRONOS EXs\'s
// real pack .zips, which run to at least EXs377, NOT the ~18 a page-scoped PDF scan happened
// to find), which is a true fact about product identity but is NOT the same claim as "the
// zone-reference byte encoding resolves identically on a Nautilus" - that second claim has
// ZERO supporting evidence (Nautilus's own factory-bundled EXs block sits at 301-314, where
// Kronos's real catalog - which does run that high - has nothing at all; if the two platforms
// shared one byte-identical registry you'd expect at least incidental overlap across ~700
// assigned numbers, and there is none). Cross-platform availability of a specific EXs pack is
// genuinely unverified, so it refuses, per Q12's own explicit default.
//
// Per Q12: any reference this can't positively resolve as cross-platform-safe is a hard
// REFUSE, not a silent skip - the caller gets back specifically what's unconvertible so it can
// name it rather than fail generically. This applies to THREE distinct cases, not just "ROM
// index not in the table":
//   1. A ROM waveform index absent from RomWaveformMap's ROM_MULTISAMPLES/ROM_DRUMSAMPLES table.
//   2. A legacy-form EXs bank reference (bank number 1-126) - always refuses; there is no
//      lookup table for these at all (see the header comment above), not merely an empty one.
//   3. A raw-UUID reference (bank number >=127, OR a raw/User-looking UUID with no legacy
//      prefix) that RESOLVES against the shipped EXs product catalog (ExsOptionIndex) to a
//      known Korg EXs product - i.e. it's confirmed to be a purchased pack, not the user's own
//      local sample content, so the same "availability unverified" refusal applies. A raw
//      UUID that does NOT resolve against that catalog is treated as genuine user/3rd-party
//      .KSC content instead - that content is the user's own, not a Korg product tied to one
//      instrument's catalog, so it passes through unchanged.
// Sampling Mode (RAM, legacy bank 0 after ROM) and "nothing assigned" both pass through
// unchanged too - RAM is ephemeral live-instrument state with no persistent identity to
// convert at all (per the original sample-dependency investigation), and an empty zone
// references nothing.
static class SampleZoneRomRemapper
{
    public sealed record UnmappedZoneRef(int Osc, int Zone, string Reason);
    public sealed record UnmappedDrumKitZoneRef(int Note, int Zone, string Reason);
    public sealed record UnmappedWaveSeqStepRef(int Step, string Reason);

    enum Outcome { PassThrough, Remapped, Unmapped }
    readonly record struct Resolution(Outcome Outcome, int NewNumber, string? UnmappedReason);

    static Resolution Resolve(byte[] uuid, int number, RomWaveformMap map, ExsOptionIndex exsCatalog, string romSection, Func<int, string> exsSectionFor)
    {
        var c = SampleReferenceWalker.Classify(uuid);
        switch (c.Class)
        {
            case SampleReferenceWalker.RefClass.NothingAssigned:
            case SampleReferenceWalker.RefClass.LegacySamplingModeRam:
                return new Resolution(Outcome.PassThrough, 0, null);

            case SampleReferenceWalker.RefClass.LegacyRom:
                return map.TryMapRomIndex(romSection, number, out int nautilusRomIdx)
                    ? new Resolution(Outcome.Remapped, nautilusRomIdx, null)
                    : new Resolution(Outcome.Unmapped, 0, $"ROM waveform #{number} ({romSection}) has no Nautilus mapping");

            case SampleReferenceWalker.RefClass.LegacyExs:
                string exsSection = exsSectionFor(c.LegacyExsNumber);
                return new Resolution(Outcome.Unmapped, 0,
                    $"EXs{c.LegacyExsNumber} waveform #{number} - EXs bank availability is not verified cross-platform (no mapping table for EXs content)");

            case SampleReferenceWalker.RefClass.RawUuid:
                if (exsCatalog.NameForUuidHex(c.RawUuidHex) is { } exsName)
                    return new Resolution(Outcome.Unmapped, 0,
                        $"EXs product \"{exsName}\" ({c.RawUuidHex[..12]}…) - EXs bank availability is not verified cross-platform");
                return new Resolution(Outcome.PassThrough, 0, null);   // unresolved raw UUID - genuine user/3rd-party content

            default:
                return new Resolution(Outcome.PassThrough, 0, null);
        }
    }

    public static (byte[] Body, List<UnmappedZoneRef> Unmapped) RemapProgramHd1SampleZones(byte[] hd1Body, RomWaveformMap map, ExsOptionIndex exsCatalog)
    {
        var result = (byte[])hd1Body.Clone();
        var unmapped = new List<UnmappedZoneRef>();

        foreach (var (osc, zone, uuid, number) in LibRefs.IterProgramSampleZoneRefs(hd1Body))
        {
            var r = Resolve(uuid, number, map, exsCatalog, RomWaveformSections.RomMultisamples, RomWaveformSections.ExsMultisamples);
            if (r.Outcome == Outcome.Remapped) LibRefs.SetProgramZoneNumber(result, osc, zone, r.NewNumber);
            else if (r.Outcome == Outcome.Unmapped) unmapped.Add(new UnmappedZoneRef(osc, zone, r.UnmappedReason!));
        }

        return (result, unmapped);
    }

    // Program ROM zones that do get remapped, but to a Nautilus waveform whose audio isn't
    // byte-identical (a re-sampled equivalent) - worth telling the user about.
    public static List<(int Osc, int Zone, int Number)> ProgramZonesNotAudioIdentical(byte[] hd1Body, RomWaveformMap map)
    {
        var list = new List<(int, int, int)>();
        foreach (var (osc, zone, uuid, number) in LibRefs.IterProgramSampleZoneRefs(hd1Body))
            if (SampleReferenceWalker.Classify(uuid).Class == SampleReferenceWalker.RefClass.LegacyRom
                && map.TryMapRomIndex(RomWaveformSections.RomMultisamples, number, out _)
                && !map.IsAudioIdentical(RomWaveformSections.RomMultisamples, number))
                list.Add((osc, zone, number));
        return list;
    }

    public static (byte[] Body, List<UnmappedDrumKitZoneRef> Unmapped) RemapDrumKitSampleZones(byte[] drumKitBody, RomWaveformMap map, ExsOptionIndex exsCatalog)
    {
        var result = (byte[])drumKitBody.Clone();
        var unmapped = new List<UnmappedDrumKitZoneRef>();

        foreach (var (note, zone, uuid, sampleId) in DrumKitBody.IterSampleZoneRefs(drumKitBody))
        {
            var r = Resolve(uuid, sampleId, map, exsCatalog, RomWaveformSections.RomDrumsamples, RomWaveformSections.ExsDrumsamples);
            if (r.Outcome == Outcome.Remapped) DrumKitBody.SetSampleZoneId(result, note, zone, r.NewNumber);
            else if (r.Outcome == Outcome.Unmapped) unmapped.Add(new UnmappedDrumKitZoneRef(note, zone, r.UnmappedReason!));
        }

        return (result, unmapped);
    }

    public static (byte[] Body, List<UnmappedWaveSeqStepRef> Unmapped) RemapWaveSequenceSampleSteps(byte[] waveSeqBody, RomWaveformMap map, ExsOptionIndex exsCatalog)
    {
        var result = (byte[])waveSeqBody.Clone();
        var unmapped = new List<UnmappedWaveSeqStepRef>();

        foreach (var (step, uuid, select) in WaveSequenceBody.IterSampleStepRefs(waveSeqBody))
        {
            var r = Resolve(uuid, select, map, exsCatalog, RomWaveformSections.RomMultisamples, RomWaveformSections.ExsMultisamples);
            if (r.Outcome == Outcome.Remapped) WaveSequenceBody.SetSampleStepSelect(result, step, r.NewNumber);
            else if (r.Outcome == Outcome.Unmapped) unmapped.Add(new UnmappedWaveSeqStepRef(step, r.UnmappedReason!));
        }

        return (result, unmapped);
    }
}

// Section-key constants shared with RomWaveformMap's JSON. The EXs<N> keys are still defined
// here (matching Nautilus Research/scripts/build_rom_waveform_map.py's own section naming)
// even though SampleZoneRomRemapper never actually looks them up in the map (EXs always
// refuses, per that class's header comment) - kept for whichever future work needs the exact
// key format (e.g. a partial/exclude-and-continue mode, Q12's (b) option, not v1 default).
static class RomWaveformSections
{
    public const string RomMultisamples = "ROM_MULTISAMPLES";
    public const string RomDrumsamples = "ROM_DRUMSAMPLES";
    public static string ExsMultisamples(int exsNumber) => $"EXS{exsNumber}_MULTISAMPLES";
    public static string ExsDrumsamples(int exsNumber) => $"EXS{exsNumber}_DRUMSAMPLES";
}
