using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// The Merge Window pane's view-state: a staging area between "loaded PCG file(s)" and Local
// Library (see MergeCache's own class doc for the full design). Owns the MergeCache and
// builds a folder-style tree from it - Set Lists (each expanding into its Combis, which
// expand into their own Program dependencies), plus independent top-level Combis/Programs
// sections for anything pulled standalone, mirroring Keyboard Library's own type-grouped tree.
// Placement into Keyboard Library (the one address-sensitive, manual step) is driven by
// LibrarianShellViewModel.PlaceFromMerge, same split as PlaceFromPcg/BatchPlaceFromPcg -
// this pane owns staging, LibrarianShellViewModel owns the cross-pane action.
partial class MergePaneViewModel : ObservableObject
{
    readonly MergeCache _cache;

    public ObservableCollection<ObjectTreeNode> Roots { get; } = new();

    // Raised at the end of RefreshTree() - see LocalLibraryPaneViewModel's own TreeRefreshed for
    // why: every pull/clear/placement rebuilds Roots from scratch.
    public event Action? TreeRefreshed;

    [ObservableProperty] string statusText = "";

    // What's staged right now, by type, plus what it still needs - shown above the pane's legend
    // and rebuilt by RefreshTally on every tree rebuild (i.e. every pull, placement, and remove).
    [ObservableProperty] string tallyText = "";
    [ObservableProperty] int missingDependencyCount;

    // Drives the tally's red styling in LibrarianShellWindow.xaml - a plain > 0 test needs a
    // converter at the binding, a bool doesn't.
    public bool HasMissingDependencies => MissingDependencyCount > 0;

    partial void OnMissingDependencyCountChanged(int value) => OnPropertyChanged(nameof(HasMissingDependencies));

    // "Force Overwrite" (Views/LibrarianShellWindow.xaml's Merge Window GroupBox): bypasses
    // BatchLibrarian.PlanBatchMove's orphan gate, which otherwise REFUSEs placing onto a slot
    // still referenced by another local Combi/Set List (overwriting it would leave that
    // referrer resolving to nothing). Read by LibrarianShellViewModel's own Merge -> Local
    // placement methods (PlaceFromMerge/PlaceMergeGroupSequentially/
    // PlaceMergeBankWithTypeChange) at the moment of placement - checking this box doesn't
    // retroactively re-plan anything already placed. The old occupant is still diverted to the
    // session clipboard (never lost outright); only its referrer(s) end up repointed to the
    // NEW object instead of the old one.
    [ObservableProperty] bool forceOverwrite;

    // Injected by LibrarianShellViewModel (same pattern as LocalLibraryPaneViewModel.BankTypeOf):
    // opens one undo capture scope per action here. Null in a headless self-test that constructs
    // this pane directly - every action below then simply isn't undoable, never broken. A scope
    // opened inside an OUTER one (the shell's own pull loops) joins that step rather than
    // splitting it, so one gesture stays one Ctrl+Z.
    public Func<string, IDisposable>? BeginUndo { get; set; }

    IDisposable? Undoable(string description) => BeginUndo?.Invoke(description);

    // Injected by LibrarianShellViewModel (same pattern as BeginUndo above), since the Local
    // Library cache is shell-owned and this pane only ever holds the MergeCache: "does Local
    // Library already have this address?", asked of each gap a PCG pull leaves behind. Null in a
    // headless self-test - the gap report then just states the count, never a wrong split.
    public Func<ObjLoc, bool>? LocalHas { get; set; }

    // Settings > Librarian > "Merge behavior" changed while this window is open - see
    // MergeCache.SetPersistence for what each direction does to what is already staged.
    public void SetPersistence(IMergeCachePersistence persistence, bool wasFileBacked) =>
        _cache.SetPersistence(persistence, wasFileBacked);

    public MergePaneViewModel(MergeCache cache)
    {
        _cache = cache;
        RefreshTree();
    }

    // Fully automatic and transitive (see MergeCache.PullFromPcg) - pulling a Set List or
    // Combi pulls in everything it references that resolves within `pcg`, with no further
    // clicks. Anything that doesn't resolve is reported in StatusText, same "flag, don't
    // block" contract DependencyScanner's existing gap-tracking already uses.
    public void PullFromPcg(PcgLibraryView pcg, string pcgFileName, ObjLoc loc) =>
        PullFromPcg(pcg, pcgFileName, new[] { loc });

    // The whole gesture (a multi-select, a whole bank, a dependency sweep) is ONE pull as far as
    // undo and StatusText are concerned. Looping the single-loc overload instead left the status
    // line reporting only the LAST loc's result - a bank drag whose last Combi was an unused INIT
    // (byte-identical to every other unused slot, so deduped) ended on "Pulled 0 object(s)" no
    // matter how much the drag actually staged. The count is the distinct content staged by the
    // gesture, new or already present (MergeCache's stagedHashes), which is what the Merge Window
    // now holds because of it; per-loc Added counts can't be summed for that - a shared dependency
    // would be counted once per referrer that pulled it.
    public void PullFromPcg(PcgLibraryView pcg, string pcgFileName, IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        using var undo = Undoable(AppMessages.Librarian.Shell.UndoPulledIntoMerge(locs.Count));
        var staged = new HashSet<string>();
        var gaps = new HashSet<ObjLoc>();
        foreach (var loc in locs)
            foreach (var (missing, _) in _cache.PullFromPcg(pcg, pcgFileName, loc, staged).Gaps)
                gaps.Add(missing);
        RefreshTree();
        StatusText = gaps.Count == 0
            ? AppMessages.Librarian.Merge.PulledIntoMerge(staged.Count)
            : DescribeGaps(staged.Count, gaps);
    }

    // Gaps are counted by distinct ADDRESS, not by reference site: one missing Program referenced
    // by eight staged Combis is one thing for the user to go find, not eight. Whatever Local
    // Library already holds at that address needs no PCG hunt at all, so it's reported apart from
    // what's genuinely still missing (and not at all when there's none, to keep the line short).
    string DescribeGaps(int staged, IReadOnlyCollection<ObjLoc> gaps)
    {
        int inLibrary = LocalHas is { } has ? gaps.Count(has) : 0;
        return inLibrary == 0
            ? AppMessages.Librarian.Merge.PulledWithGapsInPcg(staged, gaps.Count)
            : AppMessages.Librarian.Merge.PulledWithGapsPartlyLocal(staged, gaps.Count, inLibrary, gaps.Count - inLibrary);
    }

    // Requirement 3: stage a Keyboard Library object (transitively, same as PullFromPcg) back into
    // the Merge Window, so it can be rearranged and pushed to a different destination. The
    // LocalLibraryCache is supplied by the caller (LibrarianShellViewModel, which owns it) - this
    // pane only ever holds the MergeCache.
    public void PullFromLocal(LocalLibraryCache localCache, ObjLoc loc) =>
        PullFromLocal(localCache, new[] { loc });

    // Same one-gesture-one-status-line/one-undo-step reasoning as PullFromPcg's list overload.
    public void PullFromLocal(LocalLibraryCache localCache, IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        using var undo = Undoable(AppMessages.Librarian.Shell.UndoPulledIntoMerge(locs.Count));
        var staged = new HashSet<string>();
        var gaps = new HashSet<ObjLoc>();   // by address, same as PullFromPcg's - see DescribeGaps
        foreach (var loc in locs)
            foreach (var (missing, _) in _cache.PullFromLocal(localCache, loc, staged).Gaps)
                gaps.Add(missing);
        RefreshTree();
        StatusText = gaps.Count == 0
            ? AppMessages.Librarian.Merge.PulledIntoMerge(staged.Count)
            : AppMessages.Librarian.Merge.PulledWithGapsLocally(staged.Count, gaps.Count);
    }

    // Explicit "Clear Merge" - abandons everything still staged (see MergeCache.Clear's own
    // comment). Confirmation lives in code-behind, same split as ClearHistory.
    public void Clear()
    {
        using var undo = Undoable(AppMessages.Librarian.Shell.UndoClearedMerge);
        _cache.Clear();
        RefreshTree();
        StatusText = AppMessages.Librarian.Merge.Cleared;
    }

    // ── Undo support (Core/LocalLibrary/LibrarianUndo.cs) ────────────────────────────────
    // The recorder needs the staging state itself, plus a signal for WHEN it changes so it can
    // snapshot lazily; the MergeCache stays private (this pane owns it), so all three are
    // forwarded rather than handing the cache out.

    public MergeCacheSnapshot Snapshot() => _cache.Snapshot();

    public void Restore(MergeCacheSnapshot snapshot)
    {
        _cache.Restore(snapshot);
        RefreshTree();
    }

    public event Action CacheMutating
    {
        add => _cache.Mutating += value;
        remove => _cache.Mutating -= value;
    }

    public MergeEntry? TryGet(string contentHash) => _cache.TryGet(contentHash);

    // What everything staged still needs and nothing staged provides (see
    // MergeCache.UnresolvedRefSites) - listed in red at the top of the Object Dependencies panel
    // from the moment a pull leaves a gap, so it can be filled before Commit rather than after.
    public IReadOnlyList<MergeRefSite> UnresolvedDependencies => _cache.UnresolvedRefSites.ToList();

    // Everything currently staged, flat - the tree (Roots) is a DISPLAY shape that nests
    // dependencies under their referrers and hides a nested entry from its own type root, so it
    // can't be walked to answer "what is still staged". Auto-Fill needs exactly that flat answer
    // (LibrarianShellViewModel.AutoFillFromMerge) - but only for COUNTING; the walk that decides
    // placement order must use EntriesInDisplayOrder, never this raw enumeration.
    public IReadOnlyCollection<MergeEntry> Entries => _cache.Entries;

    // Everything staged, flat, in the same (source bank, source slot, hash) order the tree
    // displays - see InDisplayOrder below for why raw Entries enumeration can never be trusted:
    // after a first Auto-Fill's CommitPlacement removals recycle the backing Dictionary's array
    // slots (LIFO), the next pull's inserts land in those slots in reverse, so raw Entries
    // enumerates the re-copied content SCRAMBLED - for a whole-bank re-copy, exactly backwards.
    // Auto-Fill (LibrarianShellViewModel.AutoFillFromMerge) places whatever this returns, so a
    // second Auto-Fill lands in the same source order the Merge Window itself shows.
    public IReadOnlyList<MergeEntry> EntriesInDisplayOrder => InDisplayOrder(_cache.Entries).ToList();
    public (byte[] Body, List<MergeRefSite> Unresolved) ResolveReferencesForPlacement(
        MergeEntry entry, Func<int, string, ObjLoc?>? localLookup = null) =>
        _cache.ResolveReferencesForPlacement(entry, localLookup);

    // Right-click "Remove" - abandons specific staged entries WITHOUT placing them (unlike
    // Clear, which abandons everything and asks for confirmation first; removing a handful of
    // specific items is easily redone - just drag/pull them in again - so no confirmation
    // here). Does not touch a removed entry's own dependency children - those may still be
    // referenced by something else staged, or the user may want to keep them regardless.
    public void Remove(IReadOnlyList<string> contentHashes)
    {
        using var undo = Undoable(AppMessages.Librarian.Shell.UndoRemovedFromMerge(contentHashes.Count));
        int removed = 0;
        foreach (var hash in contentHashes)
            if (_cache.Remove(hash)) removed++;
        RefreshTree();
        StatusText = removed == 1 ? AppMessages.Librarian.Merge.RemovedOne : AppMessages.Librarian.Merge.RemovedMany(removed);
    }

    // Called by LibrarianShellViewModel.PlaceFromMerge ONLY after LocalEditOps has already
    // written `entry` into Keyboard Library - records where it landed (so any sibling entry
    // still staged, sharing the same dependency, patches to point at the SAME destination -
    // the "many-to-one" dedup payoff) and removes it from the Merge Window (move semantics:
    // this pane only ever shows what's still pending placement).
    public void CommitPlacement(string contentHash, ObjLoc destLoc)
    {
        // Scoped even for the single-item case: RecordPlacement and Remove each persist the
        // whole merge cache on their own, so without this one drop writes the file twice.
        // Nested inside CommitPlacements' own scope this is a no-op (_deferDepth).
        using var scope = DeferCommits();
        _cache.RecordPlacement(contentHash, destLoc);
        _cache.Remove(contentHash);
        RequestRefresh();
    }

    // Group form, for every caller that places more than one item (Auto-Fill, whole-bank
    // placement, the dedup sweep): one cache write and one tree rebuild for the whole group
    // instead of two writes plus a full tree reconstruction per item. Each placement still lands
    // one at a time inside the scope - see MergeCache.DeferSaves for why that ordering matters.
    public void CommitPlacements(IReadOnlyList<(string ContentHash, ObjLoc Dest)> placements)
    {
        if (placements.Count == 0) return;
        using var scope = DeferCommits();
        foreach (var (hash, dest) in placements) CommitPlacement(hash, dest);
    }

    // For a caller whose own loop has to interleave work between placements (DedupMergeGroup
    // decides each entry against the placements it has already made) and so can't hand the whole
    // group over at once.
    public IDisposable DeferCommits() => new CommitScope(this);

    int _deferDepth;
    bool _refreshPending;

    void RequestRefresh()
    {
        if (_deferDepth > 0) { _refreshPending = true; return; }
        RefreshTree();
    }

    sealed class CommitScope : IDisposable
    {
        readonly MergePaneViewModel _pane;
        readonly IDisposable _cacheScope;

        public CommitScope(MergePaneViewModel pane)
        {
            _pane = pane;
            _pane._deferDepth++;
            _cacheScope = pane._cache.DeferSaves();
        }

        public void Dispose()
        {
            _cacheScope.Dispose();
            if (--_pane._deferDepth > 0 || !_pane._refreshPending) return;
            _pane._refreshPending = false;
            _pane.RefreshTree();
        }
    }

    void RefreshTree()
    {
        // Captured before the rebuild - every pull/placement/remove calls RefreshTree(), and
        // without this a newly-pulled item would collapse whatever was already expanded (fresh
        // ObjectTreeNode instances all default IsExpanded=false).
        var expandedKeys = ObjectTreeNode.CollectExpandedKeys(Roots);
        Roots.Clear();
        var byHash = _cache.Entries.ToDictionary(e => e.ContentHash);

        // BankRef here isn't a real numbered bank (Merge Window has none) - it's the "select
        // this whole group" identity a bank-equivalent selection needs (see
        // LibrarianShellWindow.xaml.cs's PaneSelection and LibrarianShellViewModel.
        // PlaceMergeGroupSequentially), reusing the exact mechanism Local/PCG bank nodes
        // already have rather than inventing a second concept for the same idea. The type-root's
        // own bank is the sentinel -1 ("everything of this type"), deliberately NOT a real bank
        // number, so it never collides with the per-source-bank sub-groups added below
        // (requirement 4) - a bank group for I-A would otherwise share bank 0 with this root and
        // break selection identity.
        const int allBanksSentinel = -1;
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, allBanksSentinel));
        var combisRoot = new ObjectTreeNode("Combis", bankRef: (LibObj.Combi, allBanksSentinel));
        var programsRoot = new ObjectTreeNode("Programs", bankRef: (LibObj.Program, allBanksSentinel));
        var drumKitsRoot = new ObjectTreeNode("Drum Kits", bankRef: (LibObj.DrumKit, allBanksSentinel));
        var waveSequencesRoot = new ObjectTreeNode("Wave Sequences", bankRef: (LibObj.WaveSequence, allBanksSentinel));

        // A Set List is always effectively "top-level" - nothing else in this reference
        // graph ever points at one (see ObjectReferenceWalker) - so every staged Set List
        // shows up here, fully expanded.
        foreach (var e in InDisplayOrder(_cache.Entries.Where(e => e.ObjType == LibObj.SetList)))
            setListsRoot.Children.Add(MakeNodeWithChildren(e, byHash));

        // Combis/Programs/Drum Kits/Wave Sequences show at THEIR OWN top-level section when the
        // user explicitly pulled them, OR when nothing still staged references them anymore. That
        // second case is what keeps a Set List's (or Program's - a Drum Track/oscillator-zone
        // reference) own dependencies from silently vanishing the moment their referrer gets
        // placed and removed - they were never "top-level pulls," so without this they'd only
        // ever have been reachable by nesting under that referrer, which no longer exists in the
        // tree at all once it's placed: still fully staged, but with no way for the user to find
        // or place them (see this session's bug report - a Program's Wave Sequence dependency had
        // no tree node of its own at all until this section covered Drum Kit/Wave Sequence too).
        // A dependency that's still nested under something ELSE still staged is deliberately left
        // nested-only here (no duplicate flat entry) - that's still a real referrer, and is what
        // makes a genuinely-shared dependency's yellow marker show up wherever it's actually used.
        //
        // Grouped by SOURCE bank (requirement 4) so a whole bank is a single selectable unit
        // that can be placed - or, for Programs, copied across an EXi/HD-1 boundary - in one
        // action. See AddBankGroups.
        AddBankGroups(combisRoot, LibObj.Combi, byHash);
        AddBankGroups(programsRoot, LibObj.Program, byHash);
        AddBankGroups(drumKitsRoot, LibObj.DrumKit, byHash);
        AddBankGroups(waveSequencesRoot, LibObj.WaveSequence, byHash);

        if (setListsRoot.Children.Count > 0) Roots.Add(setListsRoot);
        if (combisRoot.Children.Count > 0) Roots.Add(combisRoot);
        if (programsRoot.Children.Count > 0) Roots.Add(programsRoot);
        if (drumKitsRoot.Children.Count > 0) Roots.Add(drumKitsRoot);
        if (waveSequencesRoot.Children.Count > 0) Roots.Add(waveSequencesRoot);
        ObjectTreeNode.RestoreExpandedKeys(Roots, expandedKeys);
        RefreshTally();
        TreeRefreshed?.Invoke();
    }

    // The tree only shows a type root once something of that type is staged, and a nested
    // dependency is hidden from its own root entirely (see the display rules above), so "how much
    // is actually staged" can't be read off it. This counts the flat cache instead - every staged
    // entry, at any depth. Missing dependencies are counted by distinct ADDRESS, matching the
    // pull's own gap report: one missing Program referenced by eight staged Combis is one thing
    // for the user to go and find.
    void RefreshTally()
    {
        int Count(int objType) => _cache.Entries.Count(e => e.ObjType == objType);
        TallyText = $"Programs: {Count(LibObj.Program)}   Combis: {Count(LibObj.Combi)}   " +
                    $"Drum Kits: {Count(LibObj.DrumKit)}   Wave Seq: {Count(LibObj.WaveSequence)}   " +
                    $"Set Lists: {Count(LibObj.SetList)}";
        MissingDependencyCount = _cache.UnresolvedRefSites.Select(s => s.TargetLoc).Distinct().Count();
    }

    // Top-level Combis/Programs grouped by their SOURCE bank (requirement 4) so a whole bank is
    // a single selectable/draggable unit - its BankRef expands to every content hash under it,
    // exactly like a Local/PCG bank does, and a whole bank can be placed (or, for Programs,
    // copied across an EXi/HD-1 boundary) at once. A Program bank's label carries the
    // "(EXi)"/"(HD-1)" format suffix (a real Program bank is homogeneously one format - see
    // ProgramFormatConverter), mirroring the Local pane's own bank labels and making an
    // unexpected format immediately visible. Grouped by the entry's FIRST origin's bank.
    void AddBankGroups(ObjectTreeNode root, int objType, Dictionary<string, MergeEntry> byHash)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        // "Top-level pull, OR nothing still staged references it anymore" - an entry counts as
        // still-referenced only if that referrer is ITSELF still staged (Remove/CommitPlacement
        // never retroactively clean up a placed/removed entry's ReferencedBy bookkeeping, so it's
        // recomputed fresh against the CURRENT snapshot here rather than trusted at face value).
        // InDisplayOrder sorts BEFORE grouping: GroupBy keeps each group's elements in
        // source-sequence order, so the sorted walk makes children appear in source-slot
        // order inside their bank group (and groups themselves end up bank-ordered, made
        // explicit by the OrderBy below regardless).
        var groups = InDisplayOrder(_cache.Entries
                .Where(e => e.ObjType == objType && (e.IsTopLevelPull || !e.ReferencedBy.Any(byHash.ContainsKey))))
            .GroupBy(PrimaryBank)
            .OrderBy(g => g.Key);
        foreach (var group in groups)
        {
            string label = objType == LibObj.Program
                ? $"{descriptor.BankLabel(group.Key)} ({(group.First().Body.Length == ProgramFormatConverter.WireSizeExi ? "EXi" : "HD-1")})"
                : descriptor.BankLabel(group.Key);
            var bankNode = new ObjectTreeNode(label, bankRef: (objType, group.Key));
            // Combi (timbre -> Program) and Program (Drum Track -> Program; oscillator zone ->
            // Wave Sequence/Drum Kit) are the only referrer types (ObjectTypeRegistry.IsReferrer) -
            // Drum Kit/Wave Sequence never have children, so MakeNode alone is correct for them.
            bool isReferrer = objType is LibObj.Combi or LibObj.Program;
            foreach (var e in group)
                bankNode.Children.Add(isReferrer ? MakeNodeWithChildren(e, byHash) : MakeNode(e, byHash));
            root.Children.Add(bankNode);
        }
    }

    // The bank a merge entry is grouped under - its first origin's source bank (0 if, somehow,
    // it has no origin at all). Dedup can give one entry multiple origins; the first is a stable,
    // good-enough choice for grouping (the common case is a single-source pull anyway).
    static int PrimaryBank(MergeEntry entry) => entry.Origins.Count > 0 ? entry.Origins[0].SourceLoc.Bank : 0;

    // Same idea, for the slot within that bank.
    static int PrimaryNumber(MergeEntry entry) => entry.Origins.Count > 0 ? entry.Origins[0].SourceLoc.Number : 0;

    // Canonical display order for a cache walk. MergeCache's store is a plain Dictionary,
    // whose enumeration order equals insertion order ONLY until the first removal: a removed
    // entry's array slot gets recycled by the next insert, so after any placement/removal
    // (an Auto-Fill sweep's CommitPlacement, a manual Remove) the NEXT pull can surface
    // mid-list instead of at the end - "pulled another object in right after Auto-Fill and
    // the Merge tree shows it out of order". Never trust raw enumeration order for display:
    // sort by source bank, then source slot; content hash is only a deterministic tiebreak
    // for the rare same-slot collision (two different files sharing a filename).
    static IOrderedEnumerable<MergeEntry> InDisplayOrder(IEnumerable<MergeEntry> entries) =>
        entries.OrderBy(PrimaryBank)
               .ThenBy(PrimaryNumber)
               .ThenBy(e => e.ContentHash, StringComparer.Ordinal);

    ObjectTreeNode MakeNodeWithChildren(MergeEntry entry, Dictionary<string, MergeEntry> byHash)
    {
        var node = MakeNode(entry, byHash);
        foreach (var site in entry.RefSites)
        {
            if (site.ResolvedContentHash == null || !byHash.TryGetValue(site.ResolvedContentHash, out var dep)) continue;
            // A Combi or Program child may itself have further children (folder-within-folder,
            // e.g. a Program's own Drum Track pointing at another Program); Drum Kit/Wave
            // Sequence never do (ObjectTypeRegistry.IsReferrer is false for both).
            node.Children.Add(dep.ObjType is LibObj.Combi or LibObj.Program ? MakeNodeWithChildren(dep, byHash) : MakeNode(dep, byHash));
        }
        return node;
    }

    ObjectTreeNode MakeNode(MergeEntry entry, Dictionary<string, MergeEntry> byHash)
    {
        string name = string.IsNullOrEmpty(entry.DisplayName) ? "(unnamed)" : entry.DisplayName;
        string originSummary = entry.Origins.Count == 1
            ? entry.Origins[0].PcgFileName
            : $"{entry.Origins.Count} source(s)";
        // Counted against the CURRENT snapshot, not entry.ReferencedBy.Count directly - a
        // referrer that's since been placed and removed must not keep this marked "shared"
        // (same staleness reasoning as RefreshTree's own HasCurrentReferrer).
        int currentReferrers = entry.ReferencedBy.Count(byHash.ContainsKey);
        // entry.Body is already fully in memory (a staged MergeEntry) - same "no blob-read cost"
        // reasoning as PcgPaneViewModel.MakeLeafNode, so this is computed fresh, not cached.
        bool hasSampleDep = SampleReferenceWalker.Walk(entry.ObjType, entry.Body).Count > 0;
        return new ObjectTreeNode($"{name}  [{originSummary}]", mergeContentHash: entry.ContentHash, hasSampleDependency: hasSampleDep)
        {
            SharedTooltip = currentReferrers > 1 ? "Shared by multiple Combis/Songs" : null,
        };
    }
}
