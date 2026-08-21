namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this round's UI feedback batch (see Commit Notes.md entry 19):
// AddPlaceholderZone mirroring onto a resolved stereo sibling (previously only the
// primary half gained the new zone/shrunk last zone, silently breaking the exact-
// key-range match stereo-partner resolution depends on and dropping the shared
// waveform view back to mono); the Top Key floor on the manual key-range fields
// (can't go below the previous zone's own Top Key + 1); Unload Collection; Revert KSC/
// ALL Changes; and NEWMS000/NEWMS001 missing-.KMP warning suppression. The interactive-
// only pieces (the keymap's resize cursor confined to the header, the more-visible key
// highlight, the tab framework itself, Add Zone's focus/selection, the tree's right-
// click menu) are verified visually/by click-through instead. Wired into
// App.xaml.cs's --librarian-selftest.
static class SamplePhase12SelfTests
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase12_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── AddPlaceholderZone mirrors the new zone (and the shrunk last zone) onto a
        //    resolved stereo sibling, keeping both halves' key ranges in exact parity ──
        {
            var collectionPath = Path.Combine(scratchRoot, "Stereo.KSC");
            var collection = new KscCollection { Path = collectionPath };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Stereo"));
            collection.Save(collectionPath);

            var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                collection, collectionPath, "StereoKit", 20);

            // One matching zone on each side (0..127) - AddPlaceholderZone should carve
            // the new placeholder off the top of THIS zone on both sides identically.
            left.Zones.Add(new KmpZone { Filename = "MS020000.KSF", OriginalKey = 0, TopKey = 127 });
            right.Zones.Add(new KmpZone { Filename = "MS021000.KSF", OriginalKey = 0, TopKey = 127 });
            left.Save(leftPath);
            right.Save(rightPath);

            // A real .KSF on disk for each zone - selecting a zone whose .KSF is
            // missing bails out before ever reaching ResolveStereoPartner (see
            // SelectNode's own "Referenced sample ... not found on disk" branch), so
            // without this the stereo-partner checks below would fail for an unrelated
            // reason (no sample loaded at all) rather than testing what this block
            // actually exercises.
            void WriteFakeKsf(string kmpPath, string ksfFilename)
            {
                var ksfDir = Path.Combine(Path.GetDirectoryName(kmpPath) ?? "", Path.GetFileNameWithoutExtension(kmpPath));
                Directory.CreateDirectory(ksfDir);
                var ksf = new KsfSample { Name = Path.GetFileNameWithoutExtension(ksfFilename), SampleRate = 44100 };
                ksf.SetSamples([1, 2, 3, 4, 5]);
                ksf.Save(Path.Combine(ksfDir, ksfFilename));
            }
            WriteFakeKsf(leftPath, "MS020000.KSF");
            WriteFakeKsf(rightPath, "MS021000.KSF");

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(collectionPath);
            var leftMsNode = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath);
            var leftZoneNode = leftMsNode.Children.Single();
            vm.SelectNode(leftZoneNode);
            Check("stereo-add-resolves-partner-before-add", vm.HasStereoPair);

            var kmpPath = vm.AddPlaceholderZone();
            Check("stereo-add-reports-success", kmpPath == leftPath);
            Check("stereo-add-status-mentions-both-channels", vm.StatusText.Contains("both stereo channels"));

            var reloadedLeft = KmpMultisample.Open(File.ReadAllBytes(leftPath))!;
            var reloadedRight = KmpMultisample.Open(File.ReadAllBytes(rightPath))!;
            Check("stereo-add-left-gained-a-zone", reloadedLeft.Zones.Count == 2);
            Check("stereo-add-right-gained-a-zone", reloadedRight.Zones.Count == 2);
            Check("stereo-add-key-ranges-match-exactly",
                reloadedLeft.Zones[0].TopKey == reloadedRight.Zones[0].TopKey &&
                reloadedLeft.Zones[1].OriginalKey == reloadedRight.Zones[1].OriginalKey &&
                reloadedLeft.Zones[1].TopKey == reloadedRight.Zones[1].TopKey);
            Check("stereo-add-right-placeholder-is-skipped", reloadedRight.Zones[1].IsSkipped);

            // Re-selecting the (now key-range-matching) first zone on either side must
            // resolve the stereo partner again - this is the exact bug being closed:
            // before mirroring, the left/right key ranges diverged after Add Zone and
            // this lookup silently started failing, dropping the view to mono.
            var refreshedLeftZoneNode = vm.Roots.Single().Children.Single(c => c.MultisampleRef?.Path == leftPath).Children[0];
            vm.SelectNode(refreshedLeftZoneNode);
            Check("stereo-partner-still-resolves-after-add", vm.HasStereoPair);
        }

        // ── ApplyZoneEdits: Top Key can't be typed below the previous zone's own
        //    Top Key + 1 ──
        {
            var kscPath = Path.Combine(scratchRoot, "Floor.KSC");
            var ksc = new KscCollection { Entries = ["Floor.KMP"] };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Floor"));
            ksc.Save(kscPath);

            var kmpPath = Path.Combine(scratchRoot, "Floor", "Floor.KMP");
            var kmp = new KmpMultisample { Name = "Floor" };
            kmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 0, TopKey = 60 });
            kmp.Zones.Add(new KmpZone { Filename = "MS000001.KSF", OriginalKey = 61, TopKey = 90 });
            kmp.Save(kmpPath);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var secondZoneNode = vm.Roots.Single().Children.Single().Children[1];
            vm.SelectNode(secondZoneNode);

            // Attempting a Top Key at/below the previous zone's own Top Key (60) must
            // clamp up to 61 (60 + 1), not accept it as typed.
            vm.ApplyZoneEdits(originalKey: 61, topKey: 55);
            Check("topkey-floor-clamped-to-previous-plus-one", vm.ZoneTopKey == 61);

            // A value already above the floor is untouched.
            vm.ApplyZoneEdits(originalKey: 61, topKey: 95);
            Check("topkey-above-floor-unclamped", vm.ZoneTopKey == 95);
        }

        // ── NEWMS000/NEWMS001 missing-.KMP warnings are suppressed; any other missing
        //    .KMP still warns exactly as before ──
        {
            var kscPath = Path.Combine(scratchRoot, "Placeholder.KSC");
            var ksc = new KscCollection { Entries = ["NEWMS000.KMP", "NEWMS001.KMP", "RealMissing.KMP"] };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Placeholder"));
            ksc.Save(kscPath);
            // None of the three referenced .KMP files are actually created on disk.

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            Check("newms-placeholder-kmps-not-warned",
                !vm.StatusText.Contains("NEWMS000") && !vm.StatusText.Contains("NEWMS001"));
            Check("other-missing-kmp-still-warned", vm.StatusText.Contains("RealMissing"));
        }

        // ── Unload Collection removes the root and clears the active-collection state
        //    when unloading the currently-active one; other open collections untouched ──
        {
            string BuildSimple(string name)
            {
                var kscPath = Path.Combine(scratchRoot, $"{name}.KSC");
                var ksc = new KscCollection();
                Directory.CreateDirectory(Path.Combine(scratchRoot, name));
                ksc.Save(kscPath);
                return kscPath;
            }
            var kscX = BuildSimple("UnloadX");
            var kscY = BuildSimple("UnloadY");

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscX);
            vm.OpenCollection(kscY);
            Check("unload-setup-two-roots", vm.Roots.Count == 2);
            Check("unload-active-path-is-most-recent", vm.ActiveCollectionPath == kscY);

            vm.UnloadCollection(kscX);
            Check("unload-removes-only-that-root", vm.Roots.Count == 1 && vm.Roots.Single().CollectionRef?.Path == kscY);
            Check("unload-of-inactive-collection-keeps-active-path", vm.ActiveCollectionPath == kscY);

            vm.UnloadCollection(kscY);
            Check("unload-of-active-collection-clears-it", vm.Roots.Count == 0 && !vm.HasActiveCollection);
        }

        // ── Revert KSC Changes discards an in-memory zone edit by reloading from disk,
        //    without touching a second, unrelated open collection ──
        {
            string BuildOneZone(string name)
            {
                var kscPath = Path.Combine(scratchRoot, $"{name}.KSC");
                var ksc = new KscCollection { Entries = [$"{name}.KMP"] };
                Directory.CreateDirectory(Path.Combine(scratchRoot, name));
                ksc.Save(kscPath);
                var kmpPath = Path.Combine(scratchRoot, name, $"{name}.KMP");
                var kmp = new KmpMultisample { Name = name };
                kmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 60, TopKey = 60 });
                kmp.Save(kmpPath);
                return kscPath;
            }
            var kscRevert = BuildOneZone("RevertMe");
            var kscOther = BuildOneZone("LeaveMeAlone");

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscRevert);
            vm.OpenCollection(kscOther);
            var otherRootExpandedBefore = vm.Roots.Single(r => r.CollectionRef?.Path == kscOther).IsExpanded;

            // Selecting a node re-resolves "the active collection" to whichever root
            // owns it (SelectNode's own comment) - so selecting a zone in kscRevert
            // makes IT the active one, even though kscOther was opened more recently.
            var revertZoneNode = vm.Roots.Single(r => r.CollectionRef?.Path == kscRevert).Children.Single().Children.Single();
            vm.SelectNode(revertZoneNode);
            Check("revert-active-path-tracks-selection", vm.ActiveCollectionPath == kscRevert);
            vm.ApplyZoneEdits(originalKey: 60, topKey: 70); // unsaved in-memory edit, never saved to disk

            vm.RevertActiveCollectionChanges();
            var reloadedZone = vm.Roots.Single(r => r.CollectionRef?.Path == kscRevert).Children.Single().Children.Single();
            Check("revert-discards-unsaved-topkey-edit", reloadedZone.ZoneRef!.Value.Zone.TopKey == 60);
            Check("revert-leaves-other-collection-in-place",
                vm.Roots.Any(r => r.CollectionRef?.Path == kscOther) &&
                vm.Roots.Single(r => r.CollectionRef?.Path == kscOther).IsExpanded == otherRootExpandedBefore);
        }

        // ── Revert ALL Changes closes every open collection and resets session state ──
        {
            var kscA = Path.Combine(scratchRoot, "AllA.KSC");
            new KscCollection { Path = kscA }.Save(kscA);
            var kscB = Path.Combine(scratchRoot, "AllB.KSC");
            new KscCollection { Path = kscB }.Save(kscB);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscA);
            vm.OpenCollection(kscB);
            Check("revert-all-setup-two-roots", vm.Roots.Count == 2);

            vm.RevertAllChanges();
            Check("revert-all-clears-every-root", vm.Roots.Count == 0);
            Check("revert-all-clears-active-collection", !vm.HasActiveCollection);
            Check("revert-all-clears-unsaved-flag", !vm.HasUnsavedChanges);
            Check("revert-all-clears-undo", !vm.CanUndo && !vm.CanRedo);
        }

        return fails;
    }
}
