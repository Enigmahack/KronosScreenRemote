namespace KronosScreenRemote;

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

// Kronos-only KARMA GE number -> name lookup, generated offline by
// Nautilus Research/scripts/build_ge_arp_pattern_map.py's Kronos-side parser (which extracts
// cleanly - 2044 entries from KronosV2VoiceNameList.pdf's "KARMA GEs Preset" table; it's the
// Nautilus-side Arpeggio Pattern extraction, and the name-matching between the two catalogs,
// that don't work - see karma_kronos_vs_nautilus.md's "GE name <-> Arp Pattern name mapping"
// section). This exists ONLY to name a GE specifically in a conversion warning ("GE #47
// 'Comp/Lead Generated-Riff' has no Nautilus translation") instead of a generic "KARMA data
// stripped" message - there is no lookup TABLE for converting a GE to a Nautilus Arp Pattern,
// because no reliable one exists yet (disjoint, differently-named factory catalogs; would need
// live hardware A/B testing, not PDF analysis, per that doc's conclusion).
sealed class KarmaGeCatalog
{
    readonly Dictionary<int, string> _byNumber = new();

    public int Count => _byNumber.Count;
    public string? NameForGeNumber(int geNumber) => _byNumber.GetValueOrDefault(geNumber);

    public static string OverridePath => Path.Combine(Storage.DataDir, "kronos_karma_ge_names.json");
    public bool FromOverrideFile { get; private set; }

    sealed record Entry(
        [property: JsonPropertyName("ge_number")] int GeNumber,
        [property: JsonPropertyName("name")] string Name);

    public static KarmaGeCatalog FromCatalog(string? json = null)
    {
        var catalog = new KarmaGeCatalog();
        List<Entry>? entries;
        if (json != null)
        {
            entries = Deserialize(json);
        }
        else
        {
            entries = Deserialize(ReadOverride());
            catalog.FromOverrideFile = entries != null;
            entries ??= Deserialize(ReadEmbedded());
        }
        if (entries == null) return catalog;

        foreach (var e in entries)
            catalog._byNumber[e.GeNumber] = e.Name;
        return catalog;
    }

    static List<Entry>? Deserialize(string? json)
    {
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<List<Entry>>(json); }
        catch (Exception ex)
        {
            AppLog.Warn($"[karma-ge-catalog] unreadable catalog: {ex.Message}");
            return null;
        }
    }

    static string? ReadOverride()
    {
        try { return File.Exists(OverridePath) ? File.ReadAllText(OverridePath) : null; }
        catch (Exception ex)
        {
            AppLog.Warn($"[karma-ge-catalog] override read failed: {ex.Message}");
            return null;
        }
    }

    static string? ReadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".KronosKarmaGeNames.json", StringComparison.Ordinal));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[karma-ge-catalog] embedded read failed: {ex.Message}");
            return null;
        }
    }
}
