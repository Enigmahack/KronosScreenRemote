namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for the "link to existing sample" authoring feature
// (SampleEditorViewModel.LinkExistingKsfToZone / KsfSample.SetStubTarget / the Sample
// panel's Link checkbox) - the doc §3.2/§7 write side, added once kronosology's own
// 10-session RE investigation (2026-09-03/04) confirmed the real mechanism (SNO1
// collision, not SMF1) and, critically, that a header-only .KSF with NO SMF1 chunk
// hangs Eva's Disk-page Load solid on real hardware. Wired into App.xaml.cs's
// --librarian-selftest.
static class SamplePhase21SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase21_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "LinkTest.KSC");
        var ksc = new KscCollection { Entries = ["LinkTest.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "LinkTest"));
        ksc.Save(kscPath);

        var kmpPath = Path.Combine(scratchRoot, "LinkTest", "LinkTest.KMP");
        var kmp = new KmpMultisample { Name = "LinkTest", Mno1 = 1 };
        kmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
        kmp.Zones.Add(new KmpZone { Filename = "MS001001.KSF", OriginalKey = 61, TopKey = 61 });
        kmp.Save(kmpPath);

        var ksfDir = Path.Combine(scratchRoot, "LinkTest", "LinkTest");
        Directory.CreateDirectory(ksfDir);
        var realPath = Path.Combine(ksfDir, "MS001000.KSF");
        var real = new KsfSample
        {
            Name = "RealSrc", SampleRate = 44100, Sno1 = 42, Flags = 0x01,
            SampleStart = 5, LoopStart = 10, LoopEnd = 40,
        };
        real.SetSamples(Enumerable.Range(0, 50).Select(i => (short)(i * 100)).ToArray());
        real.Save(realPath);
        // Zone1 starts as a plain (soon-to-be-overwritten) placeholder file - just
        // enough to be a readable, real, non-conflicting .KSF before linking.
        var placeholderPath = Path.Combine(ksfDir, "MS001001.KSF");
        new KsfSample { Name = "Placeholder", SampleRate = 44100, Sno1 = 99 }.Save(placeholderPath);

        var vm = new SampleEditorViewModel();
        vm.OpenCollection(kscPath);
        var zone1Node = FindZoneByFilename(vm.Roots, "MS001001.KSF");
        Check("link-zone1-found", zone1Node != null);
        var zone1 = zone1Node!.ZoneRef!.Value.Zone;

        // ── Core write: link zone1 to zone0's real audio ──
        var result = vm.LinkExistingKsfToZone(zone1, realPath);
        Check("link-reports-success", result != null);

        var linked = KsfSample.Open(File.ReadAllBytes(placeholderPath));
        Check("link-file-still-readable", linked != null);
        Check("link-is-header-only", linked!.IsHeaderOnly);
        Check("link-sno1-matches-source", linked.Sno1 == real.Sno1);
        Check("link-smf1-present-and-correct", linked.StubTargetFilename == "MS001000.KSF");
        Check("link-own-loop-fields-seeded-from-source",
            linked.SampleStart == real.SampleStart && linked.LoopStart == real.LoopStart && linked.LoopEnd == real.LoopEnd);
        Check("link-name-matches-source", linked.Name == real.Name);

        // ── Hardware-lesson regression guard: NEVER write a header-only .KSF with no
        //    SMF1 - the exact shape that hung a real Kronos solid (2026-09-04) ──
        Check("link-never-omits-smf1", linked.StubTargetFilename != null);

        // ── Read-side round-trip: SampleLinkResolver must resolve the freshly-written
        //    link back to the real audio, byte for byte ──
        var resolved = SampleLinkResolver.Resolve(linked, kmpPath);
        Check("link-resolves-via-resolver", resolved != null);
        Check("link-resolver-finds-real-pcm", resolved != null && resolved.Sample.Samples().SequenceEqual(real.Samples()));
        Check("link-resolver-sno1-verified", resolved is { Sno1Verified: true });

        // ── Refuses to link to another link (no multi-hop chains) ──
        vm.OpenCollection(kscPath); // fresh tree/state
        var zone1AfterLink = FindZoneByFilename(vm.Roots, "MS001001.KSF")!.ZoneRef!.Value.Zone;
        var refuseChain = vm.LinkExistingKsfToZone(zone1AfterLink, placeholderPath);
        Check("link-refuses-linking-to-a-link", refuseChain == null);

        // ── Refuses self-link (linking a zone to its own current file) ──
        var zone0Node = FindZoneByFilename(vm.Roots, "MS001000.KSF");
        var zone0 = zone0Node!.ZoneRef!.Value.Zone;
        var refuseSelf = vm.LinkExistingKsfToZone(zone0, realPath);
        Check("link-refuses-self-link", refuseSelf == null);
        var stillReal = KsfSample.Open(File.ReadAllBytes(realPath));
        Check("link-refused-self-link-left-source-untouched", stillReal != null && !stillReal.IsHeaderOnly);

        // ── Refuses a source filename too long for SMF1's 12-byte field, rather than
        //    silently truncating into a broken/unresolvable link (the real bug this
        //    guard replaces - a repository sample like "DirtyBit-L.KSF" truncated to
        //    "DirtyBit-L.K", which resolves to nothing) ──
        {
            var longNamePath = Path.Combine(ksfDir, "DirtyBit-L.KSF");
            var longNamed = new KsfSample { Name = "DirtyBit", Suffix = "-L", SampleRate = 44100, Sno1 = 7 };
            longNamed.SetSamples([1, 2, 3, 4, 5]);
            longNamed.Save(longNamePath);

            var zone1Again = FindZoneByFilename(vm.Roots, "MS001001.KSF")!.ZoneRef!.Value.Zone;
            var refuseLong = vm.LinkExistingKsfToZone(zone1Again, longNamePath);
            Check("link-refuses-filename-too-long-for-smf1", refuseLong == null);
            Check("link-refusal-message-names-the-file", vm.StatusText.Contains("DirtyBit-L.KSF"));
            // The zone's own prior (already-linked, from the block above) file must be
            // untouched by the refused attempt - not partially overwritten.
            var stillLinked = KsfSample.Open(File.ReadAllBytes(placeholderPath));
            Check("link-refused-long-name-left-zone-file-untouched", stillLinked != null && stillLinked.IsHeaderOnly && stillLinked.Sno1 == real.Sno1);
        }

        // ── Field-only edit on a linked stub: must commit without touching Pcm, must
        //    NOT require real audio to be present, and must leave the link (SNO1/SMF1)
        //    intact ──
        {
            vm.OpenCollection(kscPath);
            var zNode = FindZoneByFilename(vm.Roots, "MS001001.KSF");
            vm.SelectNode(zNode);
            Check("edit-linked-stub-is-recognized-as-linked", vm.SampleIsLinkedStub);
            Check("edit-linked-stub-shows-resolved-frame-count", vm.SampleFrameCount == real.FrameCount);

            vm.SetLoopTune(5);
            Check("edit-linked-stub-loop-tune-committed", vm.SampleLoopTune == 5);

            vm.SetReversed(true);
            Check("edit-linked-stub-reverse-committed", vm.SampleReverseEnabled);

            Check("edit-linked-stub-marked-dirty", vm.HasUnsavedChanges);
            vm.SaveAllChanges();

            var afterFieldEdits = KsfSample.Open(File.ReadAllBytes(placeholderPath));
            Check("edit-linked-stub-still-header-only-after-field-edits", afterFieldEdits!.IsHeaderOnly);
            Check("edit-linked-stub-sno1-unchanged-after-field-edits", afterFieldEdits.Sno1 == real.Sno1);
            Check("edit-linked-stub-smf1-unchanged-after-field-edits", afterFieldEdits.StubTargetFilename == "MS001000.KSF");
        }

        return fails;
    }

    static SampleTreeNode? FindZoneByFilename(IEnumerable<SampleTreeNode> nodes, string filename)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef is { } zr && string.Equals(zr.Zone.Filename, filename, StringComparison.OrdinalIgnoreCase)) return node;
            var found = FindZoneByFilename(node.Children, filename);
            if (found != null) return found;
        }
        return null;
    }
}
