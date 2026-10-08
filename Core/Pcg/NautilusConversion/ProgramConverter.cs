namespace KronosScreenRemote;

// Top-level Kronos -> Nautilus Program body converter - combines the three independently-built
// and independently-tested pieces (SampleZoneRomRemapper, KarmaFieldStripper, KarmaGeCatalog)
// into the one entry point the PCG converter v1 plan's Phase 3 describes. The five "Basic Arp"
// GEs become Nautilus built-in presets in the Scenes chosen by ArpConversionOptions; every other
// GE is dropped with a warning (no data-level translation exists - see
// nautilus_arp_engine_architecture.md). EXi PCM OSC zones are not handled yet.
static class ProgramConverter
{
    // `NautilusBody` is null exactly when `Refusals` is non-empty - a hard REFUSE per Q12,
    // not a partial/best-effort result. `Warnings` can be non-empty even on success (e.g. GE
    // data was dropped, but every ROM/EXs reference still resolved or wasn't present at all).
    // `ArpPatternToStore` is set when a transcribed GE pattern was assigned: the caller must write it
    // to Nautilus Arp Pattern bank B (obj 0x19, bank 0x40) at `Slot` for the Program to play it.
    public sealed record ConversionResult(byte[]? NautilusBody, List<string> Warnings, List<string> Refusals,
        (int Slot, byte[] Pattern)? ArpPatternToStore = null);

    // A Nautilus Program body is 6 bytes shorter than a Kronos one (EXi 4960 -> 4954, HD-1
    // 3706 -> 3700). The two layouts agree byte-for-byte on [0, SharedHeadEnd) and again from
    // "Common | Oscillator Mode" (Kronos 2558, Nautilus 2552) to the end - Common, Track EQ,
    // Tone Adjust, the HD-1 oscillator zones and the EXi1/EXi2 engine blocks all sit exactly
    // NautilusSizeDelta bytes earlier on Nautilus. In between, Kronos's S/PDIF + Drum Track +
    // KARMA block (1278 bytes) is replaced on Nautilus by an unrelated RT Controller / Scene /
    // Step Seq block (1272 bytes) - see Prog_EXi_Common.txt / Prog_HD-1.txt for each model.
    // Confirmed against real Nautilus dumps: EXi1 Algorithm Type reads a valid engine id at
    // 2851, not at Kronos's 2857.
    public const int SharedHeadEnd = 1280, KronosTailStart = 2558, NautilusTailStart = 2552;
    public const int NautilusSizeDelta = KronosTailStart - NautilusTailStart;

    // Nothing in Kronos's middle block maps onto Nautilus's, so that region comes from
    // `nautilusTemplate` - a real Nautilus Program body of the target size (e.g. the target
    // slot's current contents, read before writing). Without one it is zero-filled, with a
    // warning.
    static byte[] ToNautilusLayout(byte[] kronosBody, byte[]? nautilusTemplate)
    {
        var result = new byte[kronosBody.Length - NautilusSizeDelta];
        Array.Copy(kronosBody, 0, result, 0, SharedHeadEnd);
        if (nautilusTemplate != null)
            Array.Copy(nautilusTemplate, SharedHeadEnd, result, SharedHeadEnd, NautilusTailStart - SharedHeadEnd);
        Array.Copy(kronosBody, KronosTailStart, result, NautilusTailStart, kronosBody.Length - KronosTailStart);
        return result;
    }

    public static ConversionResult KronosToNautilus(byte[] kronosBody, RomWaveformMap map, ExsOptionIndex exsCatalog,
        KarmaGeCatalog geCatalog, byte[]? nautilusTemplate = null, ArpConversionOptions? arpOptions = null,
        KarmaGePatternLibrary? gePatterns = null)
    {
        var warnings = new List<string>();
        var refusals = new List<string>();
        arpOptions ??= ArpConversionOptions.Default;

        // Read the GE before StripKarmaFields zeroes it. GE Number 0 is treated as "KARMA unused"
        // (see KarmaFieldStripper.DescribeGeAssignment for why that is a heuristic).
        int geNumber = KarmaFieldStripper.ReadGeNumber(kronosBody) ?? 0;
        ArpConversion.Resolution? arp = null;
        if (geNumber != 0)
        {
            arp = ArpConversion.ResolveGe(geNumber, geCatalog, gePatterns, arpOptions.TranscribedPatternSlot,
                arpOptions.MinTranscriptionScore, ArpConversion.SceneList(arpOptions.ProgramScenes));
            if (arpOptions.ProgramScenes == 0 && arp.PatternNumber != null)
                arp = arp with { PatternNumber = null, ToStore = null,
                    Warning = $"KARMA GE #{geNumber} was not converted: no Scene was chosen to receive it." };
            if (arp.Warning != null) warnings.Add(arp.Warning);
        }

        byte[] body = kronosBody;

        // HD-1 oscillator-zone sample remapping - EXi Programs have no such zone layout (their
        // own PCM OSC sample reference, MOD-7/STR-1 only, is a separate undecoded mechanism -
        // see SampleZoneRomRemapper's header comment for why it's out of scope for now).
        if (body.Length == ProgramFormatConverter.WireSizeHd1)
        {
            var approx = SampleZoneRomRemapper.ProgramZonesNotAudioIdentical(body, map);
            if (approx.Count > 0)
                warnings.Add("ROM waveforms without an identical Nautilus sample, mapped to the nearest equivalent (audio differs): " +
                    string.Join(", ", approx.Select(a => $"osc {a.Osc + 1} zone {a.Zone + 1} (#{a.Number})")) + ".");
            var (remapped, unmapped) = SampleZoneRomRemapper.RemapProgramHd1SampleZones(body, map, exsCatalog);
            body = remapped;
            foreach (var u in unmapped)
                refusals.Add($"Oscillator {u.Osc + 1} zone {u.Zone + 1}: {u.Reason}");
        }

        var (stripped, wasStripped) = KarmaFieldStripper.StripKarmaFields(body);
        body = stripped;
        if (wasStripped)
            warnings.Add("KARMA performance data (Realtime Controls, Dynamic MIDI, per-Scene switch " +
                "states, GE/Scene assignments) was present and has been removed - not supported on Nautilus.");

        int nautilusSize = body.Length - NautilusSizeDelta;
        if (nautilusTemplate == null)
            warnings.Add("No Nautilus template Program supplied - the Nautilus-only RT Controller/Scene/Step Seq " +
                "settings were zero-filled.");
        else if (nautilusTemplate.Length != nautilusSize)
            refusals.Add($"Nautilus template Program is {nautilusTemplate.Length} bytes, expected {nautilusSize} " +
                "(an EXi template for an EXi Program, HD-1 for HD-1).");

        // Hard REFUSE per Q12: any unmapped ROM/EXs reference stops the conversion rather than
        // shipping a body with a wrong or missing waveform. NautilusBody stays null.
        if (refusals.Count > 0)
            return new ConversionResult(null, warnings, refusals);

        var nautilusBody = ToNautilusLayout(body, nautilusTemplate);
        bool assigned = arp?.PatternNumber != null;
        if (assigned) ArpConversion.ApplyToScenes(nautilusBody, arpOptions.ProgramScenes, ArpConversion.SceneStride, 0, arp!);
        ArpConversion.CarryKarmaCommon(kronosBody, nautilusBody, assigned);
        return new ConversionResult(nautilusBody, warnings, refusals, arp?.ToStore);
    }
}
