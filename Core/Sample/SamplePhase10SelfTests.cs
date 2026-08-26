namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this round's bug-fix batch: the Use Zero "floor" diagnostic
// (confirms SetMarker has no hidden floor when Use Zero is off - the reported "can't
// drag Sample Start below frame 7" almost certainly meant Use Zero was still checked
// from testing a PRIOR round, not a new bug), zone-list undo/redo (a boundary drag in
// the keymap previously recorded nothing at all - KmpZone edits had no undo stack),
// cross-domain undo ordering (a sample edit and a zone edit undo back in the actual
// order they happened, through two separate stacks arbitrated by _undoDomains), and
// stereo-shared Trim Silence (only cuts a leading/trailing run that's silent in BOTH
// channels, so the two channels never end up cropped to different lengths). The
// interactive-only pieces (zoom preserved across a loop-region drag, Sample Start's
// render-visibility at frame 0, Play looping when Loop Enabled is checked, live
// selection mirroring during a drag, the keymap boundary drag's corrected pixel-to-key
// mapping) are verified visually/by click-through instead. Wired into App.xaml.cs's
// --librarian-selftest.
static class SamplePhase10SelfTests
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase10_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "P10.KSC");
        var ksc = new KscCollection { Entries = ["P10-L.KMP", "P10-R.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "P10"));
        ksc.Save(kscPath);

        // Three zones per KMP - two real, one skipped (a boundary drag only needs two;
        // the reorder test needs a third zone to prove an untouched zone AFTER the moved
        // pair is unaffected).
        var leftKmpPath = Path.Combine(scratchRoot, "P10", "P10-L.KMP");
        var leftKmp = new KmpMultisample { Name = "P10", Suffix = "-L", Mno1 = 0 };
        leftKmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 48, TopKey = 59 });
        leftKmp.Zones.Add(new KmpZone { Filename = "MS000001.KSF", OriginalKey = 60, TopKey = 71 });
        leftKmp.Zones.Add(new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = 72, TopKey = 90 });
        leftKmp.Save(leftKmpPath);

        var rightKmpPath = Path.Combine(scratchRoot, "P10", "P10-R.KMP");
        var rightKmp = new KmpMultisample { Name = "P10", Suffix = "-R", Mno1 = 1 };
        rightKmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 48, TopKey = 59 });
        rightKmp.Zones.Add(new KmpZone { Filename = "MS001001.KSF", OriginalKey = 60, TopKey = 71 });
        rightKmp.Zones.Add(new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = 72, TopKey = 90 });
        rightKmp.Save(rightKmpPath);

        var leftKsfDir = Path.Combine(scratchRoot, "P10", "P10-L");
        Directory.CreateDirectory(leftKsfDir);
        var rightKsfDir = Path.Combine(scratchRoot, "P10", "P10-R");
        Directory.CreateDirectory(rightKsfDir);

        SampleEditorViewModel NewVm(short[] leftPcm, short[] rightPcm)
        {
            var leftSample0 = new KsfSample { Name = "P10", Suffix = "-L", SampleRate = 44100 };
            leftSample0.SetSamples(leftPcm);
            leftSample0.Save(Path.Combine(leftKsfDir, "MS000000.KSF"));
            var leftSample1 = new KsfSample { Name = "P10b", Suffix = "-L", SampleRate = 44100 };
            leftSample1.SetSamples(leftPcm);
            leftSample1.Save(Path.Combine(leftKsfDir, "MS000001.KSF"));

            var rightSample0 = new KsfSample { Name = "P10", Suffix = "-R", SampleRate = 44100 };
            rightSample0.SetSamples(rightPcm);
            rightSample0.Save(Path.Combine(rightKsfDir, "MS001000.KSF"));
            var rightSample1 = new KsfSample { Name = "P10b", Suffix = "-R", SampleRate = 44100 };
            rightSample1.SetSamples(rightPcm);
            rightSample1.Save(Path.Combine(rightKsfDir, "MS001001.KSF"));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000000.KSF"));
            return vm;
        }

        // ── Use Zero "floor" diagnostic - with Use Zero OFF, SetMarker must land
        //    EXACTLY on the requested frame, including 0. No hidden floor anywhere in
        //    the clamp chain. ──
        {
            var vm = NewVm(leftPcm: [1000, 2000, 3000, 4000, 5000], rightPcm: [1000, 2000, 3000, 4000, 5000]);
            vm.UseZeroCrossing = false;
            vm.SetMarker(SampleMarkerKind.SampleStart, 0);
            Check("usezero-off-lands-exactly-on-zero", vm.SampleSampleStart == 0);

            vm.SetMarker(SampleMarkerKind.SampleStart, 3);
            Check("usezero-off-lands-exactly-on-target", vm.SampleSampleStart == 3);
        }

        // ── Zone-list undo: a boundary drag (MoveZoneBoundary) must be undoable/
        //    redoable - previously recorded NOTHING, so Ctrl+Z after dragging a keymap
        //    boundary silently did nothing. ──
        {
            var vm = NewVm(leftPcm: [1, 2, 3], rightPcm: [1, 2, 3]);
            var zones = vm.CurrentMultisampleZones!;
            var firstZone = zones[0];
            Check("zone-undo-starts-disabled", !vm.CanUndo);

            vm.MoveZoneBoundary(firstZone, 55);
            Check("zone-undo-applies", firstZone.TopKey == 55);
            Check("zone-undo-marks-available", vm.CanUndo);

            vm.Undo();
            Check("zone-undo-reverts", firstZone.TopKey == 59);
            Check("zone-undo-enables-redo", vm.CanRedo);

            vm.Redo();
            Check("zone-redo-reapplies", firstZone.TopKey == 55);
        }

        // ── Zone undo is scoped to the MULTISAMPLE, not the zone selection - selecting
        //    a different zone in the SAME multisample (e.g. to inspect the drag result)
        //    must not wipe the history before Ctrl+Z gets a chance to run. ──
        {
            var vm = NewVm(leftPcm: [1, 2, 3], rightPcm: [1, 2, 3]);
            var zones = vm.CurrentMultisampleZones!;
            var firstZone = zones[0];

            vm.MoveZoneBoundary(firstZone, 50);
            vm.SelectNode(FindZone(vm.Roots, "MS000001.KSF")); // select the OTHER zone, same multisample
            Check("zone-undo-survives-same-multisample-navigation", vm.CanUndo);

            vm.Undo();
            Check("zone-undo-still-reverts-after-navigation", firstZone.TopKey == 59);
        }

        // ── Cross-domain ordering: a sample-field edit followed by a zone edit undoes
        //    back in the ACTUAL order they happened (zone first, then sample) - proving
        //    _undoDomains correctly arbitrates between the two independently-typed
        //    stacks rather than always preferring one kind. ──
        {
            var vm = NewVm(leftPcm: [1, 2, 3, 4, 5], rightPcm: [1, 2, 3, 4, 5]);
            var zones = vm.CurrentMultisampleZones!;
            var firstZone = zones[0];

            vm.UseZeroCrossing = false;
            vm.SetMarker(SampleMarkerKind.SampleStart, 2); // edit #1: sample domain
            vm.MoveZoneBoundary(firstZone, 50);              // edit #2: zone domain

            vm.Undo(); // should undo #2 (zone) first
            Check("chrono-first-undo-reverts-zone-only", firstZone.TopKey == 59);
            Check("chrono-first-undo-keeps-sample-edit", vm.SampleSampleStart == 2);

            vm.Undo(); // should now undo #1 (sample)
            Check("chrono-second-undo-reverts-sample-edit", vm.SampleSampleStart == 0);
        }

        // ── Trim Silence (stereo): only cuts a leading run that's silent in BOTH
        //    channels - independent per-channel trimming would crop the two channels to
        //    DIFFERENT lengths (an offset selection), the exact bug this guards against.
        //    Left is silent for its first 5 frames; right only for its first 2 - the
        //    shared cut must stop at frame 2 (the narrower of the two), not 5. ──
        {
            var vm = NewVm(
                leftPcm: [0, 0, 0, 0, 0, 1000, 2000, 3000],
                rightPcm: [0, 0, 500, 600, 700, 800, 900, 1000]);
            vm.ApplySilenceTrim();

            var leftAfter = vm.SampleWaveform!;
            var rightAfter = vm.RightSampleWaveform!;
            Check("trim-stereo-shared-start-not-independent", leftAfter.Length == 6 && rightAfter.Length == 6);
            Check("trim-stereo-left-keeps-its-own-silent-tail", leftAfter.SequenceEqual(new short[] { 0, 0, 0, 1000, 2000, 3000 }));
            Check("trim-stereo-right-unchanged-past-shared-start", rightAfter.SequenceEqual(new short[] { 500, 600, 700, 800, 900, 1000 }));
        }

        // ── Zone drag-reorder: each zone keeps its own key-range WIDTH, only its
        //    position in the sequence changes - confirmed example: A (width 10) and B
        //    (width 20) trade places; A ends up 10-wide starting where B's combined
        //    range used to begin, B ends up 20-wide occupying the earlier slot. C (an
        //    untouched zone after the pair) must be completely unaffected. ──
        {
            var vm = NewVm(leftPcm: [1, 2, 3], rightPcm: [1, 2, 3]);
            var zones = vm.CurrentMultisampleZones!;
            var a = zones[0]; // TopKey 59 -> width 60 (0..59)
            var b = zones[1]; // TopKey 71 -> width 12 (60..71)
            var c = zones[2]; // TopKey 90 -> width 19 (72..90)
            Check("reorder-setup-widths", a.TopKey == 59 && b.TopKey == 71 && c.TopKey == 90);

            vm.ReorderZone(b, a); // drag B onto A's slot
            Check("reorder-preserves-order-list", zones[0] == b && zones[1] == a && zones[2] == c);
            Check("reorder-b-keeps-its-own-width-in-new-slot", b.TopKey == 11); // 12-wide starting at 0
            Check("reorder-a-keeps-its-own-width-in-new-slot", a.TopKey == 71); // 60-wide starting at 12 -> ends at 71 (B's old top key)
            Check("reorder-c-completely-unaffected", c.TopKey == 90);
            Check("reorder-marks-undo-available", vm.CanUndo);

            vm.Undo();
            Check("reorder-undo-restores-order", zones[0] == a && zones[1] == b && zones[2] == c);
            Check("reorder-undo-restores-widths", a.TopKey == 59 && b.TopKey == 71 && c.TopKey == 90);
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
