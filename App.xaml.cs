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
            var fails = Librarian.SelfTest();
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
            fails.AddRange(LibrarianUndoSelfTests.SelfTestAsync().GetAwaiter().GetResult());
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
