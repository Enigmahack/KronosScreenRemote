namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for the Sample Editor's FTP pull/push wiring (Phase 2). Real FTP
// I/O lives entirely behind IRemoteSampleSource (see its own doc comment) - production
// code (KronosRemoteSampleSource + SampleRemoteBrowserDialog) needs a live Kronos and a
// Window, but the ViewModel-level logic this exercises (opening what a pull handed
// back, resolving push destinations from the remote map, refusing to push dirty/
// header-only/never-pulled content) doesn't, via an in-memory fake - same seam PcgPane
// LoadSelfTests already uses for IRemotePcgSource. Wired into App.xaml.cs's
// --librarian-selftest.
static class SampleRemoteSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_remote_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // A minimal .KSC + .KMP (one real zone, one header-only zone) + their .KSFs,
        // built on disk following the real folder convention - same as a real pull
        // would have produced, just without any FTP involved.
        var kscPath = Path.Combine(scratchRoot, "Test.KSC");
        var ksc = new KscCollection { Entries = ["Test.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "Test"));
        ksc.Save(kscPath);

        var kmpPath = Path.Combine(scratchRoot, "Test", "Test.KMP");
        var kmp = new KmpMultisample { Name = "Test", Mno1 = 1 };
        kmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
        kmp.Zones.Add(new KmpZone { Filename = "MS001001.KSF", OriginalKey = 61, TopKey = 61 });
        kmp.Save(kmpPath);

        var ksfDir = Path.Combine(scratchRoot, "Test", "Test");
        Directory.CreateDirectory(ksfDir);
        var ksf1Path = Path.Combine(ksfDir, "MS001000.KSF");
        var s1 = new KsfSample { Name = "S1", SampleRate = 44100 };
        s1.SetSamples([1, 2, 3, 4, 5]);
        s1.Save(ksf1Path);

        var ksf2Path = Path.Combine(ksfDir, "MS001001.KSF");
        var s2 = new KsfSample { Name = "S2" }; // Pcm defaults empty -> header-only
        s2.Save(ksf2Path);

        var map = new Dictionary<string, string>
        {
            [kscPath] = "/ClaudeTest/Test.KSC",
            [kmpPath] = "/ClaudeTest/Test/Test.KMP",
            [ksf1Path] = "/ClaudeTest/Test/Test/MS001000.KSF",
            [ksf2Path] = "/ClaudeTest/Test/Test/MS001001.KSF",
        };

        // ── A successful pull opens the collection exactly as if from disk ──
        {
            var vm = new SampleEditorViewModel();
            var fake = new FakeRemoteSampleSource(kscPath, map);
            vm.PullCollectionFromKronosAsync(fake).GetAwaiter().GetResult();
            Check("pull-uses-ksc-extension-filter", fake.PulledExtensionFilter == ".KSC");
            Check("pull-populates-tree", vm.Roots.Count > 0);

            var zoneNode = FindZone(vm.Roots, "MS001000.KSF");
            Check("pull-zone-found", zoneNode != null);
            vm.SelectNode(zoneNode);
            Check("pull-sample-loaded", vm.HasSampleLoaded && !vm.SampleIsHeaderOnly);

            // Pushing right after a pull (nothing edited, not header-only, pulled from
            // a known remote path) must succeed and hit the right remote path.
            vm.PushSelectedSampleAsync(fake).GetAwaiter().GetResult();
            Check("push-clean-sample-succeeds", fake.Pushed.Count == 1);
            Check("push-clean-sample-right-remote-path",
                fake.Pushed.Count == 1 && fake.Pushed[0].remote == "/ClaudeTest/Test/Test/MS001000.KSF");

            // A header-only sample must never be pushed - it would silently overwrite
            // a good on-Kronos sample with zero frames (doc §3.3's real failure mode).
            var headerOnlyZone = FindZone(vm.Roots, "MS001001.KSF");
            vm.SelectNode(headerOnlyZone);
            Check("push-header-only-sample-selected", vm.SampleIsHeaderOnly);
            vm.PushSelectedSampleAsync(fake).GetAwaiter().GetResult();
            Check("push-refuses-header-only", fake.Pushed.Count == 1); // still just the one from above

            // An unsaved local edit must block push until Save Sample is used - pushing
            // would otherwise upload the stale pre-edit file and silently discard the
            // in-memory edit.
            vm.SelectNode(FindZone(vm.Roots, "MS001000.KSF"));
            vm.ApplySampleEdits(22050, true, 0, 0, 0);
            vm.PushSelectedSampleAsync(fake).GetAwaiter().GetResult();
            Check("push-refuses-dirty-sample", fake.Pushed.Count == 1);
        }

        // ── Content that was never pulled has nowhere to push back to ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath); // local open, NOT PullCollectionFromKronosAsync - no remote map entries
            vm.SelectNode(FindZone(vm.Roots, "MS001000.KSF"));
            var fake = new FakeRemoteSampleSource(kscPath, map); // map is irrelevant here - this VM's own _remoteMap is empty
            vm.PushSelectedSampleAsync(fake).GetAwaiter().GetResult();
            Check("push-refuses-never-pulled", fake.Pushed.Count == 0);
        }

        // ── A cancelled/failed pull leaves the previously loaded content unchanged
        //    (same "last attempted load wins" rule PcgPaneViewModel's Kronos path
        //    already honors) and surfaces the source's status message ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var before = vm.Roots.Count;
            var cancelled = new FakeRemoteSampleSource(RemoteSamplePullResult.Failed("Load from Kronos cancelled."));
            vm.PullCollectionFromKronosAsync(cancelled).GetAwaiter().GetResult();
            Check("pull-cancel-sets-status", vm.StatusText == "Load from Kronos cancelled.");
            Check("pull-cancel-keeps-previous-tree", vm.Roots.Count == before);
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

    // In-memory IRemoteSampleSource: hands back a pre-canned pull result (already
    // written to disk by the caller, same as a real pull would have left behind) and
    // records every push call - no FTP connection or Window involved.
    sealed class FakeRemoteSampleSource : IRemoteSampleSource
    {
        readonly RemoteSamplePullResult _pullResult;
        public string? PulledExtensionFilter { get; private set; }
        public readonly List<(string local, string remote)> Pushed = [];
        public bool NextPushSucceeds = true;

        public FakeRemoteSampleSource(string pickedLocalPath, Dictionary<string, string> remoteMap) =>
            _pullResult = RemoteSamplePullResult.Ok(pickedLocalPath, remoteMap);

        public FakeRemoteSampleSource(RemoteSamplePullResult pullResult) => _pullResult = pullResult;

        public Task<RemoteSamplePullResult> PickAndPullAsync(string extensionFilter, string localRoot)
        {
            PulledExtensionFilter = extensionFilter;
            return Task.FromResult(_pullResult);
        }

        public Task<RemoteSamplePushResult> PushAsync(string localPath, string remotePath)
        {
            Pushed.Add((localPath, remotePath));
            return Task.FromResult(NextPushSucceeds
                ? RemoteSamplePushResult.Success("pushed")
                : RemoteSamplePushResult.Failed("failed"));
        }

        public string? PushedCollectionKscPath { get; private set; }
        public bool NextCollectionPushSucceeds = true;

        public Task<RemoteCollectionPushResult> PickFolderAndPushCollectionAsync(string localKscPath, KscCollection collection)
        {
            PushedCollectionKscPath = localKscPath;
            return Task.FromResult(NextCollectionPushSucceeds
                ? RemoteCollectionPushResult.Success("pushed collection")
                : RemoteCollectionPushResult.Failed("failed"));
        }
    }
}
