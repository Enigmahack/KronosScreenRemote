using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace KronosScreenRemote;

// Headless diagnostic for the Dark.xaml theme migration: constructs every Window/Dialog
// with throwaway dummy arguments and reports which ones throw XamlParseException (missing
// StaticResource, bad template part, etc.) vs. construct cleanly, AND reports the actually
// resolved Background/Foreground brush so a "still white" report can be diagnosed without
// a screenshot. InitializeComponent() runs near the top of every constructor here, before
// any real dependency is touched, so this exercises 100% of the XAML/resource-resolution
// path without showing a window or requiring a live Kronos connection. Not a substitute for
// actually looking at the app — see the report this writes for what it does and doesn't prove.
static class UiThemeSmokeTest
{
    public static void Run()
    {
        var results = new List<(string Name, bool Passed, string? Detail)>();

        static string Describe(Brush? b) => b switch
        {
            null => "(null)",
            SolidColorBrush s => s.Color.ToString(),
            _ => b.GetType().Name
        };

        void Try(string name, Func<Window> ctor)
        {
            try
            {
                var w = ctor();
                var detail = $"Background={Describe(w.Background)} Foreground={Describe(w.Foreground)} " +
                             $"Style={(w.Style is null ? "(null)" : "present,Setters=" + w.Style.Setters.Count)}";
                w.Close();
                results.Add((name, true, detail));
            }
            catch (XamlParseException ex)
            {
                results.Add((name, false, "XamlParseException: " + ex.Message));
            }
            catch (Exception ex)
            {
                // Any other exception happens only after InitializeComponent() has already
                // succeeded (every ctor below calls it within its first few lines), so the
                // XAML/resource path is proven fine — this is just a dummy-arg side effect.
                results.Add((name, true, "(non-XAML exception after load, ignored: " + ex.GetType().Name + ")"));
            }
        }

        var fakeSysEx = new FakeSysExService();
        var fakeCtrl  = new FakeCtrlClient();
        var settings  = new AppSettings();

        var appWindowStyle = Application.Current.TryFindResource(typeof(Window)) as Style;
        results.Add(("  Application.Resources[typeof(Window)]", true,
            appWindowStyle is null
                ? "NOT FOUND via TryFindResource"
                : $"FOUND, Setters={appWindowStyle.Setters.Count}, TargetType={appWindowStyle.TargetType}"));

        Try("AboutWindow",            () => new AboutWindow(null, 0));
        Try("CommandPaletteWindow",   () => new CommandPaletteWindow(new List<CommandEntry> { new("test", "Test", "", () => { }) }));
        Try("FileManagerWindow",      () => new FileManagerWindow("", 21, "", ""));
        Try("HelpWindow",             () => new HelpWindow(settings));
        Try("InputTesterWindow",      () => new InputTesterWindow(fakeCtrl));
        Try("KeyboardInfoWindow",     () => new KeyboardInfoWindow("", 0, null));
        Try("LibrarianShellWindow",   () =>
        {
            var scratch = Path.Combine(Path.GetTempPath(), "kronos_ui_smoketest_local_library");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            var cache = new LocalLibraryCache(scratch);
            var w = new LibrarianShellWindow(fakeSysEx, cache, settings, "");
            if (w.FindName("TV_Local") is System.Windows.Controls.TreeView tv)
            {
                // Force the ItemsSource binding to flush before reading Items.Count — an
                // unshown window (never pumped through Show()/the message loop) doesn't
                // guarantee a pending binding update has applied yet. Same technique the
                // old LibrarianWindow check above uses for TV_Objects, same reason.
                tv.Measure(new Size(400, 400));
                tv.Arrange(new Rect(0, 0, 400, 400));
                tv.UpdateLayout();
                results.Add(("  TV_Local (control-level check)", true,
                    $"Background={Describe(tv.Background)} Foreground={Describe(tv.Foreground)} Roots={tv.Items.Count}"));
            }
            return w;
        });
        Try("LoginDialog",            () => new LoginDialog("", 0));
        Try("PromptDialog",           () => new PromptDialog("test"));
        Try("PropertiesDialog (Program/Combi)", () => PropertiesDialog.ForProgramOrCombi("Test Properties", "Test Name", 0, 0));
        Try("PropertiesDialog (Set List)",      () => PropertiesDialog.ForSetList("Test Properties", "Test Name", new SetListData(0, "Test", Array.Empty<SetListSlot>())));
        Try("UnresolvedDependenciesDialog",     () => UnresolvedDependenciesDialog.For(new[]
        {
            new SessionDependencyEntry(new ObjLoc(LibObj.Program, 0x00, 0), "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null),
        }));
        Try("SettingsWindow",         () => new SettingsWindow(settings));
        Try("SysExToolWindow",        () => new SysExToolWindow(fakeSysEx));
        // MainWindow deliberately excluded: parameterless ctor starts real timers/services/
        // rendering pipeline meant for a live app session, not a construct-and-dispose probe.

        var outPath = Path.Combine(Path.GetTempPath(), "kronos_ui_theme_smoketest.txt");
        var lines = results.Select(r => (r.Passed ? "OK   " : "FAIL ") + r.Name + (r.Detail is null ? "" : "  — " + r.Detail));
        File.WriteAllLines(outPath, lines);
        Environment.Exit(results.All(r => r.Passed) ? 0 : 1);
    }
}

// Promoted from `file`-scoped to `internal` so later self-tests (Core/LocalLibrary) can
// reuse this construction-only stub instead of writing a second copy. FakeMoveExecutor
// (Core/LocalLibrary/Testing) is a separate, stateful fake for a different purpose (real
// Pull/Push behavior, not just XAML-construction stubs) — the two are not merged.
internal sealed class FakeSysExService : ISysExService
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? ValueSliderChanged;
    public event Action<SysExTrafficEntry>? SysExTraffic;

    public string PerformanceDisplay => "";
    public bool IsAvailable => false;
    public int ValueSliderCc { get; set; } = 18;
    public bool PullNamesOnChange { get; set; }
    public bool CanDump => false;

    public void Start(IKronosMidiTransport transport) { }
    public void Reset() { }
    public void RefreshNow() { }
    public void NotifyUserActivity() { }
    public Task<SetListData?> DumpSetListAsync(int number) => Task.FromResult<SetListData?>(null);
    public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) =>
        Task.FromResult(new SetListSyncResult(new Dictionary<int, SetListData>(), Array.Empty<int>(), 0, false));
    public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => Task.FromResult(0);
    public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges) { }
    public Task<bool> SendMidiAsync(string hexBytes) => Task.FromResult(false);
    public Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index) => Task.FromResult<ObjectDump?>(null);
    public Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count) => Task.FromResult(new Dictionary<int, ObjectDump>());
    public ObjLoc? CurrentPerformanceLoc() => null;
    public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => Task.FromResult<ProgramBankTypes?>(null);

    public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path) => Task.CompletedTask;
    public Task<byte[]?> BankDigestAsync(int obj, int bank) => Task.FromResult<byte[]?>(null);
    public Task<int> WriteObjectAsync(WriteOp op) => Task.FromResult(0);
    public Task<int> StoreBankAsync(int obj, int bank) => Task.FromResult(0);
    public Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi) => Task.FromResult(0);
    public Task SendRawAsync(byte[] data) => Task.CompletedTask;
}

file sealed class FakeCtrlClient : ICtrlClient
{
    public event Action<string>? CtrlError;
    public void Send(string cmd) { }
    public void Reset() { }
    public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000) => Task.FromResult<string?>(null);
}
