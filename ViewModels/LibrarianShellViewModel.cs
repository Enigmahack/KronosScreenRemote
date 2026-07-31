using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KronosScreenRemote.ViewModels;

// Root view-state for the new Librarian window (Views/LibrarianShellWindow.xaml). Owns both
// panes, the Pull/Push/Commit/Sync commands (wrapping Core/LocalLibrary/SyncPipeline), the
// permanent history log, and the session-only dependency clipboard. Cross-pane placement
// (PCG -> local, requirement 12) lives HERE rather than on either pane's own ViewModel,
// since it's the one thing that genuinely needs both panes plus the session clipboard at
// once - LocalPane and PcgPane individually don't know about each other.
partial class LibrarianShellViewModel : ObservableObject, IDisposable
{
    readonly ILibrarianService _sysEx;
    readonly LocalLibraryCache _cache;
    readonly AppSettings _settings;
    readonly string _host;
    readonly SessionDependencyClipboard _sessionClipboard = new();

    // Linear undo over every LOCAL (pre-Commit) edit made in this window - see
    // Core/LocalLibrary/LibrarianUndo.cs for the capture model and what's deliberately out of
    // scope. Every mutating action here (and on LocalPane, via its injected BeginUndo) wraps
    // itself in one scope, so one user gesture is one Ctrl+Z.
    readonly LibrarianUndoRecorder _undo;

    // Live-queried (func 0x61) + persisted Program Bank Types - seeded from the on-disk cache
    // at construction, refreshed from real hardware in the background (WarmProgramBankTypesAsync).
    // Null until the first successful query ever completes for this host; BankTypeOf handles
    // that (and any bank with no EXi/HD-1 concept, e.g. GM/g) by returning null - "can't
    // verify," never treated as a hard failure. See BankTypeOf's own comment for why this
    // exists at all: PlanBatchMove's fresh-placement bank-type check needs it.
    ProgramBankTypes? _programBankTypes;

    public LocalLibraryPaneViewModel LocalPane { get; }
    public PcgPaneViewModel PcgPane { get; } = new();
    public MergePaneViewModel MergePane { get; }
    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<SessionClipboardRow> SessionClipboardRows { get; } = new();
    public ObservableCollection<ObjectDependencyRow> ObjectDependencyRows { get; } = new();

    [ObservableProperty] bool isBusy;
    [ObservableProperty] string statusText = "";
    [ObservableProperty] string? warningText;
    [ObservableProperty] bool forceFullPull;

    // Set once by LibrarianShellWindow's constructor to a WPF MessageBox prompt - keeps this
    // ViewModel free of WPF types (the established split: confirmations are a code-behind
    // concern). Called from PrepareForPushAsync only when ResolvePendingDependencies couldn't
    // clear everything automatically; returns true to proceed anyway, false to cancel the
    // Sync/Commit entirely. Null (e.g. a headless self-test) defaults to proceeding.
    public Func<IReadOnlyList<SessionDependencyEntry>, Task<bool>>? ConfirmContinueWithPendingDependencies { get; set; }

    public LibrarianShellViewModel(ILibrarianService sysEx, LocalLibraryCache cache, AppSettings settings, string host)
    {
        _sysEx = sysEx;
        _cache = cache;
        _settings = settings;
        _host = host;
        LocalPane = new LocalLibraryPaneViewModel(cache);
        MergePane = new MergePaneViewModel(new MergeCache(BuildMergePersistence(settings)));
        LocalPane.BankTypeOf = BankTypeOf;

        _undo = new LibrarianUndoRecorder(
            cache,
            MergePane.Snapshot, MergePane.Restore,
            h => MergePane.CacheMutating += h, h => MergePane.CacheMutating -= h,
            () => _sessionClipboard.Pending, RestoreSessionDependencies);
        _undo.Changed += OnUndoStackChanged;
        // The Local pane's own edits (rename/paste/delete/discard/properties) are captured by the
        // same recorder - it has no access to it directly, same injection pattern BankTypeOf uses.
        LocalPane.BeginUndo = _undo.Begin;
        MergePane.BeginUndo = _undo.Begin;

        RefreshHistory();

        // Kicks off the cache's one-time referrer-catalog build (see LocalLibraryCache.
        // BuildCatalogAsync's own comment) on a background thread as soon as the window
        // opens, instead of it running inline the first time a placement needs it - a real
        // 10-20s freeze on a large library. While it runs, the Local Library pane hides its
        // tree and disables its toolbar (LocalPane.IsIndexing) so nothing can be moved/edited
        // against a half-built index; the pane reveals itself once the build completes.
        _ = WarmCatalogAsync();

        // Set Lists are the one type whose "is this an empty placeholder?" can't be answered from
        // the cached display name, so a library synced by a build without LocalIndexEntry.IsInit
        // needs its 128 Set List bodies read once to fill that in - see BackfillInitFlags. Off the
        // UI thread and fire-and-forget: nothing blocks on it, and until it lands the only effect
        // is that Set List auto-fill still sees blank slots as occupied, exactly as before.
        _ = Task.Run(() =>
        {
            try { _cache.BackfillInitFlags(LibObj.SetList); }
            catch (Exception ex) { AppLog.Warn($"[librarian] set-list init backfill failed: {ex.Message}"); }
        });

        _programBankTypes = Storage.LoadProgramBankTypes(_host) is { } cached ? new ProgramBankTypes(cached) : null;
        _ = WarmProgramBankTypesAsync();

        // TryCreate, never a direct construction: the cache file is untrusted input (truncated,
        // hand-edited, or written by a different build), and a short array would only surface as a
        // crash when the Properties dialog indexes it. Anything malformed degrades to numeric.
        CategoryNames = (Storage.LoadCategoryNames(_host) is { } names
            ? CategoryNames.TryCreate(names.Program, names.ProgramSub, names.Combi, names.CombiSub)
            : null) ?? CategoryNames.Numeric();
        _ = WarmCategoryNamesAsync();
    }

    // ── Category names (requirement 4) ──────────────────────────────────────────────────────
    // The Program/Combi Category + Sub-Category names the Properties dialog labels its dropdowns
    // with. NEVER null: seeded from the per-host disk cache at construction, falling back to plain
    // numeric labels (CategoryNames.Numeric) - the exact display this had before the feature - so
    // the dialog needs no "not synced yet" branch and works identically offline.
    public CategoryNames CategoryNames { get; private set; }

    // Same "seed from disk, refresh live in the background" shape as WarmProgramBankTypesAsync,
    // and the same failure policy: a Kronos that can't be reached (or a reply too short to decode)
    // leaves whatever labels are already in place. Category names live in the Global object
    // (obj 0x03, bank 0, index 0 - "for all other types bank must be 0", KRONOS_MIDI_SysEx.txt *2);
    // that dump is ~24 KB, so it's fetched once per window rather than per dialog.
    async Task WarmCategoryNamesAsync()
    {
        try
        {
            var dump = await _sysEx.DumpObjectAsync(LibObj.Global, 0, 0);
            if (dump?.Body is not { } body || GlobalBody.ReadCategoryNames(body) is not { } names) return;
            CategoryNames = names;
            Storage.SaveCategoryNames(_host, new Storage.CategoryNamesDto(names.Program, names.ProgramSub, names.Combi, names.CombiSub));
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the ctor: the exception must still be observed and logged, but
            // failing to LABEL a dropdown is never worth surfacing to the user - the numeric
            // fallback is already showing.
            AppLog.Warn($"[librarian] category-name warm-up failed: {ex.Message}");
        }
    }

    // Testing-only entry point, same rationale as WarmProgramBankTypesForTestingAsync: lets a
    // self-test deterministically await the constructor's own fire-and-forget warm-up instead of
    // relying on incidental continuation timing.
    internal Task WarmCategoryNamesForTestingAsync() => WarmCategoryNamesAsync();

    async Task WarmCatalogAsync()
    {
        LocalPane.IsIndexing = true;   // hide the tree + disable its toolbar until the build finishes
        StatusText = AppMessages.Librarian.Shell.Indexing;
        try
        {
            var catalog = await _cache.BuildCatalogAsync();
            RefreshStaleDependencyFlags(catalog);
            if (StatusText == AppMessages.Librarian.Shell.Indexing) StatusText = "";
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the ctor - without this, a blob-IO failure (e.g. the
            // library share going away) would be an unobserved task exception, invisible.
            AppLog.Warn($"[librarian] catalog warm-up failed: {ex.Message}");
            StatusText = AppMessages.Librarian.Shell.IndexingFailed;
        }
        finally
        {
            // Reveal the pane whether the build succeeded or failed - the tree is valid from the
            // fast index regardless; a failure is surfaced via the Sync-row status above, not by
            // leaving the pane stuck on the indexing placeholder.
            LocalPane.IsIndexing = false;
        }
    }

    // Re-derives the tree's cached dependency dot for everything still pending Sync/Commit, using
    // bodies the catalog build just read anyway - no extra disk I/O (see
    // LocalLibraryCache.RecomputeResolvedDependencies for why a cached bit needs sweeping at all).
    // Scoped to the DIRTY set because that's exactly where the dot is shown
    // (LocalLibraryPaneViewModel.MakeLeafNode), so a big clean library costs nothing here; any
    // object edited later recomputes its own bit at write time under the current rules.
    void RefreshStaleDependencyFlags(LibraryCatalog catalog)
    {
        var recompute = new List<(int, int, int, byte[])>();
        foreach (var loc in _cache.DirtyObjects())
        {
            if (loc.ObjType == LibObj.Combi && catalog.Combis.TryGetValue((loc.Bank, loc.Number), out var combi))
                recompute.Add((loc.ObjType, loc.Bank, loc.Number, combi.Body));
            else if (loc.ObjType == LibObj.SetList && catalog.Setlists.TryGetValue(loc.Number, out var setList))
                recompute.Add((loc.ObjType, loc.Bank, loc.Number, setList.Body));
        }
        if (recompute.Count == 0) return;
        if (_cache.RecomputeResolvedDependencies(recompute) > 0) LocalPane.RefreshTree();
    }

    // Refreshes _programBankTypes from real hardware (func 0x61) in the background, same
    // "seed from disk, refresh live" shape as WarmCatalogAsync. A null result (hardware
    // unreachable, or this session has no live connection at all) leaves whatever was
    // already cached - or null - alone; placement checks that can't verify a bank's type
    // are advisory (CHECK), never blocking, so working offline is never broken by this.
    async Task WarmProgramBankTypesAsync()
    {
        try
        {
            var live = await _sysEx.RequestProgramBankTypesAsync();
            if (live == null) return;
            _programBankTypes = live;
            Storage.SaveProgramBankTypes(_host, live.Value.IsExi);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the ctor; a failure here just leaves the cached (or null)
            // types in place - placement checks are advisory, so swallowing is safe, but the
            // exception must be observed and logged.
            AppLog.Warn($"[librarian] program bank-type warm-up failed: {ex.Message}");
        }
    }

    // Testing-only entry point so a self-test can deterministically await the constructor's
    // own fire-and-forget warmup completing (against a FakeMoveExecutor's synchronously-
    // resolved ProgramBankTypesToReturn) before asserting on BankTypeOf's effect, instead of
    // relying on incidental synchronous continuation timing - same shape as PcgPaneViewModel.
    // LoadBytesForTesting.
    internal Task WarmProgramBankTypesForTestingAsync() => WarmProgramBankTypesAsync();

    // The one thing PlanBatchMove's fresh-placement bank-type check (Core/BatchMoveModel.cs)
    // needs: is destination Program bank `objBank` actually configured as EXi (true) or HD-1
    // (false) on the real hardware right now? Null if we've never successfully queried it
    // (nothing pushed yet this session, or genuinely offline), or the bank has no such concept
    // at all (GM/g - KronosBanks.ProgramBankTypeBitIndex returns null for those).
    bool? BankTypeOf(int objBank) =>
        _programBankTypes is { } types && KronosBanks.ProgramBankTypeBitIndex(objBank) is int bit && bit < types.IsExi.Length
            ? types.IsExi[bit]
            : null;

    // The Merge Window's "Merge behavior" setting (Views/SettingsWindow.xaml's Librarian tab)
    // selects the persistence strategy at construction time - see MergeCachePersistence.cs.
    // A setting change while THIS window is already open only takes effect the next time the
    // Librarian is reopened (MergeCache.SetPersistence exists and is exercised by
    // MergeCacheSelfTests for when live cross-window switching is worth wiring up).
    static IMergeCachePersistence BuildMergePersistence(AppSettings settings) =>
        settings.MergeBehavior == MergeCacheBehavior.LocalStorage
            ? new FileMergeCachePersistence(Path.Combine(Storage.DataDir, "merge_cache.json"))
            : new InMemoryMergeCachePersistence();

    // Fires after EVERY local edit (NotifyLocalEditMade). Reads the op-log's in-memory display
    // mirror, NOT the file (OpLog.ReadForDisplay): re-reading a growing oplog.jsonl over a
    // possibly SMB-mounted DataDir on each edit was a UI-thread stall that grew with the log.
    // The mirror is seeded from disk once (this call, at construction) and kept in step by every
    // Append thereafter, so the per-edit refresh is now pure in-memory work. Stays synchronous
    // on purpose - an off-thread read would leave unawaited background file I/O that races
    // teardown (self-tests delete the library root out from under it). ReadForDisplay is distinct
    // from ReadAll so the fold/recovery path keeps reading the durable on-disk log unchanged.
    void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in OpLog.ReadForDisplay(_cache.Root).OrderByDescending(e => e.TimestampUtc))
            History.Add(new HistoryRow(entry));
    }

    // Wipes the persisted audit log (see OpLog.ClearAll's own comment on what this does and
    // doesn't affect). Plain method, not a [RelayCommand]: the confirmation prompt lives in
    // LibrarianShellWindow's code-behind, same as every other destructive action in this app
    // (e.g. FileManagerWindow's Delete), so this is only ever reached after the user says yes.
    public void ClearHistory()
    {
        OpLog.ClearAll(_cache.Root);
        RefreshHistory();
    }

    // ── Undo (Ctrl+Z / the toolbar's Undo button) ─────────────────────────────────────────
    // One step per user gesture, over LOCAL state only - see Core/LocalLibrary/LibrarianUndo.cs
    // for the capture model, and its class comment for what's deliberately outside undo's reach
    // (the displaced-occupant safety clipboard, anything already pushed to hardware, Clear History).
    // Every mutating method below opens exactly one scope; the Local/Merge panes' own actions do
    // the same via their injected BeginUndo.

    [ObservableProperty] string undoLabel = AppMessages.Librarian.Shell.UndoNothingTooltip;

    // Gated on !IsBusy for the same reason Sync/Commit are, plus one that's specific to undo:
    // RestoreSlots puts a whole prior LocalIndexEntry back, BaselineHash included, so an undo
    // landing while a push is mid-flight could roll a baseline RecordPushSuccesses had just
    // advanced BACKWARD - re-dirtying an object that was in fact already written to hardware.
    // Every other local edit path only ever preserves the existing baseline; this is the one that
    // can move it, so it must not run concurrently with a push.
    public bool CanUndo => _undo.CanUndo && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    void Undo()
    {
        if (_undo.Undo() is not { } description)
        {
            StatusText = AppMessages.Librarian.Shell.NothingToUndo;
            return;
        }
        // The Merge pane and the pending-dependency list refresh themselves as part of the
        // restore (MergePane.Restore / RestoreSessionDependencies); the Local tree and the
        // History panel are this method's to refresh. Missing any one of these reads to the
        // user as "undo did nothing."
        LocalPane.RefreshTree();
        NotifyLocalEditMade();
        // The per-pane lines describe the action that was just rolled back ("Placed 3...",
        // "Pulled 3 object(s)...") - stale now, same reasoning as after a successful push.
        ClearPaneStatuses();
        StatusText = AppMessages.Librarian.Shell.Undone(description);
    }

    void OnUndoStackChanged()
    {
        UndoLabel = _undo.TopDescription is { } description
            ? AppMessages.Librarian.Shell.UndoTooltip(description)
            : AppMessages.Librarian.Shell.UndoNothingTooltip;
        UndoCommand.NotifyCanExecuteChanged();
    }

    void RestoreSessionDependencies(IReadOnlyList<SessionDependencyEntry> entries)
    {
        _sessionClipboard.ReplaceAll(entries);
        RefreshSessionClipboard();
    }

    // The cache and the merge cache both outlive this ViewModel's own window, so the recorder's
    // event subscriptions have to be released when the window closes (LibrarianShellWindow's
    // Closing handler) - otherwise a reopened Librarian's edits would also be observed by the
    // previous session's dead recorder.
    public void Dispose()
    {
        _undo.Changed -= OnUndoStackChanged;
        _undo.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task SyncLibraryAsync()
    {
        IsBusy = true; WarningText = null;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var (pull, push) = await SyncPipeline.SyncLibraryAsync(
                _sysEx, _cache, _sessionClipboard, ForceFullPull, m => StatusText = m);
            StatusText = AppMessages.Librarian.Shell.SyncResult(pull.ObjectsFetched, pull.Conflicts, push.Written, push.Deleted);
            if (!push.Ok) WarningText = push.Error;
            else ClearAfterSuccessfulPush();
            AppLog.Info($"[librarian] sync done: fetched={pull.ObjectsFetched} pushed={push.Written} hasObjects={_cache.HasAnyObjects}");
        }
        catch (Exception ex)
        {
            // The pull half commits its objects to the cache BEFORE the push half runs, so a throw
            // in the push must not leave the pane showing an empty/stale tree until the next reopen
            // (the finally below re-reads the cache). Surface it too - an AsyncRelayCommand would
            // otherwise swallow it into its ExecutionTask, so the Sync would look like it did nothing.
            AppLog.Warn($"[librarian] sync failed: {ex}");
            WarningText = AppMessages.Librarian.Shell.OperationFailed(ex.Message);
        }
        finally
        {
            LocalPane.RefreshTree();
            RefreshHistory();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task CommitChangesAsync()
    {
        IsBusy = true; WarningText = null;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var result = await SyncPipeline.CommitChangesAsync(_sysEx, _cache, _sessionClipboard, m => StatusText = m);
            StatusText = result.Ok
                ? AppMessages.Librarian.Shell.CommitResult(result.Written, result.Deleted)
                : AppMessages.Librarian.Shell.CommitFailed;
            if (!result.Ok) WarningText = result.Error;
            else ClearAfterSuccessfulPush();
        }
        catch (Exception ex)
        {
            // Same rationale as SyncLibraryAsync: refresh the pane from the cache no matter what
            // (finally), and surface a thrown error rather than letting the command swallow it.
            AppLog.Warn($"[librarian] commit failed: {ex}");
            WarningText = AppMessages.Librarian.Shell.OperationFailed(ex.Message);
        }
        finally
        {
            LocalPane.RefreshTree();
            RefreshHistory();
            IsBusy = false;
        }
    }

    // Runs right before every Sync/Commit - the "lazy" half of the auto-heal placement
    // pipeline (see ResolvePendingDependencies): retries every still-pending dependency
    // against Local Library's CURRENT state (time has passed since it was placed; the
    // dependency may now exist anywhere), then - only for whatever's STILL unresolved after
    // that - asks the user via ConfirmContinueWithPendingDependencies whether to proceed
    // anyway or cancel. Returns false to abort the Sync/Commit before it touches SyncPipeline
    // at all.
    async Task<bool> PrepareForPushAsync()
    {
        ResolvePendingDependencies();
        if (_sessionClipboard.Pending.Count == 0) return true;

        bool proceed = ConfirmContinueWithPendingDependencies == null
            || await ConfirmContinueWithPendingDependencies(_sessionClipboard.Pending.ToList());
        if (!proceed)
        {
            StatusText = AppMessages.Librarian.Shell.CancelledPendingDeps;
            return false;
        }

        // The user explicitly accepted the risk - stop tracking these as blocking. Whatever
        // reference is still wrong/missing stays exactly as it is; SyncPipeline's own
        // referential REFUSE check (ChangesetBuilder) remains a final, independent backstop
        // for anything genuinely absent (see this feature's own design notes).
        _sessionClipboard.Clear();
        RefreshSessionClipboard();
        return true;
    }

    bool CanRunHardwareOp() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SyncLibraryCommand.NotifyCanExecuteChanged();
        CommitChangesCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();   // see CanUndo - undo must not race a push
    }

    // Called after any local edit (rename/move/discard/placement) so the permanent history
    // panel reflects it immediately rather than only after a Sync/Commit.
    public void NotifyLocalEditMade() => RefreshHistory();

    // Runs only after a push actually succeeded: the per-pane status lines are stale
    // (ClearPaneStatuses, requirement 5), and every undo step below now describes local state
    // that has been written to hardware - which this stack has no way to roll back (see
    // LibrarianUndoRecorder.Clear), so it's dropped rather than left offering a rollback it
    // can't honour.
    void ClearAfterSuccessfulPush()
    {
        ClearPaneStatuses();
        _undo.Clear();
    }

    // Requirement 5: after a successful Sync/Commit, the per-pane status lines under each pane's
    // buttons ("Cut ...", "Placed at ...", "Pulled N object(s)...") describe work that's now been
    // pushed and no longer reflects current state - clear them. The top Sync-row StatusText is
    // left alone: it holds the just-pressed button's own result ("Pushed N object(s).").
    void ClearPaneStatuses()
    {
        LocalPane.StatusText = "";
        MergePane.StatusText = "";
        PcgPane.StatusText = "";
    }

    [RelayCommand]
    void LoadPcgFromComputer(Window owner) => PcgPane.LoadFromComputer(owner);

    [RelayCommand]
    async Task LoadPcgFromKronosAsync(Window owner) =>
        await PcgPane.LoadFromKronosAsync(new KronosRemotePcgSource(owner, _settings, _host));

    // ── Cross-pane placement (PCG -> local), requirement 12 ──────────────────────────
    // Drop on a specific slot = exact placement. HW-write never happens here - this only
    // ever touches the local cache via LocalEditOps, exactly like every other local edit.

    public (bool Ok, string? Error) PlaceFromPcg(ObjLoc pcgLoc, ObjLoc destLoc)
    {
        // One undo step for the whole placement, including whatever occupant it displaces. A
        // guard below that returns before writing anything captures nothing, so no empty step
        // is pushed (see LibrarianUndoStep.CapturedNothing).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedAt(pcgLoc.Label(), destLoc.Label()));

        // Cross-type guard, same as BatchPlaceFromPcg's per-item check: the single-item drop
        // path has no upstream type check (OnLocalDrop filters on drag format only), and combi
        // bank numbers are a numeric subset of program bank numbers - a mismatched drop would
        // otherwise land silently in a valid-looking slot of the wrong type.
        if (pcgLoc.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(pcgLoc.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");

        var entry = PcgPane.Get(pcgLoc);
        if (entry == null) return (false, "not found in the loaded PCG file");
        var rawBody = ProgramFormatConverter.WireBodyFromPcgEntry(pcgLoc.ObjType, entry);
        if (rawBody == null) return (false, "malformed Program record in the loaded PCG file");

        // Repoint whatever of this object's OWN references already resolve somewhere in Local
        // Library (by content, not just the raw address the PCG encoded) before ever writing
        // it - see DependencyScanner.RepointPcgReferences's own comment. `entry != null` above
        // already guarantees PcgPane.View isn't null; the pattern-match is just defensive.
        var (body, unresolved) = PcgPane.View is { } view
            ? DependencyScanner.RepointPcgReferences(_cache, view, pcgLoc.ObjType, rawBody)
            : (rawBody, new List<(string RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)>());

        var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(
            _cache, destLoc, pcgLoc.ObjType, LibObj.CurrentObjectVersion(pcgLoc.ObjType) ?? 0, body, entry.Name,
            divertDisplacedToClipboard: true, DateTime.UtcNow, BankTypeOf);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        StageAndTrackPcgDependencies(unresolved, destLoc);
        LocalPane.RefreshTree();
        NotifyLocalEditMade();
        return (true, null);
    }

    // ── PCG -> Merge Window (fully automatic, transitive - see MergeCache.PullFromPcg) ──

    public void PullIntoMerge(ObjLoc pcgLoc)
    {
        if (PcgPane.View is not { } view) return;   // nothing loaded - nothing to pull from
        MergePane.PullFromPcg(view, PcgPane.LoadedFileName ?? "(unknown)", pcgLoc);
    }

    // Multi-item entry point (a multi-select or a whole-bank drag/context-menu action). Exists so
    // ONE gesture is ONE undo step: the per-loc overload above is called in a loop, and without a
    // scope around the whole loop, dragging a bank of 128 into the Merge Window would take 128
    // Ctrl+Z presses to walk back. Nested Begins inside the loop join this step (see
    // LibrarianUndoRecorder.Begin).
    public void PullIntoMerge(IReadOnlyList<ObjLoc> pcgLocs)
    {
        if (pcgLocs.Count == 0) return;
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPulledIntoMerge(pcgLocs.Count));
        foreach (var loc in pcgLocs) PullIntoMerge(loc);
    }

    // ── Local -> Merge Window (requirement 3, transitive - see MergeCache.PullFromLocal) ──
    // The Merge Window as a general scratchpad: stage an already-placed Local object (plus its
    // local dependencies) back in so it can be moved/rearranged and pushed somewhere else.
    public void PullLocalIntoMerge(ObjLoc localLoc) => MergePane.PullFromLocal(_cache, localLoc);

    // Same one-gesture-one-step reasoning as PullIntoMerge's list overload above.
    public void PullLocalIntoMerge(IReadOnlyList<ObjLoc> localLocs)
    {
        if (localLocs.Count == 0) return;
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPulledIntoMerge(localLocs.Count));
        foreach (var loc in localLocs) PullLocalIntoMerge(loc);
    }

    // ── Merge Window group -> Local (bulk placement of a multi-item Merge selection) ─────
    // Dragging a multi-item Merge Window selection - typically the whole "Set Lists"/"Combis"/
    // "Programs" group node (LibrarianShellWindow's Merge pane bank-equivalent selection), but
    // works equally for any multi-leaf Ctrl+click selection sharing one type - onto a local
    // bank or a specific slot within one, instead of placing one staged item at a time. If the
    // drop landed on a specific slot, destSlot is that slot's index and the fill starts EXACTLY
    // there - the user pointed at it, so that's where placement begins, not wherever the first
    // free slot happens to be. Dropping on the bank/group node itself instead (destSlot null)
    // falls back to destBank's own first free slot (LocalEditOps.FindNextFreeSlot). Either way,
    // fill is sequential, not "must be completely empty" (an earlier, more conservative version
    // of this method required that; real use showed a partially-filled bank with plenty of room
    // left was the common case, not the exception). An occupied-but-unreferenced slot in the
    // way is overwritten with its occupant diverted to the persisted clipboard (never lost) -
    // the same safety net every other batch placement in this app already relies on; a
    // referenced occupant still REFUSEs via LocalEditOps.BatchPlace's own orphan gate. Only
    // entries matching destBank's own type are placed (silently drops anything else, e.g. a
    // stray different-type hash) - nested dependency Programs/Combis stay staged for individual
    // placement afterward, exactly like PlaceFromMerge above already works (this doesn't
    // cascade into placing dependencies either). Anything beyond the bank's remaining room
    // stays staged too (never lost), same "flag what didn't fit" convention BatchPlaceFromPcg
    // uses.
    public (bool Ok, string? Message) PlaceMergeGroupSequentially(int objType, int destBank, IReadOnlyList<string> contentHashes, int? destSlot = null)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        // The exact gesture this feature exists for: one accidental whole-bank drag out of the
        // Merge Window is one Ctrl+Z, restoring both the staged entries and every local slot the
        // batch wrote (plus any occupant it overwrote).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedGroup(contentHashes.Count, descriptor.BankLabel(destBank)));
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e != null && e!.ObjType == objType).Select(e => e!).ToList();
        if (group.Count == 0) return (false, "nothing to place for this bank's type");

        // Duplicate-content guard (same as PlaceFromMerge's single-item path): anything whose
        // content already lives elsewhere in Local Library is repointed there instead of
        // consuming a destination slot for a second copy - never even reaches the sequential
        // fill below.
        var dedupedLocs = new List<ObjLoc>();
        var toPlace = new List<MergeEntry>();
        foreach (var entry in group)
        {
            if (!WasStagedFromLocal(entry) && _cache.FindByContentHash(entry.ObjType, entry.ContentHash) is { } existingLoc)
            {
                MergePane.CommitPlacement(entry.ContentHash, existingLoc);
                dedupedLocs.Add(existingLoc);
            }
            else toPlace.Add(entry);
        }
        string dedupNote = dedupedLocs.Count > 0 ? AppMessages.Librarian.Shell.ReusedExistingContentCount(dedupedLocs.Count) : "";

        if (toPlace.Count == 0) return (true, dedupNote);

        // A drop on a SPECIFIC slot fills contiguously from exactly there - the user pointed at it,
        // so overwriting whatever follows is their explicit intent. A drop on a bank/header is an
        // auto-fill, and must land only on slots free of real content: init placeholders count as
        // free, which makes those holes scattered rather than one contiguous tail, so a plain
        // startSlot+i walk would write over real patches sitting past the first placeholder.
        int startSlot = destSlot ?? FindNextFreeSlot(objType, destBank);
        var targetSlots = destSlot is { } fixedStart
            ? Enumerable.Range(fixedStart, Math.Max(0, Math.Min(toPlace.Count, descriptor.SlotCount - fixedStart))).ToList()
            : LocalEditOps.AvailableSlotsFrom(_cache, objType, destBank, startSlot, toPlace.Count);
        int take = targetSlots.Count;
        if (take <= 0)
        {
            return (false, destSlot is { } s
                ? $"not enough room in {descriptor.BankLabel(destBank)} from {new ObjLoc(objType, destBank, s).Label()} onward."
                : $"{descriptor.BankLabel(destBank)} is full - no free slots left.");
        }

        var bodies = new byte[take][];
        var unresolvedPerItem = new List<MergeRefSite>[take];
        var placements = new List<BatchPlacement>();
        for (int i = 0; i < take; i++)
        {
            var entry = toPlace[i];
            (bodies[i], unresolvedPerItem[i]) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);
            placements.Add(new BatchPlacement(null, new ObjLoc(objType, destBank, targetSlots[i]),
                new ObjectDump(objType, destBank, targetSlots[i], entry.Version, bodies[i]), entry.DisplayName));
        }

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow, MergePane.ForceOverwrite);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        for (int i = 0; i < take; i++)
        {
            var destLoc = new ObjLoc(objType, destBank, targetSlots[i]);
            MergePane.CommitPlacement(toPlace[i].ContentHash, destLoc);
            TrackMergeDependencies(unresolvedPerItem[i], destLoc);
        }
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        string msg = take < toPlace.Count
            ? $"Placed {take}; {toPlace.Count - take} didn't fit ({descriptor.BankLabel(destBank)} is full) - still staged in the Merge Window"
            : $"Placed {take}";
        if (dedupNote.Length > 0) msg += $"; {dedupNote}";
        return (true, msg);
    }

    // ── Whole Program bank copy with EXi/HD-1 type change (requirement 4) ────────────────
    // The func 0x7C "Change Program Bank Type" the changeset emits reformats+ERASES the whole
    // destination bank, so a type change is inherently a copy-the-entire-bank operation.

    // If placing this Merge-Window Program group into destBank would require changing the
    // destination bank's HD-1/EXi type, returns the target IsExi; otherwise null (not a Program
    // group, mixed formats, destination type unknown, or already the right type). The caller
    // (code-behind) uses this to prompt before the destructive reformat.
    public bool? BankTypeChangeNeeded(int objType, int destBank, IReadOnlyList<string> contentHashes)
    {
        if (objType != LibObj.Program) return null;
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e is { ObjType: LibObj.Program }).Select(e => e!).ToList();
        if (group.Count == 0) return null;
        bool allExi = group.All(e => e.Body.Length == ProgramFormatConverter.WireSizeExi);
        bool allHd1 = group.All(e => e.Body.Length != ProgramFormatConverter.WireSizeExi);
        if (allExi == allHd1) return null;   // mixed formats - not a clean single-type bank
        bool groupIsExi = allExi;
        // Destination bank's current type: the live func-0x61 answer if we have it, ELSE the
        // format of whatever Programs already sit in that bank locally (a real bank is
        // homogeneous). The fallback is what makes the type-change prompt fire right after the
        // window opens or offline, instead of the drop slipping through and only being caught as
        // a per-item REFUSE at Commit - the exact failure the user hit copying EXi into an HD-1
        // bank whose live type hadn't been warmed yet.
        bool? destIsExi = BankTypeOf(destBank) ?? LocalProgramBankFormat(destBank);
        return destIsExi is bool d && d != groupIsExi ? groupIsExi : null;
    }

    // The HD-1/EXi format of a destination Program bank as Local Library currently sees it. Null if
    // the bank is empty locally (nothing to infer a type from) - see LocalEditOps' own comment.
    bool? LocalProgramBankFormat(int bank) => LocalEditOps.LocalProgramBankFormat(_cache, bank);

    // ── Type-root ("Programs"/"Combis"/"Set Lists" header) drop targets, requirement 6 ──────
    // A drop on the header names a TYPE but no bank, so each entry point below resolves it to the
    // first bank with room - passing the incoming Programs' own HD-1/EXi format so the chosen bank
    // can't be one the placement would then REFUSE as wrong-format (see
    // LocalEditOps.FindBankWithFreeSlot). Null means every writable bank of that type is full.

    public int? FindBankForPcgDrop(int objType, IReadOnlyList<ObjLoc> pcgLocs) =>
        LocalEditOps.FindBankWithFreeSlot(_cache, objType, PcgGroupIsExi(objType, pcgLocs), BankTypeOf);

    public int? FindBankForMergeDrop(int objType, IReadOnlyList<string> contentHashes) =>
        LocalEditOps.FindBankWithFreeSlot(_cache, objType, MergeGroupIsExi(objType, contentHashes), BankTypeOf);

    // The first free slot in one specific bank, or null if it's full - for a SINGLE item dropped on
    // a bank/header, where a "slot 0" fallback would silently overwrite whatever sits there.
    public int? NextFreeSlotIn(int objType, int bank) => LocalEditOps.TryFindNextFreeSlot(_cache, objType, bank);

    // The wire format shared by a group of incoming Programs, or null when they're mixed, not
    // Programs, or unreadable - the body's own length says EXi vs HD-1 deterministically, the same
    // primitive BankTypeChangeNeeded already relies on. Public because the View needs the same
    // answer a moment later to word the "no bank of THIS format has room" refusal.
    public bool? PcgGroupIsExi(int objType, IReadOnlyList<ObjLoc> pcgLocs)
    {
        if (objType != LibObj.Program) return null;
        var lengths = pcgLocs
            .Select(l => PcgPane.Get(l))
            .Where(e => e != null)
            .Select(e => ProgramFormatConverter.WireBodyFromPcgEntry(LibObj.Program, e!)?.Length)
            .Where(len => len != null)
            .Distinct()
            .ToList();
        return lengths.Count == 1 ? lengths[0] == ProgramFormatConverter.WireSizeExi : null;
    }

    public bool? MergeGroupIsExi(int objType, IReadOnlyList<string> contentHashes)
    {
        if (objType != LibObj.Program) return null;
        var lengths = contentHashes
            .Select(h => MergePane.TryGet(h))
            .Where(e => e is { ObjType: LibObj.Program })
            .Select(e => e!.Body.Length)
            .Distinct()
            .ToList();
        return lengths.Count == 1 ? lengths[0] == ProgramFormatConverter.WireSizeExi : null;
    }

    // Copies a whole Program bank from the Merge Window into destBank, changing destBank's
    // HD-1/EXi type to match. Because the func 0x7C emitted at Commit ERASES the whole
    // destination bank, this REPLACES it: every existing local Program in destBank is dropped
    // first (it would be erased on hardware regardless), the group is placed from slot 0, and
    // the type-change intent is recorded for the next Commit. Placement bypasses the normal
    // format REFUSE (bankTypeOf: null) precisely because the reformat is intentional.
    public (bool Ok, string? Message) PlaceMergeBankWithTypeChange(int destBank, IReadOnlyList<string> contentHashes, bool targetIsExi)
    {
        var descriptor = ObjectTypeRegistry.Get(LibObj.Program);
        // The most destructive placement in the Librarian (it drops every local Program in destBank
        // before writing) and the one where a mid-way REFUSE from BatchPlace would otherwise leave
        // the bank wiped with no way back: the scope captures those removals as they happen, so the
        // step is pushed - and Ctrl+Z recovers the bank - even on the failure path.
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoCopiedBankWithTypeChange(descriptor.BankLabel(destBank)));
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e is { ObjType: LibObj.Program }).Select(e => e!).ToList();
        if (group.Count == 0) return (false, "nothing to place for this bank");
        if (group.Count > descriptor.SlotCount) group = group.Take(descriptor.SlotCount).ToList();

        // Replace the destination bank - the 0x7C erases everything in it on hardware anyway.
        for (int n = 0; n < descriptor.SlotCount; n++)
            if (_cache.Exists(LibObj.Program, destBank, n))
                _cache.RemoveObject(LibObj.Program, destBank, n, DateTime.UtcNow);

        var placements = new List<BatchPlacement>();
        for (int i = 0; i < group.Count; i++)
        {
            var (body, _) = MergePane.ResolveReferencesForPlacement(group[i], LocalLookup);   // Programs have no refs
            placements.Add(new BatchPlacement(null, new ObjLoc(LibObj.Program, destBank, i),
                new ObjectDump(LibObj.Program, destBank, i, group[i].Version, body), group[i].DisplayName));
        }

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, LibObj.Program, placements, divertDisplacedToClipboard: true, bankTypeOf: null, DateTime.UtcNow, MergePane.ForceOverwrite);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        for (int i = 0; i < group.Count; i++)
            MergePane.CommitPlacement(group[i].ContentHash, new ObjLoc(LibObj.Program, destBank, i));
        // Index metadata, not a slot write - the undo recorder's slot-level observation can't see
        // this one, so the prior intent (often "none at all") is captured explicitly here.
        _undo.CapturePendingBankTypeChange(destBank);
        _cache.SetPendingBankTypeChange(destBank, targetIsExi);
        _cache.Save();
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        return (true, $"Copied {group.Count} program(s) into {descriptor.BankLabel(destBank)} and set it to {(targetIsExi ? "EXi" : "HD-1")} - the bank reformats on Commit.");
    }

    // ── Merge Window -> Local (manual, per-item - the user picks every destination,
    // including a dependency's, since only they know whether a bank should stay empty or a
    // partially-filled one should be continued; see this feature's own design conversation). ──

    // The duplicate-content guard below only applies to genuinely NEW content (pulled from a
    // .pcg file) - an entry the Merge Window staged FROM Local Library itself (PullLocalIntoMerge,
    // "Move to Merge Window") already has a known local home; placing it elsewhere is the whole
    // point of that feature (an intentional copy/rearrange), not an accidental duplicate to warn
    // about or redirect. Without this exclusion, FindByContentHash would always find the entry's
    // own origin and silently no-op the placement.
    static bool WasStagedFromLocal(MergeEntry entry) =>
        entry.Origins.Any(o => o.PcgFileName == MergeCache.LocalSourceLabel);

    public (bool Ok, string? Error) PlaceFromMerge(string mergeContentHash, ObjLoc destLoc)
    {
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedMergeItemAt(destLoc.Label()));
        var entry = MergePane.TryGet(mergeContentHash);
        if (entry == null) return (false, "not found in the Merge Window");
        // Cross-type guard - see PlaceFromPcg's identical check for why this can't be
        // left to the drop handlers.
        if (entry.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(entry.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");

        // Duplicate-content guard: this entry's OWN content (not just its references - see
        // ResolveReferencesForPlacement below for that) may already be sitting somewhere else
        // in Local Library, byte-identical. Rather than writing a second copy, repoint this
        // hash at that existing location the same way a dependency would (RecordPlacement),
        // so any Merge-staged sibling that references it resolves to the ONE copy. Skipped
        // when the match IS the requested destination - that's just re-placing onto its own
        // slot, not a duplicate elsewhere.
        if (!WasStagedFromLocal(entry) &&
            _cache.FindByContentHash(entry.ObjType, entry.ContentHash) is { } existingLoc && !existingLoc.Equals(destLoc))
        {
            MergePane.CommitPlacement(mergeContentHash, existingLoc);
            return (true, AppMessages.Librarian.Shell.ReusedExistingContent(existingLoc.Label()));
        }

        // Patches whatever of this entry's OWN dependency references resolve - either because
        // the dependency was ALSO placed via Merge this session (_placedAddresses), or because
        // it already exists ANYWHERE in Local Library (LocalLookup, by content) - the
        // many-to-one dedup payoff, generalized beyond just this-session Merge placements.
        // Anything still unresolved is tracked for a later retry (TrackMergeDependencies).
        var (body, unresolved) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);

        var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(
            _cache, destLoc, entry.ObjType, entry.Version, body, entry.DisplayName,
            divertDisplacedToClipboard: true, DateTime.UtcNow, BankTypeOf, MergePane.ForceOverwrite);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        MergePane.CommitPlacement(mergeContentHash, destLoc);
        TrackMergeDependencies(unresolved, destLoc);
        LocalPane.RefreshTree();
        NotifyLocalEditMade();
        return (true, null);
    }

    ObjLoc? LocalLookup(int objType, string contentHash) => _cache.FindByContentHash(objType, contentHash);

    // Drop on a bank/root = auto-fill starting at the next free local slot in that bank,
    // reusing BatchLibrarian.ResolveSequentialFill - the same sequential-fill-with-clipboard-
    // overflow logic the persisted clipboard's Paste Multi/All already uses.
    public (bool Ok, string? Message) BatchPlaceFromPcg(int objType, IReadOnlyList<ObjLoc> pcgLocs, int destBank)
    {
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedGroup(
            pcgLocs.Count, ObjectTypeRegistry.Get(objType).BankLabel(destBank)));
        var pending = new List<ClipboardEntry>();
        foreach (var loc in pcgLocs)
        {
            // A loc whose own type doesn't match objType would otherwise get force-fed through
            // the WRONG type's WireBodyFromPcgEntry converter below (it trusts objType, not
            // loc) - skip rather than risk decoding e.g. a Combi body as a Program. The Local
            // tree's own UI-level selection guard (LibrarianShellWindow.OnPcgPreviewMouseDown)
            // already prevents building a mixed-type selection in the first place; this is
            // defense in depth for this method's other/future callers.
            if (loc.ObjType != objType) continue;
            if (PcgPane.Get(loc) is not { } e) continue;
            var body = ProgramFormatConverter.WireBodyFromPcgEntry(objType, e);
            if (body == null) continue;   // malformed Program record - skip rather than fail the whole batch
            pending.Add(new ClipboardEntry { ObjType = objType, Origin = loc, Version = LibObj.CurrentObjectVersion(objType) ?? 0, Body = body, Provenance = ClipboardProvenance.UserCopy, CutAt = DateTime.UtcNow });
        }
        if (pending.Count == 0) return (false, "nothing to place");

        int startSlot = FindNextFreeSlot(objType, destBank);
        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(pending, objType, destBank, startSlot, bankTypeOf: null,
            slotAvailable: s => !_cache.HasContent(objType, destBank, s));
        if (placed.Count == 0) return (false, "nothing could be placed (bank full or type mismatch)");

        // Repoint each placed item's OWN references before writing, same as the single-item
        // path - every dependency that already resolves somewhere in Local Library gets
        // pointed there; whatever doesn't is tracked per item below.
        var view = PcgPane.View;
        var bodies = new byte[placed.Count][];
        var unresolvedPerItem = new List<(string RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)>[placed.Count];
        for (int i = 0; i < placed.Count; i++)
        {
            (bodies[i], unresolvedPerItem[i]) = view != null
                ? DependencyScanner.RepointPcgReferences(_cache, view, objType, placed[i].Entry.Body)
                : (placed[i].Entry.Body, new List<(string, int, ObjLoc, string?)>());
        }

        var placements = new List<BatchPlacement>();
        for (int i = 0; i < placed.Count; i++)
            placements.Add(new BatchPlacement(null, new ObjLoc(objType, destBank, placed[i].Slot),
                new ObjectDump(objType, destBank, placed[i].Slot, 0, bodies[i]), placed[i].Entry.Origin.Label()));

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        for (int i = 0; i < placed.Count; i++)
            StageAndTrackPcgDependencies(unresolvedPerItem[i], new ObjLoc(objType, destBank, placed[i].Slot));
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        string msg = stillPending.Count > 0
            ? $"Placed {placed.Count}; {stillPending.Count} didn't fit (bank full or type mismatch)"
            : $"Placed {placed.Count}";
        return (true, msg);
    }

    int FindNextFreeSlot(int objType, int bank) => LocalEditOps.FindNextFreeSlot(_cache, objType, bank);

    void MergeDisplacedIntoPersistentClipboard(List<ClipboardEntry> newEntries)
    {
        if (newEntries.Count == 0) return;
        var clip = BatchLibrarian.LoadClipboardGlobal();
        clip.Entries.AddRange(newEntries);
        BatchLibrarian.SaveClipboardGlobal(clip);
    }

    // Requirement 14's dependency-completeness gate feeds off this: whatever
    // ResolveReferencesForPlacement (Merge path) couldn't resolve gets tracked so
    // ResolvePendingDependencies can retry it later, by content, against Local Library's
    // then-current state - not just re-checking the one address it currently encodes.
    void TrackMergeDependencies(List<MergeRefSite> stillUnresolved, ObjLoc placedAt)
    {
        if (stillUnresolved.Count == 0) return;
        foreach (var site in stillUnresolved)
            _sessionClipboard.Add(new SessionDependencyEntry(site.TargetLoc, site.RefKind, site.Site, placedAt, site.ResolvedContentHash));
        RefreshSessionClipboard();
    }

    // Same tracking, for the direct-PCG path - plus auto-staging: a reference RepointPcgReferences
    // couldn't resolve locally, but whose expected content the loaded PCG DOES have, gets pulled
    // into the Merge Window right away (reusing the existing transitive pull) so the user has a
    // clear, visible next step instead of a silently wrong/missing reference. A null expected
    // hash (the PCG doesn't have it either - a true gap) is left alone; nothing to stage.
    void StageAndTrackPcgDependencies(List<(string RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)> unresolved, ObjLoc placedAt)
    {
        if (unresolved.Count == 0) return;
        if (PcgPane.View is { } view)
        {
            foreach (var (_, _, originalTarget, expectedHash) in unresolved)
                if (expectedHash != null)
                    MergePane.PullFromPcg(view, PcgPane.LoadedFileName ?? "(unknown)", originalTarget);
        }
        foreach (var (refKind, site, originalTarget, expectedHash) in unresolved)
            _sessionClipboard.Add(new SessionDependencyEntry(originalTarget, refKind, site, placedAt, expectedHash));
        RefreshSessionClipboard();
    }

    // Runs right before every Sync/Commit (see PrepareForPushAsync) - retries every pending
    // dependency against Local Library's CURRENT state (time has passed; the dependency may
    // now exist anywhere, not necessarily at the address it was originally tracked against),
    // and repatches whatever's found via a REAL edit (LocalEditOps.RepatchReference -
    // re-dirties the referrer, appears in History, feeds the next push changeset; never a
    // silent byte mutation, since the referrer may already be dirty or previously pushed).
    void ResolvePendingDependencies()
    {
        bool anyResolved = false;
        foreach (var entry in _sessionClipboard.Pending.ToList())
        {
            if (entry.ExpectedContentHash is not { } hash) continue;   // a true gap - nothing to search for
            if (_cache.FindByContentHash(entry.MissingRef.ObjType, hash) is not { } foundLoc) continue;
            if (LocalEditOps.RepatchReference(_cache, entry.RequiredBy, entry.Site, entry.RefKind, foundLoc, DateTime.UtcNow))
            {
                _sessionClipboard.Remove(entry);
                anyResolved = true;
            }
        }
        RefreshSessionClipboard();
        if (anyResolved) { LocalPane.RefreshTree(); NotifyLocalEditMade(); }
    }

    void RefreshSessionClipboard()
    {
        SessionClipboardRows.Clear();
        foreach (var e in _sessionClipboard.Pending) SessionClipboardRows.Add(new SessionClipboardRow(e));
    }

    // ── "Object Dependencies" panel (Views/LibrarianShellWindow.xaml's GroupBox, driven by
    // LibrarianShellWindow.xaml.cs's PaneSelection.SelectionChanged) - a live, read-only view
    // of what the CURRENTLY SELECTED Combi(s)/Set List(s) reference, transitively (a Set
    // List's Combis, and THEIR Programs in turn). A selected Program contributes nothing of
    // its own here - Programs never reference anything - so a mixed Program+Combi selection
    // only ever shows the Combis' own dependencies, never the Programs themselves, unless one
    // of them also happens to BE a dependency of a selected Combi. Distinct from
    // _sessionClipboard above: that tracks a placement's references that still need pushing;
    // this is just "what does this object need," independent of placement history.

    public void ShowLocalObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        var seen = new HashSet<ObjLoc>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var loc in selectedLocs)
            if (loc.ObjType != LibObj.Program) CollectLocalDeps(loc, seen, rows);
        ReplaceObjectDependencies(rows);
    }

    // `missing` is optional: the selection-driven panel only wants display rows, while
    // InspectDependencies wants the gaps from the SAME walk rather than a second one.
    void CollectLocalDeps(ObjLoc loc, HashSet<ObjLoc> seen, List<ObjectDependencyRow> rows,
                          List<MissingDependency>? missing = null)
    {
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return;
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seen.Add(refLoc)) continue;
            // A ROM (GM/g) reference is shown, but never as missing - it resolves on the
            // instrument no matter what the local library holds (ObjectReferenceWalker.
            // IsAlwaysAvailable), and nothing can be pulled or placed to "fix" it.
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc)));
                continue;
            }
            // Cached at write time (LocalIndexEntry.DisplayName) - never a blob read, same
            // discipline as the tree's own labels (LocalLibraryPaneViewModel.MakeLeafNode).
            bool found = _cache.Exists(refLoc.ObjType, refLoc.Bank, refLoc.Number);
            string name = found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "";
            // An INIT Program satisfies the reference technically but is a placeholder, not the
            // sound the referrer expects - worth saying so, since it's also the case that places
            // freely (see ProgramBody.IsInit and BatchLibrarian.PlanBatchMove's orphan gate).
            rows.Add(new ObjectDependencyRow(
                found && refLoc.ObjType == LibObj.Program && ProgramBody.IsInitName(name)
                    ? $"{TypeName(refLoc.ObjType)}: {refLoc.Label()} - {name} {AppMessages.Librarian.Shell.InitPlaceholderSuffix}"
                    : DescribeDependency(refLoc, name, found, "locally")));
            // A ROM reference is listed but is never a gap (IsAlwaysAvailable - it resolves on the
            // instrument and can't be searched for), so it never enters `missing`.
            if (!found && missing != null && !ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
                missing.Add(new MissingDependency(refLoc, refKind, site, loc));
            if (found && refLoc.ObjType != LibObj.Program) CollectLocalDeps(refLoc, seen, rows, missing);
        }
    }

    public void ShowPcgObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        var rows = new List<ObjectDependencyRow>();
        if (PcgPane.View is { } view)
        {
            var seen = new HashSet<ObjLoc>();
            foreach (var loc in selectedLocs)
                if (loc.ObjType != LibObj.Program) CollectPcgDeps(view, loc, seen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    void CollectPcgDeps(PcgLibraryView view, ObjLoc loc, HashSet<ObjLoc> seen, List<ObjectDependencyRow> rows)
    {
        var entry = view.Get(loc);
        var body = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
        if (body == null) return;
        foreach (var (_, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seen.Add(refLoc)) continue;
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))   // see CollectLocalDeps
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc)));
                continue;
            }
            var depEntry = view.Get(refLoc);
            rows.Add(new ObjectDependencyRow(DescribeDependency(refLoc, depEntry?.Name ?? "", depEntry != null, "in this PCG")));
            if (depEntry != null && refLoc.ObjType != LibObj.Program) CollectPcgDeps(view, refLoc, seen, rows);
        }
    }

    public void ShowMergeObjectDependencies(IReadOnlyList<string> selectedHashes)
    {
        var seen = new HashSet<string>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var hash in selectedHashes)
        {
            var entry = MergePane.TryGet(hash);
            if (entry != null && entry.ObjType != LibObj.Program) CollectMergeDeps(entry, seen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    // Merge entries are keyed by content hash, not address - RefSites already carry the
    // resolved-dependency lookup (or the original PCG address for a still-unresolved gap), so
    // this needs none of ObjectReferenceWalker's own byte-decoding, unlike the Local/PCG paths.
    void CollectMergeDeps(MergeEntry entry, HashSet<string> seen, List<ObjectDependencyRow> rows)
    {
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string key = site.ResolvedContentHash ?? site.TargetLoc.Label();
            if (!seen.Add(key)) continue;
            // No real address yet (Merge Window is bag-based, not addressed) - name is all
            // there is to show until it's actually placed.
            rows.Add(new ObjectDependencyRow(dep != null
                ? $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}"
                : $"{TypeName(site.TargetLoc.ObjType)}: {site.TargetLoc.Label()} - not found in any loaded PCG"));
            if (dep != null && dep.ObjType != LibObj.Program) CollectMergeDeps(dep, seen, rows);
        }
    }

    public void ClearObjectDependencies() => ObjectDependencyRows.Clear();

    // ── Per-object dependency detail (the Properties dialog's own lists, requirement 1) ──────
    // Same data the "Object Dependencies" panel shows, but for ONE object and in both directions:
    // what it REQUIRES (its own outgoing references, transitively - a Set List's Combis and their
    // Programs) and what USES it (incoming referrers). The panel is selection-driven and
    // outgoing-only; this is the "tell me everything about this one object" view.

    // One reference site with nothing local behind it. Site is carried (not just RefKind) because
    // resolving it later means patching THAT byte site inside RequiredBy - see
    // LocalEditOps.RepatchReference.
    public readonly record struct MissingDependency(ObjLoc Missing, string RefKind, int Site, ObjLoc RequiredBy);

    // Both dependency views in ONE transitive walk. Each level reads the owner's full body off the
    // CAS store, so a Set List with 128 populated slots is ~129 blob reads - fine once per user
    // action, not fine repeated per caller. Callers that need both the display rows and the gaps
    // (the Properties dialog needs exactly that) must use this rather than calling the two
    // convenience wrappers below in sequence.
    public (IReadOnlyList<string> Rows, IReadOnlyList<MissingDependency> Missing) InspectDependencies(ObjLoc loc)
    {
        var rows = new List<ObjectDependencyRow>();
        var missing = new List<MissingDependency>();
        if (loc.ObjType != LibObj.Program) CollectLocalDeps(loc, new HashSet<ObjLoc>(), rows, missing);
        return (rows.Select(r => r.Description).ToList(), missing);
    }

    public IReadOnlyList<string> DescribeRequirements(ObjLoc loc) => InspectDependencies(loc).Rows;

    // What currently points AT `loc` (Combi timbres / Set List slots) - the delete-warning's own
    // referrer lookup, surfaced read-only. Empty for a Set List (nothing ever references one).
    public IReadOnlyList<string> DescribeReferrers(ObjLoc loc) => LocalPane.DescribeReferrers(loc);

    // Every reference of `loc` (transitively) with nothing local behind it - what a "find this
    // dependency" action has to go looking for. ROM (GM/g) references are excluded by
    // construction: they resolve on the instrument and can't be searched for.
    public IReadOnlyList<MissingDependency> MissingDependenciesOf(ObjLoc loc) => InspectDependencies(loc).Missing;

    // ── "Scan PCG for dependency" (requirement 2) ────────────────────────────────────────────
    // The manual counterpart to the automatic auto-heal pipeline: when an object shows unmet
    // dependencies, point this at a .pcg file and whatever it contains is staged into the Merge
    // Window, ready to place. Deliberately staged rather than placed automatically - only the user
    // knows which bank/slot a recovered dependency belongs in (the same reasoning the Merge
    // Window's manual placement rests on), and staging is undoable in one step.
    //
    // Reads the file into a PcgLibraryView of its own instead of loading it into the PCG pane: the
    // user is mid-task on whatever is already loaded there, and a scan for a missing Program
    // shouldn't replace it. Each found dependency comes in transitively (MergeCache.PullFromPcg),
    // so a recovered Combi brings its own Programs too.
    // Searches one .pcg for ONE specific missing address - the Unresolved Dependencies dialog's
    // right-click action, where the user is looking at a single reported gap rather than an
    // object's whole dependency set. Anything found is staged transitively, exactly like the
    // object-level scan; no new session-clipboard tracking is needed here because these entries
    // are ALREADY tracked (that's why they're in the dialog), so ResolvePendingDependencies will
    // repoint them by content wherever the user places them.
    public (bool Found, string? Error) ScanPcgForOneDependency(ObjLoc missing, byte[] pcgBytes, string fileName)
    {
        var file = PcgFile.Open(pcgBytes);
        if (file == null) return (false, AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));

        var view = new PcgLibraryView(file);
        if (view.Get(missing) == null) return (false, null);

        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoScannedPcgForDependencies(fileName));
        MergePane.PullFromPcg(view, fileName, missing);
        return (true, null);
    }

    // A display name for an address, wherever it can be found - Local Library first (the cached
    // DisplayName, no blob read), then the loaded PCG. Empty when neither knows it, which is itself
    // informative in the unresolved list: nothing loaded has this object at all.
    public string DescribeMissingName(ObjLoc loc)
    {
        if (_cache.Exists(loc.ObjType, loc.Bank, loc.Number))
            return _cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number);
        return PcgPane.Get(loc)?.Name ?? "";
    }

    // `missing` comes from the caller's own InspectDependencies/MissingDependenciesOf call, so the
    // transitive walk (one blob read per owner object) happens ONCE per user action rather than
    // again in here.
    public (int Found, int Missing, string? Error) ScanPcgForDependencies(
        ObjLoc loc, IReadOnlyList<MissingDependency> missing, byte[] pcgBytes, string fileName)
    {
        if (missing.Count == 0) return (0, 0, null);

        var file = PcgFile.Open(pcgBytes);
        if (file == null) return (0, missing.Count, AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));
        var view = new PcgLibraryView(file);

        // One undo step for the whole scan, however many dependencies it recovers - same
        // one-gesture-one-step rule as PullIntoMerge's list overload.
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoScannedPcgForDependencies(fileName));
        int found = 0;
        bool tracked = false;
        foreach (var gap in missing)
        {
            var entry = view.Get(gap.Missing);
            if (entry == null) continue;
            MergePane.PullFromPcg(view, fileName, gap.Missing);
            found++;

            // Staging alone doesn't repair anything: the referrer still encodes the OLD address,
            // and the user is free to place the recovered object anywhere. Tracking the gap in the
            // session clipboard - keyed by the CONTENT hash of what the PCG holds, exactly like
            // the direct-PCG placement path (StageAndTrackPcgDependencies) - is what lets
            // ResolvePendingDependencies find it by content at the next Sync/Commit and repatch
            // the reference to wherever it actually landed. Without this the feature would find
            // the dependency and still leave the referrer pointing at nothing unless the user
            // happened to place it at the exact original address.
            if (ProgramFormatConverter.WireBodyFromPcgEntry(gap.Missing.ObjType, entry) is { } wireBody)
            {
                _sessionClipboard.Add(new SessionDependencyEntry(
                    gap.Missing, gap.RefKind, gap.Site, gap.RequiredBy, LocalObjectStore.ComputeHash(wireBody)));
                tracked = true;
            }
        }
        if (tracked) RefreshSessionClipboard();
        return (found, missing.Count, null);
    }

    void ReplaceObjectDependencies(List<ObjectDependencyRow> rows)
    {
        ObjectDependencyRows.Clear();
        foreach (var r in rows) ObjectDependencyRows.Add(r);
    }

    static string TypeName(int objType) => ObjectTypeRegistry.Get(objType).DisplayName;

    // Shared row format for the Local/PCG collectors above (Merge has no real address, so it
    // formats its own rows separately) - slot address alone isn't useful on its own, hence
    // type + name alongside it.
    // A read-only ROM (GM/g) Program reference - present on every Kronos, so it's listed for
    // completeness but never as a gap. See ObjectReferenceWalker.IsAlwaysAvailable.
    static string DescribeRomDependency(ObjLoc loc) =>
        $"{TypeName(loc.ObjType)}: {loc.Label()} - {AppMessages.Librarian.Shell.RomBankAlwaysAvailable}";

    static string DescribeDependency(ObjLoc loc, string name, bool found, string whereMissing) =>
        found
            ? $"{TypeName(loc.ObjType)}: {loc.Label()} - {(string.IsNullOrEmpty(name) ? "(unnamed)" : name)}"
            : $"{TypeName(loc.ObjType)}: {loc.Label()} - not found {whereMissing}";
}

// Display wrapper for one OpLogEntry, for the history list.
sealed class HistoryRow
{
    public string Description { get; }
    public string Timestamp { get; }
    public bool IsSynced { get; }

    public HistoryRow(OpLogEntry entry)
    {
        Description = entry.Description;
        Timestamp = entry.TimestampUtc.ToLocalTime().ToString("g");
        IsSynced = entry.SyncedAtUtc != null;
    }
}

// Display wrapper for one pending SessionDependencyEntry.
sealed class SessionClipboardRow
{
    public string Description { get; }

    // Type names on BOTH addresses, not bare labels: Program and Combi bank labels are identical
    // strings ("I-C:008" is a valid address in either), so without them a row can't be read at all
    // - the same ambiguity the Unresolved Dependencies dialog was reported for.
    public SessionClipboardRow(SessionDependencyEntry e) =>
        Description = $"{ObjectTypeRegistry.Get(e.MissingRef.ObjType).DisplayName} {e.MissingRef.Label()} - needed by " +
                      $"{ObjectTypeRegistry.Get(e.RequiredBy.ObjType).DisplayName} {e.RequiredBy.Label()} ({e.RefKind})";
}

// Display wrapper for one entry in the "Object Dependencies" panel - see
// LibrarianShellViewModel's ShowLocal/Pcg/MergeObjectDependencies.
sealed class ObjectDependencyRow
{
    public string Description { get; }
    public ObjectDependencyRow(string description) => Description = description;
}
