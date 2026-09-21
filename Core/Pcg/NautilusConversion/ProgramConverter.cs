namespace KronosScreenRemote;

// Top-level Kronos -> Nautilus Program body converter - combines the three independently-built
// and independently-tested pieces (SampleZoneRomRemapper, KarmaFieldStripper, KarmaGeCatalog)
// into the one entry point the PCG converter v1 plan's Phase 3 describes. Does NOT yet write
// Nautilus's own Scene 1 arp assignment (blocked on live hardware A/B testing for GE->Arp
// Pattern translation - see karma_kronos_vs_nautilus.md's "GE name <-> Arp Pattern name
// mapping" section) or handle EXi PCM OSC zones (no decoded bank/number scheme exists yet).
static class ProgramConverter
{
    // `NautilusBody` is null exactly when `Refusals` is non-empty - a hard REFUSE per Q12,
    // not a partial/best-effort result. `Warnings` can be non-empty even on success (e.g. GE
    // data was dropped, but every ROM/EXs reference still resolved or wasn't present at all).
    public sealed record ConversionResult(byte[]? NautilusBody, List<string> Warnings, List<string> Refusals);

    public static ConversionResult KronosToNautilus(byte[] kronosBody, RomWaveformMap map, ExsOptionIndex exsCatalog, KarmaGeCatalog geCatalog)
    {
        var warnings = new List<string>();
        var refusals = new List<string>();

        // Describe the GE assignment before anything else touches the body - reads the
        // original, unmodified bytes (KarmaFieldStripper.StripKarmaFields would otherwise
        // zero this same field before it could be named).
        if (KarmaFieldStripper.DescribeGeAssignment(kronosBody, geCatalog) is { } geWarning)
            warnings.Add(geWarning);

        byte[] body = kronosBody;

        // HD-1 oscillator-zone sample remapping - EXi Programs have no such zone layout (their
        // own PCM OSC sample reference, MOD-7/STR-1 only, is a separate undecoded mechanism -
        // see SampleZoneRomRemapper's header comment for why it's out of scope for now).
        if (body.Length == ProgramFormatConverter.WireSizeHd1)
        {
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

        // Hard REFUSE per Q12: any unmapped ROM/EXs reference stops the conversion rather than
        // shipping a body with a wrong or missing waveform. NautilusBody stays null.
        if (refusals.Count > 0)
            return new ConversionResult(null, warnings, refusals);

        return new ConversionResult(body, warnings, refusals);
    }
}
