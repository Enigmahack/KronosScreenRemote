namespace KronosScreenRemote;

using System.IO;
using NAudio.Wave;

// Export semantics, stated once so they never drift: exporting a single .KSF writes
// one WAV. Exporting a .KSC bulk-exports every zone .KSF referenced by every .KMP it
// lists, each to its own WAV, named after the sample itself (not the zone's cryptic
// MSxxxyyy.KSF filename) so a folder of exported WAVs is actually browsable. No code
// path here ever treats writing a .KSC/.KMP/.KSF file as "export" - that's the
// import/build side (SampleImportBuilder, KscCollection.Save/ToBytes), never this one.
static class SampleExport
{
    // Header-only (zero-frame) samples are skipped, never exported as a 0-sample WAV -
    // doc §3.3's real failure mode, IsHeaderOnly's third consumer after the Phase 1
    // waveform view and Phase 2 FTP push guard. Returns false (no file written) rather
    // than throwing, since "skip and keep going" is what a bulk export needs from this.
    public static bool ExportSampleToWav(KsfSample sample, string wavPath)
    {
        if (sample.IsHeaderOnly) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath) is { Length: > 0 } d ? d : ".");
        var pcm = sample.Samples();
        using var writer = new WaveFileWriter(wavPath, new WaveFormat((int)sample.SampleRate, 16, 1));
        writer.WriteSamples(pcm, 0, pcm.Length);
        return true;
    }

    // Walks every .KMP a .KSC lists, every non-skipped zone's .KSF, exporting each to
    // <outputDir>/<sample-name><suffix>.wav (de-duplicated if two samples share a
    // name). Returns (exported, skipped) - skipped covers header-only samples AND
    // zones/files that couldn't be read at all; neither ever throws out of a bulk
    // export over one bad entry.
    public static (int exported, int skipped) ExportCollection(KscCollection collection, string kscPath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        int exported = 0, skipped = 0;
        var kscDir = Path.GetDirectoryName(kscPath) ?? "";
        var kscBase = Path.GetFileNameWithoutExtension(kscPath);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var kmpPath = Path.Combine(kscDir, kscBase, entry);
            KmpMultisample? m = null;
            try { if (File.Exists(kmpPath)) m = KmpMultisample.Open(File.ReadAllBytes(kmpPath)); }
            catch (Exception ex) { AppLog.Warn($"Sample export: skipping unreadable multisample '{kmpPath}': {ex.Message}"); }
            if (m == null) { skipped++; continue; }

            var (e, s) = ExportMultisampleZones(m, kmpPath, outputDir, usedNames);
            exported += e;
            skipped += s;
        }
        return (exported, skipped);
    }

    // Every non-skipped zone in one multisample, exported to <outputDir>/<sample-name>
    // <suffix>.wav - the middle ground between a single-sample export and a whole-
    // collection export ("batch export" scoped to one multisample rather than
    // everything). Directly reuses ExportCollection's own per-multisample walk.
    public static (int exported, int skipped) ExportMultisample(KmpMultisample m, string kmpPath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        return ExportMultisampleZones(m, kmpPath, outputDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    static (int exported, int skipped) ExportMultisampleZones(
        KmpMultisample m, string kmpPath, string outputDir, HashSet<string> usedNames)
    {
        int exported = 0, skipped = 0;
        foreach (var zone in m.Zones)
        {
            if (zone.IsSkipped) continue;
            var ksfPath = zone.KsfPath(kmpPath);
            KsfSample? s = null;
            try { if (File.Exists(ksfPath)) s = KsfSample.Open(File.ReadAllBytes(ksfPath)); }
            catch (Exception ex) { AppLog.Warn($"Sample export: skipping unreadable sample '{ksfPath}': {ex.Message}"); }
            if (s == null) { skipped++; continue; }

            var baseName = MakeUniqueFileName(usedNames, $"{s.Name}{s.Suffix}");
            var wavPath = Path.Combine(outputDir, baseName + ".wav");
            if (ExportSampleToWav(s, wavPath)) exported++; else skipped++;
        }
        return (exported, skipped);
    }

    static string MakeUniqueFileName(HashSet<string> used, string desired)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(desired.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (sanitized.Length == 0) sanitized = "sample";
        var candidate = sanitized;
        int n = 1;
        while (!used.Add(candidate)) candidate = $"{sanitized}_{n++}";
        return candidate;
    }
}
