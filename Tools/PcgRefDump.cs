namespace KronosScreenRemote;

using System.IO;

// Headless diagnostic ONLY (see App.xaml.cs's `--dump-pcg-refs` flag) — not part of the
// shipped feature set. Written to settle a specific question: the user reported the "Object
// Dependencies" panel showing U-DD/U-EE for Combi timbre references they've confirmed are
// actually U-EE/U-FF in the PCG file — a suspiciously consistent off-by-one. KronosBanks.
// Func33ToObjBank treats Program as having 7 internal linear banks (I-A..I-G); the .pcg
// bankId decoder (PcgObjectExtractor.DecodeProgramObjBank) was fixed once already to treat
// Program as having only 6 (I-A..I-F, hardware-confirmed) — the SAME asymmetry may still be
// live in Func33ToObjBank, which is used for every Combi-timbre/Set-List-slot reference
// resolution, not just display. This dumps the RAW reference byte alongside both the CURRENT
// decode and a "6-internal-bank" hypothesis decode, so a real file's own bytes — not
// consistency reasoning — settle which is actually correct before anything gets changed.
static class PcgRefDump
{
    public static void Run(string pcgPath, string? nameFilter)
    {
        var outPath = Path.Combine(Path.GetTempPath(), "kronos_pcg_ref_dump.txt");
        using var writer = new StreamWriter(outPath, append: false);
        void Line(string s) { writer.WriteLine(s); Console.WriteLine(s); }

        byte[] data;
        try { data = File.ReadAllBytes(pcgPath); }
        catch (Exception ex) { Line($"Couldn't read '{pcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var file = PcgFile.Open(data);
        if (file == null) { Line($"'{pcgPath}' isn't a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        Line($"Loaded {file.Objects.Count} object(s) from '{pcgPath}'. Output also written to {outPath}");
        int shown = 0;
        foreach (var entry in file.Objects)
        {
            if (entry.Loc.ObjType != LibObj.Combi) continue;
            if (nameFilter != null && entry.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            Line($"\nCombi {entry.Loc.Label()} \"{entry.Name}\":");
            foreach (var (t, rawBank, num) in LibRefs.IterCombiTimbreRefs(entry.Body))
            {
                int currentObjBank = KronosBanks.Func33ToObjBank(1, rawBank);
                string currentLabel = currentObjBank < 0 ? "?(unresolvable)" : $"{KronosBanks.ProgramLabel(currentObjBank)}:{num:D3}";
                int shiftedObjBank = ShiftedFunc33ToObjBank(rawBank);
                string shiftedLabel = shiftedObjBank < 0 ? "?(unresolvable)" : $"{KronosBanks.ProgramLabel(shiftedObjBank)}:{num:D3}";
                Line($"  timbre {t + 1,2}: raw bank byte = {rawBank,2} (0x{rawBank:X2}), number = {num,3}  |  current decode -> {currentLabel,-12}  |  6-int-bank hypothesis -> {shiftedLabel}");
            }
            shown++;
        }
        if (shown == 0) Line(nameFilter != null ? $"No Combi name matched \"{nameFilter}\"." : "No Combis found in this file.");
        Environment.Exit(0);
    }

    // Hypothesis: Program has only 6 internal linear banks (I-A..I-F), not 7 — matching
    // PcgObjectExtractor's own hardware-confirmed finding for the .pcg bankId encoding — so
    // everything from GM onward shifts down by exactly one raw index versus KronosBanks.
    // Func33ToObjBank's current (7-internal-bank) table.
    static int ShiftedFunc33ToObjBank(int idx) => idx switch
    {
        >= 0 and <= 5   => idx,                       // I-A..I-F -> 0x00..0x05
        6               => 0x10,                      // GM
        >= 7 and <= 16  => 0x10 + (idx - 6),           // g(1)..g(9), g(d) -> 0x11..0x1A
        >= 17 and <= 30 => 0x40 + (idx - 17),          // U-A..U-GG -> 0x40..0x4D
        _ => -1,
    };
}
