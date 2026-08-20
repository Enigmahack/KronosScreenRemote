using System.IO;
using System.Windows;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostic: `--librarian-selftest` runs the pure Librarian model
        // checks, writes OK / FAIL to a temp file, and exits without a window. Used to
        // verify the reference/bank/plan logic off-hardware (no unit-test project here).
        if (e.Args.Contains("--librarian-selftest"))
        {
            // Several Core/Sample/* self-tests drive a real SampleEditorViewModel
            // through OpenCollection, which writes Recent Files to the REAL
            // settings.json (Storage.SaveSettings has no test-injectable override).
            // Snapshot/restore around the whole run (not per-test) so a person running
            // --librarian-selftest on their own machine gets their real settings back
            // untouched, however many self-tests end up touching it. Safe to wrap with
            // a try/finally here specifically because Environment.Exit is called AFTER
            // this block returns, not from inside it - Environment.Exit doesn't run
            // pending finally blocks, so this only works because of that ordering.
            List<string> fails = null!;
            Storage.RunWithSettingsFileProtected(() =>
            {
            fails = Librarian.SelfTest();
            fails.AddRange(BatchLibrarian.SelfTest());
            fails.AddRange(ObjectBodySelfTests.SelfTest());
            fails.AddRange(LocalLibrarySelfTests.SelfTest());
            fails.AddRange(LocalLibrarySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LocalEditOpsSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SyncPipelineSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(DataSafetySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(PcgFileSelfTests.SelfTest());
            fails.AddRange(PcgPaneLoadSelfTests.SelfTest());
            fails.AddRange(CrossPanePlacementSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LocalCutCopyPasteSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(ReadOnlyBankBrowseSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeCacheSelfTests.SelfTest());
            fails.AddRange(DependencyResolutionSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeTreeVisibilitySelfTests.SelfTest());
            fails.AddRange(PaneSelectionSelfTests.SelfTest());
            fails.AddRange(RawKeyMapSelfTests.SelfTest());
            fails.AddRange(DetectionSelfTests.SelfTest());
            fails.AddRange(DumpGateSelfTests.SelfTest());
            fails.AddRange(ScreenSessionSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MidiTransportReplySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeGroupPlacementSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeAutoFillSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeOrderingSelfTests.SelfTest());
            fails.AddRange(LibrarianUndoSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(PlacementStalenessGateSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SyncCancellationSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SampleSelfTests.SelfTest());
            fails.AddRange(SampleDspSelfTests.SelfTest());
            fails.AddRange(SampleRemoteSelfTests.SelfTest());
            fails.AddRange(SampleTranscodeSelfTests.SelfTest());
            fails.AddRange(SampleStereoSelfTests.SelfTest());
            fails.AddRange(SampleTreeSelectionSelfTests.SelfTest());
            fails.AddRange(SamplePhase5SelfTests.SelfTest());
            });
            var outPath = Path.Combine(Path.GetTempPath(), "kronos_librarian_selftest.txt");
            File.WriteAllText(outPath, fails.Count == 0 ? "OK" : "FAIL: " + string.Join(", ", fails));
            Environment.Exit(fails.Count == 0 ? 0 : 1);
        }

        // Headless diagnostic: `--ui-theme-smoketest` constructs every Window/Dialog with
        // dummy args to catch XamlParseException from the Themes/Dark.xaml migration
        // (missing StaticResource, bad template part) without showing a window.
        if (e.Args.Contains("--ui-theme-smoketest"))
        {
            UiThemeSmokeTest.Run();
        }

        // Headless diagnostic: `--dump-pcg-refs <path-to.pcg> [name filter]` - see
        // Tools/PcgRefDump.cs for why this exists (settling a suspected Program-bank
        // off-by-one in Combi timbre reference decoding using a real file's own bytes).
        int dumpRefsIdx = Array.IndexOf(e.Args, "--dump-pcg-refs");
        if (dumpRefsIdx >= 0 && dumpRefsIdx + 1 < e.Args.Length)
        {
            string? filter = dumpRefsIdx + 2 < e.Args.Length ? e.Args[dumpRefsIdx + 2] : null;
            PcgRefDump.Run(e.Args[dumpRefsIdx + 1], filter);
        }

        // Headless diagnostic: `--sample-format-fixture-check <folder>` - the runnable
        // acceptance gate for the Core/Sample/* format-layer port (byte-identical
        // round-trip against a local, gitignored folder of real Kronos fixtures).
        int fixtureCheckIdx = Array.IndexOf(e.Args, "--sample-format-fixture-check");
        if (fixtureCheckIdx >= 0 && fixtureCheckIdx + 1 < e.Args.Length)
        {
            SampleFormatFixtureCheck.Run(e.Args[fixtureCheckIdx + 1]);
        }

        // Headless diagnostic: `--sample-editor-smoketest <path-to.ksc>` - drives the
        // real SampleEditorViewModel end-to-end against a real fixture (see
        // Tools/SampleEditorSmokeTest.cs).
        int sampleEditorSmokeIdx = Array.IndexOf(e.Args, "--sample-editor-smoketest");
        if (sampleEditorSmokeIdx >= 0 && sampleEditorSmokeIdx + 1 < e.Args.Length)
        {
            SampleEditorSmokeTest.Run(e.Args[sampleEditorSmokeIdx + 1]);
        }

        // One-off visual verification, NOT a shipped feature: `--sample-editor-visual-check
        // <path.ksc>` shows the real SampleEditorWindow and screenshots it through a few
        // real selections (see Tools/SampleEditorVisualCheck.cs). Lets MainWindow's
        // normal StartupUri open too rather than fighting it - harmless, and simpler.
        int sampleEditorVisualIdx = Array.IndexOf(e.Args, "--sample-editor-visual-check");
        if (sampleEditorVisualIdx >= 0 && sampleEditorVisualIdx + 1 < e.Args.Length)
        {
            SampleEditorVisualCheck.Schedule(e.Args[sampleEditorVisualIdx + 1]);
        }

        // Headless diagnostic: `--sample-ftp-pull-check <remote-path.KSC-or-.KMP>` - pulls
        // that file plus its dependency closure from the configured Kronos over FTP (no
        // Window, no dialog - uses settings.json's saved FtpUsername/Password/KronosHost),
        // then re-opens everything from disk to confirm it parses. See
        // Tools/SampleFtpPullCheck.cs; this is the real-hardware counterpart to
        // --sample-format-fixture-check, which only ever reads already-local files.
        int sampleFtpPullIdx = Array.IndexOf(e.Args, "--sample-ftp-pull-check");
        if (sampleFtpPullIdx >= 0 && sampleFtpPullIdx + 1 < e.Args.Length)
        {
            var s = Storage.LoadSettings();
            SampleFtpPullCheck.Run(s.KronosHost, s.FtpPort, s.FtpUsername, s.FtpPassword, e.Args[sampleFtpPullIdx + 1]);
        }

        // One-off reconnaissance, NOT a shipped feature: `--sample-stereo-scan <folder>`
        // (see Tools/SampleStereoScan.cs) - grounds Phase 5's stereo-pair-creation work
        // in what real Kronos-authored .KMP/.KSF fixtures actually do with -L/-R.
        int stereoScanIdx = Array.IndexOf(e.Args, "--sample-stereo-scan");
        if (stereoScanIdx >= 0 && stereoScanIdx + 1 < e.Args.Length)
        {
            SampleStereoScan.Run(e.Args[stereoScanIdx + 1]);
        }

        // Single source of truth for the app directory (settings, palette, cal, log all colocate).
        var logPath = Path.Combine(Storage.DataDir, "screenremote.log");
        AppLog.Init(logPath);

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Error($"[ui-crash] {ex.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            AppLog.Error($"[crash] {ex.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Error($"[task-crash] {ex.Exception}");
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Close();
        base.OnExit(e);
    }
}
