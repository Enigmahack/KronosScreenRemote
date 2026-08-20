namespace KronosScreenRemote;

using System.IO;

// One-off reconnaissance tool (see App.xaml.cs's `--sample-stereo-scan <folder>` flag) -
// NOT a shipped feature. Walks every real .KMP fixture through the actual format layer
// (not a raw byte grep, which false-positives constantly against PCM data that happens
// to contain "-L"/"-R" byte sequences) and reports the KMP-LEVEL pairing signal: two
// multisamples sharing the same Name with opposite "-L"/"-R" Suffix. This is what
// actually grounds the stereo-pair feature in real Kronos-authored data - an earlier
// version of this tool paired by same-.KMP/same-key-range zone suffixes and found ZERO
// pairs against these exact fixtures, because that model is wrong: real stereo content
// is two SEPARATE .KMP files (see kronosology's ksc_kmp_ksf_file_format.md §2.2), not
// two zones inside one.
static class SampleStereoScan
{
    public static void Run(string folder)
    {
        var kmps = new List<(string Path, string Name, string Suffix, uint Mno1, int ZoneCount)>();

        foreach (var kmpPath in Directory.EnumerateFiles(folder, "*.KMP", SearchOption.AllDirectories))
        {
            KmpMultisample? m;
            try { m = KmpMultisample.Open(File.ReadAllBytes(kmpPath)); }
            catch (Exception ex) { Console.WriteLine($"SKIP (unreadable KMP) {kmpPath}: {ex.Message}"); continue; }
            if (m == null) continue;

            kmps.Add((kmpPath, m.Name, m.Suffix, m.Mno1, m.Zones.Count));
            Console.WriteLine($"KMP {kmpPath}: Name='{m.Name}' Suffix='{m.Suffix}' Mno1={m.Mno1} Zones={m.Zones.Count}");
        }

        var suffixed = kmps.Where(k => k.Suffix is "-L" or "-R").ToList();
        Console.WriteLine($"\n{suffixed.Count} multisample(s) with a -L/-R Suffix found under {folder}.\n");

        // Matches SampleImportBuilder.FindStereoSibling's own rule exactly (same Name,
        // opposite Suffix, AND adjacent MNO1) - Name alone isn't enough, several
        // unrelated multisamples below share the Kronos's own unedited default name.
        Console.WriteLine("── Pairing (same Name, opposite Suffix, adjacent MNO1 - matches FindStereoSibling) ──\n");
        int paired = 0, unpaired = 0;
        var reported = new HashSet<string>();
        foreach (var l in suffixed.Where(k => k.Suffix == "-L"))
        {
            if (!reported.Add(l.Path)) continue;
            var r = suffixed.FirstOrDefault(k => k.Suffix == "-R" && k.Name == l.Name &&
                (k.Mno1 == l.Mno1 + 1 || (l.Mno1 > 0 && k.Mno1 == l.Mno1 - 1)));
            if (r.Path != null)
            {
                reported.Add(r.Path);
                Console.WriteLine($"  PAIR '{l.Name}': {Path.GetFileName(l.Path)} (MNO1={l.Mno1}, {l.ZoneCount} zone(s)) "
                    + $"+ {Path.GetFileName(r.Path)} (MNO1={r.Mno1}, {r.ZoneCount} zone(s))");
                paired++;
            }
            else
            {
                Console.WriteLine($"  UNPAIRED-L '{l.Name}': {Path.GetFileName(l.Path)} (MNO1={l.Mno1}) - no matching -R sibling found");
                unpaired++;
            }
        }
        foreach (var r in suffixed.Where(k => k.Suffix == "-R"))
        {
            if (reported.Contains(r.Path)) continue;
            Console.WriteLine($"  UNPAIRED-R '{r.Name}': {Path.GetFileName(r.Path)} (MNO1={r.Mno1}) - no matching -L sibling found");
            unpaired++;
        }
        Console.WriteLine($"\n{paired} pair(s), {unpaired} unpaired -L/-R multisample(s).");
        Environment.Exit(0);
    }
}
