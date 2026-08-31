using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;   // Dispatcher.Yield - see AutoFillToLibraryAsync on why the priority matters
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

    // Bounds a Sync's PULL half (LibraryPullPipeline.PullAsync, the only phase that loops over
    // every registry bank - up to minutes on a Force Full Sync) to this window's own lifetime.
    // _cache and _sysEx are the MainWindow-owned, app-lifetime singletons (constructed once,
    // reused across every open/close of the Librarian), so without this a window closed
    // mid-pull left its SyncLibraryAsync Task running headless in the background - nothing ever
    // cancelled it, nothing ever awaited it. Reopening and starting another sync while the
    // orphan was still grinding through hundreds of banks meant two concurrent pulls serializing
    // through the same SysExDumpCollector gate, each still allocating for as long as it ran -
    // repeat that a few times (open, start a Full Sync, close before it finishes) and memory
    // climbs with every cycle, exactly because nothing was ever torn down, only orphaned. See
    // Dispose().
    CancellationTokenSource? _syncCts;

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

    // Built once by an explicit user action (Views/LibrarianShellWindow.xaml.cs's "Resolve
    // Sample Bank Names..." handler, which owns the live FTP connection - see ExsOptionIndex's
    // own header comment for why this is never built automatically). Null means "not resolved
    // this session" - every sample-dependency row then shows exactly what it always has
    // (SampleReferenceWalker's own EXs<N>/raw-UUID label), never blocked or delayed by it.
    ExsOptionIndex? _exsIndex;

    // Re-runs whichever Show{Local,Pcg,Merge}ObjectDependencies call built the CURRENTLY shown
    // panel, with the same selection - set by each of those three methods, invoked by
    // ApplyExsOptionIndex once a name index becomes available so already-visible rows pick up
    // resolved names without the user needing to re-click the selection.
    Action? _refreshObjectDependencies;

    public bool ExsNamesResolved => _exsIndex != null;

    public void ApplyExsOptionIndex(ExsOptionIndex index)
    {
        _exsIndex = index;
        _refreshObjectDependencies?.Invoke();
    }

    public LocalLibraryPaneViewModel LocalPane { get; }
    public PcgPaneViewModel PcgPane { get; } = new();
    public MergePaneViewModel MergePane { get; }
    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<SessionClipboardRow> SessionClipboardRows { get; } = new();
    public ObservableCollection<ObjectDependencyRow> ObjectDependencyRows { get; } = new();

    [ObservableProperty] bool isBusy;
    [ObservableProperty] string statusText = "";
    // Drives StatusText's green/neutral styling (LibrarianShellWindow.xaml) - true only for the
    // "pulled fine, nothing local to push" outcome of a Sync (see SyncLibraryAsync). Reset in
    // ClearPaneStatuses so a later Undo/Commit never inherits a stale green from a prior Sync.
    [ObservableProperty] bool statusIsSuccess;
    [ObservableProperty] string? warningText;
    [ObservableProperty] bool forceFullPull;

    // Merge Window -> Local Library duplication policy, seeded from AppSettings in the ctor and
    // mirrored by the Merge Window toolbar's quick toggles (persisted defaults live in Settings >
    // Librarian). ON = always write a FRESH copy, even when byte-identical content already sits
    // somewhere in Local Library ("preserve duplication"); OFF = reuse the existing copy instead
    // of writing a duplicate (see FindExistingLocalCopy). Read at the moment of placement, same
    // as MergePane.ForceOverwrite - flipping one doesn't retroactively change anything placed.
    [ObservableProperty] bool mergePreserveDuplicatePrograms;
    [ObservableProperty] bool mergePreserveDuplicateCombis;

    // Write-through persistence for the two toggles above - LibrarianShellWindow sets this to
    // Storage.SaveSettings so a toolbar flip survives a restart. Left null in a headless
    // self-test: flipping a toggle mid-test must never touch the real settings.json beside the
    // exe (the AppSettings OBJECT is still updated, which is all the placement paths read).
    public Action<AppSettings>? PersistSettings { get; set; }

    partial void OnMergePreserveDuplicateProgramsChanged(bool value)
    {
        _settings.MergePreserveDuplicatePrograms = value;
        PersistSettings?.Invoke(_settings);
    }

    partial void OnMergePreserveDuplicateCombisChanged(bool value)
    {
        _settings.MergePreserveDuplicateCombis = value;
        PersistSettings?.Invoke(_settings);
    }

    // Set once by LibrarianShellWindow's constructor to a WPF MessageBox prompt - keeps this
    // ViewModel free of WPF types (the established split: confirmations are a code-behind
    // concern). Called from PrepareForPushAsync only when ResolvePendingDependencies couldn't
    // clear everything automatically; returns true to proceed anyway, false to cancel the
    // Sync/Commit entirely. Null (e.g. a headless self-test) defaults to proceeding.
    public Func<IReadOnlyList<SessionDependencyEntry>, Task<bool>>? ConfirmContinueWithPendingDependencies { get; set; }

    // Set once by LibrarianShellWindow's constructor to a WPF MessageBox prompt, same split as
    // ConfirmContinueWithPendingDependencies above. Called (via ConfirmDestinationBankAsync) only
    // when the destination bank of a Merge/PCG -> Local placement has never been confirmed
    // against the Kronos. Returns true to place anyway, false to cancel. Null (e.g. a headless
    // self-test) defaults to proceeding - same convention as every other confirm gate here.
    public Func<int, int, Task<bool>>? ConfirmDestinationBankMaybeStale { get; set; }

    public LibrarianShellViewModel(ILibrarianService sysEx, LocalLibraryCache cache, AppSettings settings, string host)
    {
        _sysEx = sysEx;
        _cache = cache;
        _settings = settings;
        _host = host;
        // Field assignment, not the property setters: seeding from settings must not fire the
        // OnChanged write-through above (nothing changed - and a headless self-test has no
        // PersistSettings hook to fire it into anyway).
        mergePreserveDuplicatePrograms = settings.MergePreserveDuplicatePrograms;
        mergePreserveDuplicateCombis   = settings.MergePreserveDuplicateCombis;
        LocalPane = new LocalLibraryPaneViewModel(cache);
        MergePane = new MergePaneViewModel(new MergeCache(BuildMergePersistence(settings)));
        LocalPane.BankTypeOf = BankTypeOf;
        // Assigned AFTER the pane's ctor has already built its tree once, so that first tree has
        // no read-only rows in it - and nothing else refreshes it on the way to being shown
        // (WarmCatalogAsync only flips IsIndexing). Without this rebuild the GM/g banks would
        // stay invisible until some unrelated edit or sync happened to refresh the tree.
        LocalPane.ReadOnlyBankNames = ReadOnlyBankNames;
        LocalPane.RefreshTree();

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
        // Read lazily (a Func, not the value) so a category-name warm-up landing AFTER this pane
        // is wired up still gets picked up the next time BuildSearchText runs (a load, or any
        // other RefreshTree) - never the CategoryNames snapshot from the moment this line ran.
        // NOT re-evaluated per keystroke: a search only filters the haystack each leaf's
        // SearchText already has, it doesn't rebuild it - see PcgPaneViewModel.BuildSearchText.
        PcgPane.GetCategoryNames = () => CategoryNames;

        // The missing-dependency rows are a property of what's STAGED, not of what's selected, so
        // they're rebuilt on every merge mutation (TreeRefreshed fires on pull, remove, clear,
        // placement and undo alike) rather than off the selection-change path. Called once here
        // too: a merge cache restored from disk (MergeCachePersistence) already has its gaps
        // before anything in this window has been clicked.
        MergePane.TreeRefreshed += RebuildObjectDependencies;
        RebuildObjectDependencies();
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

    // ── Read-only factory (GM/g) bank names for the Local pane's browse-only rows ───────────
    // These banks' BODIES are deliberately never pulled (they are ROM: nothing can be written
    // there, and ObjectReferenceWalker.IsAlwaysAvailable already treats references into them as
    // permanently satisfied, so the library needs no copy). Only names are shown, and they come
    // from the name cache the shared name sweep already maintains - NOT from a second sweep of
    // our own. That matters: the instrument rate-limits name dumps to roughly a dozen banks per
    // app session, so a competing sweep would spend the same scarce budget twice and neither
    // would converge.
    //
    // Read through the service (ISysExService.CachedBankNames), never Storage.LoadNames(_host):
    // the cache is keyed by the TRANSPORT's key, which is the host only for TCP - a USB session
    // keys on "usb:<device match>", so a host-keyed read would come back empty on USB and look
    // identical to "no names known yet".
    //
    // The name-cache type code is the func-33 SLOT TYPE (1 = program, 0 = combi), NOT a LibObj
    // constant - the same trap InitObjects documents (LibObj.Program is 0x00, which would select
    // combi here).
    Dictionary<(int ObjType, int Bank), IReadOnlyDictionary<int, string>>? _readOnlyNames;

    IReadOnlyDictionary<int, string> ReadOnlyBankNames(int objType, int bank)
    {
        var byBank = _readOnlyNames ??= new();
        if (byBank.TryGetValue((objType, bank), out var cached)) return cached;
        IReadOnlyDictionary<int, string> names;
        try
        {
            names = _sysEx.CachedBankNames(objType == LibObj.Program ? 1 : 0, bank);
        }
        catch (Exception ex)
        {
            // Browse-only decoration: no names means no GM rows, never a broken Librarian.
            AppLog.Warn($"[librarian] read-only bank names unavailable: {ex.Message}");
            names = EmptyReadOnlyNames;
        }
        return byBank[(objType, bank)] = names;
    }

    static readonly Dictionary<int, string> EmptyReadOnlyNames = new();

    // Drops the memoized snapshot so the next tree refresh re-reads the name cache - the sweep
    // runs outside this window and converges over several sessions, so a long-open Librarian
    // would otherwise keep showing whichever GM banks were known when it opened.
    void InvalidateReadOnlyBankNames() => _readOnlyNames = null;

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
    //
    // Also gated on !IsAutoFilling: AutoFillFromMergeAsync holds ONE undo capture scope open
    // across its per-bank `await pump(...)` yields (deliberately, so the progress bar can paint -
    // see AutoFillToLibraryAsync's own comment), which means the UI stays interactive for the
    // whole sweep. Without this, Undo could fire mid-sweep and roll back the step BEFORE the one
    // currently being written, while the sweep keeps going and pushes its own step on top - a
    // silently half-reverted Auto-Fill.
    public bool CanUndo => _undo.CanUndo && !IsBusy && !IsAutoFilling;

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
        // Cancel BEFORE unhooking undo: an in-flight pull observes this within one bank's worth
        // of network round trips (LibraryPullPipeline.PullAsync's own checks), not "runs to
        // completion headless" - see _syncCts's own comment for why that distinction is the
        // whole point. Deliberately not awaited here - Dispose is synchronous, and the pull
        // unwinds on its own within a few seconds now that it actually observes cancellation;
        // waiting for that here would block the window's Closing handler on network I/O.
        _syncCts?.Cancel();
        _undo.Changed -= OnUndoStackChanged;
        _undo.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task SyncLibraryAsync()
    {
        IsBusy = true; WarningText = null; StatusIsSuccess = false;
        // Same gate WarmCatalogAsync uses at startup, for the same reason: LibraryPullPipeline
        // writes everything in ONE batch at the very end (see RecordPullBaselines), so a visible,
        // interactive tree during the pull would just be showing whatever was there BEFORE this
        // sync - never more misleading than for a brand-new type (Drum Kit/Wave Sequence on a
        // library that never pulled them before shows an empty root that looks final, not "still
        // coming"). Unlike Auto-Fill's IsInputLocked, nothing here writes progressively, so hiding
        // behind the placeholder loses nothing worth watching.
        LocalPane.IsIndexing = true;
        using var cts = new CancellationTokenSource();
        _syncCts = cts;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var (pull, push) = await SyncPipeline.SyncLibraryAsync(
                _sysEx, _cache, _sessionClipboard, ForceFullPull, m => StatusText = m, cts.Token);
            // A pull that succeeded with nothing locally dirty to push back is a complete,
            // successful Sync - not the CHECK/warning ChangesetBuilder's early-return produces for
            // the same state (that's meant for Commit Changes, which has no pull to justify "why
            // did nothing happen"). Only when the pull ALSO flagged zero conflicts: a pull that
            // conflicted something is not "Complete" even though the push still had nothing to
            // write (a conflicted object is deliberately excluded from Writes, not "not dirty").
            bool nothingToPushClean = push.Ok && push.Written == 0 && push.Deleted == 0
                && pull.Conflicts == 0 && push.Error == AppMessages.Librarian.Sync.CheckNothingToPush;
            if (nothingToPushClean)
            {
                StatusText = AppMessages.Librarian.Shell.SyncComplete(ForceFullPull, pull.ObjectsFetched, pull.Conflicts);
                StatusIsSuccess = true;
            }
            else
            {
                StatusText = AppMessages.Librarian.Shell.SyncResult(pull.ObjectsFetched, pull.Conflicts, push.Written, push.Deleted);
                // Surface a CHECK/REFUSE explanation whenever one exists, not just on a hard
                // failure - a "0 written" push (nothing to push, or every change conflicted) still
                // reports Ok: true, and the text is the only thing that explains why.
                if (push.Error != null) WarningText = push.Error;
            }
            // Only actually reaching hardware justifies dropping the undo stack (see
            // ClearAfterSuccessfulPush's own comment) - a push that wrote/deleted nothing must not
            // be treated as "this local state is now safely on hardware."
            if (push.Ok && push.Written + push.Deleted > 0) ClearAfterSuccessfulPush();
            AppLog.Info($"[librarian] sync done: fetched={pull.ObjectsFetched} pushed={push.Written} hasObjects={_cache.HasAnyObjects}");
            InvalidateReadOnlyBankNames();   // the name sweep may have learned new GM/g banks meanwhile
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
            // Cleared before `using var cts` disposes it below (both run on the UI thread, in
            // this order, with nothing able to interleave) - Dispose() calling Cancel() on an
            // already-disposed CTS would throw ObjectDisposedException instead of the no-op it
            // needs to be once a sync has already finished on its own.
            _syncCts = null;
            LocalPane.RefreshTree();
            LocalPane.IsIndexing = false;
            RefreshHistory();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task CommitChangesAsync()
    {
        IsBusy = true; WarningText = null; StatusIsSuccess = false;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var result = await SyncPipeline.CommitChangesAsync(_sysEx, _cache, _sessionClipboard, m => StatusText = m);
            StatusText = result.Ok
                ? AppMessages.Librarian.Shell.CommitResult(result.Written, result.Deleted)
                : AppMessages.Librarian.Shell.CommitFailed;
            // See SyncLibraryAsync's identical comment: a CHECK explanation must surface even on
            // the Ok path, and the undo stack is only ever safe to drop once something actually
            // reached hardware.
            if (result.Error != null) WarningText = result.Error;
            if (result.Ok && result.Written + result.Deleted > 0) ClearAfterSuccessfulPush();
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

    // ── Cross-pane placement staleness gate (Merge Window / Loaded PCG File -> Local Library) ──
    // A destination bank Local Library has never digested against the Kronos - no
    // BankDigestBaselineHex entry at all, or the Kronos didn't answer the last time one was
    // requested (LibraryPullPipeline.NoDigest) - might already differ from what's about to be
    // built on top of it (a front-panel edit, a write from elsewhere). This is deliberately a
    // CHEAP, LOCAL-ONLY, OFFLINE-SAFE check: it only reads what the last Pull/Push already
    // recorded, never a fresh BankDigestAsync round trip. A live per-drop hardware query would
    // be both redundant with ChangesetBuilder's own fresh-digest conflict pre-scan (the real,
    // authoritative gate - this runs again at every Sync/Commit regardless) and actively wrong
    // offline: BankDigestAsync returns null with no transport at all, which is indistinguishable
    // from "hardware answered and differs" - it would warn on every single PCG-only placement
    // with no Kronos ever connected. Reading the persisted baseline sidesteps both problems.
    async Task<bool> ConfirmDestinationBankAsync(int objType, int bank)
    {
        var baseline = _cache.BankDigestBaselineHex();
        bool neverConfirmed = !baseline.TryGetValue((objType, bank), out var hex) || hex == LibraryPullPipeline.NoDigest;
        if (!neverConfirmed) return true;
        return ConfirmDestinationBankMaybeStale == null || await ConfirmDestinationBankMaybeStale(objType, bank);
    }

    // Thin async wrappers around the synchronous placement methods below/above, for the real
    // drag-drop entry points (LibrarianShellWindow's OnLocalDrop/OnMergeToLocalDrop) to call
    // instead. The confirm has to happen BEFORE the synchronous method's own
    // `using var undo = _undo.Begin(...)` scope opens - awaiting a modal dialog inside that scope
    // would let any UI input during the wait fold into the wrong undo step, the same hazard
    // IsInputLocked guards against elsewhere (see AutoFillToLibraryAsync) - so these wrap,
    // never modify, the existing methods. Every self-test that calls the synchronous methods
    // directly bypasses this gate entirely, by design - it exists for the live UI only.
    public async Task<(bool Ok, string? Error)> PlaceFromPcgAsync(ObjLoc pcgLoc, ObjLoc destLoc) =>
        await ConfirmDestinationBankAsync(destLoc.ObjType, destLoc.Bank)
            ? PlaceFromPcg(pcgLoc, destLoc) : (false, AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

    public async Task<(bool Ok, string? Message)> BatchPlaceFromPcgAsync(int objType, IReadOnlyList<ObjLoc> pcgLocs, int destBank) =>
        await ConfirmDestinationBankAsync(objType, destBank)
            ? BatchPlaceFromPcg(objType, pcgLocs, destBank) : (false, AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

    public async Task<(bool Ok, string? Error)> PlaceFromMergeAsync(string mergeContentHash, ObjLoc destLoc) =>
        await ConfirmDestinationBankAsync(destLoc.ObjType, destLoc.Bank)
            ? PlaceFromMerge(mergeContentHash, destLoc) : (false, AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

    public async Task<(bool Ok, string? Message)> PlaceMergeGroupSequentiallyAsync(int objType, int destBank, IReadOnlyList<string> contentHashes, int? destSlot = null) =>
        await ConfirmDestinationBankAsync(objType, destBank)
            ? PlaceMergeGroupSequentially(objType, destBank, contentHashes, destSlot) : (false, AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

    public async Task<(bool Ok, string? Message)> PlaceMergeBankWithTypeChangeAsync(int destBank, IReadOnlyList<string> contentHashes, bool targetIsExi) =>
        await ConfirmDestinationBankAsync(LibObj.Program, destBank)
            ? PlaceMergeBankWithTypeChange(destBank, contentHashes, targetIsExi) : (false, AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

    bool CanRunHardwareOp() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SyncLibraryCommand.NotifyCanExecuteChanged();
        CommitChangesCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();   // see CanUndo - undo must not race a push
        AutoFillToLibraryCommand.NotifyCanExecuteChanged();
    }

    // Set for the duration of an Auto-Fill so the button can show it's working. Distinct from
    // IsBusy, which means "a HARDWARE operation is in flight" - Auto-Fill touches only Local
    // Library, so it must not read as a sync/commit, but it does still need to lock its own
    // button against a second click landing mid-run.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoFillToLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]   // see CanUndo - undo must not race a sweep
    bool isAutoFilling;

    bool CanAutoFill() => !IsBusy && !IsAutoFilling;

    // The placement work can't leave the UI thread - it mutates the ObservableCollections both
    // trees bind to - so "don't freeze" here means "yield to the Dispatcher between banks", which
    // is what the pump below does.
    //
    // The priority matters and is the whole reason a first attempt showed no indicator at all.
    // Task.Yield() posts its continuation through the DispatcherSynchronizationContext at
    // DispatcherPriority.Normal (9), which OUTRANKS Render (7) - so the continuation ran, and the
    // entire sweep completed, before WPF ever painted. IsAutoFilling went true and false again
    // without a single render pass. Yielding at Background (4) sits BELOW Render, so the pending
    // layout/render/animation actually runs before control comes back here.
    [RelayCommand(CanExecute = nameof(CanAutoFill))]
    async Task AutoFillToLibraryAsync()
    {
        IsAutoFilling = true;
        WarningText = null;   // this sweep's own outcome, not the last one's
        // Locks the Local pane's own tree/toolbar for the same span the undo scope below stays
        // open across - a rename/paste/delete landing mid-sweep would otherwise silently fold
        // into the sweep's own undo step instead of getting one of its own (see IsInputLocked's
        // own comment). CanUndo (!IsAutoFilling) covers the Undo button itself.
        LocalPane.IsInputLocked = true;
        try
        {
            // Once up front so the button repaints as busy before any work starts, then again
            // per bank from inside the sweep.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var (_, message) = await AutoFillFromMergeAsync(async status =>
            {
                MergePane.StatusText = status;
                await Dispatcher.Yield(DispatcherPriority.Background);
            });
            MergePane.StatusText = message;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] auto-fill failed: {ex}");
            MergePane.StatusText = AppMessages.Librarian.Shell.OperationFailed(ex.Message);
        }
        finally { IsAutoFilling = false; LocalPane.IsInputLocked = false; }
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
        StatusIsSuccess = false;
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
    // A read-only GM/g row has no body in the library to stage - it is a name from the shared
    // name cache and nothing more (see ReadOnlyBankNames), so staging it would add an empty or
    // phantom Merge entry.
    public void PullLocalIntoMerge(ObjLoc localLoc)
    {
        if (ObjectTypeRegistry.IsReadOnly(localLoc)) return;
        MergePane.PullFromLocal(_cache, localLoc);
    }

    // Same one-gesture-one-step reasoning as PullIntoMerge's list overload above.
    public void PullLocalIntoMerge(IReadOnlyList<ObjLoc> localLocs)
    {
        localLocs = localLocs.Where(l => !ObjectTypeRegistry.IsReadOnly(l)).ToList();
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
    // Duplicate-content guard (same as PlaceFromMerge's single-item path): anything whose
    // content already lives elsewhere in Local Library is repointed there instead of consuming
    // a destination slot for a second copy - never needs a bank, let alone a free slot in one.
    // Honours the per-type preserve-duplication toggles (FindExistingLocalCopy). Shared by
    // PlaceMergeGroupSequentially and AutoFillFromMergeAsync so a duplicate is caught the same
    // way regardless of which one is asking - see AutoFillFromMergeAsync's own comment for the
    // real bug this split fixes (a fully-packed bank used to skip this check entirely for
    // Auto-Fill, stranding pure duplicates in the Merge Window that a manual drag deduped fine).
    (List<MergeEntry> ToPlace, int Deduped) DedupMergeGroup(int objType, IReadOnlyList<string> contentHashes)
    {
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e != null && e!.ObjType == objType).Select(e => e!).ToList();
        var toPlace = new List<MergeEntry>();
        int deduped = 0;
        foreach (var entry in group)
        {
            if (FindExistingLocalCopy(entry) is { } existingLoc)
            {
                MergePane.CommitPlacement(entry.ContentHash, existingLoc);
                deduped++;
            }
            else toPlace.Add(entry);
        }
        return (toPlace, deduped);
    }

    public (bool Ok, string? Message) PlaceMergeGroupSequentially(int objType, int destBank, IReadOnlyList<string> contentHashes, int? destSlot = null)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        // The exact gesture this feature exists for: one accidental whole-bank drag out of the
        // Merge Window is one Ctrl+Z, restoring both the staged entries and every local slot the
        // batch wrote (plus any occupant it overwrote).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedGroup(contentHashes.Count, descriptor.BankLabel(destBank)));
        var (toPlace, dedupedCount) = DedupMergeGroup(objType, contentHashes);
        if (toPlace.Count + dedupedCount == 0) return (false, "nothing to place for this bank's type");
        string dedupNote = dedupedCount > 0 ? AppMessages.Librarian.Shell.ReusedExistingContentCount(dedupedCount) : "";

        if (toPlace.Count == 0) return (true, dedupNote);

        // A drop on a SPECIFIC slot fills contiguously from exactly there - the user pointed at it,
        // so overwriting whatever follows is their explicit intent. A drop on a bank/header is an
        // auto-fill, and must land only on slots free of real content: init placeholders count as
        // free, which makes those holes scattered rather than one contiguous tail, so a plain
        // startSlot+i walk would write over real patches sitting past the first placeholder.
        int startSlot = destSlot ?? FindNextFreeSlot(objType, destBank);
        var targetSlots = destSlot is { } fixedStart
            ? Enumerable.Range(fixedStart, Math.Max(0, Math.Min(toPlace.Count, descriptor.SlotCount(destBank) - fixedStart))).ToList()
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

    // ── Auto-Fill: place EVERYTHING staged into the next free slots ──────────────────────
    // One button for what the Merge Window otherwise costs a drag per type per bank: take every
    // staged Set List / Combi / Program / Drum Kit / Wave Sequence (top-level pulls AND the
    // dependencies that came with them) and fill them into Local Library's next free slots of
    // their own type. Purely LOCAL - it stages, exactly like every other placement in this pane;
    // nothing reaches the instrument until Commit Changes. One Ctrl+Z undoes the whole sweep
    // (LibrarianUndo's nested Begins join the outer step, so the per-bank scopes inside
    // PlaceMergeGroupSequentially fold in).
    //
    // DEPENDENCIES FIRST, and that ordering is the whole reason this isn't just five loops in
    // any order: PlaceMergeGroupSequentially resolves each entry's outgoing references against
    // what is local AT PLACEMENT TIME (MergeCache.ResolveReferencesForPlacement), so a Combi
    // placed after its Programs gets repointed at where they actually landed, while a Combi
    // placed first can only be tracked as pending and repaired later, lazily, at Commit. Both
    // paths are correct - one just leaves nothing to repair. Hence Drum Kits/Wave Sequences
    // (referenced by a Program's oscillator zones, never referrers themselves), then Programs
    // (also referenced by another Program's Drum Track), then Combis, then Set Lists: strictly
    // referenced-before-referrer.
    //
    // Programs are additionally partitioned by WIRE FORMAT, and each partition placed into the
    // next free slots of a bank of ITS OWN format. Without that, a mixed EXi+HD-1 group makes
    // MergeGroupIsExi answer null, and a null format turns FindBankWithFreeSlot's format filter
    // OFF - so it returns the first bank with room whatever its type, and BatchPlace then refuses
    // the whole thing with Move.WrongFormatForBank. Nothing is destroyed by that (the refusal is
    // the gate doing its job), but it aborts the entire Program pass over a bank the user never
    // chose, leaving everything staged. Partitioning means the question never comes up.
    //
    // Note this path never calls BankTypeChangeNeeded - that's the drag handler's escalation
    // (LibrarianShellWindow.OnMergeToLocalDrop), and a func-0x7C bank reformat is not something
    // "fill the free slots" should ever reach.
    // `pump`, when supplied, is awaited before each bank's placement: the UI passes one that
    // reports the message and then yields to the Dispatcher, which is what lets the button's
    // progress indicator paint and animate instead of the whole sweep landing as one frozen
    // block. Pass null (the self-tests do) and NOTHING suspends - every await completes
    // synchronously, so this stays safe to drive from App.xaml.cs's GetAwaiter().GetResult() on
    // the UI thread, which a genuinely-async version would deadlock.
    public async Task<(bool Ok, string Message)> AutoFillFromMergeAsync(Func<string, Task>? pump = null)
    {
        // EntriesInDisplayOrder, NEVER raw Entries: MergeCache's backing Dictionary recycles
        // the array slots this sweep's own CommitPlacement removals freed, LIFO, so a re-copied
        // PCG pulled in afterwards enumerates SCRAMBLED (a whole-bank re-copy comes out exactly
        // backwards) - and anything enumerated here is the order slots are filled in. Display
        // order (source bank, then source slot) is the order the Merge Window itself shows, so
        // every Auto-Fill lands in the same order the user sees, first run or fifth.
        var staged = MergePane.EntriesInDisplayOrder;
        if (staged.Count == 0) return (false, AppMessages.Librarian.Merge.AutoFillNothingStaged);

        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoAutoFilled(staged.Count));

        int startCount = staged.Count;
        string? refusal = null, refusedWhat = null;
        var noRoom = new List<(string What, int Count)>();

        foreach (var objType in new[] { LibObj.DrumKit, LibObj.WaveSequence, LibObj.Program, LibObj.Combi, LibObj.SetList })
        {
            var ofType = staged.Where(e => e.ObjType == objType).ToList();
            if (ofType.Count == 0) continue;

            // Programs: one run per wire format. Everything else is a single run (format is not a
            // concept for Combis/Set Lists, so the lone partition carries a null "isExi").
            var partitions = objType == LibObj.Program
                ? ofType.GroupBy(e => e.Body.Length == ProgramFormatConverter.WireSizeExi)
                        .Select(g => ((bool?)g.Key, g.Select(e => e.ContentHash).ToList()))
                        .ToList()
                : new List<(bool?, List<string>)> { (null, ofType.Select(e => e.ContentHash).ToList()) };

            foreach (var (isExi, hashes) in partitions)
            {
                // Dedup FIRST, before ever asking for a free bank: a pure duplicate is repointed
                // to its existing address and needs no slot at all, so gating this behind "is
                // there room somewhere" (as PlaceMergeGroupSequentially alone would, called only
                // from inside the loop below) stranded every duplicate in a fully-packed bank -
                // the exact bug report this fixes. A manual drag-drop already deduped fine
                // (PlaceFromMerge checks unconditionally); Auto-Fill just never got there.
                var (toPlace, _) = DedupMergeGroup(objType, hashes);
                var remaining = toPlace.Select(e => e.ContentHash).ToList();
                while (remaining.Count > 0)
                {
                    // Re-asked every pass: the previous pass filled that bank up, so the next one
                    // has to resolve to a different bank (or to null = nothing of this format has
                    // room left, and the rest stays staged rather than being lost).
                    if (LocalEditOps.FindBankWithFreeSlot(_cache, objType, isExi, BankTypeOf) is not { } bank)
                    {
                        // Nothing of this kind has room anywhere. This used to end the pass
                        // silently, leaving the items staged with only a clause at the end of the
                        // Merge pane's status line to say why - which is not where anyone looks
                        // after clicking Auto-Fill. Collected per type/format so the warning can
                        // name exactly what has nowhere to go (an EXi Program can only ever land
                        // in an EXi bank, so "Programs are full" would be the wrong thing to say
                        // when the HD-1 banks are half empty).
                        noRoom.Add((DescribeAutoFillKind(objType, isExi), remaining.Count));
                        break;
                    }

                    // One yield per bank - the finest granularity available without splitting a
                    // BatchPlace, which has to stay atomic. Placing a full 128-slot bank is
                    // therefore still one un-interruptible step; this makes the sweep BETWEEN
                    // banks visible, it doesn't make any single bank's write interruptible.
                    var descriptor = ObjectTypeRegistry.Get(objType);
                    if (pump != null)
                        await pump(AppMessages.Librarian.Merge.AutoFillProgress(
                            descriptor.DisplayName, descriptor.BankLabel(bank), remaining.Count));

                    int before = remaining.Count;
                    var (ok, message) = PlaceMergeGroupSequentially(objType, bank, remaining);
                    // Survivors, straight from the Merge Window itself rather than from the count
                    // in `message`: PlaceMergeGroupSequentially both PLACES and DEDUPES (content
                    // already elsewhere locally is reused, not written), and CommitPlacement
                    // removes an entry either way. "Still staged" is the only definition of
                    // "still needs a slot" that covers both.
                    remaining = remaining.Where(h => MergePane.TryGet(h) != null).ToList();

                    if (!ok)
                    {
                        // A REFUSE (orphan gate, wrong format, ...) is reported, not swallowed,
                        // and not retried against the same bank - but the other types still get
                        // their turn, so one bad group can't strand the rest.
                        refusal ??= message;
                        refusedWhat ??= ObjectTypeRegistry.Get(objType).DisplayName;
                        break;
                    }
                    // Progress guard: a bank that accepted nothing would otherwise be chosen
                    // again forever, since FindBankWithFreeSlot would keep returning it. Reported
                    // through the same warning as a genuinely full library - a different cause,
                    // but from the user's side the identical outcome (these items are still
                    // staged and nothing said so).
                    if (remaining.Count >= before)
                    {
                        noRoom.Add((DescribeAutoFillKind(objType, isExi), remaining.Count));
                        break;
                    }
                }
            }
        }

        LocalPane.RefreshTree();
        int stillStaged = MergePane.Entries.Count;
        int resolved = startCount - stillStaged;

        // The banner, not a pop-up: Auto-Fill is a sweep the user watches, and a modal that has
        // to be dismissed before the result can even be looked at is the wrong shape for "some of
        // this didn't fit". Left set until dismissed or until the next Sync/Commit clears it.
        if (noRoom.Count > 0) WarningText = AppMessages.Librarian.Merge.AutoFillNoRoom(noRoom);

        if (refusal != null)
            return (false, AppMessages.Librarian.Merge.AutoFillRefused(resolved, refusedWhat ?? "a staged group", refusal));

        return (resolved > 0, AppMessages.Librarian.Merge.AutoFillResult(resolved, stillStaged));
    }

    // What kind of thing ran out of room, as the warning names it. Programs carry their wire
    // format because that's what actually constrains them - an EXi Program is only ever placeable
    // in an EXi bank (see LocalEditOps.FindBankWithFreeSlot's own isExi argument).
    static string DescribeAutoFillKind(int objType, bool? isExi)
    {
        string name = ObjectTypeRegistry.Get(objType).DisplayName;
        return isExi is { } exi ? $"{(exi ? "EXi" : "HD-1")} {name}" : name;
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
        if (group.Count > descriptor.SlotCount(destBank)) group = group.Take(descriptor.SlotCount(destBank)).ToList();

        // Replace the destination bank - the 0x7C erases everything in it on hardware anyway.
        for (int n = 0; n < descriptor.SlotCount(destBank); n++)
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

    // The per-type "preserve duplication" policy (Settings > Librarian; Merge Window toolbar
    // quick toggles). Set Lists have no toggle and always take the duplicate-reuse path below.
    bool PreserveDuplicationFor(int objType) => objType switch
    {
        LibObj.Program => MergePreserveDuplicatePrograms,
        LibObj.Combi   => MergePreserveDuplicateCombis,
        _              => false,
    };

    // The duplicate-content guard shared by PlaceFromMerge and PlaceMergeGroupSequentially:
    // returns the Local Library location whose content already IS this entry (so the placement
    // should repoint at it instead of writing a second copy), or null when the entry must be
    // written - because it was staged FROM Local (its placement elsewhere is the whole point),
    // because the user asked for duplication to be preserved for its type, or because nothing
    // byte-identical exists anywhere locally.
    //
    // A Combi is compared TWICE when duplication isn't being preserved. Its staged body still
    // holds the SOURCE (PCG) addresses for its timbres, so the raw content hash almost never
    // matches a previously-placed copy - that copy was repointed at where its Programs actually
    // landed before being written. Comparing the RESOLVED body (references repointed at local
    // reality, exactly as placement itself would write it) is what makes "scan duplicates and
    // reuse" actually fire for the re-copied-PCG case: the second copy resolves to the very
    // same Program destinations the first copy was written with, so the bodies hash equal.
    // Programs have no outgoing references (resolved == raw), and Set Lists deliberately keep
    // the historical raw-hash-only comparison.
    ObjLoc? FindExistingLocalCopy(MergeEntry entry)
    {
        if (WasStagedFromLocal(entry)) return null;
        if (PreserveDuplicationFor(entry.ObjType)) return null;
        if (_cache.FindByContentHash(entry.ObjType, entry.ContentHash) is { } raw) return raw;
        // A Program can carry RefSites too now (Drum Track; HD-1 Wave Sequence/Drum Kit
        // oscillator zones) - same reasoning as Combi: its RAW content may differ from an
        // already-local twin only in an unresolved reference address, which resolving before
        // re-hashing corrects for.
        if (entry.ObjType is LibObj.Combi or LibObj.Program && entry.RefSites.Count > 0)
        {
            var (resolved, _) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);
            string resolvedHash = LocalObjectStore.ComputeHash(resolved);
            if (resolvedHash != entry.ContentHash &&
                _cache.FindByContentHash(entry.ObjType, resolvedHash) is { } found)
                return found;
        }
        return null;
    }

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
        // slot, not a duplicate elsewhere - or when the user preserves duplicates for this
        // type (FindExistingLocalCopy's own policy check).
        if (FindExistingLocalCopy(entry) is { } existingLoc && !existingLoc.Equals(destLoc))
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
                new ObjectDump(objType, destBank, placed[i].Slot, placed[i].Entry.Version, bodies[i]), placed[i].Entry.Origin.Label()));

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
    // of what the CURRENTLY SELECTED Program(s)/Combi(s)/Set List(s) reference, transitively (a
    // Set List's Combis and their Programs; a Program's Drum Track and, for HD-1, its Wave
    // Sequence/Drum Kit oscillator zones). Distinct from _sessionClipboard above: that tracks a
    // placement's references that still need pushing; this is just "what does this object need,"
    // independent of placement history.

    public void ShowLocalObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        _refreshObjectDependencies = () => ShowLocalObjectDependencies(selectedLocs);
        var seen = new HashSet<ObjLoc>();
        var sampleSeen = new HashSet<string>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var loc in selectedLocs)
            CollectLocalDeps(loc, seen, sampleSeen, rows);
        ReplaceObjectDependencies(rows);
    }

    // Both walks over ONE object's body. Pure functions of (objType, body), which is what makes
    // the memo below safe.
    sealed record ObjectWalk(
        List<(string RefKind, int Site, ObjLoc Ref)> Refs,
        List<SampleReferenceWalker.SampleDependencyRow> SampleRows);

    // Populating this panel walks the selection's dependencies TRANSITIVELY, and every step used
    // to re-read the referenced body off the CAS store - two filesystem round trips each
    // (LocalObjectStore.TryGet), over a DataDir that is routinely an SMB share. A Set List
    // selection is 128 slots x 16 timbres of that, so clicking one object took seconds, and a
    // plain click runs the walk twice anyway (PaneSelection.HandleMouseUpWithoutDrag fires
    // SelectionChanged unconditionally).
    //
    // Keyed on the body's CONTENT hash, read from the index with no blob read at all
    // (LocalLibraryCache.GetContentHash), so this is self-invalidating rather than something
    // that has to be flushed: any edit rewrites the body, which changes the hash, which misses.
    // Only the object actually edited is invalidated - the rest of the graph stays warm. Two
    // different slots sharing a hash (every INIT Program has an identical body) SHOULD share an
    // entry: the walkers return targets derived from the body, never the owner's own address.
    //
    // Deliberately memoizes ONLY the body-derived walk. Whether each referenced object is
    // present (_cache.Exists) and what it is called (_cache.GetDisplayName) stay live on every
    // population - both are index-only lookups, and they are exactly what must stay fresh so a
    // dependency that gets placed flips from missing to found with no flush anywhere.
    readonly Dictionary<(int ObjType, string Hash), ObjectWalk> _walkCache = new();

    // Test-only visibility into whether the memo is actually being HIT - a correctness test
    // passes identically when every lookup silently misses (see LibrarianDependencyCacheSelfTests).
    internal int WalkCacheMisses { get; private set; }

    ObjectWalk? LocalWalk(ObjLoc loc)
    {
        string? hash = _cache.GetContentHash(loc.ObjType, loc.Bank, loc.Number);
        // NoBaselineSentinel ("") is not an identity - keying on it would serve one object's
        // dependencies for every other local-only object of the same type.
        bool cacheable = !string.IsNullOrEmpty(hash);
        if (cacheable && _walkCache.TryGetValue((loc.ObjType, hash!), out var hit)) return hit;

        WalkCacheMisses++;
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return null;
        var walk = new ObjectWalk(
            ObjectReferenceWalker.Walk(loc.ObjType, body).ToList(),
            SampleReferenceWalker.Walk(loc.ObjType, body).ToList());
        if (cacheable) _walkCache[(loc.ObjType, hash!)] = walk;
        return walk;
    }

    // `missing` is optional: the selection-driven panel only wants display rows, while
    // InspectDependencies wants the gaps from the SAME walk rather than a second one.
    void CollectLocalDeps(ObjLoc loc, HashSet<ObjLoc> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows,
                          List<MissingDependency>? missing = null)
    {
        if (LocalWalk(loc) is not { } walk) return;
        string parentName = _cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number);
        AddSampleRows(walk.SampleRows, DescribeParent(loc, parentName, "sample"), sampleSeen, rows);
        foreach (var (refKind, site, refLoc) in walk.Refs)
        {
            if (!seen.Add(refLoc)) continue;
            string parentInfo = DescribeParent(loc, parentName, refKind);
            // A ROM (GM/g) reference is shown, but never as missing - it resolves on the
            // instrument no matter what the local library holds (ObjectReferenceWalker.
            // IsAlwaysAvailable), and nothing can be pulled or placed to "fix" it.
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc), parentInfo));
                continue;
            }
            // Cached at write time (LocalIndexEntry.DisplayName) - never a blob read, same
            // discipline as the tree's own labels (LocalLibraryPaneViewModel.MakeLeafNode).
            // AvailableLocally, not bare Exists: an object already marked pending-delete still has
            // an index entry, so Exists reported a Combi's timbre as satisfied by a Program the
            // very next Commit removes. Now consistent with the PCG/Merge rows, which use the same
            // test to decide whether a reference is really covered.
            bool found = AvailableLocally(refLoc);
            string name = found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "";
            // An INIT Program satisfies the reference technically but is a placeholder, not the
            // sound the referrer expects - worth saying so, since it's also the case that places
            // freely (see ProgramBody.IsInit and BatchLibrarian.PlanBatchMove's orphan gate).
            rows.Add(new ObjectDependencyRow(
                found && refLoc.ObjType == LibObj.Program && ProgramBody.IsInitName(name)
                    ? $"{TypeName(refLoc.ObjType)}: {refLoc.Label()} - {name} {AppMessages.Librarian.Shell.InitPlaceholderSuffix}"
                    : DescribeDependency(refLoc, name, found, "locally"),
                parentInfo,
                // "(references nothing)" - ObjectInfoDialog's default for a null callback - is the
                // wrong answer for a missing object: nothing local HAS it, so what it references
                // is unknowable, not empty.
                found ? () => DescribeLocalChildren(refLoc)
                      : () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
                // A "not found locally" row IS a missing dependency - it was already saying so in
                // words while rendering in the same color as every satisfied row beside it, which
                // read as "noted, and fine". Flagging it red also makes it right-clickable to
                // search a .pcg, the same recovery every other missing row now offers.
                missingRef: found ? null : refLoc));
            // A ROM reference is listed but is never a gap (IsAlwaysAvailable - it resolves on the
            // instrument and can't be searched for), so it never enters `missing`.
            if (!found && missing != null && !ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
                missing.Add(new MissingDependency(refLoc, refKind, site, loc));
            if (found) CollectLocalDeps(refLoc, seen, sampleSeen, rows, missing);
        }
    }

    // SAMPLE dependencies (Sampling Mode/RAM, EXs, User Sample Banks) for one object's own
    // body - shared by the Local/PCG/Merge collectors below. Display-only rows: unlike an
    // object reference, a sample bank can never be "found"/resolved locally, pulled, or
    // repointed (SampleReferenceWalker's own header comment has the full reasoning), so there
    // is no recursion, no "missing" tracking, and no child-describe callback - just a flat
    // row per distinct bank this object references, deduped against `sampleSeen` the same way
    // `seen` dedupes object refs across one panel population.
    void AddSampleRows(IEnumerable<SampleReferenceWalker.SampleDependencyRow> walked, string parentInfo, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        foreach (var row in walked)
        {
            if (!sampleSeen.Add(row.Key)) continue;
            rows.Add(new ObjectDependencyRow(ResolveSampleDescription(row), parentInfo, sampleBucket: row.Bucket));
        }
    }

    // Appends a resolved friendly name from _exsIndex when one's available - never changes the
    // row's Bucket/color (that's fixed at classification time from the PCG bytes alone, so
    // coloring stays deterministic and identical whether or not the user ever resolves names -
    // see ExsOptionIndex's own header comment on why a raw-UUID bank found there isn't
    // reclassified from UserOrThirdParty to Exs even when it turns out to be an EXs127+ pack).
    // Falls back to the row's own description untouched when no index is loaded yet, or the
    // bank isn't in it (not installed on this instrument, or genuinely unresolvable - e.g. the
    // live Sampling Mode/RAM bucket, which SampleReferenceWalker's own comment explains has no
    // persistent identity to look up at all).
    string ResolveSampleDescription(SampleReferenceWalker.SampleDependencyRow row)
    {
        if (_exsIndex == null) return row.Description;
        string? name = row.Bucket switch
        {
            SampleReferenceWalker.BankBucket.Exs when ParseExsNumber(row.Key) is { } n => _exsIndex.NameForExsNumber(n),
            SampleReferenceWalker.BankBucket.UserOrThirdParty => _exsIndex.NameForUuidHex(row.Key),
            _ => null,
        };
        return name == null ? row.Description : $"{row.Description} - {name}";
    }

    // Inverse of SampleReferenceWalker.ClassifyLegacyUuid's own "exs{exsNumber}" key format.
    static int? ParseExsNumber(string key) =>
        key.StartsWith("exs", StringComparison.Ordinal) && int.TryParse(key.AsSpan(3), out int n) ? n : null;

    public void ShowPcgObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        _refreshObjectDependencies = () => ShowPcgObjectDependencies(selectedLocs);
        var rows = new List<ObjectDependencyRow>();
        if (PcgPane.View is { } view)
        {
            var seen = new HashSet<ObjLoc>();
            var sampleSeen = new HashSet<string>();
            foreach (var loc in selectedLocs)
                CollectPcgDeps(view, loc, seen, sampleSeen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    // The PCG walk had no memo of its own, unlike LocalWalk's _walkCache: every population
    // re-decoded every referenced body through WireBodyFromPcgEntry, and a Set List selection is
    // 128 slots x 16 timbres of that, on the UI thread. Tolerable off a click (the user asked for
    // it and waits once), but ApplyExsOptionIndex re-runs the SAME walk to pick up resolved names
    // - which is what made "Resolve Sample Bank Names..." lock the window the moment its FTP work
    // returned, long after the part that talks to the instrument was done.
    //
    // A loaded PcgLibraryView is immutable, so keying the memo on the instance makes it
    // self-invalidating: loading a different file is a different instance and misses everything.
    readonly Dictionary<(int ObjType, int Bank, int Number), byte[]?> _pcgBodyCache = new();
    PcgLibraryView? _pcgBodyCacheView;

    byte[]? PcgBody(PcgLibraryView view, ObjLoc loc)
    {
        if (!ReferenceEquals(view, _pcgBodyCacheView))
        {
            _pcgBodyCache.Clear();
            _pcgBodyCacheView = view;
        }
        var key = (loc.ObjType, loc.Bank, loc.Number);
        if (_pcgBodyCache.TryGetValue(key, out var hit)) return hit;
        var entry = view.Get(loc);
        return _pcgBodyCache[key] = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
    }

    void CollectPcgDeps(PcgLibraryView view, ObjLoc loc, HashSet<ObjLoc> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        var entry = view.Get(loc);
        if (entry == null || PcgBody(view, loc) is not { } body) return;
        AddSampleRows(SampleReferenceWalker.Walk(loc.ObjType, body), DescribeParent(loc, entry!.Name, "sample"), sampleSeen, rows);
        foreach (var (refKind, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seen.Add(refLoc)) continue;
            string parentInfo = DescribeParent(loc, entry!.Name, refKind);
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))   // see CollectLocalDeps
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc), parentInfo));
                continue;
            }
            var depEntry = view.Get(refLoc);
            if (depEntry != null)
            {
                rows.Add(new ObjectDependencyRow(DescribeDependency(refLoc, depEntry.Name, true, "in this PCG"),
                    parentInfo, () => DescribePcgChildren(view, refLoc)));
                CollectPcgDeps(view, refLoc, seen, sampleSeen, rows);
                continue;
            }
            // Absent from the PCG is NOT the same as missing: the reference is an ADDRESS, so
            // whatever Local Library already holds there is what the Kronos will play. Checking
            // that before calling it a gap is the difference between "you need to find this" and
            // "you already have this" - see DescribeAvailableLocally.
            rows.Add(DescribeGapOrLocal(refLoc, parentInfo, "in this PCG"));
        }
    }

    public void ShowMergeObjectDependencies(IReadOnlyList<string> selectedHashes)
    {
        _refreshObjectDependencies = () => ShowMergeObjectDependencies(selectedHashes);
        var seen = new HashSet<string>();
        var sampleSeen = new HashSet<string>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var hash in selectedHashes)
        {
            var entry = MergePane.TryGet(hash);
            if (entry != null) CollectMergeDeps(entry, seen, sampleSeen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    // Merge entries are keyed by content hash, not address - RefSites already carry the
    // resolved-dependency lookup (or the original PCG address for a still-unresolved gap), so
    // this needs none of ObjectReferenceWalker's own byte-decoding, unlike the Local/PCG paths.
    // SAMPLE refs are the one thing still read straight off entry.Body (RefSites only carries
    // OBJECT references - see MergeEntry's own field comments) via the shared AddSampleRows.
    void CollectMergeDeps(MergeEntry entry, HashSet<string> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        string parentName = string.IsNullOrEmpty(entry.DisplayName) ? "(unnamed)" : entry.DisplayName;
        AddSampleRows(SampleReferenceWalker.Walk(entry.ObjType, entry.Body), $"{TypeName(entry.ObjType)}: {parentName} (via sample, staged - not yet placed)", sampleSeen, rows);
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string key = site.ResolvedContentHash ?? site.TargetLoc.Label();
            if (!seen.Add(key)) continue;
            string parentInfo = $"{TypeName(entry.ObjType)}: {parentName} (via {site.RefKind}, staged - not yet placed)";
            // No real address yet (Merge Window is bag-based, not addressed) - name is all
            // there is to show until it's actually placed.
            if (dep != null)
            {
                rows.Add(new ObjectDependencyRow(
                    $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}",
                    parentInfo, () => DescribeMergeChildren(dep)));
                CollectMergeDeps(dep, seen, sampleSeen, rows);
                continue;
            }
            rows.Add(DescribeGapOrLocal(site.TargetLoc, parentInfo, "in any loaded PCG"));
        }
    }

    // Empties the SELECTION half of the panel only - the staged-gap rows above it belong to the
    // Merge Window, not to whatever happens to be selected, and must not disappear when the user
    // clicks empty space (that list is the pre-Commit checklist; a checklist that vanishes on a
    // stray click is worse than none).
    // One reference that its own source (a loaded PCG, the Merge Window) doesn't satisfy - which
    // is only half the question. The reference is an ADDRESS, so if Local Library already holds
    // something there, the Kronos resolves it on load and there is nothing for the user to go
    // find: that gets an ordinary row naming what's actually there, not a red one. Only an
    // address nothing local covers is a real gap.
    // Present locally AND still going to be there after the next Commit. _cache.Exists alone
    // counts an object already marked pending-delete, so reassuring the user that a dependency is
    // "already in your Local Library" off Exists would be pointing at something Commit is about to
    // remove. Only used for the green-light direction - saying something IS satisfied is the claim
    // that has to be strict.
    bool AvailableLocally(ObjLoc loc) =>
        _cache.Exists(loc.ObjType, loc.Bank, loc.Number) && !_cache.IsPendingDelete(loc.ObjType, loc.Bank, loc.Number);

    ObjectDependencyRow DescribeGapOrLocal(ObjLoc refLoc, string parentInfo, string whereMissing)
    {
        if (AvailableLocally(refLoc))
        {
            string localName = _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number);
            return new ObjectDependencyRow(
                $"{TypeName(refLoc.ObjType)}: {refLoc.Label()} - {(string.IsNullOrEmpty(localName) ? "(unnamed)" : localName)} " +
                $"{AppMessages.Librarian.Shell.ResolvedFromLocalLibrary(whereMissing)}",
                parentInfo, () => DescribeLocalChildren(refLoc));
        }
        return new ObjectDependencyRow(
            DescribeDependency(refLoc, "", false, whereMissing), parentInfo,
            () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
            missingRef: refLoc);
    }

    public void ClearObjectDependencies()
    {
        _selectionDependencyRows.Clear();
        RebuildObjectDependencies();
    }

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
        CollectLocalDeps(loc, new HashSet<ObjLoc>(), new HashSet<string>(), rows, missing);
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
    // Scans ONE .pcg for EVERY address in `missing`, not just the one the user right-clicked.
    // A .pcg that holds one of a Combi's missing Programs very often holds the rest of them too
    // (they were saved together), so checking only the clicked row made the user re-pick the same
    // file once per gap. Everything present is staged in a single undo scope; the caller drops the
    // rows for what came back and leaves the rest of the list for another file.
    public (List<ObjLoc> Found, string? Error) ScanPcgForMissingObjects(
        IReadOnlyList<ObjLoc> missing, byte[] pcgBytes, string fileName)
    {
        var found = new List<ObjLoc>();
        if (missing.Count == 0) return (found, null);

        var file = PcgFile.Open(pcgBytes);
        if (file == null) return (found, AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));

        var view = new PcgLibraryView(file);
        foreach (var loc in missing)
            if (view.Get(loc) != null) found.Add(loc);
        if (found.Count == 0) return (found, null);

        // One undo step for the whole sweep - the user made one decision (this file), so one
        // Ctrl+Z should take all of it back, not unwind object by object.
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoScannedPcgForDependencies(fileName));
        foreach (var loc in found)
            MergePane.PullFromPcg(view, fileName, loc);
        return (found, null);
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

    // ── Missing staged dependencies (the panel's red section) ────────────────────────────────
    // The panel shows two things stacked: everything the Merge Window still MISSES (red, always
    // first, independent of any selection), then what the current selection references. Keeping
    // the selection half cached rather than re-derived is what lets a merge mutation refresh the
    // red rows for free: ScanPcgForMissingObjects pulls once per object it finds, so re-running
    // the selection's own transitive walk on each of those would be dozens of walks over a
    // DataDir that is routinely an SMB share (see _walkCache's comment).
    readonly List<ObjectDependencyRow> _selectionDependencyRows = new();

    void ReplaceObjectDependencies(List<ObjectDependencyRow> rows)
    {
        _selectionDependencyRows.Clear();
        _selectionDependencyRows.AddRange(rows);
        RebuildObjectDependencies();
    }

    void RebuildObjectDependencies()
    {
        ObjectDependencyRows.Clear();
        foreach (var r in BuildMergeGapRows()) ObjectDependencyRows.Add(r);
        foreach (var r in _selectionDependencyRows) ObjectDependencyRows.Add(r);
    }

    // One row per missing ADDRESS, not per reference site: a Set List whose Combis all want the
    // same absent Program would otherwise repeat that Program once per timbre and bury the
    // selection's own rows underneath. Reuses the Unresolved Dependencies dialog's own row format
    // so the same gap reads identically wherever the user meets it.
    List<ObjectDependencyRow> BuildMergeGapRows()
    {
        var rows = new List<ObjectDependencyRow>();
        foreach (var group in MergePane.UnresolvedDependencies
                     .GroupBy(s => s.TargetLoc)
                     // Local Library already covers this address, so the reference resolves on the
                     // instrument and there is nothing to go find - the gap is only in the Merge
                     // Window's own pull source, which is not the user's problem. Filtering here
                     // rather than styling it differently keeps the red section to exactly the
                     // things that still need an action.
                     .Where(g => !AvailableLocally(g.Key))
                     .OrderBy(g => g.Key.ObjType).ThenBy(g => g.Key.Bank).ThenBy(g => g.Key.Number))
        {
            var referrers = group.Select(DescribeGapReferrer).ToList();
            rows.Add(new ObjectDependencyRow(
                // Safe to name now: the local-library case is filtered out above, so
                // DescribeMissingName can only be answering from a loaded PCG - i.e. "this is
                // sitting in the PCG you have open, pull it in", which is exactly the useful hint.
                AppMessages.UnresolvedDependencies.Row(
                    TypeName(group.Key.ObjType), group.Key.Label(), DescribeMissingName(group.Key), group.Count()),
                string.Join("; ", referrers),
                // Nothing staged HAS this object, so its own outgoing references are genuinely
                // unknowable until it's found - say that rather than letting the "More Info"
                // popup's default read as "it references nothing".
                () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
                missingRef: group.Key));
        }
        return rows;
    }

    // Which staged object needs this gap, and through which site - the merge cache is keyed by
    // content, so the referrer is named, never addressed.
    string DescribeGapReferrer(MergeRefSite site)
    {
        var owner = MergePane.TryGet(site.OwnerHash);
        string name = owner == null || string.IsNullOrEmpty(owner.DisplayName) ? "(unnamed)" : owner.DisplayName;
        return $"{TypeName(owner?.ObjType ?? site.TargetLoc.ObjType)}: {name} (via {site.RefKind})";
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

    // "Referenced by ..." line for the "More Info" popup - the object that pulled this row's
    // own object into the walk, and which of its reference sites (timbre 4, drum track, osc1
    // zone2, slot 9, ...) did it.
    static string DescribeParent(ObjLoc parentLoc, string parentName, string refKind) =>
        $"{TypeName(parentLoc.ObjType)}: {parentLoc.Label()} - {(string.IsNullOrEmpty(parentName) ? "(unnamed)" : parentName)} (via {refKind})";

    // One level of a LOCAL object's own outgoing references, each annotated with which
    // reference site it came from - the "More Info" popup's "References:" section. Deliberately
    // NOT the transitive walk CollectLocalDeps does for the panel itself (that's for a whole
    // selection's dependency list; this is "what does just THIS one object point at").
    IReadOnlyList<string> DescribeLocalChildren(ObjLoc loc)
    {
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(loc.ObjType, body)) lines.Add(ResolveSampleDescription(row));
        foreach (var (refKind, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            bool found = AvailableLocally(refLoc);   // see CollectLocalDeps on why not bare Exists
            string desc = ObjectReferenceWalker.IsAlwaysAvailable(refLoc)
                ? DescribeRomDependency(refLoc)
                : DescribeDependency(refLoc,
                    found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "", found, "locally");
            lines.Add($"{desc} (via {refKind})");
        }
        return lines;
    }

    // Same as DescribeLocalChildren, sourced from a loaded PCG instead of Local Library.
    IReadOnlyList<string> DescribePcgChildren(PcgLibraryView view, ObjLoc loc)
    {
        var entry = view.Get(loc);
        var body = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
        if (body == null) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(loc.ObjType, body)) lines.Add(ResolveSampleDescription(row));
        foreach (var (refKind, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            var depEntry = view.Get(refLoc);
            string desc = ObjectReferenceWalker.IsAlwaysAvailable(refLoc)
                ? DescribeRomDependency(refLoc)
                : DescribeDependency(refLoc, depEntry?.Name ?? "", depEntry != null, "in this PCG");
            lines.Add($"{desc} (via {refKind})");
        }
        return lines;
    }

    // Same idea for a staged Merge Window entry - sourced from its own precomputed RefSites
    // (see CollectMergeDeps's own comment for why that needs none of ObjectReferenceWalker's
    // byte-decoding, unlike the Local/PCG variants above) plus SampleReferenceWalker straight
    // off entry.Body for the same reason CollectMergeDeps does.
    IReadOnlyList<string> DescribeMergeChildren(MergeEntry entry)
    {
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(entry.ObjType, entry.Body)) lines.Add(ResolveSampleDescription(row));
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string desc = dep != null
                ? $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}"
                : $"{TypeName(site.TargetLoc.ObjType)}: {site.TargetLoc.Label()} - not found in any loaded PCG";
            lines.Add($"{desc} (via {site.RefKind})");
        }
        return lines;
    }
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
// LibrarianShellViewModel's ShowLocal/Pcg/MergeObjectDependencies. ParentInfo/DescribeChildren
// back the "More Info..." popup (double-click or right-click a row) - who referenced this
// object, and what it in turn references one level out. DescribeChildren is LAZY (a fresh
// re-walk, not captured at panel-build time) so it reflects any edit made since the panel was
// last populated, and so building the (possibly long) panel doesn't pay for detail nobody asks
// to see.
sealed class ObjectDependencyRow
{
    public string Description { get; }
    public string ParentInfo { get; }
    public Func<IReadOnlyList<string>> DescribeChildren { get; }

    // Set only for a SAMPLE dependency row (SampleReferenceWalker's own bucket) - null for a
    // normal object-reference/ROM row, which the View colors with the panel's default text
    // color. Drives the per-bucket text color in Views/LibrarianShellWindow.xaml's Object
    // Dependencies list (Yellow=EXs, two blues=user-bank/live-RAM, orange=EXi external) so a
    // sample dependency reads as a distinct kind of row at a glance, not just by its text.
    public SampleReferenceWalker.BankBucket? SampleBucket { get; }

    // Set only on a MISSING-dependency row (LibrarianShellViewModel.BuildMergeGapRows) - the
    // address nothing staged can satisfy. Drives the row's red styling and enables its
    // "Search a PCG for this object..." right-click; null on every ordinary dependency row.
    public ObjLoc? MissingRef { get; }
    public bool IsMissing => MissingRef != null;

    public ObjectDependencyRow(string description, string parentInfo = "", Func<IReadOnlyList<string>>? describeChildren = null,
                                SampleReferenceWalker.BankBucket? sampleBucket = null, ObjLoc? missingRef = null)
    {
        Description = description;
        ParentInfo = parentInfo;
        DescribeChildren = describeChildren ?? (() => Array.Empty<string>());
        SampleBucket = sampleBucket;
        MissingRef = missingRef;
    }
}
