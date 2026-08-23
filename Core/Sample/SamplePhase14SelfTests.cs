namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;
using NAudio.Wave;

// Off-hardware checks for two real bugs found by the 2026-08-22 Opus redundancy review
// (neither had ANY prior test coverage - the review's own finding #16):
//
//  1. AssignExistingKsfToZone's stereo dual-assign picked BOTH the target multisamples
//     AND the source samples off the SAME discriminator (the clicked zone's own
//     multisample Suffix) - so assigning the REPOSITORY'S "-R" entry to a zone in the
//     "-L" multisample silently wrote the R audio into the L zone and vice versa. A
//     real, reachable case: the Sample combo's grouping picks whichever half
//     BareSampleEntries() lists first, which isn't always "-L".
//  2. Create Multisample (Stereo) computed its MNO1 slot BEFORE the dialog said mono or
//     stereo, always via NextFreeMno1() (slotsNeeded defaulting to 1) - so a stereo
//     pair could take a 2-slot range that collides with an existing multisample,
//     falsifying SampleImportBuilder.FindStereoSibling's own documented invariant that
//     app-created pairs always get non-colliding IDs.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase14SelfTests
{
    public static List<string> SelfTest()
    {
        var settingsPath = Path.Combine(Storage.DataDir, "settings.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try { return RunChecks(); }
        finally
        {
            if (settingsBackup != null) File.WriteAllBytes(settingsPath, settingsBackup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    static List<string> RunChecks()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase14_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── Bug 1: assigning the repository's "-R" entry to the "-L" multisample's
        //    zone must NOT swap the channels ──
        {
            var collectionPath = Path.Combine(scratchRoot, "Stereo.KSC");
            var collection = new KscCollection { Path = collectionPath };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Stereo"));
            collection.Save(collectionPath);

            var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                collection, collectionPath, "StereoKit", 0);
            left.Zones.Add(new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = 0, TopKey = 127 });
            right.Zones.Add(new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = 0, TopKey = 127 });
            left.Save(leftPath);
            right.Save(rightPath);

            // Genuinely distinguishable stereo source: left channel = constant +1000,
            // right channel = constant -1000.
            const int frames = 100;
            var leftPcm = new short[frames];
            var rightPcm = new short[frames];
            for (int i = 0; i < frames; i++) { leftPcm[i] = 1000; rightPcm[i] = -1000; }
            var wavPath = Path.Combine(scratchRoot, "stereo_source.wav");
            WriteStereoWav(wavPath, leftPcm, rightPcm, 44100);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(collectionPath);

            var written = vm.ImportSamplesToCollection([wavPath]);
            Check("bug1-import-wrote-two-bare-entries", written.Count == 2);

            // Pick the "-R" entry specifically - the exact trigger for the bug (picking
            // "-L" would coincidentally "work" even with the bug present, since the
            // buggy code's L/R choice happened to agree with the source's for that case).
            var rPath = written.FirstOrDefault(p => p.Contains("-R", StringComparison.OrdinalIgnoreCase));
            Check("bug1-found-R-entry", rPath != null);
            if (rPath == null) { fails.Add("bug1-aborted-no-R-entry"); return fails; }

            var leftMsNode = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath);
            var leftZone = leftMsNode.MultisampleRef!.Value.Multisample.Zones[0];

            var resultKmpPath = vm.AssignExistingKsfToZone(leftZone, rPath);
            Check("bug1-assign-reported-success", resultKmpPath != null);
            Check("bug1-status-mentions-both-channels", vm.StatusText.Contains("both channels"));

            // Re-read BOTH zones' own .KSF straight from disk - the ground truth,
            // independent of any in-memory state.
            var reloadedLeft = KmpMultisample.Open(File.ReadAllBytes(leftPath))!;
            var reloadedRight = KmpMultisample.Open(File.ReadAllBytes(rightPath))!;
            var leftKsf = KsfSample.Open(File.ReadAllBytes(reloadedLeft.Zones[0].KsfPath(leftPath)))!;
            var rightKsf = KsfSample.Open(File.ReadAllBytes(reloadedRight.Zones[0].KsfPath(rightPath)))!;

            Check("bug1-left-zone-has-left-audio-not-swapped", leftKsf.Samples().All(s => s == 1000));
            Check("bug1-right-zone-has-right-audio-not-swapped", rightKsf.Samples().All(s => s == -1000));
            Check("bug1-left-zone-suffix-is-L", leftKsf.Suffix == "-L");
            Check("bug1-right-zone-suffix-is-R", rightKsf.Suffix == "-R");
        }

        // ── Bug 2: NextFreeMno1 must be asked for 2 CONTIGUOUS slots when the caller
        //    is about to create a stereo pair, not asked for 1 and then used for 2 ──
        {
            var collectionPath = Path.Combine(scratchRoot, "Mno1.KSC");
            var collection = new KscCollection { Path = collectionPath };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Mno1"));
            collection.Save(collectionPath);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(collectionPath);

            // Slot 0 = mono "A", slot 1 = mono "B" - then delete A, freeing slot 0 while
            // slot 1 stays taken. NextFreeMno1(1) now returns 0, which collides with B
            // if a STEREO pair (needing 0 AND 1) is created using slotsNeeded=1.
            vm.NewMultisampleInCollection("A", 0);
            vm.NewMultisampleInCollection("B", 1);
            var aNode = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Multisample.Name == "A");
            vm.SelectNode(aNode);
            vm.DeleteSelectedMultisample();

            Check("bug2-next-free-mno1-single-still-0", vm.NextFreeMno1() == 0);
            Check("bug2-next-free-mno1-pair-skips-past-B", vm.NextFreeMno1(2) == 2);
        }

        return fails;
    }

    // Writes a real 2-channel 16-bit WAV file to disk - AudioImport.GetSourceChannelCount/
    // ImportStereoToLR44100 both open a real file path (WaveFileReader), not a stream, so
    // this is the minimum needed to exercise the actual ImportSamplesToCollection code path
    // rather than a lower-level stand-in for it.
    static void WriteStereoWav(string path, short[] left, short[] right, int sampleRate)
    {
        var interleaved = new short[left.Length + right.Length];
        for (int i = 0; i < left.Length; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 2));
        writer.WriteSamples(interleaved, 0, interleaved.Length);
    }
}
