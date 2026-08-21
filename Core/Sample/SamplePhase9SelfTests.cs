namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this session's follow-up bug-fix batch: Use Zero's
// dual-channel search (either channel of a stereo pair is a valid snap target, ties go
// to the lower frame) and Normalize's stereo-shared-peak behavior (a stereo pair scales
// by ONE factor derived from whichever channel is louder, not two independent factors).
// The interactive/audio pieces (loop-region click-drag now gated on Loop Enabled, the
// piano's white-key-proportional layout, the WASAPI stop-glitch reduction) are verified
// visually/by ear instead. Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase9SelfTests
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase9_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "P9.KSC");
        var ksc = new KscCollection { Entries = ["P9-L.KMP", "P9-R.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "P9"));
        ksc.Save(kscPath);

        var leftKmpPath = Path.Combine(scratchRoot, "P9", "P9-L.KMP");
        var leftKmp = new KmpMultisample { Name = "P9", Suffix = "-L", Mno1 = 0 };
        leftKmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 60, TopKey = 60 });
        leftKmp.Save(leftKmpPath);

        var rightKmpPath = Path.Combine(scratchRoot, "P9", "P9-R.KMP");
        var rightKmp = new KmpMultisample { Name = "P9", Suffix = "-R", Mno1 = 1 };
        rightKmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
        rightKmp.Save(rightKmpPath);

        var leftKsfDir = Path.Combine(scratchRoot, "P9", "P9-L");
        Directory.CreateDirectory(leftKsfDir);
        var rightKsfDir = Path.Combine(scratchRoot, "P9", "P9-R");
        Directory.CreateDirectory(rightKsfDir);

        SampleEditorViewModel NewVm(short[] leftPcm, short[] rightPcm)
        {
            var leftSample = new KsfSample { Name = "P9", Suffix = "-L", SampleRate = 44100 };
            leftSample.SetSamples(leftPcm);
            leftSample.Save(Path.Combine(leftKsfDir, "MS000000.KSF"));

            var rightSample = new KsfSample { Name = "P9", Suffix = "-R", SampleRate = 44100 };
            rightSample.SetSamples(rightPcm);
            rightSample.Save(Path.Combine(rightKsfDir, "MS001000.KSF"));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));
            return vm;
        }

        // ── Use Zero: primary channel has NO crossing near the target, but the partner
        //    does - the snap must still find it (search both channels, not just primary) ──
        {
            // Primary (L): flat positive, no crossing anywhere near frame 0.
            // Partner (R): crosses at frame 2 (10 -> -10).
            var vm = NewVm(
                leftPcm: [5, 10, 15, 20, 25, 30, 35],
                rightPcm: [5, 10, -10, -20, -25, -30, -35]);
            vm.UseZeroCrossing = true;
            vm.SetMarker(SampleMarkerKind.SampleStart, 0);
            Check("use-zero-finds-crossing-on-partner-channel", vm.SampleSampleStart == 2);
        }

        // ── Use Zero: an exact tie between primary and partner crossings picks the
        //    LOWER frame value, per explicit instruction. Target frame is 3; primary's
        //    only crossing is at frame 1 (distance 2), partner's only crossing is at
        //    frame 5 (distance 2) - an exact tie, neither channel crossing AT frame 3
        //    itself (which would trivially "win" at distance 0 and not exercise the
        //    tie-break at all). ──
        {
            var vm = NewVm(
                leftPcm: [5, -5, -10, -15, -20, -25, -30],  // crossing only at frame 1 (5 -> -5)
                rightPcm: [20, 25, 30, 35, 20, -5, -10]);   // crossing only at frame 5 (20 -> -5)
            vm.UseZeroCrossing = true;
            vm.SetMarker(SampleMarkerKind.SampleStart, 3);
            Check("use-zero-tie-picks-lower-frame", vm.SampleSampleStart == 1);
        }

        // ── Normalize: a stereo pair scales by ONE shared factor (derived from the
        //    LOUDER channel), not two independent per-channel factors - a quieter
        //    channel must NOT end up normalized to the same peak as its partner ──
        {
            // L peaks at 10000, R peaks at 20000 - R is louder. Normalizing to -0.1dB
            // (~0.988 of full scale) should scale BOTH by the factor derived from R's
            // peak (20000), leaving L well under -0.1dB, not normalized to its own peak.
            var vm = NewVm(
                leftPcm: [1000, -2000, 10000, -5000],
                rightPcm: [2000, -4000, 20000, -10000]);
            vm.ApplyNormalize();

            short leftPeakAfter = 0, rightPeakAfter = 0;
            foreach (var s in vm.SampleWaveform!) if (Math.Abs((int)s) > leftPeakAfter) leftPeakAfter = Math.Abs(s);
            foreach (var s in vm.RightSampleWaveform!) if (Math.Abs((int)s) > rightPeakAfter) rightPeakAfter = Math.Abs(s);

            // R (the louder channel, which drove the shared scale factor) should land
            // right at the target peak; L, scaled by the SAME factor, must land well
            // below it (~half, matching its 10000-vs-20000 original peak ratio) rather
            // than also being pushed up to the target.
            Check("normalize-partner-reaches-target-peak", rightPeakAfter >= 32000);
            Check("normalize-primary-stays-proportionally-quieter", leftPeakAfter < rightPeakAfter - 5000);
        }

        // ── Undo now covers field-only edits (SetMarker), not just PCM edits - before
        //    this batch, marker/field edits never called RecordBeforeEdit at all, so
        //    Ctrl+Z after typing a Loop Start value did nothing observable (there was
        //    nothing on the stack to undo), which read exactly like "the shortcut isn't
        //    bound" even though the key handling itself was fine. ──
        {
            var vm = NewVm(leftPcm: [1, 2, 3, 4, 5], rightPcm: [10, 20, 30, 40, 50]);
            Check("field-edit-undo-starts-disabled", !vm.CanUndo);

            vm.SetMarker(SampleMarkerKind.LoopStart, 2);
            Check("field-edit-marks-undo-available", vm.CanUndo);
            Check("field-edit-applied", vm.SampleLoopStart == 2);

            vm.Undo();
            Check("field-edit-undo-reverts-value", vm.SampleLoopStart == 0);
            Check("field-edit-undo-does-not-touch-pcm", vm.SampleWaveform!.SequenceEqual(new short[] { 1, 2, 3, 4, 5 }));
        }

        // ── A PCM edit followed by a field-only edit undoes in the correct
        //    chronological order through the ONE shared stack - the field edit first
        //    (most recent), then the PCM edit - with no special-casing needed to
        //    interleave two separately-typed stacks. ──
        {
            var vm = NewVm(leftPcm: [1000, 2000, 3000, 4000], rightPcm: [1000, 2000, 3000, 4000]);
            vm.ApplyGainAdjust(6.0); // PCM edit #1 - GainAdjustEffect applies to the whole buffer
            var afterGain = vm.SampleWaveform!.ToArray();

            vm.SetMarker(SampleMarkerKind.SampleStart, 1); // field edit #2 (no PCM change)
            Check("chrono-field-edit-on-top-of-pcm-edit", vm.SampleSampleStart == 1);
            Check("chrono-pcm-unchanged-by-field-edit", vm.SampleWaveform!.SequenceEqual(afterGain));

            vm.Undo(); // should undo #2 (the field edit) first
            Check("chrono-first-undo-reverts-field-edit-only", vm.SampleSampleStart == 0);
            Check("chrono-first-undo-keeps-pcm-edit", vm.SampleWaveform!.SequenceEqual(afterGain));

            vm.Undo(); // should now undo #1 (the PCM edit)
            Check("chrono-second-undo-reverts-pcm-edit", vm.SampleWaveform!.SequenceEqual(new short[] { 1000, 2000, 3000, 4000 }));
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
