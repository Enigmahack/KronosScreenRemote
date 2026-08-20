namespace KronosScreenRemote;

using System.IO;

// "Polish" per the Sample Editor plan's Phase 5: a sample-rate/bit-depth consistency
// report across a whole collection. The Kronos itself doesn't warn about mixed-format
// content anywhere - this is a quick way to spot the outlier before finalizing/pushing
// a collection, not a format-layer concern.
static class SampleNormalizationReport
{
    public static List<SampleNormalizationEntry> Build(KscCollection collection, string kscPath)
    {
        var entries = new List<SampleNormalizationEntry>();
        var kscDir = Path.GetDirectoryName(kscPath) ?? "";
        var kscBase = Path.GetFileNameWithoutExtension(kscPath);

        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var kmpPath = Path.Combine(kscDir, kscBase, entry);
            KmpMultisample? m;
            try { m = File.Exists(kmpPath) ? KmpMultisample.Open(File.ReadAllBytes(kmpPath)) : null; }
            catch { m = null; }
            if (m == null) continue;

            foreach (var zone in m.Zones)
            {
                if (zone.IsSkipped) continue;
                var ksfPath = zone.KsfPath(kmpPath);
                KsfSample? s;
                try { s = File.Exists(ksfPath) ? KsfSample.Open(File.ReadAllBytes(ksfPath)) : null; }
                catch { s = null; }
                if (s == null) continue;

                entries.Add(new SampleNormalizationEntry(
                    $"{Path.GetFileName(kmpPath)}/{zone.Filename}", s.Name + s.Suffix,
                    (int)s.SampleRate, s.Bits, s.Channels, s.IsHeaderOnly, false));
            }
        }

        if (entries.Count > 0)
        {
            int majorityRate = entries.GroupBy(e => e.SampleRate).OrderByDescending(g => g.Count()).First().Key;
            byte majorityBits = (byte)entries.GroupBy(e => e.Bits).OrderByDescending(g => g.Count()).First().Key;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                entries[i] = e with { Flagged = e.IsHeaderOnly || e.SampleRate != majorityRate || e.Bits != majorityBits };
            }
        }
        return entries;
    }
}

// One row of the report - a real record (not a tuple) so it binds cleanly to a WPF
// DataGrid by property name. Public: passed into SampleNormalizationReportWindow's
// (public) constructor.
public readonly record struct SampleNormalizationEntry(
    string Location, string SampleName, int SampleRate, byte Bits, byte Channels, bool IsHeaderOnly, bool Flagged);
