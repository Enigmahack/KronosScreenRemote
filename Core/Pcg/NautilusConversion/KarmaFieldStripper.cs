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
// This operates on the Kronos-layout body. ProgramConverter then replaces the whole Kronos
// [1280, 2558) block with Nautilus's own RT Controller/Scene/Step Seq block and shifts
// everything after it down by 6 bytes (see ProgramConverter.ToNautilusLayout), so none of
// these zeroed bytes reach the Nautilus body. Stripping still matters for the Chord bits
// below, which share bytes with fields that do carry over.
static class KarmaFieldStripper
{
    public const int KarmaStart = 1304, KarmaEnd = 2557;   // inclusive, both HD-1 and EXi bodies

    // Two more Kronos-only KARMA sub-fields sit just past [1304,2557], found 2026-09-22 via a
    // full field-by-field doc diff (Prog_HD-1.txt/Prog_EXi_Common.txt vs their Nautilus
    // equivalents - see pcg_format_kronos_vs_nautilus.md). These can NOT be handled by extending
    // KarmaStart/KarmaEnd: byte 2558 also holds Oscillator Mode (bits 2-0 -
    // LibRefs.OscModeOffset, load-bearing elsewhere in this codebase), Favorite (bit 5), Hold
    // (bit 6), Assign Mode (bit 7); byte 2562 also holds Unison Thickness (bits 3-0). Only the
    // specific KARMA-only bits are masked, leaving the rest of each byte untouched.
    //
    // Offsets 2572-2577 (Assignable Controller Switch1/2 Assign+Mode+Status, Knob5-8 Assign) are
    // ALSO Kronos-only per the same doc diff, but deliberately NOT stripped here - unlike Chord
    // Mode/Source, these aren't obviously KARMA-specific by name, and whether Nautilus silently
    // repurposes these bytes for something else (like the Vector Control -> X-Y Control rename
    // elsewhere in the same doc) or truly leaves them dead is unconfirmed. Left as an open
    // question rather than guessed at - see pcg_format_kronos_vs_nautilus.md.
    const int ChordModeOffset = 2558, ChordModeMask = 0b0001_1000;    // bits 4-3
    const int ChordSourceOffset = 2562, ChordSourceMask = 0b0111_0000; // bits 6-4

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
        if (MaskBits(result, ChordModeOffset, ChordModeMask)) stripped = true;
        if (MaskBits(result, ChordSourceOffset, ChordSourceMask)) stripped = true;
        return (result, stripped);
    }

    // Clears only `mask`'s bits at `offset`, leaving the byte's other bits untouched. Returns
    // whether any of those bits were set beforehand (for the caller's `Stripped` reporting).
    static bool MaskBits(byte[] body, int offset, int mask)
    {
        if (offset >= body.Length) return false;
        bool hadBits = (body[offset] & mask) != 0;
        body[offset] &= (byte)~mask;
        return hadBits;
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
    public static int? ReadGeNumber(byte[] body) =>
        GeNumberOffset + 1 < body.Length ? body[GeNumberOffset] | (body[GeNumberOffset + 1] << 8) : null;

    public static string? DescribeGeAssignment(byte[] body, KarmaGeCatalog catalog)
    {
        if (ReadGeNumber(body) is not int geNumber || geNumber == 0) return null;

        string name = catalog.NameForGeNumber(geNumber) is { } n ? $"\"{n}\"" : "(name unknown)";
        return $"KARMA GE #{geNumber} {name} was assigned - Nautilus has no automatic translation " +
               "for this (Kronos and Nautilus factory arp/GE catalogs are different content, not " +
               "just differently numbered); assign a Nautilus Arp Pattern to Scene 1 manually if wanted.";
    }
}
