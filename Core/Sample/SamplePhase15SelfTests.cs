namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for "Save as..." (2026-08-24, tree right-click). The one real risk
// in this feature, per its own design comment (SampleEditorViewModel.SaveCollectionAs):
// an edit made BEFORE Save As runs must survive at the NEW path once actually saved, and
// the ORIGINAL file must never be touched, in either direction - copy-first, then re-key
// the pending-edit dictionaries onto the new content folder, never flush-then-copy.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase15SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase15_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var oldKscPath = Path.Combine(scratchRoot, "Orig.KSC");
        var newKscPath = Path.Combine(scratchRoot, "Copy.KSC");

        var vm = new SampleEditorViewModel();
        vm.NewCollection(oldKscPath);
        vm.NewMultisampleInCollection("Kick", 0);

        var oldKmpPath = Path.Combine(KscCollection.ContentDirFor(oldKscPath), "Kick.KMP");
        var originalBytes = File.ReadAllBytes(oldKmpPath);

        // A pending, unsaved edit - made BEFORE Save As runs.
        var msNode = vm.Roots.Single().Children.Single();
        vm.SelectNode(msNode);
        vm.RenameSelectedMultisample("KickRenamed");
        Check("rename-is-pending-before-save-as", vm.HasUnsavedChanges);

        vm.SaveCollectionAs(newKscPath);

        Check("original-kmp-untouched-immediately-after-save-as", File.ReadAllBytes(oldKmpPath).SequenceEqual(originalBytes));
        Check("active-path-switched-to-new-collection", vm.ActiveCollectionPath == newKscPath);
        Check("old-root-removed-from-tree", vm.Roots.All(r => r.CollectionRef?.Path != oldKscPath));
        Check("new-root-added-to-tree", vm.Roots.Any(r => r.CollectionRef?.Path == newKscPath));

        var newRoot = vm.Roots.FirstOrDefault(r => r.CollectionRef?.Path == newKscPath);
        var newMsNode = newRoot?.Children.SingleOrDefault();
        Check("new-tree-has-the-multisample", newMsNode != null);

        if (newMsNode != null)
        {
            // The pending rename must have followed the multisample to its new path
            // (RekeyPendingEdits) - not silently dropped by RebuildTreeFromCollection
            // reading the just-copied CLEAN file instead (the entry-21 stale-dirty-key
            // regression class this is guarding against).
            vm.SelectNode(newMsNode);
            Check("pending-rename-survived-at-new-path", vm.CurrentMultisampleName == "KickRenamed");
        }

        // Right after Save As, the NEW .KMP on disk is still the OLD (unrenamed) content -
        // Save As copies clean disk state, it does not flush pending edits into the copy
        // either. Only an explicit Save writes them out, and only to the new location.
        var newKmpPath = Path.Combine(KscCollection.ContentDirFor(newKscPath), "Kick.KMP");
        var newKmpBeforeSave = KmpMultisample.Open(File.ReadAllBytes(newKmpPath));
        Check("new-kmp-not-renamed-on-disk-until-saved", newKmpBeforeSave?.Name == "Kick");

        vm.SaveAllChanges();

        var newKmpAfterSave = KmpMultisample.Open(File.ReadAllBytes(newKmpPath));
        Check("rename-written-to-new-kmp-after-save", newKmpAfterSave?.Name == "KickRenamed");

        var oldKmpAfterSave = KmpMultisample.Open(File.ReadAllBytes(oldKmpPath));
        Check("original-kmp-still-untouched-after-save", oldKmpAfterSave?.Name == "Kick");

        // Picking the exact same path Save As started from must be refused, not silently
        // no-op or corrupt the open collection.
        var vm2 = new SampleEditorViewModel();
        vm2.NewCollection(Path.Combine(scratchRoot, "SamePath.KSC"));
        var samePath = vm2.ActiveCollectionPath!;
        vm2.SaveCollectionAs(samePath);
        Check("save-as-same-path-refused", vm2.StatusText.Contains("different file name"));

        return fails;
    }
}
