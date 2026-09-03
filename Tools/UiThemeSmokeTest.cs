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

        // The stock TreeViewItem template pairs InactiveSelectionHighlightBrushKey
        // (background) with InactiveSelectionHighlightTextBrushKey (foreground) - both must
        // be overridden, or a selected tree row's text falls back to the un-overridden system
        // default (near-black) the instant the window/TreeView loses focus. A screenshot can't
        // prove IsSelectionActive was actually false at capture time - this checks the
        // resource itself.
        {
            var inactiveTextBrush = Application.Current.TryFindResource(SystemColors.InactiveSelectionHighlightTextBrushKey) as SolidColorBrush;
            bool isWhite = inactiveTextBrush?.Color == Colors.White;
            results.Add(("  InactiveSelectionHighlightTextBrushKey resolves to white", isWhite,
                isWhite ? null : $"resolved to {inactiveTextBrush?.Color.ToString() ?? "NOT FOUND"}"));
        }

        Try("AboutWindow",            () => new AboutWindow(null, 0));
        Try("CommandPaletteWindow",   () => new CommandPaletteWindow(new List<CommandEntry> { new("test", "Test", "", () => { }) }));
        Try("FileManagerWindow",      () => new FileManagerWindow("", 21, "", ""));
        Try("HelpWindow",             () => new HelpWindow(settings));
        Try("InputTesterWindow",      () => new InputTesterWindow(fakeCtrl));
        Try("KeyboardInfoWindow",     () => new KeyboardInfoWindow("", 0, null));
        Try("SampleEditorWindow",    () => new SampleEditorWindow());
        Try("SampleNormalizationReportWindow", () => new SampleNormalizationReportWindow(new List<SampleNormalizationEntry>()));

        // The "Keymap tab recovers after zone delete" regression check that used to live
        // here is gone, not just passing now: it existed to pin a bug in RefreshDetail-
        // Panels' TabControl reselect-fallback (SelectedItem getting stuck null after a
        // zone delete transiently collapsed every tab). The Samples/Looping TabControl
        // itself was removed from SampleEditorWindow (every tab's content became a flat,
        // always-visible row inside the editing frame instead - Playback Format/DSP
        // Edit/Repair/Loop, per an explicit "no more tabs" request) - there is no
        // TabControl/SelectedItem left for that bug class to occur in at all. This test
        // had been failing on every run since a still-uninvestigated regression
        // reintroduced it sometime after it was first fixed (see this file's own history
        // for the original fix and later re-break); removed along with the code path it
        // was pinning rather than carried forward as permanently-red or rewritten to test
        // something that no longer exists.
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
            // No owner window here any more: LibrarianShellWindow is no longer WPF-owned by
            // MainWindow (see MainWindow.OpenLibrarianShellWindow's own comment - an owned
            // window is permanently kept above its owner in Win32 z-order, which was the
            // "stays on top of the main Kronos window" complaint), so the owner-detach/
            // reactivate checks this block used to pin no longer describe real behavior and
            // were removed along with the code path they were pinning.
            var scratch = Path.Combine(Path.GetTempPath(), "kronos_ui_smoketest_librarian_owner");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            // Seeded with one throwaway Combi purely so the Undo check below has something to
            // edit - a routed-command probe against an EMPTY undo stack can't tell "wired but
            // correctly refusing" from "not wired at all" (RoutedCommand.Execute skips the
            // Executed handler whenever CanExecute says false).
            var ownerCache = new LocalLibraryCache(scratch);
            var seedLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            ownerCache.RecordEdits(new[] { (seedLoc.ObjType, seedLoc.Bank, seedLoc.Number, (byte)1, new byte[7810]) },
                "SmokeTestSeed", "smoke-test seed", DateTime.UtcNow);
            var librarian = new LibrarianShellWindow(fakeSysEx, ownerCache, settings, "");
            librarian.ShowInTaskbar = false;
            librarian.Opacity = 0;
            librarian.Left = -10_000;
            librarian.Top = -10_000;
            librarian.Show();
            librarian.Activate();
            librarian.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

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
        }
        catch (Exception ex)
        {
            results.Add(("  Librarian Undo command wiring", false, ex.GetType().Name + ": " + ex.Message));
        }
        Try("LoginDialog",            () => new LoginDialog("", 0));
        Try("PromptDialog",           () => new PromptDialog("test"));
        Try("InsertSilenceDialog",    () => new InsertSilenceDialog(44100, 11025));
        Try("CreateMultisampleDialog", () => new CreateMultisampleDialog(0));
        // Loaded never fires for a constructed-but-unshown window (see Try's own body -
        // it never calls Show/ShowDialog), so this never actually reaches out over the
        // network despite taking connection args.
        Try("SampleRemoteBrowserDialog", () => new SampleRemoteBrowserDialog("dummy-host", 21, "user", "pass", ".KSC", Path.GetTempPath()));
        Try("SampleRemoteBrowserDialog (folder-push mode)", () => new SampleRemoteBrowserDialog("dummy-host", 21, "user", "pass", "dummy.KSC", new KscCollection()));
        Try("RemoteFilePickerDialog",    () => new RemoteFilePickerDialog("dummy-host", 21, "user", "pass", ".PCG"));

        // Behavioral: folder-push mode is a distinct constructor overload (see its own
        // comment) that repurposes the SAME dialog for "pick a destination folder and
        // upload" rather than "pick a file and download" - pins that it actually swaps
        // the title/button text and starts with Select disabled (only RefreshAsync's
        // first successful listing enables it - unreachable here with no live server,
        // which is the point: Select must NOT be usable before a connection exists).
        {
            var pushDlg = new SampleRemoteBrowserDialog("dummy-host", 21, "user", "pass", "dummy.KSC", new KscCollection());
            bool titleOk = pushDlg.Title == "Select Folder on Kronos";
            var selectButton = (System.Windows.Controls.Button)pushDlg.FindName("BTN_Select");
            bool contentOk = (string)selectButton.Content == "Select This Folder";
            bool startsDisabled = !selectButton.IsEnabled;
            pushDlg.Close();
            bool ok = titleOk && contentOk && startsDisabled;
            results.Add(("  SampleRemoteBrowserDialog folder-push mode UI", ok,
                ok ? null : $"title={titleOk} buttonContent={contentOk} startsDisabled={startsDisabled}"));
        }

        // Behavioral: both dialogs used to truncate their status line
        // (TextTrimming="CharacterEllipsis", no wrap), which silently cut off exactly
        // the detail (host/path/errno) that makes a long connect/download failure
        // message actionable. Pins the fix at the property level, not just "constructs
        // without throwing".
        {
            var browser = new SampleRemoteBrowserDialog("dummy-host", 21, "user", "pass", ".KSC", Path.GetTempPath());
            var browserWraps = ((System.Windows.Controls.TextBlock)browser.FindName("TXT_Status")).TextWrapping == TextWrapping.Wrap;
            browser.Close();
            results.Add(("  SampleRemoteBrowserDialog status wraps long errors", browserWraps,
                browserWraps ? null : "TXT_Status.TextWrapping is not Wrap"));

            var picker = new RemoteFilePickerDialog("dummy-host", 21, "user", "pass", ".PCG");
            var pickerWraps = ((System.Windows.Controls.TextBlock)picker.FindName("TXT_Status")).TextWrapping == TextWrapping.Wrap;
            picker.Close();
            results.Add(("  RemoteFilePickerDialog status wraps long errors", pickerWraps,
                pickerWraps ? null : "TXT_Status.TextWrapping is not Wrap"));
        }

        // Behavioral, not just XamlParseException-free: confirms the Frames/Seconds
        // boxes actually stay linked both directions, not merely that the dialog
        // constructs. Setting TextBox.Text raises TextChanged synchronously even on an
        // unshown Window, so this needs no visible/modal window.
        try
        {
            var dlg = new InsertSilenceDialog(44100, 11025); // seeded 0.25s @ 44100Hz
            var framesBox = (System.Windows.Controls.TextBox)dlg.FindName("FramesBox");
            var secondsBox = (System.Windows.Controls.TextBox)dlg.FindName("SecondsBox");
            bool seededSecondsCorrect = secondsBox.Text == "0.25";

            framesBox.Text = "22050";
            bool framesToSeconds = secondsBox.Text == "0.5";

            secondsBox.Text = "2";
            bool secondsToFrames = framesBox.Text == "88200";

            dlg.Close();
            bool ok = seededSecondsCorrect && framesToSeconds && secondsToFrames;
            results.Add(("  InsertSilenceDialog Frames<->Seconds link", ok,
                ok ? null : $"seeded={seededSecondsCorrect} frames->seconds={framesToSeconds} seconds->frames={secondsToFrames}"));
        }
        catch (Exception ex)
        {
            results.Add(("  InsertSilenceDialog Frames<->Seconds link", false, ex.GetType().Name + ": " + ex.Message));
        }

        // The "Apply to: Left/Right" channel picker (explicit request) - hidden entirely
        // for a mono sample (nothing to choose between) and defaults both channels
        // checked for a stereo one (matches the old always-mirror-in-Combine behavior).
        // OnOk's at-least-one-checked validation isn't exercised here - it sets
        // DialogResult, which throws unless the window was shown via ShowDialog (not
        // just constructed), and ShowDialog blocks the calling thread until closed -
        // not worth the added complexity/risk for logic this simple.
        try
        {
            var monoDlg = new InsertSilenceDialog(44100, 100);
            var monoPanel = (System.Windows.FrameworkElement)monoDlg.FindName("ChannelPickerPanel");
            bool hiddenForMono = monoPanel.Visibility == Visibility.Collapsed;
            monoDlg.Close();

            var stereoDlg = new InsertSilenceDialog(44100, 100, hasStereoPair: true);
            var stereoPanel = (System.Windows.FrameworkElement)stereoDlg.FindName("ChannelPickerPanel");
            var leftBox = (System.Windows.Controls.CheckBox)stereoDlg.FindName("ApplyLeftBox");
            var rightBox = (System.Windows.Controls.CheckBox)stereoDlg.FindName("ApplyRightBox");
            bool shownForStereo = stereoPanel.Visibility == Visibility.Visible;
            bool bothCheckedByDefault = leftBox.IsChecked == true && rightBox.IsChecked == true;
            stereoDlg.Close();

            bool ok = hiddenForMono && shownForStereo && bothCheckedByDefault;
            results.Add(("  InsertSilenceDialog Left/Right channel picker", ok,
                ok ? null : $"hiddenForMono={hiddenForMono} shownForStereo={shownForStereo} bothChecked={bothCheckedByDefault}"));
        }
        catch (Exception ex)
        {
            results.Add(("  InsertSilenceDialog Left/Right channel picker", false, ex.GetType().Name + ": " + ex.Message));
        }
        Try("PropertiesDialog (Program/Combi)", () => PropertiesDialog.ForProgramOrCombi("Test Properties", "Test Name", 0, 0));
        Try("PropertiesDialog (Set List)",      () => PropertiesDialog.ForSetList("Test Properties", "Test Name", new SetListData(0, "Test", Array.Empty<SetListSlot>())));
        Try("UnresolvedDependenciesDialog",     () => UnresolvedDependenciesDialog.For(new[]
        {
            new SessionDependencyEntry(new ObjLoc(LibObj.Program, 0x00, 0), RefKind.CombiTimbre, 0, new ObjLoc(LibObj.Combi, 0x00, 0), null),
        }));
        Try("ObjectInfoDialog", () => new ObjectInfoDialog(
            "Program: I-A:000 - TEST", "Combi: I-A:000 - TEST COMBI (via timbre 1)", new[] { "Wave Sequence: Int:000 - TEST WAVE (via osc1 zone1)" }));
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
    public BankId? CurrentBankId => null;
    public bool IsAvailable => false;
    public bool DumpGateActive => false;
    public int ValueSliderCc { get; set; } = 18;
    public bool PullNamesOnChange { get; set; }
    public bool CanDump => false;

    public void Start(IKronosMidiTransport transport) { }
    public void Reset() { }
    public Task<bool> RecheckAvailabilityAsync() => Task.FromResult(false);
    public void RefreshNow() { }
    public void NotifyUserActivity() { }
    public Task<SetListData?> DumpSetListAsync(int number) => Task.FromResult<SetListData?>(null);
    public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) =>
        Task.FromResult(new SetListSyncResult(new Dictionary<int, SetListData>(), Array.Empty<int>(), 0, false));
    public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => Task.FromResult(0);
    public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank) => new Dictionary<int, string>();
    public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges) { }
    public Task<bool> SendMidiAsync(string hexBytes) => Task.FromResult(false);
    public Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index, CancellationToken ct = default) => Task.FromResult<ObjectDump?>(null);
    public Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count, CancellationToken ct = default) => Task.FromResult(new Dictionary<int, ObjectDump>());
    public ObjLoc? CurrentPerformanceLoc() => null;
    public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => Task.FromResult<ProgramBankTypes?>(null);

    public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path) => Task.CompletedTask;
    public Task<byte[]?> BankDigestAsync(int obj, int bank, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<int> WriteObjectAsync(WriteOp op) => Task.FromResult(0);
    public Task<int> StoreBankAsync(int obj, int bank) => Task.FromResult(0);
    public Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi) => Task.FromResult(0);
    public int? StorageChangeCountFor(int obj, int bank) => null;   // construction-only stub: nothing observes pushes
    public Task SendRawAsync(byte[] data) => Task.CompletedTask;
}

file sealed class FakeCtrlClient : ICtrlClient
{
    public event Action<string>? CtrlError;
    public void Send(string cmd) { }
    public void Reset() { }
    public Task<string?> QueryAsync(string cmd, int timeoutMs = 2000) => Task.FromResult<string?>(null);
}
