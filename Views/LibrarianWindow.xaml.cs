using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace KronosScreenRemote;

// Move Programs/Combis between slots while keeping every Combi timbre and Set List
// slot that references them coherent, live over SysEx. Orchestration + safety UI;
// the risky logic is in LibrarianModel / KronosSysEx (verified off-hardware via
// Librarian.SelfTest, and on real hardware via a manual Store-Bank verification —
// see memory/kronos-librarian.md). Nothing is written until Commit.
partial class LibrarianWindow : Window
{
    readonly ISysExService _sysEx;
    readonly string _host;
    Dictionary<(int Type, int Bank, int Number), string> _names = new();
    Dictionary<int, string> _setListNames = new();

    RefIndex? _refIndex;
    MovePlan? _plan;
    ObjLoc? _src, _dst;
    bool _busy;                              // guards Scan, Preview, Commit, Sync Names, Sync All, Copy — one at a time
    CancellationTokenSource? _scanCts;
    CancellationTokenSource? _syncAllCts;    // non-null while Sync All runs → a second click cancels
    bool[]? _programBankTypes;              // func-0x61 bitmap (true = EXi) — pre-populated from Storage's
                                             // disk cache in the ctor so labels show immediately on open;
                                             // refreshed from hardware on the first successful Scan of the
                                             // session (see _programBankTypesLive) and re-saved to disk then
    bool _programBankTypesLive;             // true once THIS session has a hardware-fresh bitmap, not just cache

    // Batch move (Core/BatchMoveModel.cs) — see LibrarianWindow.ObjectBrowser.cs for the
    // Ctrl/Shift-click selection mechanism and LibrarianWindow.Batch.cs for the orchestration.
    readonly HashSet<ObjLoc> _batchSelection = new();
    Dictionary<ObjLoc, ObjectBrowserNode> _nodeByLoc = new();   // rebuilt only by RefreshObjectTree
    ObjLoc? _lastBatchTouch;                // anchor for Shift-click range-select
    BatchClipboard _batchClipboard = new();
    BatchMovePlan? _batchPlan;
    readonly List<(ClipboardEntry Entry, ObjLoc To)> _stagedPastes = new();       // awaiting a Preview to fold in
    List<(ClipboardEntry Entry, ObjLoc To)> _batchStagedInPlan = new();           // subset actually included in _batchPlan

    // Clipboard multi-select (Views/LibrarianWindow.Clipboard.cs) — mirrors _batchSelection/
    // _nodeByLoc above, but for TV_Clipboard's rows. Feeds "Paste Multi".
    readonly HashSet<ClipboardEntry> _clipboardSelection = new();
    ClipboardEntry? _lastClipboardTouch;

    // Git-style staging color (Views/LibrarianWindow.ObjectBrowser.cs's SetPasteState/
    // ClearPasteState) — None/Staged(red)/Committed(green) per destination ObjLoc. Deliberately
    // transient: reapplied across RefreshObjectTree() rebuilds (so Commit's own tree refresh
    // doesn't erase the green it just set) but cleared only by a real Scan/Rescan, never by
    // RefreshObjectTree() itself.
    readonly Dictionary<ObjLoc, PasteState> _pasteState = new();

    // Verify/Commit are unified across the swap flow (_plan) and the clipboard/paste flow
    // (_batchPlan) — both implement IExecutablePlan and already share ArmPlanAsync/
    // ApplyMoveAsync. Whichever flow was most recently verified wins; each flow's own
    // PreviewAsync/PreviewBatchAsync clears the OTHER's plan so a stale plan from a flow that
    // isn't the one just verified can never be the one Commit acts on.
    IExecutablePlan? ArmedPlan => (IExecutablePlan?)_batchPlan ?? _plan;

    public LibrarianWindow(ISysExService sysEx, string host)
    {
        _sysEx = sysEx;
        _host  = host;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        ReloadNameCaches();
        _batchClipboard = BatchLibrarian.LoadClipboard(_host);
        _programBankTypes = Storage.LoadProgramBankTypes(_host);

        BTN_Scan.Click       += async (_, _) => await ScanAsync();
        BTN_ScanCancel.Click += (_, _) => _scanCts?.Cancel();
        BTN_Verify.Click     += async (_, _) => await VerifyAsync();
        BTN_CommitAll.Click  += async (_, _) => await CommitAllAsync();
        BTN_SyncNames.Click  += async (_, _) => await SyncNamesAsync();
        BTN_SyncAll.Click    += async (_, _) => await SyncAllAsync();
        BTN_ClearClipboard.Click += async (_, _) => await OnClearClipboardAsync();
        // Only react when the double-click actually hit a row's label (our
        // HierarchicalDataTemplate's TextBlock) — not the expander toggle, which
        // wouldn't change SelectedItem and would otherwise re-trigger on whatever
        // leaf was selected before.
        TV_Objects.MouseDoubleClick += async (_, e) => { if (e.OriginalSource is TextBlock) await OnObjectDoubleClickAsync(); };

        TXT_BackupHint.Text = "Backups: " + Storage.BackupDir();
        RefreshObjectTree();
        RefreshEnable();
        RefreshBatchSelectionUi();
        RefreshClipboardUi();
    }

    void ReloadNameCaches()
    {
        _names = new Dictionary<(int, int, int), string>();
        foreach (var n in Storage.LoadNames(_host))
            _names[(n.Type, n.Bank, n.Number)] = n.Name;

        _setListNames = new Dictionary<int, string>();
        foreach (var kv in Storage.LoadSetLists(_host))
            _setListNames[kv.Key] = kv.Value.Name;
    }

    string NameOf(ObjLoc loc)
    {
        int t = loc.ObjType == LibObj.Program ? 1 : 0;
        return _names.TryGetValue((t, loc.Bank, loc.Number), out var nm) ? nm : "";
    }

    void UpdateUsage()
    {
        void One(ObjLoc? loc, TextBlock lbl)
        {
            if (loc is not { } l) { lbl.Text = "(right-click a Program/Combi slot above)"; return; }
            string name = NameOf(l);
            string namePart = string.IsNullOrEmpty(name) ? "(name unknown — Sync Names)" : $"'{name}'";
            lbl.Text = _refIndex != null
                ? $"{l.Label()}   {namePart}   used by {_refIndex.UsageCount(l)} ref(s)"
                : $"{l.Label()}   {namePart}   (scan for usage)";
        }
        One(_src, TXT_SrcUsage);
        One(_dst, TXT_DstUsage);
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    async Task ScanAsync()
    {
        if (_busy || !_sysEx.CanDump) { Log("Cannot scan — MIDI monitoring off or not connected."); return; }
        _busy = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        BTN_Scan.IsEnabled = false;
        BTN_ScanCancel.IsEnabled = true;
        TXT_ScanStatus.Text = "Scanning…";
        _pasteState.Clear();   // a real Scan means "start fresh from what's actually on the instrument now"
        RefreshEnable();

        bool full = CHK_Full.IsChecked == true;
        try
        {
            // One-off, cheap bulk query (func 0x60/0x61) — fetched here rather than at
            // construction so it's serialized behind _busy/_dumping like everything else
            // this window sends, instead of racing a scan the user starts immediately
            // after opening the window. Gated on _programBankTypesLive (not on
            // _programBankTypes == null) because the ctor may have already populated
            // _programBankTypes from Storage's disk cache — that's a display value to
            // show immediately, not proof this session has asked the hardware yet.
            if (!_programBankTypesLive)
            {
                var types = await _sysEx.RequestProgramBankTypesAsync();
                if (types is { } t)
                {
                    _programBankTypes = t.IsExi;
                    _programBankTypesLive = true;
                    await Task.Run(() => Storage.SaveProgramBankTypes(_host, t.IsExi));
                }
            }

            var (ri, plan) = await LibraryRepository.ScanAsync(_sysEx, _host, full,
                progress: msg => TXT_ScanStatus.Text = msg, ct);

            _refIndex = ri;
            if (ct.IsCancellationRequested)
            {
                TXT_ScanStatus.Text = "Scan cancelled";
            }
            else
            {
                string scope = plan.FirstRun ? "first scan — full sweep"
                    : full ? "full scan"
                    : plan.CombiBanksToFetch.Count == 0 && !plan.FetchSetLists ? "lazy scan — nothing changed"
                    : $"lazy scan — {plan.CombiBanksToFetch.Count} combi bank(s) changed" + (plan.FetchSetLists ? " + set lists" : "");
                TXT_ScanStatus.Text = $"Indexed {ri.CombiRefs.Count} combis, {ri.SetlistRefs.Count} set lists ({scope})";
            }
            Log(TXT_ScanStatus.Text);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] scan failed: {ex.Message}");
            TXT_ScanStatus.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            _busy = false;
            BTN_Scan.IsEnabled = true;
            BTN_ScanCancel.IsEnabled = false;
            RefreshObjectTree();   // usage counts changed
            UpdateUsage();
            RefreshEnable();
        }
    }

    // ── Sync Names / Sync All ────────────────────────────────────────────────
    // Ported from MainWindow's former Tools-menu entries: same confirm/progress/cache-merge
    // behavior, now sharing this window's _busy guard and Log/status UI instead of
    // MainWindow's SetNotification (which only exists on MainWindow's own status bar).

    async Task SyncNamesAsync()
    {
        if (_busy) { Log("A scan or sync is already running."); return; }
        if (!_sysEx.CanDump) { Log("Enable MIDI monitoring first (Settings → MIDI/SysEx)."); return; }

        var choice = MessageBox.Show(this,
            "Request all program & combi names from the Kronos and cache them locally.\n\n" +
            "Internal and GM banks sync reliably. Some user banks may not — for those, with " +
            "this app still connected and MIDI Monitor on, trigger Global → Dump on the Kronos " +
            "itself: the app passively captures names from that dump while it streams by (it " +
            "does nothing if triggered while the app is disconnected or MIDI Monitor is off). " +
            "Briefly shows \"Transmitting MIDI Data…\" on the Kronos.\n\nStart now?",
            "Sync Names", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK) return;

        _busy = true;
        RefreshEnable();
        int lastDone = 0, lastTotal = 0;
        var progress = new Progress<(int Done, int Total, int Names)>(p =>
        {
            lastDone = p.Done; lastTotal = p.Total;
            TXT_SyncStatus.Text = $"Syncing names… {p.Done}/{p.Total} banks — {p.Names} names";
        });
        try
        {
            int names = await LibraryRepository.SyncNamesAsync(_sysEx, progress, CancellationToken.None);
            TXT_SyncStatus.Text = lastTotal > 0 && lastDone < lastTotal
                ? $"Synced {lastDone}/{lastTotal} banks ({names} names cached). Any user banks that didn't sync: with MIDI Monitor on and this app connected, trigger Global → Dump on the Kronos — its names are captured from the stream as it passes by."
                : $"Name sync complete — {names} names cached";
            Log(TXT_SyncStatus.Text);
            ReloadNameCaches();
            RefreshObjectTree();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] sync-names failed: {ex.Message}");
            TXT_SyncStatus.Text = $"Name sync failed: {ex.Message}";
            Log(TXT_SyncStatus.Text);
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // Toggle-cancel: invoke again while it runs to stop; whatever synced so far is already saved.
    async Task SyncAllAsync()
    {
        if (_syncAllCts is { } running)
        {
            running.Cancel();
            TXT_SyncStatus.Text = "Sync All: cancelling after the current item…";
            return;
        }
        if (_busy) { Log("A scan or sync is already running."); return; }
        if (!_sysEx.CanDump) { Log("Enable MIDI monitoring first (Settings → MIDI/SysEx)."); return; }

        var choice = MessageBox.Show(this,
            "Sync everything from the Kronos and cache it locally:\n" +
            "  •  All program & combi names\n" +
            "  •  All 128 set lists (names, slot colors, notes)\n\n" +
            "This can take several minutes depending on how many set lists you have. " +
            "The Kronos briefly shows \"Transmitting MIDI Data…\". You can cancel anytime " +
            "(click Sync All again); progress is saved as it goes.\n\nStart now?",
            "Sync All", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK) return;

        _busy = true;
        RefreshEnable();
        var cts = new CancellationTokenSource();
        _syncAllCts = cts;
        BTN_SyncAll.Content = "Cancel Sync All";
        try
        {
            var nameProgress = new Progress<(int Done, int Total, int Names)>(p =>
                TXT_SyncStatus.Text = $"Sync All — names: {p.Done}/{p.Total} banks, {p.Names} cached");
            var listProgress = new Progress<(int Done, int Total, int Found)>(p =>
                TXT_SyncStatus.Text = $"Sync All — set lists: {p.Done}/{p.Total}, {p.Found} with content");

            var (names, result) = await LibraryRepository.SyncAllAsync(_sysEx, _host, nameProgress, listProgress, cts.Token);

            TXT_SyncStatus.Text = result.Cancelled
                ? $"Sync All cancelled — {names} names, {result.Found.Count} set lists saved so far"
                : $"Sync All complete — {names} names, {result.Found.Count} set lists cached";
            Log(TXT_SyncStatus.Text);
            ReloadNameCaches();
            RefreshObjectTree();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] sync-all failed: {ex.Message}");
            TXT_SyncStatus.Text = $"Sync All failed: {ex.Message}";
            Log(TXT_SyncStatus.Text);
        }
        finally
        {
            _busy = false;
            _syncAllCts = null;
            cts.Dispose();
            BTN_SyncAll.Content = "Sync All…";
            RefreshEnable();
        }
    }

    // ── Verify / Commit — unified across the swap flow and the clipboard/paste flow ───────────
    // One Verify button, one Commit button (LibrarianWindow.xaml's bottom row) for both
    // mechanisms: a swap (Set as Source/Destination -> MovePlan) and a clipboard paste (Copy +
    // any paste variant -> BatchMovePlan). Staged clipboard pastes take priority — that's the
    // primary mechanic now — falling back to the swap flow when nothing's staged there but a
    // Source/Destination pair is set. See ArmedPlan's doc comment for why each Preview clears
    // the OTHER flow's plan.
    async Task VerifyAsync()
    {
        if (_stagedPastes.Count > 0) await PreviewBatchAsync();
        else if (_src != null && _dst != null) await PreviewAsync();
        else Log("Nothing staged — Copy something and Paste it below, or right-click to Set a Source and Destination, first.");
    }

    async Task CommitAllAsync()
    {
        switch (ArmedPlan)
        {
            case BatchMovePlan: await CommitBatchAsync(); break;
            case MovePlan: await CommitAsync(); break;
            default: Log("Nothing verified to commit — run Verify first."); break;
        }
    }

    // ── Preview (swap) ──────────────────────────────────────────────────────

    async Task PreviewAsync()
    {
        if (_busy) return;
        if (_refIndex is not { } ri) { Log("Scan the library first (needed to find references)."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }
        if (_src is not { } s || _dst is not { } d) { Log("Right-click a Program or Combi slot in the list to set both Source and Destination first."); return; }

        _busy = true;
        _plan = null;
        _batchPlan = null;   // ArmedPlan must never resolve to a stale plan from the other flow
        BRD_Warning.Visibility = Visibility.Collapsed;
        RefreshEnable();
        Log($"\nPreviewing  {s.Label()}  <->  {d.Label()} …");
        try
        {
            // Freshness gate: if a swept bank changed since the scan, refuse (a new
            // referrer could have appeared and been missed).
            var stale = await ri.StaleBanksAsync((o, b) => _sysEx.BankDigestAsync(o, b));
            if (stale.Count > 0)
            {
                Log("  STALE: " + string.Join(", ", stale.Select(x => Librarian.StoreLabel(x.Obj, x.Bank))) +
                    " changed since the last scan — re-scan before moving (a new referrer could be left dangling).");
                return;
            }

            var ids = ri.ReferrerObjectIds(s);
            ids.UnionWith(ri.ReferrerObjectIds(d));
            var cat = new LibraryCatalog();
            foreach (var (obj, bank, index) in ids)
            {
                var dump = await _sysEx.DumpObjectAsync(obj, bank, index);
                if (dump == null) { Log($"  Failed to dump referrer obj {obj:X2} bank {bank:X2} idx {index}"); return; }
                if (obj == LibObj.Combi) cat.AddCombi(dump); else cat.AddSetlist(dump);
            }
            var srcDump = await _sysEx.DumpObjectAsync(s.ObjType, s.Bank, s.Number);
            var dstDump = await _sysEx.DumpObjectAsync(d.ObjType, d.Bank, d.Number);
            if (srcDump == null || dstDump == null) { Log("  Failed to dump source/destination object."); return; }

            var active = _sysEx.CurrentPerformanceLoc();
            var plan = Librarian.PlanMove(cat, s, srcDump, d, dstDump, active);
            await Librarian.ArmPlanAsync(plan, _sysEx);   // capture digest baseline now

            _plan = plan;
            foreach (var line in plan.Preview) Log("  " + line);
            foreach (var w in plan.Warnings) Log("  ! " + w);
            if (plan.DigestBaseline.Count == 0)
                Log("  ! WARNING: no bank-digest baseline captured — staleness gate will be skipped.");
            Log(plan.IsRefusable ? "  This move is REFUSED (see above)."
                                 : "  Preview complete. Review the warnings above, then Commit.");
            ShowPlanWarningBanner(plan.Warnings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] preview failed: {ex.Message}");
            Log($"  Preview error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // Non-refusable CHECK: warnings (e.g. cross-bank-type program moves, which the
    // Kronos itself only catches at write time via Reply 64) get a banner above the
    // log instead of just another scrolling line — easy to miss there before Commit.
    void ShowPlanWarningBanner(IReadOnlyList<string> warnings)
    {
        var checks = warnings.Where(w => w.StartsWith("CHECK:", StringComparison.Ordinal)).ToList();
        if (checks.Count == 0) { BRD_Warning.Visibility = Visibility.Collapsed; return; }

        TXT_Warning.Text = "⚠ " + string.Join("\n⚠ ", checks.Select(w => w["CHECK:".Length..].Trim()));
        BRD_Warning.Visibility = Visibility.Visible;
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    async Task CommitAsync()
    {
        if (_busy || _plan is not { } plan) return;
        if (plan.IsRefusable) { Log("Refused plan — cannot commit."); return; }
        if (!_sysEx.CanDump) { Log("Not connected."); return; }

        _busy = true;
        RefreshEnable();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Log("\nCommitting…");
        try
        {
            // ApplyMoveAsync awaits with ConfigureAwait(false) internally, so its
            // progress callback fires off the UI thread — marshal each line back.
            var (ok, _, aborted) = await Librarian.ApplyMoveAsync(
                plan, _sysEx, Storage.BackupDir(), stamp,
                progress: m => Dispatcher.Invoke(() => Log("  · " + m)),
                doLive: CHK_Live.IsChecked == true);

            Log(ok ? "  DONE — move committed." : $"  ABORTED — {aborted}");
            Log("  (Re-scan recommended: references have changed.)");
            if (ok)
            {
                SetPasteState(plan.Src, PasteState.Committed);
                SetPasteState(plan.Dst, PasteState.Committed);
                _src = null;
                _dst = null;
                UpdateUsage();
            }
            _plan = null;
            BRD_Warning.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] commit failed: {ex.Message}");
            Log($"  Commit crashed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    void RefreshEnable()
    {
        bool connected = _sysEx.CanDump;
        // Verify is ready whenever something is actually staged — a clipboard paste (works with
        // an EMPTY tree selection by design, see VerifyAsync/PreviewBatchAsync) or a swap's
        // Source+Destination. Never gated on _batchSelection.Count — that was the confirmed bug
        // that left Commit permanently disabled for the primary Copy+Paste-with-cleared-selection
        // workflow.
        bool hasPending = _stagedPastes.Count > 0 || (_src != null && _dst != null);
        BTN_Scan.IsEnabled       = connected && !_busy;
        BTN_Verify.IsEnabled     = connected && !_busy && _refIndex != null && hasPending;
        BTN_CommitAll.IsEnabled  = connected && !_busy && ArmedPlan is { IsRefusable: false };
        BTN_SyncNames.IsEnabled  = connected && !_busy;
        BTN_SyncAll.IsEnabled    = connected && (!_busy || _syncAllCts != null);   // stays enabled while running so a second click can cancel
        BTN_ClearClipboard.IsEnabled = !_busy;   // pure local op, no hardware needed — just can't race an in-flight Copy/Paste/Commit
    }

    void Log(string text)
    {
        TXT_Log.AppendText(text + "\n");
        TXT_Log.ScrollToEnd();
    }
}
