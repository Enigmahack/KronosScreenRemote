namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for the bug-sweep round (see Commit Notes.md entry 21). Every
// block here pins a bug that was live in the shipped editor, so a regression on any of
// them is a return to real, previously-observed misbehaviour rather than a hypothetical:
//
//  1. Unsaved sample edits used to be destroyed by clicking another tree node -
//     SelectNode re-read the .KSF from disk with nothing holding the edit.
//  2. In stereo Combine mode the PARTNER was edited by every mirrored operation but
//     never saved, so the pair silently diverged on disk.
//  3. Cut/Paste were the only length-changing edits that skipped the stereo mirror, so
//     L and R ended up different lengths and played back time-offset.
//  4. ApplyZoneEdits/MoveZoneBoundary/ReorderZone/DeleteSelectedZone all mutated ONE
//     half's key ranges, breaking the exact (OriginalKey, TopKey) match that stereo
//     partner resolution depends on - the same bug class entry 19 fixed only for
//     AddPlaceholderZone.
//  5. Zone undo restored only the clicked half, re-introducing (4) on Ctrl+Z.
//  6. Loop/Sample-Start markers were never clamped when an edit SHORTENED the buffer,
//     so a crop wrote out-of-range loop points straight into the .KSF.
//  7. HasUnsavedChanges went stale-negative: saving multisample B cleared one global
//     flag that also covered an unsaved multisample A.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase13SelfTests
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase13_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── Stereo pair fixture: two zones per side, identical key ranges, real audio ──
        (SampleEditorViewModel Vm, string LeftPath, string RightPath, string KscPath) MakeStereoFixture(string name)
        {
            var dir = Path.Combine(scratchRoot, name);
            Directory.CreateDirectory(dir);
            var kscPath = Path.Combine(dir, $"{name}.KSC");
            var collection = new KscCollection { Path = kscPath };
            Directory.CreateDirectory(Path.Combine(dir, name));
            collection.Save(kscPath);

            var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                collection, kscPath, "Pair", 20);

            left.Zones.Add(new KmpZone { Filename = "MS020000.KSF", OriginalKey = 0, TopKey = 60 });
            left.Zones.Add(new KmpZone { Filename = "MS020001.KSF", OriginalKey = 61, TopKey = 127 });
            right.Zones.Add(new KmpZone { Filename = "MS021000.KSF", OriginalKey = 0, TopKey = 60 });
            right.Zones.Add(new KmpZone { Filename = "MS021001.KSF", OriginalKey = 61, TopKey = 127 });
            left.Save(leftPath);
            right.Save(rightPath);

            // Distinct per-channel PCM so a mirror that writes the WRONG channel's data
            // is detectable, not masked by both sides being identical.
            void WriteKsf(string kmpPath, string ksfFilename, short seed)
            {
                var ksfDir = Path.Combine(Path.GetDirectoryName(kmpPath) ?? "", Path.GetFileNameWithoutExtension(kmpPath));
                Directory.CreateDirectory(ksfDir);
                var ksf = new KsfSample { Name = Path.GetFileNameWithoutExtension(ksfFilename), SampleRate = 44100 };
                var pcm = new short[200];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(seed + i);
                ksf.SetSamples(pcm);
                ksf.Save(Path.Combine(ksfDir, ksfFilename));
            }
            WriteKsf(leftPath, "MS020000.KSF", 1000);
            WriteKsf(leftPath, "MS020001.KSF", 2000);
            WriteKsf(rightPath, "MS021000.KSF", 5000);
            WriteKsf(rightPath, "MS021001.KSF", 6000);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            return (vm, leftPath, rightPath, kscPath);
        }

        SampleTreeNode ZoneNode(SampleEditorViewModel vm, string kmpPath, int index) =>
            vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == kmpPath).Children[index];

        // ── 1. Unsaved sample edits survive navigating to another zone and back ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("Navigate");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            vm.SplitLR = true; // isolate the primary channel; stereo mirroring is checked separately
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            vm.SplitLR = true;

            int originalFrames = vm.SampleFrameCount;
            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 50;
            vm.ApplyCrop();
            int croppedFrames = vm.SampleFrameCount;
            Check("navigate-crop-actually-shortened", croppedFrames == 50 && croppedFrames != originalFrames);
            Check("navigate-crop-marks-unsaved", vm.HasUnsavedChanges);

            // Navigate away to the sibling zone, then back. This is the exact gesture
            // that used to silently discard the crop.
            vm.SelectNode(ZoneNode(vm, leftPath, 1));
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("navigate-edit-survives-round-trip", vm.SampleFrameCount == croppedFrames);
            Check("navigate-still-unsaved-after-round-trip", vm.HasUnsavedChanges);

            // ...and is what actually reaches disk on Save.
            vm.SaveSelectedSample();
            Check("navigate-save-clears-unsaved", !vm.HasUnsavedChanges);
            var onDisk = KsfSample.Open(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(leftPath)!, Path.GetFileNameWithoutExtension(leftPath), "MS020000.KSF")))!;
            Check("navigate-edit-persisted-to-disk", onDisk.FrameCount == croppedFrames);
        }

        // ── 2. A stereo Combine-mode edit saves BOTH channels ──
        {
            var (vm, leftPath, rightPath, _) = MakeStereoFixture("StereoSave");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("stereosave-partner-resolved", vm.HasStereoPair && !vm.SplitLR);

            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 40;
            vm.ApplyCrop();
            vm.SaveSelectedSample();

            string LeftKsf(string kmp, string f) =>
                Path.Combine(Path.GetDirectoryName(kmp)!, Path.GetFileNameWithoutExtension(kmp), f);
            var savedLeft = KsfSample.Open(File.ReadAllBytes(LeftKsf(leftPath, "MS020000.KSF")))!;
            var savedRight = KsfSample.Open(File.ReadAllBytes(LeftKsf(rightPath, "MS021000.KSF")))!;
            Check("stereosave-left-written", savedLeft.FrameCount == 40);
            // The bug: the partner was edited in memory but Save only ever wrote the
            // selected path, so R kept its original 200 frames.
            Check("stereosave-right-written-too", savedRight.FrameCount == 40);
            Check("stereosave-channels-same-length", savedLeft.FrameCount == savedRight.FrameCount);
            Check("stereosave-clears-unsaved", !vm.HasUnsavedChanges);
        }

        // ── 3. Cut and Paste mirror onto the stereo partner ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("StereoCut");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("stereocut-partner-resolved", vm.HasStereoPair);

            vm.SelectionStartFrame = 10;
            vm.SelectionEndFrame = 30;
            vm.CutSelection();
            Check("stereocut-primary-shortened", vm.SampleFrameCount == 180);
            Check("stereocut-partner-shortened-too", vm.PartnerSampleWaveform?.Length == 180);

            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 0;
            vm.PasteAtSelection();
            Check("stereocut-paste-lengthens-primary", vm.SampleFrameCount == 200);
            Check("stereocut-paste-lengthens-partner-too", vm.PartnerSampleWaveform?.Length == 200);
        }

        // ── 4. Every zone key-range edit mirrors onto the stereo sibling ──
        {
            var (vm, leftPath, rightPath, _) = MakeStereoFixture("ZoneMirror");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("zonemirror-partner-resolved-before", vm.HasStereoPair);

            // (a) typed Top Key
            vm.ApplyZoneEdits(originalKey: 0, topKey: 55);
            var leftMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath).MultisampleRef!.Value.Multisample;
            var rightMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == rightPath).MultisampleRef!.Value.Multisample;
            Check("zonemirror-applyzoneedits-primary", leftMs.Zones[0].TopKey == 55);
            Check("zonemirror-applyzoneedits-sibling", rightMs.Zones[0].TopKey == 55);
            Check("zonemirror-applyzoneedits-status", vm.StatusText.Contains("both L/R channels"));
            // The point of mirroring: the pair must still resolve afterwards.
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("zonemirror-partner-resolves-after-applyzoneedits", vm.HasStereoPair);

            // (b) keymap boundary drag
            vm.MoveZoneBoundary(leftMs.Zones[0], 40);
            Check("zonemirror-boundary-primary", leftMs.Zones[0].TopKey == 40);
            Check("zonemirror-boundary-sibling", rightMs.Zones[0].TopKey == 40);
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("zonemirror-partner-resolves-after-boundary", vm.HasStereoPair);

            // (c) reorder - rewrites EVERY TopKey, the worst case for divergence
            vm.ReorderZone(leftMs.Zones[0], leftMs.Zones[1]);
            Check("zonemirror-reorder-same-order-both-sides",
                leftMs.Zones.Select(z => (int)z.TopKey).SequenceEqual(rightMs.Zones.Select(z => (int)z.TopKey)));
            Check("zonemirror-reorder-sibling-kept-its-own-filenames",
                rightMs.Zones.All(z => z.Filename.StartsWith("MS021")));
        }

        // ── 5. Zone undo restores BOTH halves of the pair ──
        {
            var (vm, leftPath, rightPath, _) = MakeStereoFixture("ZoneUndo");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            var leftMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath).MultisampleRef!.Value.Multisample;
            var rightMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == rightPath).MultisampleRef!.Value.Multisample;

            vm.MoveZoneBoundary(leftMs.Zones[0], 30);
            Check("zoneundo-edit-applied-both", leftMs.Zones[0].TopKey == 30 && rightMs.Zones[0].TopKey == 30);
            Check("zoneundo-can-undo", vm.CanUndo);

            vm.Undo();
            Check("zoneundo-primary-restored", leftMs.Zones[0].TopKey == 60);
            // The bug: undo restored only the clicked half, leaving the sibling at 30 -
            // re-creating exactly the divergence the mirror exists to prevent.
            Check("zoneundo-sibling-restored-too", rightMs.Zones[0].TopKey == 60);

            vm.Redo();
            Check("zoneundo-redo-reapplies-both", leftMs.Zones[0].TopKey == 30 && rightMs.Zones[0].TopKey == 30);
        }

        // ── 6. Markers are clamped when an edit shortens the buffer ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("Markers");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));

            vm.SetLoopEnabled(true);
            vm.SelectionStartFrame = 100;
            vm.SelectionEndFrame = 190;
            vm.SetLoopFromSelection();
            Check("markers-loop-set", vm.SampleLoopEnd == 190);

            // Crop to the first 50 frames - the loop end (190) is now past the end.
            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 50;
            vm.ApplyCrop();
            Check("markers-cropped", vm.SampleFrameCount == 50);
            Check("markers-loopend-clamped", vm.SampleLoopEnd <= 50);
            Check("markers-loopstart-clamped", vm.SampleLoopStart <= 50);
            Check("markers-samplestart-clamped", vm.SampleSampleStart <= 50);

            // ...and the clamp reaches the FILE, which is where the damage used to land
            // (playback always re-clamped in LoopingSampleProvider's constructor, so
            // this was invisible until the .KSF was read back somewhere else).
            vm.SaveSelectedSample();
            var reopened = KsfSample.Open(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(leftPath)!, Path.GetFileNameWithoutExtension(leftPath), "MS020000.KSF")))!;
            Check("markers-loopend-in-range-on-disk", reopened.LoopEnd <= (uint)reopened.FrameCount);
        }

        // ── 7. HasUnsavedChanges tracks every pending file, not one global flag ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("DirtyExact");
            vm.SplitLR = true;

            // Edit multisample A's key range...
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            vm.SplitLR = true;
            vm.ApplyZoneEdits(originalKey: 0, topKey: 50);
            Check("dirtyexact-zone-edit-marks-unsaved", vm.HasUnsavedChanges);

            // ...then edit a sample elsewhere and save only the samples. The zone edit
            // must still be pending: this is the stale-negative case, where one shared
            // flag let a save of one thing silently cover another.
            vm.SelectNode(ZoneNode(vm, leftPath, 1));
            vm.SplitLR = true;
            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 20;
            vm.ApplyCrop();
            vm.SaveSelectedSample();
            Check("dirtyexact-zone-edit-still-pending-after-sample-save", vm.HasUnsavedChanges);

            vm.SaveSelectedMultisample();
            Check("dirtyexact-clean-after-both-saves", !vm.HasUnsavedChanges);
        }

        // ── 8. No-op field commits don't create undo steps or mark the file dirty ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("NoOp");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("noop-clean-to-start", !vm.HasUnsavedChanges && !vm.CanUndo);

            // Re-committing the values the fields already hold - what LostFocus does on
            // every focus change, whether or not anything was typed.
            vm.SetMarker(SampleMarkerKind.SampleStart, vm.SampleSampleStart);
            vm.SetMarker(SampleMarkerKind.LoopStart, vm.SampleLoopStart);
            vm.SetMarker(SampleMarkerKind.LoopEnd, vm.SampleLoopEnd);
            vm.SetLoopEnabled(vm.SampleLoopEnabled);
            vm.ApplyZoneEdits(vm.ZoneOriginalKey, vm.ZoneTopKey);
            Check("noop-no-undo-steps", !vm.CanUndo);
            Check("noop-not-marked-dirty", !vm.HasUnsavedChanges);

            // A real change still registers, so the guard isn't just disabling edits.
            vm.SetMarker(SampleMarkerKind.SampleStart, 5);
            Check("noop-real-edit-still-registers", vm.CanUndo && vm.HasUnsavedChanges);
        }

        // ── 9. Add Zone still mirrors when the sibling already has a PENDING edit ──
        //
        // The interaction that made this necessary: RebuildTreeFromCollection now keeps a
        // pending multisample's live object instead of re-reading it. AddPlaceholderZone
        // used to mirror onto SampleImportBuilder.FindStereoSibling's FRESH DISK copy, so
        // with a pending sibling the new zone landed on an object the tree then threw
        // away - halves out of parity, stereo match broken, and a later Save writing the
        // pending object back over the zone that was just added.
        {
            var (vm, leftPath, rightPath, _) = MakeStereoFixture("PendingSibling");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("pendingsibling-partner-resolved", vm.HasStereoPair);

            // Put the sibling into the pending-save registry via a mirrored key edit...
            vm.ApplyZoneEdits(originalKey: 0, topKey: 50);
            Check("pendingsibling-edit-marks-unsaved", vm.HasUnsavedChanges);

            // ...then Add Zone, which saves + rebuilds the tree.
            var addedTo = vm.AddPlaceholderZone();
            Check("pendingsibling-add-succeeded", addedTo == leftPath);

            var leftMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath).MultisampleRef!.Value.Multisample;
            var rightMs = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == rightPath).MultisampleRef!.Value.Multisample;
            Check("pendingsibling-both-halves-same-zone-count", leftMs.Zones.Count == rightMs.Zones.Count);
            Check("pendingsibling-key-ranges-still-in-parity",
                leftMs.Zones.Select(z => ((int)z.OriginalKey, (int)z.TopKey))
                    .SequenceEqual(rightMs.Zones.Select(z => ((int)z.OriginalKey, (int)z.TopKey))));
            // The earlier mirrored edit must have survived the rebuild on BOTH sides.
            Check("pendingsibling-earlier-edit-survived", leftMs.Zones[0].TopKey == 50 && rightMs.Zones[0].TopKey == 50);

            vm.SelectNode(ZoneNode(vm, leftPath, 0));
            Check("pendingsibling-partner-still-resolves", vm.HasStereoPair);
        }

        // ── 10. Pending-edit discard is scoped by whole directory, not bare prefix ──
        //
        // "Foo.KSC" and "FooBar.KSC" have content dirs ".../Foo" and ".../FooBar", so a
        // bare StartsWith made unloading Foo silently discard FooBar's pending edits.
        {
            var (vmFoo, fooLeft, _, fooKsc) = MakeStereoFixture("Foo");
            var (vmFooBar, fooBarLeft, _, _) = MakeStereoFixture("FooBar");

            // Both fixtures are independent VMs; open FooBar's collection in Foo's VM so
            // one VM holds pending edits under both content dirs.
            vmFoo.OpenCollection(Path.Combine(scratchRoot, "FooBar", "FooBar.KSC"));
            vmFoo.SelectNode(vmFoo.Roots.Single(r => r.CollectionRef?.Path.Contains("FooBar") == true)
                .Children.Single(c => c.MultisampleRef?.Path == fooBarLeft).Children[0]);
            vmFoo.ApplyZoneEdits(originalKey: 0, topKey: 45);
            Check("prefixscope-foobar-edit-pending", vmFoo.HasUnsavedChanges);

            // Unloading Foo must NOT drop FooBar's pending edit.
            vmFoo.UnloadCollection(fooKsc);
            Check("prefixscope-foobar-edit-survives-foo-unload", vmFoo.HasUnsavedChanges);
            _ = vmFooBar; // fixture VM kept only to build the second collection on disk
        }

        // ── 11. Tempo/pitch is bounded rather than allocating an enormous buffer ──
        {
            var (vm, leftPath, _, _) = MakeStereoFixture("Tempo");
            vm.SelectNode(ZoneNode(vm, leftPath, 0));

            // 0.001x would ask for ~1000x the buffer. Clamped to MinTempoRatio instead.
            vm.ApplyTempoPitch(0.001, 0);
            Check("tempo-clamped-not-exploded",
                vm.SampleFrameCount <= (int)(200 / SampleEditorViewModel.MinTempoRatio) + 64);
        }

        return fails;
    }
}
