namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this session's stereo-view/piano-keymap/loop-interaction
// batch: HasStereoPair resolution + Left/RightSampleWaveform, Combine-mode edit
// mirroring (ApplyEffect/SetLoopFromSelection/ApplySampleEdits/Undo/Redo), Split mode
// NOT mirroring, MoveZoneBoundary, and MoveLoopRegion. The interactive pieces
// (piano rendering, boundary drag, loop drag/select/nudge, the volume/VU controls)
// are verified visually instead (--sample-editor-visual-check). Wired into
// App.xaml.cs's --librarian-selftest.
static class SamplePhase7SelfTests
{
    public static List<string> SelfTest()
    {
        // Every OpenCollection call below writes Recent Files to the REAL
        // settings.json - same snapshot/restore discipline as SamplePhase5/6SelfTests.
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase7_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // Build a real stereo pair collection: two multisamples, same Name, opposite
        // -L/-R Suffix, adjacent MNO1, one matching zone each (same key range) - the
        // exact shape SampleStereoSelfTests already proved SampleImportBuilder builds
        // correctly; this test is specifically about the VIEWMODEL consuming it.
        var kscPath = Path.Combine(scratchRoot, "Stereo.KSC");
        var ksc = new KscCollection { Entries = ["Stereo-L.KMP", "Stereo-R.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "Stereo"));
        ksc.Save(kscPath);

        var leftKmpPath = Path.Combine(scratchRoot, "Stereo", "Stereo-L.KMP");
        var leftKmp = new KmpMultisample { Name = "Stereo", Suffix = "-L", Mno1 = 0 };
        leftKmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 60, TopKey = 60 });
        leftKmp.Save(leftKmpPath);

        var rightKmpPath = Path.Combine(scratchRoot, "Stereo", "Stereo-R.KMP");
        var rightKmp = new KmpMultisample { Name = "Stereo", Suffix = "-R", Mno1 = 1 };
        rightKmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
        rightKmp.Save(rightKmpPath);

        // Deliberately large-magnitude values, not {1,2,3,4,5} - a small +6dB gain on a
        // value like 1 rounds right back to 1 after truncating to short (1 * ~1.995 =
        // 1.995 -> (short)1), which would make a real gain change look like a no-op
        // and produce a false test failure unrelated to whether gain mirroring works.
        var leftKsfDir = Path.Combine(scratchRoot, "Stereo", "Stereo-L");
        Directory.CreateDirectory(leftKsfDir);
        var leftSample = new KsfSample { Name = "Snare", Suffix = "-L", SampleRate = 44100 };
        leftSample.SetSamples([1000, 2000, 3000, 4000, 5000]);
        leftSample.Save(Path.Combine(leftKsfDir, "MS000000.KSF"));

        var rightKsfDir = Path.Combine(scratchRoot, "Stereo", "Stereo-R");
        Directory.CreateDirectory(rightKsfDir);
        var rightSample = new KsfSample { Name = "Snare", Suffix = "-R", SampleRate = 44100 };
        rightSample.SetSamples([10000, 11000, 12000, 13000, 14000]);
        rightSample.Save(Path.Combine(rightKsfDir, "MS001000.KSF"));

        // ── Selecting the L zone resolves the R partner correctly ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var leftZoneNode = FindZone(vm.Roots, "MS000000.KSF");
            Check("left-zone-found", leftZoneNode != null);
            vm.SelectNode(leftZoneNode);

            Check("stereo-pair-detected-from-left", vm.HasStereoPair);
            Check("primary-is-left", vm.IsPrimaryLeftChannel);
            Check("left-waveform-is-primary-samples", vm.LeftSampleWaveform != null && vm.LeftSampleWaveform.SequenceEqual(new short[] { 1000, 2000, 3000, 4000, 5000 }));
            Check("right-waveform-is-partner-samples", vm.RightSampleWaveform != null && vm.RightSampleWaveform.SequenceEqual(new short[] { 10000, 11000, 12000, 13000, 14000 }));
        }

        // ── Selecting the R zone instead resolves the L partner, with Left/Right
        //    waveform properties still correctly L-top/R-bottom regardless ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var rightZoneNode = FindZone(vm.Roots, "MS001000.KSF");
            vm.SelectNode(rightZoneNode);

            Check("stereo-pair-detected-from-right", vm.HasStereoPair);
            Check("primary-is-right", !vm.IsPrimaryLeftChannel);
            Check("left-waveform-is-partner-when-primary-is-right", vm.LeftSampleWaveform != null && vm.LeftSampleWaveform.SequenceEqual(new short[] { 1000, 2000, 3000, 4000, 5000 }));
            Check("right-waveform-is-primary-when-primary-is-right", vm.RightSampleWaveform != null && vm.RightSampleWaveform.SequenceEqual(new short[] { 10000, 11000, 12000, 13000, 14000 }));
        }

        // ── Combine mode: ApplyEffect mirrors to the partner; Split mode: it doesn't ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));
            Check("combine-is-default", !vm.SplitLR);

            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 3;
            vm.ApplyGainAdjust(6.0); // +6dB ~doubles both channels' first 3 frames
            Check("combine-mirrors-primary-changed", vm.SampleWaveform![0] != 1000);
            Check("combine-mirrors-partner-changed", vm.RightSampleWaveform![0] != 10000);

            vm.Undo();
            Check("combine-undo-restores-primary", vm.SampleWaveform![0] == 1000);
            Check("combine-undo-restores-partner", vm.RightSampleWaveform![0] == 10000);

            vm.SplitLR = true;
            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 3;
            vm.ApplyGainAdjust(6.0);
            Check("split-still-changes-primary", vm.SampleWaveform![0] != 1000);
            Check("split-does-not-touch-partner", vm.RightSampleWaveform![0] == 10000);
            vm.Undo();
        }

        // ── SetLoopFromSelection and ApplySampleEdits mirror to the partner in
        //    Combine mode too (loop-point sync matters for real stereo playback) ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));

            vm.SelectionStartFrame = 1;
            vm.SelectionEndFrame = 4;
            vm.SetLoopFromSelection();
            Check("loop-from-selection-mirrors-status", vm.StatusText.Contains("both L/R"));

            vm.ApplySampleEdits(44100, true, 0, 1, 4);
            Check("sample-edits-mirrors-status", vm.StatusText.Contains("both L/R"));
        }

        // ── A header-only partner (real hardware case - SampleFixtures/SMPTEST/LOOP/
        //    CLAUD001/MS001000.KSF is exactly 124 bytes, doc §3.3's corrupted-save
        //    signature) is detected as a stereo pair (there IS a real sibling multi-
        //    sample) but never mirrored into - ShouldMirrorToPartner's own
        //    IsHeaderOnly:false guard - and never crashes trying to edit PCM that
        //    doesn't exist. ──
        {
            var headerOnlyRight = new KsfSample { Name = "Snare", Suffix = "-R", SampleRate = 44100 }; // Pcm defaults empty
            headerOnlyRight.Save(Path.Combine(rightKsfDir, "MS001000.KSF"));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));

            Check("header-only-partner-still-detected-as-pair", vm.HasStereoPair);
            Check("header-only-partner-waveform-is-null", vm.RightSampleWaveform == null);

            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 3;
            vm.ApplyGainAdjust(6.0); // must not throw trying to mirror into empty partner PCM
            Check("header-only-partner-not-mirrored-into", !vm.StatusText.Contains("both L/R"));
            Check("header-only-partner-primary-still-edited", vm.SampleWaveform![0] != 1000);
            vm.Undo();

            // Restore the real audio partner for the tests that follow.
            rightSample.Save(Path.Combine(rightKsfDir, "MS001000.KSF"));
        }

        // ── MoveZoneBoundary updates TopKey and reports via StatusText ──
        {
            var m = new KmpMultisample { Name = "T", Mno1 = 9 };
            m.Zones.Add(new KmpZone { Filename = "A.KSF", OriginalKey = 0, TopKey = 40 });
            m.Zones.Add(new KmpZone { Filename = "B.KSF", OriginalKey = 41, TopKey = 80 });

            var vm = new SampleEditorViewModel();
            vm.MoveZoneBoundary(m.Zones[0], 50);
            Check("move-boundary-updates-topkey", m.Zones[0].TopKey == 50);
        }

        // ── MoveLoopRegion sets both start/end and mirrors in Combine mode ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));
            vm.MoveLoopRegion(2, 5);
            Check("move-loop-region-sets-start", vm.SampleLoopStart == 2);
            Check("move-loop-region-sets-end", vm.SampleLoopEnd == 5);
            Check("move-loop-region-mirrors-status", vm.StatusText.Contains("both L/R"));
        }

        return fails;
    }

    static SampleTreeNode? FindZone(IEnumerable<SampleTreeNode> nodes, string filename)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef?.Zone.Filename == filename) return node;
            var found = FindZone(node.Children, filename);
            if (found != null) return found;
        }
        return null;
    }
}
