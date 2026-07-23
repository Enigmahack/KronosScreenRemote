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
// once — LocalPane and PcgPane individually don't know about each other.
partial class LibrarianShellViewModel : ObservableObject
{
    readonly ILibrarianService _sysEx;
    readonly LocalLibraryCache _cache;
    readonly AppSettings _settings;
    readonly string _host;
    readonly SessionDependencyClipboard _sessionClipboard = new();

    // Live-queried (func 0x61) + persisted Program Bank Types — seeded from the on-disk cache
    // at construction, refreshed from real hardware in the background (WarmProgramBankTypesAsync).
    // Null until the first successful query ever completes for this host; BankTypeOf handles
    // that (and any bank with no EXi/HD-1 concept, e.g. GM/g) by returning null — "can't
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

    // Set once by LibrarianShellWindow's constructor to a WPF MessageBox prompt — keeps this
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
        RefreshHistory();

        // Kicks off the cache's one-time referrer-catalog build (see LocalLibraryCache.
        // BuildCatalogAsync's own comment) on a background thread as soon as the window
        // opens, instead of it running inline the first time a placement needs it — a real
        // 10-20s freeze on a large library. The tree above is already populated from the
        // index alone (no blob reads), so the window is usable immediately; only a
        // drop/paste attempted before this finishes pays a (shrinking) wait, same as before.
        _ = WarmCatalogAsync();

        _programBankTypes = Storage.LoadProgramBankTypes(_host) is { } cached ? new ProgramBankTypes(cached) : null;
        _ = WarmProgramBankTypesAsync();
    }

    async Task WarmCatalogAsync()
    {
        StatusText = "Indexing local library…";
        try
        {
            await _cache.BuildCatalogAsync();
            if (StatusText == "Indexing local library…") StatusText = "";
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the ctor — without this, a blob-IO failure (e.g. the
            // library share going away) would be an unobserved task exception, invisible.
            AppLog.Warn($"[librarian] catalog warm-up failed: {ex.Message}");
            StatusText = "Local library indexing failed — see log";
        }
    }

    // Refreshes _programBankTypes from real hardware (func 0x61) in the background, same
    // "seed from disk, refresh live" shape as WarmCatalogAsync. A null result (hardware
    // unreachable, or this session has no live connection at all) leaves whatever was
    // already cached — or null — alone; placement checks that can't verify a bank's type
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
            // types in place — placement checks are advisory, so swallowing is safe, but the
            // exception must be observed and logged.
            AppLog.Warn($"[librarian] program bank-type warm-up failed: {ex.Message}");
        }
    }

    // Testing-only entry point so a self-test can deterministically await the constructor's
    // own fire-and-forget warmup completing (against a FakeMoveExecutor's synchronously-
    // resolved ProgramBankTypesToReturn) before asserting on BankTypeOf's effect, instead of
    // relying on incidental synchronous continuation timing — same shape as PcgPaneViewModel.
    // LoadBytesForTesting.
    internal Task WarmProgramBankTypesForTestingAsync() => WarmProgramBankTypesAsync();

    // The one thing PlanBatchMove's fresh-placement bank-type check (Core/BatchMoveModel.cs)
    // needs: is destination Program bank `objBank` actually configured as EXi (true) or HD-1
    // (false) on the real hardware right now? Null if we've never successfully queried it
    // (nothing pushed yet this session, or genuinely offline), or the bank has no such concept
    // at all (GM/g — KronosBanks.ProgramBankTypeBitIndex returns null for those).
    bool? BankTypeOf(int objBank) =>
        _programBankTypes is { } types && KronosBanks.ProgramBankTypeBitIndex(objBank) is int bit && bit < types.IsExi.Length
            ? types.IsExi[bit]
            : null;

    // The Merge Window's "Merge behavior" setting (Views/SettingsWindow.xaml's Librarian tab)
    // selects the persistence strategy at construction time — see MergeCachePersistence.cs.
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
    // on purpose — an off-thread read would leave unawaited background file I/O that races
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

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task SyncLibraryAsync()
    {
        IsBusy = true; WarningText = null;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var (pull, push) = await SyncPipeline.SyncLibraryAsync(
                _sysEx, _cache, _sessionClipboard, ForceFullPull, m => StatusText = m);
            StatusText = $"Pulled {pull.ObjectsFetched} object(s) ({pull.Conflicts} conflict(s)). Pushed {push.Written} object(s).";
            if (!push.Ok) WarningText = push.Error;
            LocalPane.RefreshTree();
            RefreshHistory();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunHardwareOp))]
    async Task CommitChangesAsync()
    {
        IsBusy = true; WarningText = null;
        try
        {
            if (!await PrepareForPushAsync()) return;
            var result = await SyncPipeline.CommitChangesAsync(_sysEx, _cache, _sessionClipboard, m => StatusText = m);
            StatusText = result.Ok ? $"Pushed {result.Written} object(s)." : "Commit failed — see warning.";
            if (!result.Ok) WarningText = result.Error;
            LocalPane.RefreshTree();
            RefreshHistory();
        }
        finally { IsBusy = false; }
    }

    // Runs right before every Sync/Commit — the "lazy" half of the auto-heal placement
    // pipeline (see ResolvePendingDependencies): retries every still-pending dependency
    // against Local Library's CURRENT state (time has passed since it was placed; the
    // dependency may now exist anywhere), then — only for whatever's STILL unresolved after
    // that — asks the user via ConfirmContinueWithPendingDependencies whether to proceed
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
            StatusText = "Cancelled — unresolved dependencies still pending.";
            return false;
        }

        // The user explicitly accepted the risk — stop tracking these as blocking. Whatever
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
    }

    // Called after any local edit (rename/move/discard/placement) so the permanent history
    // panel reflects it immediately rather than only after a Sync/Commit.
    public void NotifyLocalEditMade() => RefreshHistory();

    [RelayCommand]
    void LoadPcgFromComputer(Window owner) => PcgPane.LoadFromComputer(owner);

    [RelayCommand]
    async Task LoadPcgFromKronosAsync(Window owner) =>
        await PcgPane.LoadFromKronosAsync(new KronosRemotePcgSource(owner, _settings, _host));

    // ── Cross-pane placement (PCG -> local), requirement 12 ──────────────────────────
    // Drop on a specific slot = exact placement. HW-write never happens here — this only
    // ever touches the local cache via LocalEditOps, exactly like every other local edit.

    public (bool Ok, string? Error) PlaceFromPcg(ObjLoc pcgLoc, ObjLoc destLoc)
    {
        // Cross-type guard, same as BatchPlaceFromPcg's per-item check: the single-item drop
        // path has no upstream type check (OnLocalDrop filters on drag format only), and combi
        // bank numbers are a numeric subset of program bank numbers — a mismatched drop would
        // otherwise land silently in a valid-looking slot of the wrong type.
        if (pcgLoc.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(pcgLoc.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");

        var entry = PcgPane.Get(pcgLoc);
        if (entry == null) return (false, "not found in the loaded PCG file");
        var rawBody = ProgramFormatConverter.WireBodyFromPcgEntry(pcgLoc.ObjType, entry);
        if (rawBody == null) return (false, "malformed Program record in the loaded PCG file");

        // Repoint whatever of this object's OWN references already resolve somewhere in Local
        // Library (by content, not just the raw address the PCG encoded) before ever writing
        // it — see DependencyScanner.RepointPcgReferences's own comment. `entry != null` above
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

    // ── PCG -> Merge Window (fully automatic, transitive — see MergeCache.PullFromPcg) ──

    public void PullIntoMerge(ObjLoc pcgLoc)
    {
        if (PcgPane.View is not { } view) return;   // nothing loaded — nothing to pull from
        MergePane.PullFromPcg(view, PcgPane.LoadedFileName ?? "(unknown)", pcgLoc);
    }

    // ── Merge Window group -> Local (bulk placement of a multi-item Merge selection) ─────
    // Dragging a multi-item Merge Window selection — typically the whole "Set Lists"/"Combis"/
    // "Programs" group node (LibrarianShellWindow's Merge pane bank-equivalent selection), but
    // works equally for any multi-leaf Ctrl+click selection sharing one type — onto a local
    // bank or a specific slot within one, instead of placing one staged item at a time. Fills
    // sequentially starting at destBank's own first free slot (LocalEditOps.FindNextFreeSlot)
    // — exactly the same convention BatchPlaceFromPcg's own multi-item drop already uses, not
    // "must be completely empty" (an earlier, more conservative version of this method
    // required that; real use showed a partially-filled bank with plenty of room left was the
    // common case, not the exception). An occupied-but-unreferenced slot in the way is
    // overwritten with its occupant diverted to the persisted clipboard (never lost) — the
    // same safety net every other batch placement in this app already relies on; a referenced
    // occupant still REFUSEs via LocalEditOps.BatchPlace's own orphan gate. Only entries
    // matching destBank's own type are placed (silently drops anything else, e.g. a stray
    // different-type hash) — nested dependency Programs/Combis stay staged for individual
    // placement afterward, exactly like PlaceFromMerge above already works (this doesn't
    // cascade into placing dependencies either). Anything beyond the bank's remaining room
    // stays staged too (never lost), same "flag what didn't fit" convention BatchPlaceFromPcg
    // uses.
    public (bool Ok, string? Message) PlaceMergeGroupSequentially(int objType, int destBank, IReadOnlyList<string> contentHashes)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e != null && e!.ObjType == objType).Select(e => e!).ToList();
        if (group.Count == 0) return (false, "nothing to place for this bank's type");

        int startSlot = FindNextFreeSlot(objType, destBank);
        int take = Math.Min(group.Count, descriptor.SlotCount - startSlot);
        if (take <= 0) return (false, $"{descriptor.BankLabel(destBank)} is full — no free slots left.");

        var bodies = new byte[take][];
        var unresolvedPerItem = new List<MergeRefSite>[take];
        var placements = new List<BatchPlacement>();
        for (int i = 0; i < take; i++)
        {
            var entry = group[i];
            (bodies[i], unresolvedPerItem[i]) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);
            placements.Add(new BatchPlacement(null, new ObjLoc(objType, destBank, startSlot + i),
                new ObjectDump(objType, destBank, startSlot + i, entry.Version, bodies[i]), entry.DisplayName));
        }

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        for (int i = 0; i < take; i++)
        {
            var destLoc = new ObjLoc(objType, destBank, startSlot + i);
            MergePane.CommitPlacement(group[i].ContentHash, destLoc);
            TrackMergeDependencies(unresolvedPerItem[i], destLoc);
        }
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        string msg = take < group.Count
            ? $"Placed {take}; {group.Count - take} didn't fit ({descriptor.BankLabel(destBank)} is full) — still staged in the Merge Window"
            : $"Placed {take}";
        return (true, msg);
    }

    // ── Merge Window -> Local (manual, per-item — the user picks every destination,
    // including a dependency's, since only they know whether a bank should stay empty or a
    // partially-filled one should be continued; see this feature's own design conversation). ──

    public (bool Ok, string? Error) PlaceFromMerge(string mergeContentHash, ObjLoc destLoc)
    {
        var entry = MergePane.TryGet(mergeContentHash);
        if (entry == null) return (false, "not found in the Merge Window");
        // Cross-type guard — see PlaceFromPcg's identical check for why this can't be
        // left to the drop handlers.
        if (entry.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(entry.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");
        // Patches whatever of this entry's OWN dependency references resolve — either because
        // the dependency was ALSO placed via Merge this session (_placedAddresses), or because
        // it already exists ANYWHERE in Local Library (LocalLookup, by content) — the
        // many-to-one dedup payoff, generalized beyond just this-session Merge placements.
        // Anything still unresolved is tracked for a later retry (TrackMergeDependencies).
        var (body, unresolved) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);

        var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(
            _cache, destLoc, entry.ObjType, entry.Version, body, entry.DisplayName,
            divertDisplacedToClipboard: true, DateTime.UtcNow, BankTypeOf);
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
    // reusing BatchLibrarian.ResolveSequentialFill — the same sequential-fill-with-clipboard-
    // overflow logic the persisted clipboard's Paste Multi/All already uses.
    public (bool Ok, string? Message) BatchPlaceFromPcg(int objType, IReadOnlyList<ObjLoc> pcgLocs, int destBank)
    {
        var pending = new List<ClipboardEntry>();
        foreach (var loc in pcgLocs)
        {
            // A loc whose own type doesn't match objType would otherwise get force-fed through
            // the WRONG type's WireBodyFromPcgEntry converter below (it trusts objType, not
            // loc) — skip rather than risk decoding e.g. a Combi body as a Program. The Local
            // tree's own UI-level selection guard (LibrarianShellWindow.OnPcgPreviewMouseDown)
            // already prevents building a mixed-type selection in the first place; this is
            // defense in depth for this method's other/future callers.
            if (loc.ObjType != objType) continue;
            if (PcgPane.Get(loc) is not { } e) continue;
            var body = ProgramFormatConverter.WireBodyFromPcgEntry(objType, e);
            if (body == null) continue;   // malformed Program record — skip rather than fail the whole batch
            pending.Add(new ClipboardEntry { ObjType = objType, Origin = loc, Version = LibObj.CurrentObjectVersion(objType) ?? 0, Body = body, Provenance = ClipboardProvenance.UserCopy, CutAt = DateTime.UtcNow });
        }
        if (pending.Count == 0) return (false, "nothing to place");

        int startSlot = FindNextFreeSlot(objType, destBank);
        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(pending, objType, destBank, startSlot, bankTypeOf: null);
        if (placed.Count == 0) return (false, "nothing could be placed (bank full or type mismatch)");

        // Repoint each placed item's OWN references before writing, same as the single-item
        // path — every dependency that already resolves somewhere in Local Library gets
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
    // then-current state — not just re-checking the one address it currently encodes.
    void TrackMergeDependencies(List<MergeRefSite> stillUnresolved, ObjLoc placedAt)
    {
        if (stillUnresolved.Count == 0) return;
        foreach (var site in stillUnresolved)
            _sessionClipboard.Add(new SessionDependencyEntry(site.TargetLoc, site.RefKind, site.Site, placedAt, site.ResolvedContentHash));
        RefreshSessionClipboard();
    }

    // Same tracking, for the direct-PCG path — plus auto-staging: a reference RepointPcgReferences
    // couldn't resolve locally, but whose expected content the loaded PCG DOES have, gets pulled
    // into the Merge Window right away (reusing the existing transitive pull) so the user has a
    // clear, visible next step instead of a silently wrong/missing reference. A null expected
    // hash (the PCG doesn't have it either — a true gap) is left alone; nothing to stage.
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

    // Runs right before every Sync/Commit (see PrepareForPushAsync) — retries every pending
    // dependency against Local Library's CURRENT state (time has passed; the dependency may
    // now exist anywhere, not necessarily at the address it was originally tracked against),
    // and repatches whatever's found via a REAL edit (LocalEditOps.RepatchReference —
    // re-dirties the referrer, appears in History, feeds the next push changeset; never a
    // silent byte mutation, since the referrer may already be dirty or previously pushed).
    void ResolvePendingDependencies()
    {
        bool anyResolved = false;
        foreach (var entry in _sessionClipboard.Pending.ToList())
        {
            if (entry.ExpectedContentHash is not { } hash) continue;   // a true gap — nothing to search for
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
    // LibrarianShellWindow.xaml.cs's PaneSelection.SelectionChanged) — a live, read-only view
    // of what the CURRENTLY SELECTED Combi(s)/Set List(s) reference, transitively (a Set
    // List's Combis, and THEIR Programs in turn). A selected Program contributes nothing of
    // its own here — Programs never reference anything — so a mixed Program+Combi selection
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

    void CollectLocalDeps(ObjLoc loc, HashSet<ObjLoc> seen, List<ObjectDependencyRow> rows)
    {
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return;
        foreach (var (_, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seen.Add(refLoc)) continue;
            // Cached at write time (LocalIndexEntry.DisplayName) — never a blob read, same
            // discipline as the tree's own labels (LocalLibraryPaneViewModel.MakeLeafNode).
            bool found = _cache.Exists(refLoc.ObjType, refLoc.Bank, refLoc.Number);
            string name = found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "";
            rows.Add(new ObjectDependencyRow(DescribeDependency(refLoc, name, found, "locally")));
            if (found && refLoc.ObjType != LibObj.Program) CollectLocalDeps(refLoc, seen, rows);
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

    // Merge entries are keyed by content hash, not address — RefSites already carry the
    // resolved-dependency lookup (or the original PCG address for a still-unresolved gap), so
    // this needs none of ObjectReferenceWalker's own byte-decoding, unlike the Local/PCG paths.
    void CollectMergeDeps(MergeEntry entry, HashSet<string> seen, List<ObjectDependencyRow> rows)
    {
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string key = site.ResolvedContentHash ?? site.TargetLoc.Label();
            if (!seen.Add(key)) continue;
            // No real address yet (Merge Window is bag-based, not addressed) — name is all
            // there is to show until it's actually placed.
            rows.Add(new ObjectDependencyRow(dep != null
                ? $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}"
                : $"{TypeName(site.TargetLoc.ObjType)}: {site.TargetLoc.Label()} — not found in any loaded PCG"));
            if (dep != null && dep.ObjType != LibObj.Program) CollectMergeDeps(dep, seen, rows);
        }
    }

    public void ClearObjectDependencies() => ObjectDependencyRows.Clear();

    void ReplaceObjectDependencies(List<ObjectDependencyRow> rows)
    {
        ObjectDependencyRows.Clear();
        foreach (var r in rows) ObjectDependencyRows.Add(r);
    }

    static string TypeName(int objType) => ObjectTypeRegistry.Get(objType).DisplayName;

    // Shared row format for the Local/PCG collectors above (Merge has no real address, so it
    // formats its own rows separately) — slot address alone isn't useful on its own, hence
    // type + name alongside it.
    static string DescribeDependency(ObjLoc loc, string name, bool found, string whereMissing) =>
        found
            ? $"{TypeName(loc.ObjType)}: {loc.Label()} — {(string.IsNullOrEmpty(name) ? "(unnamed)" : name)}"
            : $"{TypeName(loc.ObjType)}: {loc.Label()} — not found {whereMissing}";
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
    public SessionClipboardRow(SessionDependencyEntry e) =>
        Description = $"{e.MissingRef.Label()} — needed by {e.RequiredBy.Label()} ({e.RefKind})";
}

// Display wrapper for one entry in the "Object Dependencies" panel — see
// LibrarianShellViewModel's ShowLocal/Pcg/MergeObjectDependencies.
sealed class ObjectDependencyRow
{
    public string Description { get; }
    public ObjectDependencyRow(string description) => Description = description;
}
