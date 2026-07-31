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
// path without presenting a visible window or requiring a live Kronos connection. The Librarian
// ownership check temporarily shows invisible windows to exercise its real close path. Not a
// substitute for actually looking at the app - see the report this writes for what it does and
// doesn't prove.
static class UiThemeSmokeTest
{
    public static void Run()
    {
        // This diagnostic closes throwaway windows itself, so it must own application shutdown.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var results = new List<(string Name, bool Passed, string? Detail)>();

        static string Describe(Brush? b) => b switch
        {
            null => "(null)",
            SolidColorBrush s => s.Color.ToString(),
            _ => b.GetType().Name
        };

        static void ExpandAll(System.Windows.Controls.ItemsControl parent)
        {
            parent.UpdateLayout();
            foreach (var item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.TreeViewItem child)
                    continue;
                child.IsExpanded = true;
                ExpandAll(child);
            }
        }

        static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
            }
        }

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
                // XAML/resource path is proven fine - this is just a dummy-arg side effect.
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
                // Force the ItemsSource binding to flush before reading Items.Count - an
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
        try
        {
            var scratch = Path.Combine(Path.GetTempPath(), "kronos_ui_smoketest_librarian_owner");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            var owner = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0,
                Width = 1,
                Height = 1,
                Left = -10_000,
                Top = -10_000,
            };
            owner.Show();
            owner.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // Seeded with one throwaway Combi purely so the Undo check below has something to
            // edit - a routed-command probe against an EMPTY undo stack can't tell "wired but
            // correctly refusing" from "not wired at all" (RoutedCommand.Execute skips the
            // Executed handler whenever CanExecute says false).
            var ownerCache = new LocalLibraryCache(scratch);
            var seedLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            ownerCache.RecordEdits(new[] { (seedLoc.ObjType, seedLoc.Bank, seedLoc.Number, (byte)1, new byte[7810]) },
                "SmokeTestSeed", "smoke-test seed", DateTime.UtcNow);
            var librarian = new LibrarianShellWindow(fakeSysEx, ownerCache, settings, "")
                .OwnedBy(owner);
            librarian.ShowInTaskbar = false;
            librarian.Opacity = 0;
            librarian.Left = -10_000;
            librarian.Top = -10_000;
            librarian.Show();
            librarian.Activate();
            librarian.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            librarian.Closed += (_, _) => owner.Dispatcher.BeginInvoke(
                owner.Activate, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Undo (Ctrl+Z) wiring, on the one Librarian instance here that's actually shown.
            // Two halves, checked separately because only one of them is our code:
            //  1. the gesture is declared (Window.InputBindings' Ctrl+Z -> ApplicationCommands.Undo);
            //     translating a real keypress into that command is WPF's own job, not testable here.
            //  2. the command ROUTES from a focused pane up to the window's CommandBinding and
            //     actually rolls the last edit back - armed by renaming the seeded Combi, then
            //     executing the routed command from TV_Local (exactly where the Ctrl+Z gesture
            //     lands with a tree focused) and checking BOTH the status line and the restored
            //     name. A missing/mis-wired CommandBinding leaves both untouched.
            bool gestureDeclared = librarian.InputBindings.OfType<System.Windows.Input.KeyBinding>().Any(
                b => b.Key == System.Windows.Input.Key.Z &&
                     b.Modifiers == System.Windows.Input.ModifierKeys.Control &&
                     b.Command == System.Windows.Input.ApplicationCommands.Undo);
            string undoDetail = gestureDeclared ? "Ctrl+Z gesture declared" : "NO Ctrl+Z KeyBinding on the window";
            bool routed = false;
            var librarianVm = librarian.DataContext as ViewModels.LibrarianShellViewModel;
            if (librarianVm != null && librarian.FindName("TV_Local") is System.Windows.Controls.TreeView localTree)
            {
                for (int i = 0; i < 100 && !librarianVm.LocalPane.ShowTree; i++)
                {
                    Thread.Sleep(10);
                    librarian.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                ExpandAll(localTree);
                librarian.UpdateLayout();
                bool dotVisible = VisualDescendants<System.Windows.Controls.TextBlock>(localTree).Any(
                    t => t.Text == "●" &&
                         t.Visibility == Visibility.Visible &&
                         Equals(t.ToolTip, "Locally changed - pending Sync/Commit."));
                results.Add(("  Librarian dirty local-object dot", dotVisible,
                    dotVisible ? null : "Dirty local object did not render its red-dot marker"));
            }

            if (librarianVm != null && librarian.FindName("TV_Local") is IInputElement fromPane)
            {
                librarianVm.LocalPane.Rename(seedLoc, "SMOKE UNDO");
                bool armed = librarianVm.CanUndo &&
                    System.Windows.Input.ApplicationCommands.Undo.CanExecute(null, fromPane);
                System.Windows.Input.ApplicationCommands.Undo.Execute(null, fromPane);
                routed = armed
                    && librarianVm.StatusText.StartsWith("Undone: ", StringComparison.Ordinal)
                    && ownerCache.GetDisplayName(seedLoc.ObjType, seedLoc.Bank, seedLoc.Number) != "SMOKE UNDO";
                undoDetail += routed
                    ? "; routed command undid the last local edit"
                    : $"; NOT effective (armed={armed}, status=\"{librarianVm.StatusText}\")";
            }
            results.Add(("  Librarian Undo command wiring", gestureDeclared && routed, undoDetail));

            librarian.Close();
            owner.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            results.Add(("  Librarian close detaches owner", librarian.Owner == null,
                librarian.Owner == null ? null : "Owner remained attached after Closing"));
            results.Add(("  Librarian close reactivates owner", owner.IsActive,
                owner.IsActive ? null : "Owner did not regain activation after Librarian closed"));
            owner.Hide();
        }
        catch (Exception ex)
        {
            results.Add(("  Librarian close detaches owner", false, ex.GetType().Name + ": " + ex.Message));
        }
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
        var lines = results.Select(r => (r.Passed ? "OK   " : "FAIL ") + r.Name + (r.Detail is null ? "" : "  - " + r.Detail));
        File.WriteAllLines(outPath, lines);
        Environment.Exit(results.All(r => r.Passed) ? 0 : 1);
    }
}

// Promoted from `file`-scoped to `internal` so later self-tests (Core/LocalLibrary) can
// reuse this construction-only stub instead of writing a second copy. FakeMoveExecutor
// (Core/LocalLibrary/Testing) is a separate, stateful fake for a different purpose (real
// Pull/Push behavior, not just XAML-construction stubs) - the two are not merged.
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
    public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank) => new Dictionary<int, string>();
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
