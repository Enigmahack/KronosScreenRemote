namespace KronosScreenRemote;

// Strips the Kronos-only KARMA Common/KARMA Module byte range from a Program body - Q4's
// "Kronos-only fields with no Nautilus target -> strip the field(s), show a visible UI
// warning, do NOT blind-copy bytes through" decision.
//
// Range: Prog_HD-1.txt/Prog_EXi_Common.txt both document "KARMA Common" starting at offset
// 1304 (Tempo) and "KARMA Module" ending at offset 2557 (the last byte of "Master Scene
// Scene8"), IDENTICAL in both docs - confirmed by grepping both files this session: same
// field count (1357 matching lines), same starting offset, and the very next field in both
// docs is "Common | Oscillator Mode" at offset 2558 (LibRefs.OscModeOffset - already a known,
// load-bearing constant elsewhere in this codebase, corroborating the boundary). No non-KARMA
// field is interspersed anywhere in [1304, 2557] - verified by checking every distinct
// section-name label across that whole line range, not just spot-checking the two endpoints.
//
// This is a single 1254-byte contiguous strip, not hundreds of individually-named parameter
// strips (Realtime Controls Switch/Fader/Pad assignments, Dynamic MIDI 1-8, GE Module
// selection, Start Seed, Scene Run-DT flags, etc.) - the section is far larger than "Start
// Seed etc." originally suggested, but per-field granularity doesn't change what happens to
// any of these bytes (all get zeroed), so one warning naming the whole region is more useful
// than hundreds of near-identical ones.
//
// Why zero-fill rather than remove/shrink: pcg_format_kronos_vs_nautilus.md confirms Nautilus's
// own Program record is only 6 bytes smaller than Kronos's (4954 vs 4960) - far too small a
// delta to represent removing a 1254-byte section - and confirms the Category byte (offset
// 2568, immediately after this range) reads correctly at the SAME offset on two real Nautilus
// files. Nautilus does not shift this region out; it repurposes/leaves it in place. Phase 3c
// (Scene KARMA->Arp/DrumTrack structural conversion, Q7) will later write its own specific
// bytes into small sub-ranges of this same [1304,2557] window once Nautilus's own Scene
// layout there is pinned down - safe to run AFTER this stripper, same clone-and-patch pipeline
// pattern as SampleZoneRomRemapper.
static class KarmaFieldStripper
{
    public const int KarmaStart = 1304, KarmaEnd = 2557;   // inclusive, both HD-1 and EXi bodies

    // `Stripped` is false only when every byte in range was already zero (e.g. an untouched
    // INIT Program) - lets a caller skip emitting a warning for a Program that had no real
    // KARMA data to lose in the first place.
    public static (byte[] Body, bool Stripped) StripKarmaFields(byte[] body)
    {
        var result = (byte[])body.Clone();
        bool stripped = false;
        int end = Math.Min(KarmaEnd, result.Length - 1);
        for (int i = KarmaStart; i <= end; i++)
        {
            if (result[i] != 0) stripped = true;
            result[i] = 0;
        }
        return (result, stripped);
    }

    // GE Number - the one KARMA GE assigned to the whole Program (not per-Scene, see
    // karma_kronos_vs_nautilus.md's "Kronos's Scene and Nautilus's Scene are not the same
    // concept" section) - offset 1814, 2 bytes LE (Start Seed at 1840 is documented as the ONE
    // big-endian exception field in this whole object; everything else, including this, is LE).
    const int GeNumberOffset = 1814;

    // Call BEFORE StripKarmaFields, on the original body - names the specific GE being lost
    // instead of a generic "KARMA data stripped" warning, using KarmaGeCatalog (Kronos-only;
    // there is no GE->Nautilus-Arp-Pattern translation table - see that class's own header for
    // why). Returns null for GE Number 0 - by Korg's own convention this is "Arp Model 01
    // Up/Dn", a real named pattern, not a documented "off" sentinel; treating it as "nothing to
    // warn about" is a heuristic (avoids a spurious warning on the common case of a Program
    // that never touched KARMA, which defaults to 0), not a proven detection of "KARMA unused."
    public static string? DescribeGeAssignment(byte[] body, KarmaGeCatalog catalog)
    {
        if (GeNumberOffset + 1 >= body.Length) return null;
        int geNumber = body[GeNumberOffset] | (body[GeNumberOffset + 1] << 8);
        if (geNumber == 0) return null;

        string name = catalog.NameForGeNumber(geNumber) is { } n ? $"\"{n}\"" : "(name unknown)";
        return $"KARMA GE #{geNumber} {name} was assigned - Nautilus has no automatic translation " +
               "for this (Kronos and Nautilus factory arp/GE catalogs are different content, not " +
               "just differently numbered); assign a Nautilus Arp Pattern to Scene 1 manually if wanted.";
    }
}
