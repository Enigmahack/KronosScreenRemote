namespace KronosScreenRemote;

using System.IO;

// Headless diagnostic ONLY (see App.xaml.cs's `--sample-format-fixture-check` flag) -
// the runnable acceptance gate for "byte-identical round-trip against real files."
// Walks a folder of real Kronos .KSC/.KMP/.KSF fixtures (pulled via FTP, kept local
// and gitignored - never committed, see kronosology/docs/interfaces/
// ksc_kmp_ksf_file_format.md §5.1 for how they were obtained), Opens + ToBytes()s
// each, and byte-compares against the original.
static class SampleFormatFixtureCheck
{
    public static void Run(string folder)
    {
        var outPath = Path.Combine(Path.GetTempPath(), "kronos_sample_format_fixture_check.txt");
        using var writer = new StreamWriter(outPath, append: false);
        void Line(string s) { writer.WriteLine(s); Console.WriteLine(s); }

        if (!Directory.Exists(folder))
        {
            Line($"'{folder}' is not a directory.");
            Environment.Exit(1);
            return;
        }

        int checkedCount = 0, mismatchCount = 0, skippedCount = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToUpperInvariant();
            byte[] original;
            try { original = File.ReadAllBytes(path); }
            catch (Exception ex) { Line($"SKIP (read failed) {path}: {ex.Message}"); skippedCount++; continue; }

            byte[]? roundTripped = ext switch
            {
                ".KSF" => KsfSample.Open(original)?.ToBytes(),
                ".KMP" => KmpMultisample.Open(original)?.ToBytes(),
                ".KSC" => Path.GetFileName(path).EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase)
                    ? null // write path deliberately refuses these - read-only sanity check instead, below
                    : KscCollection.Open(original).ToBytes(Path.GetFileName(path)),
                _ => null,
            };

            if (ext == ".KSC" && Path.GetFileName(path).EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase))
            {
                // Never round-tripped (non-goal) - just confirm it opens without throwing.
                KscCollection.Open(original);
                Line($"OK (read-only, _UserBank.KSC never written) {path}");
                checkedCount++;
                continue;
            }

            if (roundTripped == null)
            {
                if (ext is ".KSF" or ".KMP" or ".KSC")
                {
                    Line($"MISMATCH (failed to open) {path}");
                    mismatchCount++;
                }
                continue;
            }

            checkedCount++;
            if (!original.AsSpan().SequenceEqual(roundTripped))
            {
                mismatchCount++;
                Line($"MISMATCH {path} (original {original.Length}B, round-trip {roundTripped.Length}B)");
            }
            else
            {
                Line($"OK {path}");
            }
        }

        Line($"\n{checkedCount} file(s) checked, {mismatchCount} mismatch(es), {skippedCount} skipped. Output also written to {outPath}");
        Environment.Exit(mismatchCount == 0 ? 0 : 1);
    }
}
