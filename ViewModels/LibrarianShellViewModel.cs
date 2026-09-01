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
    // Not readonly: MainWindow.ApplySettingsResult REPLACES its AppSettings instance, and this
    // window has to be handed the new one (ApplySettings below) - see that method's comment.
    AppSettings _settings;
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
    // Cancelled once, in Dispose - covers the launch pull's pre-start warm-up wait, which
    // happens before any _syncCts exists. Never reset: this window opens and closes once.
    readonly CancellationTokenSource _lifetime = new();

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

    // Built once per session, on the first PCG load (EnsureExsNamesResolved). Null means "not
    // resolved yet" - every sample-dependency row then shows exactly what it always has
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
        forceDestructiveWrite          = settings.LibrarianForceDestructiveWrite;
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
        MergePane.LocalHas = loc => _cache.Exists(loc.ObjType, loc.Bank, loc.Number);

        RefreshHistory();

        // Kicks off the cache's one-time referrer-catalog build (see LocalLibraryCache.
        // BuildCatalogAsync's own comment) on a background thread as soon as the window
        // opens, instead of it running inline the first time a placement needs it - a real
        // 10-20s freeze on a large library. While it runs, the Local Library pane hides its
        // tree and disables its toolbar (LocalPane.IsIndexing) so nothing can be moved/edited
        // against a half-built index; the pane reveals itself once the build completes.
        var catalogWarm = WarmCatalogAsync();

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

        // Establishes SysExUnavailable (and with it the banner + the Sync/Commit gate) before the
        // user can reach for either. Fire-and-forget like every other warm-up here: the commands
        // start enabled and are disabled a round-trip later, which is the right way round - a
        // Sync that beats the probe fails fast on its own now (LibraryPullPipeline.NoReplyGiveUp).
        var sysExProbe = RecheckSysExAsync();

        // Settings > Librarian > "Full sync on launch". Chained behind both warm-ups above rather
        // than fired alongside them - see LaunchPullAsync.
        if (settings.LibrarianFullSyncOnLaunch) _ = LaunchPullAsync(catalogWarm, sysExProbe);

        // Conflicts survive in the index across sessions, so the banner has to be right from the
        // moment the window opens - not only after the next Sync/Commit sets one.
        RefreshConflictState();

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
    // selects the persistence strategy - see MergeCachePersistence.cs. Used at construction, and
    // again from ApplySettings when the setting changes while this window is already open.
    static IMergeCachePersistence BuildMergePersistence(AppSettings settings) =>
        settings.MergeBehavior == MergeCacheBehavior.LocalStorage
            ? new FileMergeCachePersistence(MergeCachePath)
            : new InMemoryMergeCachePersistence();

    // Also read by MainWindow.ApplySettingsResult, which drops the snapshot when the user switches
    // Local Storage -> Temporary Memory with NO Librarian open (an open one goes through
    // ApplySettings above instead). InMemoryMergeCachePersistence.Clear() is a no-op, so nothing
    // on the reopen path would otherwise remove it.
    public static string MergeCachePath => Path.Combine(Storage.DataDir, "merge_cache.json");

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
        // The launch pull can still be sitting in its warm-up await when the window closes, i.e.
        // before it has ever published a _syncCts for the line above to find. Cancelling the
        // window-lifetime source stops it in that window too, so a Librarian opened and closed
        // straight away never goes on to sweep every bank on behalf of a gone session.
        _lifetime.Cancel();
        _undo.Changed -= OnUndoStackChanged;
        _undo.Dispose();
    }

    // Settings > Librarian > "Full sync on launch" (AppSettings.LibrarianFullSyncOnLaunch).
    // A full PULL, never the push half of Sync: an action nobody clicked must not write to the
    // instrument, and SyncLibraryAsync's PrepareForPushAsync can raise a modal confirm while the
    // window is still coming up.
    //
    // Sequenced behind both ctor warm-ups rather than fired alongside them:
    //  - WarmCatalogAsync owns LocalPane.IsIndexing and clears it in its own finally, so running
    //    concurrently would have the catalog un-hide the tree while this pull is still mid-sweep -
    //    showing pre-pull contents that look final (LibraryPullPipeline commits in ONE batch at
    //    the very end, so there is genuinely nothing to watch until then).
    //  - RecheckSysExAsync is what establishes SysExUnavailable. Going first would mean sweeping
    //    every registry bank at an instrument already known to be silent; NoReplyGiveUp would
    //    eventually catch it, but only after burning the probe's own timeouts again.
    async Task LaunchPullAsync(Task catalogWarm, Task sysExProbe)
    {
        try
        {
            await catalogWarm;
            await sysExProbe;
            // SysExUnavailable: its banner already explains why nothing pulled - a launch action
            // the user never clicked should not stack a second failure message on top of it.
            // IsBusy: the awaits above are a real window in which the user can already click Sync
            // Library (nothing is busy yet, and the probe has just enabled it). Losing that race
            // would run this sweep concurrently with a full Sync over the same cache, so yield to
            // it - the manual click is the more specific intent, and it pulls too.
            if (_lifetime.IsCancellationRequested || SysExUnavailable || IsBusy) return;

            IsBusy = true; WarningText = null; StatusIsSuccess = false;
            LocalPane.IsIndexing = true;   // same gate, same reason as SyncLibraryAsync's own
            // Deliberately NOT published as _syncCts: SyncLibraryAsync owns that field, and a
            // launch pull clearing it in its own finally could null out a manual Sync's token.
            // _lifetime is cancelled by Dispose directly, so window-close still stops this.
            try
            {
                var pull = await SyncPipeline.PullAsync(
                    _sysEx, _cache, full: true, m => StatusText = m, _lifetime.Token);
                if (pull.Aborted is { } aborted)
                {
                    SysExUnavailable = true;
                    StatusText = "";
                    WarningText = aborted.ToString();
                }
                else if (!_lifetime.IsCancellationRequested)
                {
                    StatusText = AppMessages.Librarian.Shell.LaunchPullComplete(pull.ObjectsFetched, pull.Conflicts);
                    StatusIsSuccess = pull.Conflicts == 0;
                }
                AppLog.Info($"[librarian] launch pull done: fetched={pull.ObjectsFetched} conflicts={pull.Conflicts}");
                InvalidateReadOnlyBankNames();
            }
            finally
            {
                LocalPane.RefreshTree();
                LocalPane.IsIndexing = false;
                RefreshHistory();
                RefreshConflictState();
                IsBusy = false;
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the ctor - an unobserved task exception here would be invisible.
            AppLog.Warn($"[librarian] launch pull failed: {ex}");
            WarningText = AppMessages.Librarian.Shell.OperationFailed(ex.Message);
        }
    }

    // What Sync/Commit actually reads, and what the standing red banner binds to. A settable
    // mirror rather than a read-through to _settings, because MainWindow.ApplySettingsResult
    // REPLACES the AppSettings instance: this window keeps its own reference, so a user who
    // opens Settings and turns this OFF while the Librarian is open would otherwise still get a
    // destructive push. MainWindow pushes the new value in here instead (SetForceDestructiveWrite).
    [ObservableProperty] bool forceDestructiveWrite;
    public string DestructiveWriteBannerText => AppMessages.Librarian.Shell.DestructiveWriteArmed;

    // Called by MainWindow when the Settings dialog (or File > Import Settings) is applied while
    // this window is open. Two things go wrong without it, because ApplySettingsResult swaps the
    // AppSettings OBJECT rather than mutating it:
    //  - a destructive-write toggle never reaches the Librarian, including turning it OFF;
    //  - worse, the merge-duplicate toolbar toggles write through to whatever instance this VM
    //    holds and then persist it (PersistSettings = Storage.SaveSettings), so flipping one after
    //    a Settings change wrote the PRE-DIALOG snapshot back over settings.json, silently
    //    reverting everything the user had just changed.
    // _settings is swapped FIRST so the duplicate toggles' write-through (which fires only if a
    // value actually changed) persists the new instance rather than the one being replaced.
    public void ApplySettings(AppSettings settings)
    {
        // Merge behavior has to switch LIVE, not on the next reopen. Deleting merge_cache.json from
        // MainWindow is not enough on its own while this window is up: our MergeCache still holds a
        // FileMergeCachePersistence aimed at that path, so the very next drag rewrites the file -
        // and a later switch back to Local Storage re-adopts it. SetPersistence clears the file AND
        // swaps the strategy, and carries whatever is currently staged across either way.
        bool wasFileBacked = _settings.MergeBehavior == MergeCacheBehavior.LocalStorage;
        if (wasFileBacked != (settings.MergeBehavior == MergeCacheBehavior.LocalStorage))
            MergePane.SetPersistence(BuildMergePersistence(settings), wasFileBacked);

        _settings = settings;
        ForceDestructiveWrite          = settings.LibrarianForceDestructiveWrite;
        MergePreserveDuplicatePrograms = settings.MergePreserveDuplicatePrograms;
        MergePreserveDuplicateCombis   = settings.MergePreserveDuplicateCombis;
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
                _sysEx, _cache, _sessionClipboard, ForceFullPull, m => StatusText = m, cts.Token,
                ForceDestructiveWrite);
            // A pull that succeeded with nothing locally dirty to push back is a complete,
            // successful Sync - not the CHECK/warning ChangesetBuilder's early-return produces for
            // the same state (that's meant for Commit Changes, which has no pull to justify "why
            // did nothing happen"). Only when the pull ALSO flagged zero conflicts: a pull that
            // conflicted something is not "Complete" even though the push still had nothing to
            // write (a conflicted object is deliberately excluded from Writes, not "not dirty").
            bool nothingToPushClean = push.Ok && push.Written == 0 && push.Deleted == 0
                && pull.Conflicts == 0 && push.Error == AppMessages.Librarian.Sync.CheckNothingToPush.ToString();
            // The pull gave up because the instrument answered nothing. Raising the banner from
            // here as well as from the probe matters: the probe only runs at open and on demand,
            // so an instrument that goes quiet mid-session would otherwise leave Sync enabled and
            // failing with nothing on screen to explain the state.
            if (pull.Aborted is { } pullAborted)
            {
                SysExUnavailable = true;
                StatusText = "";
                WarningText = pullAborted.ToString();
            }
            else if (nothingToPushClean)
            {
                StatusText = AppMessages.Librarian.Shell.SyncComplete(ForceFullPull, pull.ObjectsFetched, pull.Conflicts);
                StatusIsSuccess = true;
            }
            else
            {
                StatusText = AppMessages.Librarian.Shell.SyncResult(pull.ObjectsFetched, pull.Conflicts, push.Written, push.Deleted, push.Conflicted.Count);
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
            RefreshConflictState();   // the pull and the push both set/clear conflicts
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
            var result = await SyncPipeline.CommitChangesAsync(
                _sysEx, _cache, _sessionClipboard, m => StatusText = m, ForceDestructiveWrite);
            StatusText = result.Ok
                ? AppMessages.Librarian.Shell.CommitResult(result.Written, result.Deleted, result.Conflicted.Count)
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
            RefreshConflictState();   // ChangesetBuilder's pre-scan flags conflicts as it runs
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

    bool CanRunHardwareOp() => !IsBusy && !SysExUnavailable;

    // ── SysEx-off read-only fallback ────────────────────────────────────────────────────────
    // With SysEx switched off on the Kronos (GLOBAL > MIDI) every request times out rather than
    // erroring, so without this the Librarian looked functional and then sat for hours. True =
    // the instrument answered nothing; the window stays fully usable for browsing, staging and
    // organising Local Library / the Merge Window / a loaded PCG, and only the two commands that
    // actually talk to hardware (Sync, Commit) are disabled. Deliberately NOT a read of
    // IBankDumpService.CanDump, which is the LOCAL MIDI-monitor setting, not the instrument's.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitChangesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveConflictsKeepMineCommand))]
    [NotifyPropertyChangedFor(nameof(CommitTooltip))]
    bool sysExUnavailable;

    public string SysExOffBannerText => AppMessages.Librarian.Shell.SysExOffBanner;

    // Bound rather than static so the disabled Commit button can say WHY it is disabled - the
    // banner above carries the fix, this only has to name the cause.
    public string CommitTooltip => SysExUnavailable
        ? AppMessages.Librarian.Shell.SysExOffCommitTooltip
        : AppMessages.Librarian.Shell.CommitTooltip;

    // One func-0x37 bank-digest request against Program INT A - the same detector
    // LibraryPullPipeline's sweep leads with, so the banner and a Sync can never disagree about
    // whether the instrument is answering. INT A always exists and always answers a digest on a
    // live unit (unlike the GM/g banks, which have none by design), so a null here means silence,
    // not a bank quirk.
    //
    // Re-runnable on purpose: the fix for this state is something the user does AT THE PANEL
    // while the window is open, so a probe that only ran at open time would leave the banner up
    // forever after they fixed it - see the Re-check button in LibrarianShellWindow.xaml.
    // Retried, and this is not belt-and-braces. A single attempt false-negatived on real
    // hardware: the ctor also fires WarmCategoryNamesAsync, whose Global-object dump is ~24 KB
    // with a 10 s no-response window, and both go through the same DumpGate - so the probe's
    // 5 s digest timeout expired waiting behind it and disabled Sync and Commit on a perfectly
    // healthy instrument. Any bulk dump in flight can do that; the instrument being genuinely
    // silent looks identical on one attempt and only differs across several.
    const int SysExProbeAttempts = 3;

    [RelayCommand]
    async Task RecheckSysExAsync()
    {
        for (int attempt = 1; attempt <= SysExProbeAttempts; attempt++)
        {
            try
            {
                if (await _sysEx.BankDigestAsync(LibObj.Program, 0x00) != null)
                {
                    if (SysExUnavailable) AppLog.Info("[librarian] SysEx probe: answered - Sync/Commit re-enabled");
                    SysExUnavailable = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                // Observe and log; a throwing probe is treated exactly like a silent one.
                AppLog.Warn($"[librarian] SysEx probe attempt {attempt} failed: {ex.Message}");
            }
            AppLog.Debug($"[librarian] SysEx probe attempt {attempt}/{SysExProbeAttempts}: no reply");
            if (attempt < SysExProbeAttempts) await Task.Delay(1500);
        }
        SysExUnavailable = true;
        AppLog.Warn($"[librarian] SysEx probe: no reply after {SysExProbeAttempts} attempts - Sync/Commit disabled");
    }

    // ── Conflict resolution ─────────────────────────────────────────────────────────────────
    // A conflicted object is one this library has edits for whose BANK changed on the Kronos
    // since the baseline was taken. The push excludes the whole bank rather than clobber
    // whatever changed - correct, but until now there was no way out of it from this window and
    // no sign it had happened, so a Commit could write 99 Programs, silently drop 47 Combis, and
    // report "Pushed 99 object(s)." That is the bug this pairs with; see
    // AppMessages.Librarian.Sync.CheckConflictedNotPushed.
    public int ConflictedCount => _cache.ConflictedObjects().Count();
    public bool HasConflicts => ConflictedCount > 0;

    // Set by LibrarianShellWindow to a WPF confirmation, same code-behind split every other
    // destructive prompt here uses. Null (headless self-test) proceeds.
    public Func<int, string, Task<bool>>? ConfirmResolveConflicts { get; set; }

    // "Keep mine": clear the flags AND re-baseline each affected bank from the instrument's
    // CURRENT digest, so the next Commit's pre-scan passes and the local edits go out. Both
    // halves are required - clearing the flag alone changes nothing, because the pre-scan
    // compares bank digests, not flags.
    //
    // This is destructive TO THE INSTRUMENT by design: whatever changed in those banks is about
    // to be overwritten by this library's copy, which is exactly what the user is choosing. The
    // other resolution (take theirs) already exists as Sync Library, which pulls the bank and
    // leaves hardware alone.
    [RelayCommand(CanExecute = nameof(CanResolveConflicts))]
    async Task ResolveConflictsKeepMineAsync()
    {
        var stuck = _cache.ConflictedObjects().ToList();
        if (stuck.Count == 0) return;
        var banks = stuck.Select(l => (l.ObjType, l.Bank)).Distinct().ToList();
        string bankList = string.Join(", ", banks.Select(b => Librarian.StoreLabel(b.ObjType, b.Bank)));

        if (ConfirmResolveConflicts != null && !await ConfirmResolveConflicts(stuck.Count, bankList)) return;

        IsBusy = true; WarningText = null; StatusIsSuccess = false;
        try
        {
            int rebased = 0;
            foreach (var (objType, bank) in banks)
            {
                // A bank we cannot get a digest for keeps its stale baseline and its conflicts:
                // re-baselining on a timeout would clear the flag while leaving the pre-scan
                // still excluding the bank, which looks like the resolve silently did nothing.
                var fresh = await _sysEx.BankDigestAsync(objType, bank).ConfigureAwait(true);
                if (fresh == null) continue;
                _cache.SetBankDigestBaseline(objType, bank, Convert.ToHexString(fresh).ToLowerInvariant());
                rebased++;
                foreach (var loc in stuck.Where(l => l.ObjType == objType && l.Bank == bank))
                    _cache.ClearConflict(loc.ObjType, loc.Bank, loc.Number);
            }
            _cache.Save();
            StatusText = AppMessages.Librarian.Shell.ConflictsResolved(stuck.Count, rebased, banks.Count);
            StatusIsSuccess = rebased == banks.Count;
            if (rebased < banks.Count)
                WarningText = AppMessages.Librarian.Sync.CheckResolveNoDigest.ToString();
        }
        finally
        {
            LocalPane.RefreshTree();
            RefreshConflictState();
            IsBusy = false;
        }
    }

    bool CanResolveConflicts() => !IsBusy && !SysExUnavailable && HasConflicts;

    // Conflicts are set/cleared deep in the pipelines (ChangesetBuilder, LibraryPullPipeline),
    // not through a property this can observe, so every path that can change them calls this.
    public void RefreshConflictState()
    {
        OnPropertyChanged(nameof(ConflictedCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictBannerText));
        ResolveConflictsKeepMineCommand.NotifyCanExecuteChanged();
    }

    public string ConflictBannerText => AppMessages.Librarian.Shell.ConflictBanner(ConflictedCount);

    partial void OnIsBusyChanged(bool value)
    {
        SyncLibraryCommand.NotifyCanExecuteChanged();
        CommitChangesCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();   // see CanUndo - undo must not race a push
        AutoFillToLibraryCommand.NotifyCanExecuteChanged();
        ResolveConflictsKeepMineCommand.NotifyCanExecuteChanged();
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
    void LoadPcgFromComputer(Window owner)
    {
        PcgPane.LoadFromComputer(owner);
        EnsureExsNamesResolved();
    }

    [RelayCommand]
    async Task LoadPcgFromKronosAsync(Window owner)
    {
        await PcgPane.LoadFromKronosAsync(new KronosRemotePcgSource(owner, _settings, _host));
        EnsureExsNamesResolved();
    }

    // Replaces the old "Resolve Sample Bank Names..." button: the catalog is a local read (no
    // connection, no login, nothing to cancel), so the button only ever gated whether sample rows
    // got names at all - a decision the user had no reason to make. Runs at most once per session
    // (the catalog can't change under a running app) and writes no status of its own, so the
    // load's own message stays on screen.
    void EnsureExsNamesResolved()
    {
        if (_exsIndex != null) return;
        var index = ExsOptionIndex.FromCatalog();
        AppLog.Info($"[librarian] EXs sample bank names: {index.Count} from {(index.FromOverrideFile ? "override file" : "embedded catalog")}");
        ApplyExsOptionIndex(index);
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
