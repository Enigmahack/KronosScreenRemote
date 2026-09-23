namespace KronosScreenRemote;

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

// Kronos<->Nautilus ROM/EXs waveform-index lookup, generated offline by
// Nautilus Research/scripts/build_rom_waveform_map.py from Korg's own Voice Name List PDFs
// (word-bbox extraction, name-matched per section - see that script's own header for the full
// derivation, including two real parsing bugs found and fixed this session). Same
// override-file-next-to-exe-else-embedded convention as ExsOptionIndex/Core/Sample/
// ExsOptionIndex.cs, for the same reason: lets a revised table ship without a rebuild.
//
// Sections actually populated as of the 2026-09-23 regeneration (fixed two real bugs in the
// generator: a column-splitting gap threshold that silently merged and dropped adjacent-column
// entries when a name ran long, and a space-before-hyphen inconsistency between the two source
// PDFs that spuriously failed otherwise-identical names, e.g. Kronos "mf -L" vs Nautilus
// "mf-L" - together recovered 55 entries, including the whole "Acoustic Piano 1" ROM run):
// ROM_MULTISAMPLES (1129 matched entries, 75% of Kronos's table - the one Program HD-1 zone
// remapping actually uses) and ROM_DRUMSAMPLES (392 matched, 28% - Nautilus renamed its ROM
// drum-sample set with an incompatible naming convention, a confirmed real limitation, not a
// bug - see the generator's own REVIEW.md). EXs<N> sections are present in the JSON's shape but
// empty in practice AND are never looked up by the remapper regardless (see
// SampleZoneRomRemapper's header comment: an
// EXs reference is always a hard refuse, not a table lookup - EXs bank identity is a real
// Korg-assigned global product id, but that does NOT establish the reference resolves
// identically on Nautilus, which is unverified). Do NOT read the PDF-sourced "1-18 vs 301-314"
// finding as "Kronos's own EXs numbering caps at 18" - it doesn't: real Kronos EXs packs run to
// at least EXs377 (confirmed against F:\KRONOS EXs\'s real pack .zips this session). The Voice
// Name List PDFs only document each platform's bundled/known-at-print-time packs, not the full
// catalog - "zero overlap" is a true fact about what's bundled, not about a numbering scheme.
sealed class RomWaveformMap
{
    readonly Dictionary<(string Section, int KronosIndex), int> _map = new();

    public int Count => _map.Count;

    public bool TryMapRomIndex(string section, int kronosIndex, out int nautilusIndex) =>
        _map.TryGetValue((section, kronosIndex), out nautilusIndex);

    public static string OverridePath => Path.Combine(Storage.DataDir, "nautilus_rom_waveform_map.json");

    public bool FromOverrideFile { get; private set; }

    sealed record Entry(
        [property: JsonPropertyName("section")] string Section,
        [property: JsonPropertyName("kronos_index")] int KronosIndex,
        [property: JsonPropertyName("nautilus_index")] int NautilusIndex,
        [property: JsonPropertyName("name")] string Name);

    // Pure, no network, no hardware - `json` is only ever passed by self-tests.
    public static RomWaveformMap FromCatalog(string? json = null)
    {
        var map = new RomWaveformMap();
        List<Entry>? entries;
        if (json != null)
        {
            entries = Deserialize(json);
        }
        else
        {
            // A hand-dropped override that won't parse must never shadow the embedded copy -
            // same rationale as ExsOptionIndex.FromCatalog.
            entries = Deserialize(ReadOverride());
            map.FromOverrideFile = entries != null;
            entries ??= Deserialize(ReadEmbedded());
        }
        if (entries == null) return map;

        foreach (var e in entries)
            map._map[(e.Section, e.KronosIndex)] = e.NautilusIndex;
        return map;
    }

    static List<Entry>? Deserialize(string? json)
    {
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<List<Entry>>(json); }
        catch (Exception ex)
        {
            AppLog.Warn($"[nautilus-rom-map] unreadable catalog: {ex.Message}");
            return null;
        }
    }

    static string? ReadOverride()
    {
        try { return File.Exists(OverridePath) ? File.ReadAllText(OverridePath) : null; }
        catch (Exception ex)
        {
            AppLog.Warn($"[nautilus-rom-map] override read failed: {ex.Message}");
            return null;
        }
    }

    static string? ReadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".NautilusRomWaveformMap.json", StringComparison.Ordinal));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[nautilus-rom-map] embedded read failed: {ex.Message}");
            return null;
        }
    }
}
