namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this round's bug-fix batch: opening a second .KSC must add a
// second tree root rather than replacing the first (RebuildTreeFromCollection now
// rebuilds/replaces only the ONE root matching the .KSC path it was given); an edit-
// triggered tree rebuild must carry expansion state forward instead of silently
// re-collapsing everything (SampleTreeNode.IsExpanded is now actually bound to the
// TreeViewItem - see SampleEditorWindow.xaml - and RebuildTreeFromCollection restores
// it by matching each multisample node's stable .KMP path); and session-wide unsaved-
// changes tracking (HasUnsavedChanges) must survive navigating away from the dirty
// item, which is exactly the scenario "closed the window and lost my edits" needs
// caught. The interactive-only pieces (Delete key cutting a waveform selection instead
// of deleting the whole zone, Ctrl+A working regardless of which control has focus,
// live marker-drag mirroring in stereo, the keymap boundary line's corrected pixel
// alignment, click-to-set-cursor no longer auto-playing) are verified visually/by
// click-through instead. Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase11SelfTests
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

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase11_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // Two SEPARATE single-multisample collections, "A" and "B" - simulating the
        // user opening two different .KSCs in the same session.
        string BuildCollection(string name)
        {
            var kscPath = Path.Combine(scratchRoot, $"{name}.KSC");
            var ksc = new KscCollection { Entries = [$"{name}.KMP"] };
            Directory.CreateDirectory(Path.Combine(scratchRoot, name));
            ksc.Save(kscPath);

            var kmpPath = Path.Combine(scratchRoot, name, $"{name}.KMP");
            var kmp = new KmpMultisample { Name = name, Mno1 = 0 };
            kmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 60, TopKey = 60 });
            kmp.Save(kmpPath);

            var ksfDir = Path.Combine(scratchRoot, name, name);
            Directory.CreateDirectory(ksfDir);
            var ksf = new KsfSample { Name = name, SampleRate = 44100 };
            ksf.SetSamples([1, 2, 3, 4, 5]);
            ksf.Save(Path.Combine(ksfDir, "MS000000.KSF"));

            return kscPath;
        }

        var kscA = BuildCollection("PhaseA");
        var kscB = BuildCollection("PhaseB");

        // ── Opening a second .KSC adds a second root, doesn't replace the first ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscA);
            Check("first-collection-opens-one-root", vm.Roots.Count == 1);

            vm.OpenCollection(kscB);
            Check("second-collection-adds-a-root-not-replaces", vm.Roots.Count == 2);
            Check("both-collections-still-present",
                vm.Roots.Any(r => r.CollectionRef?.Path == kscA) && vm.Roots.Any(r => r.CollectionRef?.Path == kscB));

            // Re-opening the FIRST collection again must replace only ITS OWN root.
            vm.OpenCollection(kscA);
            Check("reopening-a-collection-still-two-roots", vm.Roots.Count == 2);
        }

        // ── Expansion state survives an edit-triggered tree rebuild ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscA);
            var root = vm.Roots.Single(r => r.CollectionRef?.Path == kscA);
            var msNode = root.Children.Single();
            root.IsExpanded = true;
            msNode.IsExpanded = true;

            var zoneNode = msNode.Children.Single();
            vm.SelectNode(zoneNode);
            vm.AddPlaceholderZone(); // triggers RebuildTreeFromCollection via RefreshTreeAfterMutation

            var newRoot = vm.Roots.Single(r => r.CollectionRef?.Path == kscA);
            Check("root-expansion-survives-rebuild", newRoot.IsExpanded);
            Check("multisample-expansion-survives-rebuild", newRoot.Children.Single().IsExpanded);
        }

        // ── Session-wide unsaved-changes tracking survives navigating away - its own
        //    fresh collection ("PhaseC"), not kscA/kscB (both now mutated on disk by
        //    the earlier blocks above - re-opening them fresh here would pick up
        //    those edits and no longer have exactly one zone). ──
        var kscC = BuildCollection("PhaseC");
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscC);
            Check("starts-with-no-unsaved-changes", !vm.HasUnsavedChanges);

            var msNode = vm.Roots.Single(r => r.CollectionRef?.Path == kscC).Children.Single();
            var zoneNode = msNode.Children.Single();
            vm.SelectNode(zoneNode);
            vm.ApplyZoneEdits(61, 61); // a zone-field edit, sets _zoneDirty
            Check("edit-marks-session-dirty", vm.HasUnsavedChanges);

            // Navigate away (SelectNode resets the PER-SELECTION dirty flags) - the
            // session-wide flag must NOT reset, or the whole point of this tracking
            // (catching an edit left behind after navigating elsewhere) is defeated.
            vm.SelectNode(null);
            Check("session-dirty-survives-navigation-away", vm.HasUnsavedChanges);

            vm.SelectNode(zoneNode);
            vm.SaveSelectedMultisample();
            Check("save-clears-session-dirty", !vm.HasUnsavedChanges);
        }

        return fails;
    }
}
