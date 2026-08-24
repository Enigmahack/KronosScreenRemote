namespace KronosScreenRemote;

using System.IO;
using NAudio.Utils;
using NAudio.Wave;

// Off-hardware checks for stereo-pair creation/import, grounded in what real
// Kronos-authored content actually does (kronosology/docs/interfaces/
// ksc_kmp_ksf_file_format.md §2.2, confirmed via Tools/SampleStereoScan.cs against real
// fixtures): a stereo instrument is two full multisamples - same Name, opposite
// "-L"/"-R" Suffix, matching key ranges - never two zones inside one .KMP. Wired into
// App.xaml.cs's --librarian-selftest.
static class SampleStereoSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── AudioImport: a true stereo source keeps channels separate, not averaged ──
        {
            // Interleaved L,R: two frames, L always +10000, R always -10000.
            short[] interleaved = [10000, -10000, 10000, -10000];
            using var reader = new WaveFileReader(BuildWav16(interleaved, AudioImport.TargetSampleRate, 2));
            var (l, r) = AudioImport.ConvertToStereo44100(reader);
            Check("stereo-import-frame-count", l.Length == 2 && r.Length == 2);
            Check("stereo-import-left-channel", l.All(v => v == 10000));
            Check("stereo-import-right-channel", r.All(v => v == -10000));
        }

        // ── AudioImport: a mono source duplicates into both channels rather than
        //    refusing or silently producing an empty second channel ──
        {
            short[] mono = [1234, -5678, 999];
            using var reader = new WaveFileReader(BuildWav16(mono, AudioImport.TargetSampleRate, 1));
            var (l, r) = AudioImport.ConvertToStereo44100(reader);
            Check("mono-to-stereo-left-matches-source", l.SequenceEqual(mono));
            Check("mono-to-stereo-right-duplicates-left", r.SequenceEqual(l));
        }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_stereo_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── SampleImportBuilder.CreateStereoMultisamplePair: same Name, opposite
        //    Suffix, MNO1/MNO1+1, both added to the collection and saved to disk ──
        KscCollection collection;
        string collectionPath;
        KmpMultisample left, right;
        string leftPath, rightPath;
        {
            collectionPath = Path.Combine(scratchRoot, "StereoTest.KSC");
            collection = new KscCollection { Path = collectionPath };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "StereoTest"));
            collection.Save(collectionPath);

            (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                collection, collectionPath, "MyStereoKit", 10);

            Check("stereo-pair-same-name", left.Name == "MyStereoKit" && right.Name == "MyStereoKit");
            Check("stereo-pair-suffixes", left.Suffix == "-L" && right.Suffix == "-R");
            Check("stereo-pair-mno1-adjacent", left.Mno1 == 10 && right.Mno1 == 11);
            Check("stereo-pair-kmp-files-written", File.Exists(leftPath) && File.Exists(rightPath));

            // Hardware-confirmed 2026-08-24: the .KMP filename must NOT bake -L/-R into
            // itself (real Kronos silently fails to load the audio behind such a pair,
            // even though every other byte is correct) - it must be <first 5 chars of
            // Name, sanitized+uppercased><MNO1:03d>.KMP, matching real fixtures
            // (NEWMS000/001, GAGA_000/001). "MyStereoKit" -> "MYSTE".
            Check("stereo-pair-filename-no-suffix",
                !Path.GetFileName(leftPath).Contains('-') && !Path.GetFileName(rightPath).Contains('-'));
            Check("stereo-pair-filename-convention",
                Path.GetFileName(leftPath) == "MYSTE010.KMP" && Path.GetFileName(rightPath) == "MYSTE011.KMP");
            Check("stereo-pair-added-to-collection-entries",
                collection.Entries.Contains(Path.GetFileName(leftPath)) &&
                collection.Entries.Contains(Path.GetFileName(rightPath)));

            var reopenedCollection = KscCollection.Open(File.ReadAllBytes(collectionPath));
            Check("stereo-pair-collection-persisted",
                reopenedCollection.Entries.Contains(Path.GetFileName(leftPath)) &&
                reopenedCollection.Entries.Contains(Path.GetFileName(rightPath)));
        }

        // ── FindStereoSibling: resolves L->R and R->L, returns null for a non-paired
        //    (or non-L/R) multisample ──
        {
            // FindStereoSibling re-opens the candidate fresh from disk, so it's never
            // reference-equal to the in-memory `left`/`right` instances - compare by
            // the fields that actually identify a sibling instead.
            var (foundFromLeft, foundFromLeftPath) = SampleImportBuilder.FindStereoSibling(collection, left, leftPath);
            Check("sibling-from-left-finds-right", foundFromLeft?.Name == right.Name &&
                foundFromLeft?.Suffix == right.Suffix && foundFromLeftPath == rightPath);

            var (foundFromRight, foundFromRightPath) = SampleImportBuilder.FindStereoSibling(collection, right, rightPath);
            Check("sibling-from-right-finds-left", foundFromRight?.Name == left.Name &&
                foundFromRight?.Suffix == left.Suffix && foundFromRightPath == leftPath);

            var mono = new KmpMultisample { Name = "Solo", Suffix = "" };
            var (noSibling, noSiblingPath) = SampleImportBuilder.FindStereoSibling(collection, mono, leftPath);
            Check("sibling-none-for-non-lr-suffix", noSibling == null && noSiblingPath == null);

            // A real -L with no matching -R anywhere in the collection (like the real
            // fixture's NEWMS002/003) must not falsely pair with an unrelated -R.
            var lonelyLeft = new KmpMultisample { Name = "NobodyElse", Suffix = "-L" };
            var (lonelyResult, _) = SampleImportBuilder.FindStereoSibling(collection, lonelyLeft, leftPath);
            Check("sibling-none-for-unmatched-name", lonelyResult == null);
        }

        // ── AddStereoSampleZonePair: matching key range + base name on both halves,
        //    opposite -L/-R baked into each zone's own .KSF, correct per-channel PCM ──
        {
            short[] leftPcm = [111, 222, 333];
            short[] rightPcm = [444, 555, 666];
            var (lz, rz) = SampleImportBuilder.AddStereoSampleZonePair(
                left, leftPath, right, rightPath, "Snare Hit", leftPcm, rightPcm, 44100, 50, 55);

            Check("stereo-zone-same-key-range", lz.OriginalKey == 50 && lz.TopKey == 55 &&
                rz.OriginalKey == 50 && rz.TopKey == 55);

            var leftKsf = KsfSample.Open(File.ReadAllBytes(lz.KsfPath(leftPath)));
            var rightKsf = KsfSample.Open(File.ReadAllBytes(rz.KsfPath(rightPath)));
            Check("stereo-zone-left-ksf-readable", leftKsf != null);
            Check("stereo-zone-right-ksf-readable", rightKsf != null);
            Check("stereo-zone-left-name-suffix", leftKsf != null && leftKsf.Name == "Snare Hit" && leftKsf.Suffix == "-L");
            Check("stereo-zone-right-name-suffix", rightKsf != null && rightKsf.Name == "Snare Hit" && rightKsf.Suffix == "-R");
            Check("stereo-zone-left-pcm", leftKsf != null && leftKsf.Samples().SequenceEqual(leftPcm));
            Check("stereo-zone-right-pcm", rightKsf != null && rightKsf.Samples().SequenceEqual(rightPcm));

            // Sno1 must be collection-unique (hardware-confirmed 2026-08-24): every
            // sample this app wrote used to leave it at the field's default (0), which
            // real hardware silently treats as a collision - a .KSC bulk load dropped 2
            // of 3 identically-Sno1'd zones' audio while the multisample entries
            // themselves still registered fine. Fixed live against a real Kronos before
            // landing this check.
            Check("stereo-zone-sno1-distinct", leftKsf != null && rightKsf != null && leftKsf.Sno1 != rightKsf.Sno1);

            // MSP1's trailing 2 bytes = zone count as LE u16 (doc §2, hardware-confirmed
            // 2026-08-24) - a real regression here silently produces a multisample real
            // Kronos hardware registers as zero-zone: its .KSF loads fine standalone, but
            // tapping the multisample itself prompts "Create New Sample" instead of
            // selecting it. Both halves have exactly 1 zone at this point.
            var leftBytes = left.ToBytes();
            var rightBytes = right.ToBytes();
            Check("stereo-msp1-tail-is-zone-count-left", leftBytes[24] == 1 && leftBytes[25] == 0);
            Check("stereo-msp1-tail-is-zone-count-right", rightBytes[24] == 1 && rightBytes[25] == 0);
        }

        return fails;
    }

    static Stream BuildWav16(short[] samples, int sampleRate, int channels)
    {
        var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(sampleRate, 16, channels)))
            writer.WriteSamples(samples, 0, samples.Length);
        ms.Position = 0;
        return ms;
    }
}
