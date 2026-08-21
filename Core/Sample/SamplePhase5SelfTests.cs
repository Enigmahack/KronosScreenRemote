namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for Phase 5's polish items: zone deletion (marks SKIPPEDSAMPLE,
// never touches the underlying .KSF), Recent Files tracking, multisample-scoped export,
// the normalization report, and the looping playback provider's frame math. Wired into
// App.xaml.cs's --librarian-selftest.
static class SamplePhase5SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase5_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

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
        foreach (var (name, key) in new[] { ("MS001000.KSF", 0), ("MS001001.KSF", 1) })
        {
            var s = new KsfSample { Name = $"S{key}", SampleRate = 44100 };
            s.SetSamples([1, 2, 3, 4, 5]);
            s.Save(Path.Combine(ksfDir, name));
        }

        // ── DeleteSelectedZone: marks SKIPPEDSAMPLE, leaves the .KSF file on disk,
        //    clears the sample-detail panel, and refuses a double-delete ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var zoneNode = FindZoneByFilename(vm.Roots, "MS001000.KSF");
            Check("delete-zone-found", zoneNode != null);
            vm.SelectNode(zoneNode);
            Check("delete-zone-selected-before", vm.HasZoneSelected && !vm.ZoneIsSkipped);

            var ksfPath = zoneNode!.ZoneRef!.Value.Zone.KsfPath(zoneNode.ZoneRef.Value.KmpPath);
            vm.DeleteSelectedZone();
            Check("delete-zone-marks-skipped", vm.ZoneIsSkipped);
            Check("delete-zone-clears-sample-panel", !vm.HasSampleLoaded);
            Check("delete-zone-ksf-file-untouched", File.Exists(ksfPath));

            vm.SaveSelectedMultisample();
            var reopened = KmpMultisample.Open(File.ReadAllBytes(kmpPath));
            Check("delete-zone-persisted-as-skipped",
                reopened != null && reopened.Zones.Any(z => z.IsSkipped));

            vm.DeleteSelectedZone();
            Check("delete-zone-refuses-double-delete", vm.StatusText.Contains("already marked as skipped"));
        }

        // ── Recent Files: newest first, capped, dedup-on-reopen, clearable ──
        //
        // AddRecentFile/ClearRecentFiles go through Storage.LoadSettings()/
        // SaveSettings() - the REAL app settings.json, not an isolated scratch copy
        // (no test-injectable override exists for it). Snapshot it first and restore
        // it in a finally, the same "never touch the real thing" discipline
        // SampleEditorSmokeTest already applies to fixture files - a user running
        // --librarian-selftest on their own machine must not lose their real Recent
        // Files list (or worse, any other setting, if a future Storage bug corrupts
        // the generic round-trip) to this test.
        {
            var settingsPath = Path.Combine(Storage.DataDir, "settings.json");
            byte[]? settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
            try
            {
                var vm = new SampleEditorViewModel();
                vm.ClearRecentFiles();
                vm.OpenCollection(kscPath);
                var recent = vm.GetRecentFiles();
                Check("recent-files-has-entry", recent.Contains(kscPath));
                Check("recent-files-newest-first", recent.Count > 0 && recent[0] == kscPath);

                vm.OpenCollection(kscPath); // reopening the same file shouldn't duplicate it
                Check("recent-files-no-duplicate", vm.GetRecentFiles().Count(p => p == kscPath) == 1);

                vm.ClearRecentFiles();
                Check("recent-files-clears", vm.GetRecentFiles().Count == 0);
            }
            finally
            {
                if (settingsBackup != null) File.WriteAllBytes(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
        }

        // ── ExportSelectedMultisampleToFolder: exports only this multisample's own
        //    zones, named after the sample ──
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var zoneNode = FindZoneByFilename(vm.Roots, "MS001001.KSF");
            vm.SelectNode(zoneNode);
            var outDir = Path.Combine(scratchRoot, "multisample_export_out");
            vm.ExportSelectedMultisampleToFolder(outDir);
            Check("export-multisample-status-ok", vm.StatusText.StartsWith("Exported"));
            // MS001000.KSF was deleted (skipped) above - only S1 (MS001001) should export.
            Check("export-multisample-only-remaining-zone", File.Exists(Path.Combine(outDir, "S1.wav")));
            Check("export-multisample-skips-deleted-zone", !File.Exists(Path.Combine(outDir, "S0.wav")));
        }

        // ── SampleNormalizationReport: flags the outlier sample rate/bit depth,
        //    flags header-only, leaves the majority unflagged ──
        {
            var reportRoot = Path.Combine(scratchRoot, "report");
            Directory.CreateDirectory(reportRoot);
            var reportKscPath = Path.Combine(reportRoot, "R.KSC");
            var reportKsc = new KscCollection { Entries = ["R.KMP"] };
            Directory.CreateDirectory(Path.Combine(reportRoot, "R"));
            reportKsc.Save(reportKscPath);

            var reportKmpPath = Path.Combine(reportRoot, "R", "R.KMP");
            var reportKmp = new KmpMultisample { Name = "R", Mno1 = 9 };
            reportKmp.Zones.Add(new KmpZone { Filename = "MS009000.KSF", OriginalKey = 10, TopKey = 10 });
            reportKmp.Zones.Add(new KmpZone { Filename = "MS009001.KSF", OriginalKey = 20, TopKey = 20 });
            reportKmp.Zones.Add(new KmpZone { Filename = "MS009002.KSF", OriginalKey = 30, TopKey = 30 });
            reportKmp.Save(reportKmpPath);

            var reportKsfDir = Path.Combine(reportRoot, "R", "R");
            Directory.CreateDirectory(reportKsfDir);
            var normal1 = new KsfSample { Name = "Normal1", SampleRate = 44100 };
            normal1.SetSamples([1, 2, 3]);
            normal1.Save(Path.Combine(reportKsfDir, "MS009000.KSF"));
            var normal2 = new KsfSample { Name = "Normal2", SampleRate = 44100 };
            normal2.SetSamples([1, 2, 3]);
            normal2.Save(Path.Combine(reportKsfDir, "MS009001.KSF"));
            var outlier = new KsfSample { Name = "Outlier", SampleRate = 22050 };
            outlier.SetSamples([1, 2, 3]);
            outlier.Save(Path.Combine(reportKsfDir, "MS009002.KSF"));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(reportKscPath);
            var report = vm.BuildNormalizationReport();
            Check("report-entry-count", report.Count == 3);
            Check("report-majority-unflagged", report.Where(e => e.SampleName == "Normal1" || e.SampleName == "Normal2").All(e => !e.Flagged));
            Check("report-outlier-flagged", report.Single(e => e.SampleName == "Outlier").Flagged);
        }

        // ── LoopingSampleProvider: wraps exactly at loopEndFrame back to
        //    loopStartFrame, and falls back to looping the whole buffer for
        //    degenerate (end <= start) loop points ──
        {
            short[] samples = [10, 20, 30, 40, 50]; // frames 0..4
            // sampleStartFrame == loopStartFrame skips the "intro" (attack) phase, so
            // playback jumps straight into the loop - the same immediate-loop behavior
            // this test exercised before LoopingSampleProvider grew the intro phase.
            var provider = new LoopingSampleProvider(samples, 44100, channels: 1, sampleStartFrame: 1, loopStartFrame: 1, loopEndFrame: 4, reverse: false);
            var buf = new byte[20]; // 10 frames worth
            int read = provider.Read(buf, 0, buf.Length);
            var frames = new short[10];
            Buffer.BlockCopy(buf, 0, frames, 0, read);
            // Loop is frames [1,4) = {20,30,40}, repeating: 20,30,40,20,30,40,20,30,40,20
            Check("loop-provider-wraps-correctly",
                frames.SequenceEqual(new short[] { 20, 30, 40, 20, 30, 40, 20, 30, 40, 20 }));

            var degenerate = new LoopingSampleProvider(samples, 44100, channels: 1, sampleStartFrame: 3, loopStartFrame: 3, loopEndFrame: 1, reverse: false);
            var buf2 = new byte[10]; // 5 frames worth - exactly the whole buffer once
            degenerate.Read(buf2, 0, buf2.Length);
            var frames2 = new short[5];
            Buffer.BlockCopy(buf2, 0, frames2, 0, buf2.Length);
            Check("loop-provider-degenerate-falls-back-to-whole-buffer",
                frames2.SequenceEqual(samples));
        }

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
