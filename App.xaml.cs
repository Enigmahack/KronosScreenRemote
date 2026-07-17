using System.IO;
using System.Windows;

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
