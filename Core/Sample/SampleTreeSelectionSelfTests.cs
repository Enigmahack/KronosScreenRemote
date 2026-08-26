namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Regression coverage for a real bug caught via the stereo-pair E2E smoke test:
// SampleEditorViewModel's tree-rebuilding methods (RebuildTreeFromCollection,
// RefreshTreeAfterMutation - called after NewMultisampleInCollection, ImportAudioAsNewZone,
// NewStereoMultisamplePairInCollection, ImportStereoAudioAsNewZonePair, ...) replace every
// multisample/zone node with a freshly-opened-from-disk object. Before the fix, the
// ViewModel's own _selectedNode/_selectedZone/_selectedSample fields were left pointing
// at the now-stale pre-rebuild objects, so a subsequent SaveSelectedMultisample/
// SaveSelectedSample would silently act on stale (pre-rebuild) in-memory state - capable
// of overwriting freshly-written content on disk with an older in-memory copy that never
// saw the new zone. The fix: every tree rebuild clears selection first (SelectNode(null)),
// so a stale selection can never drive a save - the caller must explicitly re-select,
// same as choosing a different tree item. Wired into App.xaml.cs's --librarian-selftest.
static class SampleTreeSelectionSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_tree_selection_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "Test.KSC");
        var ksc = new KscCollection { Entries = ["Test.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "Test"));
        ksc.Save(kscPath);

        var kmpPath = Path.Combine(scratchRoot, "Test", "Test.KMP");
        var kmp = new KmpMultisample { Name = "Test", Mno1 = 1 };
        kmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
        kmp.Save(kmpPath);

        var ksfDir = Path.Combine(scratchRoot, "Test", "Test");
        Directory.CreateDirectory(ksfDir);
        var s = new KsfSample { Name = "S1", SampleRate = 44100 };
        s.SetSamples([1, 2, 3, 4, 5]);
        s.Save(Path.Combine(ksfDir, "MS001000.KSF"));

        var vm = new SampleEditorViewModel();
        vm.OpenCollection(kscPath);
        var zoneNode = FindZoneByFilename(vm.Roots, "MS001000.KSF");
        Check("setup-zone-found", zoneNode != null);
        vm.SelectNode(zoneNode);
        Check("setup-zone-selected", vm.HasZoneSelected && vm.HasSampleLoaded);

        // Any operation that rebuilds the tree (creating a second multisample here,
        // the simplest one that doesn't require real audio decode) must clear the
        // stale selection, not leave HasZoneSelected/HasSampleLoaded showing pre-
        // rebuild state for an object no longer in the tree.
        vm.NewMultisampleInCollection("Other", 2);
        Check("rebuild-clears-zone-selection", !vm.HasZoneSelected);
        Check("rebuild-clears-sample-selection", !vm.HasSampleLoaded);

        // With selection cleared, a Save attempt must fail loudly ("no multisample
        // selected") rather than silently resolving to stale pre-rebuild state.
        vm.SaveSelectedMultisample();
        Check("rebuild-then-save-refuses-not-silently-succeeds",
            vm.StatusText.Contains("No multisample selected"));

        return fails;
    }

    static SampleTreeNode? FindZoneByFilename(IEnumerable<SampleTreeNode> nodes, string filename)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef?.Zone.Filename == filename) return node;
            var found = FindZoneByFilename(node.Children, filename);
            if (found != null) return found;
        }
        return null;
    }
}
