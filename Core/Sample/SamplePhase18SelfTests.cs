namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for the Multisample rename cascade: renaming a
// multisample now moves its .KMP file + zone-content folder to match the Kronos naming
// scheme (SampleEditorViewModel.ComputeKmpBaseName/MoveMultisampleFilesIfNeeded, both
// verified against real MASTER-LIBRARY content over FTP - see that method's own
// comment), leaves in-keymap .KSF files untouched, mirrors the move onto a stereo
// sibling, and regenerates _UserBank.KSC on save (SaveCollectionWithUserBank).
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase18SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase18_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── ComputeKmpBaseName - delegates entirely to KmpMultisample.AutoFileName
        //    (the pre-existing, hardware-confirmed writer - see that method's own
        //    comment), so these pin the SAME contract from the rename side. ──
        {
            Check("basename-tier1-5plus3", SampleEditorViewModel.ComputeKmpBaseName("DaveTest", 0) == "DAVET000");
            Check("basename-tier1-pads-short-name", SampleEditorViewModel.ComputeKmpBaseName("Bo", 5) == "BO___005");
            Check("basename-tier2-4plus4", SampleEditorViewModel.ComputeKmpBaseName("DaveTest", 1028) == "DAVE1028");
            Check("basename-uppercases-and-sanitizes-non-alphanumeric", SampleEditorViewModel.ComputeKmpBaseName("beer-", 0) == "BEER_000");
        }

        // ── NextKsfFilename/NextFreeZoneFileName at Mno1 >= 1000 - hardware-confirmed
        //    "M<mno1:D4><zone:D3>.KSF" tier (24K_1028/'s own zones: M1028000.KSF etc.),
        //    not MS<mno1:D3> (never 9-character-safe for any Mno1 past 999). ──
        {
            var m = new KmpMultisample { Name = "Test", Mno1 = 1028 };
            Check("ksf-filename-tier2-empty", m.NextKsfFilename() == "M1028000.KSF");
            m.Zones.Add(new KmpZone { Filename = "M1028000.KSF" });
            Check("ksf-filename-tier2-second-zone", m.NextKsfFilename() == "M1028001.KSF");
            Check("free-zone-filename-tier2-skips-used-index", m.NextFreeZoneFileName() == "M1028001.KSF");
        }

        // ── Stereo fixture: real KSF audio, matching the AIRTO097/BEER-000 shape
        //    (a single real zone per side, Filename following NextKsfFilename's own
        //    MS<Mno1><zoneIndex> convention) ──
        var kscPath = Path.Combine(scratchRoot, "RenameTest.KSC");
        var collection = new KscCollection { Path = kscPath };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "RenameTest"));
        collection.Save(kscPath);

        var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(collection, kscPath, "OldName", 0);
        void WriteRealZone(KmpMultisample m, string kmpPath, short seed)
        {
            var filename = m.NextKsfFilename();
            m.Zones.Add(new KmpZone { Filename = filename, OriginalKey = 36, TopKey = 36 });
            var ksfDir = Path.Combine(Path.GetDirectoryName(kmpPath)!, Path.GetFileNameWithoutExtension(kmpPath));
            Directory.CreateDirectory(ksfDir);
            var ksf = new KsfSample { Name = "OldName", SampleRate = 44100 };
            ksf.SetSamples(Enumerable.Range(0, 50).Select(i => (short)(seed + i)).ToArray());
            ksf.Save(Path.Combine(ksfDir, filename));
        }
        WriteRealZone(left, leftPath, 1000);
        WriteRealZone(right, rightPath, 2000);
        left.Save(leftPath);
        right.Save(rightPath);
        collection.Save(kscPath);

        var oldLeftFolder = Path.Combine(scratchRoot, "RenameTest", "OLDNA000");
        var oldRightFolder = Path.Combine(scratchRoot, "RenameTest", "OLDNA001");
        Check("fixture-old-left-ksf-exists", File.Exists(Path.Combine(oldLeftFolder, "MS000000.KSF")));
        Check("fixture-old-right-ksf-exists", File.Exists(Path.Combine(oldRightFolder, "MS001000.KSF")));

        var vm = new SampleEditorViewModel();
        vm.OpenCollection(kscPath);
        var leftNode = vm.AllMultisampleNodes().FirstOrDefault(n => string.Equals(n.MultisampleRef!.Value.Path, leftPath, StringComparison.OrdinalIgnoreCase));
        Check("fixture-left-node-found", leftNode != null);
        if (leftNode == null) return fails;

        vm.SelectNode(leftNode);
        vm.RenameSelectedMultisample("DaveTest");

        var newLeftKmp = Path.Combine(scratchRoot, "RenameTest", "DAVET000.KMP");
        var newRightKmp = Path.Combine(scratchRoot, "RenameTest", "DAVET001.KMP");
        var newLeftFolder = Path.Combine(scratchRoot, "RenameTest", "DAVET000");
        var newRightFolder = Path.Combine(scratchRoot, "RenameTest", "DAVET001");

        Check("old-left-kmp-gone", !File.Exists(leftPath));
        Check("old-right-kmp-gone", !File.Exists(rightPath));
        Check("new-left-kmp-present", File.Exists(newLeftKmp));
        Check("new-right-kmp-present-stereo-sibling-mirrored", File.Exists(newRightKmp));
        Check("old-left-folder-gone", !Directory.Exists(oldLeftFolder));
        Check("old-right-folder-gone", !Directory.Exists(oldRightFolder));
        Check("new-left-folder-present", Directory.Exists(newLeftFolder));
        Check("new-right-folder-present", Directory.Exists(newRightFolder));

        // In-keymap .KSF filenames must NOT change (Mno1-keyed, not name-keyed) - only
        // the FOLDER containing them moved.
        Check("left-ksf-filename-unchanged-after-move", File.Exists(Path.Combine(newLeftFolder, "MS000000.KSF")));
        Check("right-ksf-filename-unchanged-after-move", File.Exists(Path.Combine(newRightFolder, "MS001000.KSF")));

        // The rename itself is immediate (file move), but the .KMP's own CONTENT (the
        // new Name bytes) is still a deferred edit - the file at the new path should
        // still hold the OLD Name until Save. Guarded with File.Exists rather than
        // reading straight through - a regression in the move above (new file never
        // created) must show up as a graceful Check() failure here, not an unhandled
        // FileNotFoundException that crashes the whole --librarian-selftest run and
        // takes every OTHER phase's checks down with it (caught exactly this way while
        // negative-controlling this file).
        if (File.Exists(newLeftKmp))
        {
            var kmpAtNewPathBeforeSave = KmpMultisample.Open(File.ReadAllBytes(newLeftKmp));
            Check("kmp-content-still-deferred-before-save", kmpAtNewPathBeforeSave?.Name == "OldName");
        }
        else fails.Add("kmp-content-still-deferred-before-save (new .KMP doesn't exist)");

        vm.SaveAllChanges();
        if (File.Exists(newLeftKmp))
        {
            var kmpAtNewPathAfterSave = KmpMultisample.Open(File.ReadAllBytes(newLeftKmp));
            Check("kmp-content-flushed-after-save-changes", kmpAtNewPathAfterSave?.Name == "DaveTest");
        }
        else fails.Add("kmp-content-flushed-after-save-changes (new .KMP doesn't exist)");

        var kscAfterSave = KscCollection.Open(File.ReadAllBytes(kscPath));
        Check("ksc-entries-updated-to-new-filenames", kscAfterSave.Entries.Contains("DAVET000.KMP", StringComparer.OrdinalIgnoreCase));
        Check("ksc-entries-no-longer-list-old-filename", !kscAfterSave.Entries.Contains("OLDNA000.KMP", StringComparer.OrdinalIgnoreCase));

        var userBankPath = Path.Combine(scratchRoot, "RenameTest_UserBank.KSC");
        Check("userbank-generated-on-save-changes", File.Exists(userBankPath));

        // ── Collision guard: renaming to a name that truncates to an ALREADY-USED
        //    filename must refuse, leaving everything untouched ──
        var vm2 = new SampleEditorViewModel();
        var kscPath2 = Path.Combine(scratchRoot, "Collision.KSC");
        var collection2 = new KscCollection { Path = kscPath2 };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "Collision"));
        collection2.Save(kscPath2);
        vm2.OpenCollection(kscPath2);
        var nodeA = vm2.NewMultisampleInCollection("AAAAA", 0);
        var nodeB = vm2.NewMultisampleInCollection("BBBBB", 1);
        Check("collision-fixture-both-created", nodeA != null && nodeB != null);
        if (nodeA != null && nodeB != null)
        {
            vm2.SelectNode(nodeB);
            // Two LIVE multisamples can never collide via a rename (unique Mno1 among
            // live nodes always yields a different computed index suffix), so this
            // exercises the guard against a plain FILESYSTEM collision instead - an
            // unrelated file already sitting at the exact target path (B, Mno1=1,
            // renamed to "CCCCC" -> CCCCC001.KMP).
            var contentDir = Path.Combine(scratchRoot, "Collision");
            File.WriteAllBytes(Path.Combine(contentDir, "CCCCC001.KMP"), [1, 2, 3]);
            vm2.RenameSelectedMultisample("CCCCC");
            Check("collision-guard-refuses-when-target-file-exists", nodeB.MultisampleRef!.Value.Multisample.Name == "BBBBB");
            // NewMultisampleInCollection names a fresh .KMP via AutoFileName too - B's
            // own file is "BBBBB001.KMP" from creation, matching what a subsequent
            // rename would also compute.
            Check("collision-guard-leaves-original-kmp-in-place", File.Exists(Path.Combine(contentDir, "BBBBB001.KMP")));
        }

        return fails;
    }
}
