using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// The Merge Window pane's view-state: a staging area between "loaded PCG file(s)" and Local
// Library (see MergeCache's own class doc for the full design). Owns the MergeCache and
// builds a folder-style tree from it — Set Lists (each expanding into its Combis, which
// expand into their own Program dependencies), plus independent top-level Combis/Programs
// sections for anything pulled standalone, mirroring Local Library's own type-grouped tree.
// Placement into Local Library (the one address-sensitive, manual step) is driven by
// LibrarianShellViewModel.PlaceFromMerge, same split as PlaceFromPcg/BatchPlaceFromPcg —
// this pane owns staging, LibrarianShellViewModel owns the cross-pane action.
partial class MergePaneViewModel : ObservableObject
{
    readonly MergeCache _cache;

    public ObservableCollection<ObjectTreeNode> Roots { get; } = new();

    // Raised at the end of RefreshTree() — see LocalLibraryPaneViewModel's own TreeRefreshed for
    // why: every pull/clear/placement rebuilds Roots from scratch.
    public event Action? TreeRefreshed;

    [ObservableProperty] string statusText = "";

    public MergePaneViewModel(MergeCache cache)
    {
        _cache = cache;
        RefreshTree();
    }

    // Fully automatic and transitive (see MergeCache.PullFromPcg) — pulling a Set List or
    // Combi pulls in everything it references that resolves within `pcg`, with no further
    // clicks. Anything that doesn't resolve is reported in StatusText, same "flag, don't
    // block" contract DependencyScanner's existing gap-tracking already uses.
    public void PullFromPcg(PcgLibraryView pcg, string pcgFileName, ObjLoc loc)
    {
        var (added, gaps) = _cache.PullFromPcg(pcg, pcgFileName, loc);
        RefreshTree();
        StatusText = gaps.Count == 0
            ? AppMessages.Librarian.Merge.PulledIntoMerge(added.Count)
            : AppMessages.Librarian.Merge.PulledWithGapsInPcg(added.Count, gaps.Count);
    }

    // Requirement 3: stage a Local Library object (transitively, same as PullFromPcg) back into
    // the Merge Window, so it can be rearranged and pushed to a different destination. The
    // LocalLibraryCache is supplied by the caller (LibrarianShellViewModel, which owns it) — this
    // pane only ever holds the MergeCache.
    public void PullFromLocal(LocalLibraryCache localCache, ObjLoc loc)
    {
        var (added, gaps) = _cache.PullFromLocal(localCache, loc);
        RefreshTree();
        StatusText = gaps.Count == 0
            ? AppMessages.Librarian.Merge.PulledIntoMerge(added.Count)
            : AppMessages.Librarian.Merge.PulledWithGapsLocally(added.Count, gaps.Count);
    }

    // Explicit "Clear Merge" — abandons everything still staged (see MergeCache.Clear's own
    // comment). Confirmation lives in code-behind, same split as ClearHistory.
    public void Clear()
    {
        _cache.Clear();
        RefreshTree();
        StatusText = AppMessages.Librarian.Merge.Cleared;
    }

    public MergeEntry? TryGet(string contentHash) => _cache.TryGet(contentHash);
    public (byte[] Body, List<MergeRefSite> Unresolved) ResolveReferencesForPlacement(
        MergeEntry entry, Func<int, string, ObjLoc?>? localLookup = null) =>
        _cache.ResolveReferencesForPlacement(entry, localLookup);

    // Right-click "Remove" — abandons specific staged entries WITHOUT placing them (unlike
    // Clear, which abandons everything and asks for confirmation first; removing a handful of
    // specific items is easily redone — just drag/pull them in again — so no confirmation
    // here). Does not touch a removed entry's own dependency children — those may still be
    // referenced by something else staged, or the user may want to keep them regardless.
    public void Remove(IReadOnlyList<string> contentHashes)
    {
        int removed = 0;
        foreach (var hash in contentHashes)
            if (_cache.Remove(hash)) removed++;
        RefreshTree();
        StatusText = removed == 1 ? AppMessages.Librarian.Merge.RemovedOne : AppMessages.Librarian.Merge.RemovedMany(removed);
    }

    // Called by LibrarianShellViewModel.PlaceFromMerge ONLY after LocalEditOps has already
    // written `entry` into Local Library — records where it landed (so any sibling entry
    // still staged, sharing the same dependency, patches to point at the SAME destination —
    // the "many-to-one" dedup payoff) and removes it from the Merge Window (move semantics:
    // this pane only ever shows what's still pending placement).
    public void CommitPlacement(string contentHash, ObjLoc destLoc)
    {
        _cache.RecordPlacement(contentHash, destLoc);
        _cache.Remove(contentHash);
        RefreshTree();
    }

    void RefreshTree()
    {
        // Captured before the rebuild — every pull/placement/remove calls RefreshTree(), and
        // without this a newly-pulled item would collapse whatever was already expanded (fresh
        // ObjectTreeNode instances all default IsExpanded=false).
        var expandedKeys = ObjectTreeNode.CollectExpandedKeys(Roots);
        Roots.Clear();
        var byHash = _cache.Entries.ToDictionary(e => e.ContentHash);

        // BankRef here isn't a real numbered bank (Merge Window has none) — it's the "select
        // this whole group" identity a bank-equivalent selection needs (see
        // LibrarianShellWindow.xaml.cs's PaneSelection and LibrarianShellViewModel.
        // PlaceMergeGroupSequentially), reusing the exact mechanism Local/PCG bank nodes
        // already have rather than inventing a second concept for the same idea. The type-root's
        // own bank is the sentinel -1 ("everything of this type"), deliberately NOT a real bank
        // number, so it never collides with the per-source-bank sub-groups added below
        // (requirement 4) — a bank group for I-A would otherwise share bank 0 with this root and
        // break selection identity.
        const int allBanksSentinel = -1;
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, allBanksSentinel));
        var combisRoot = new ObjectTreeNode("Combis", bankRef: (LibObj.Combi, allBanksSentinel));
        var programsRoot = new ObjectTreeNode("Programs", bankRef: (LibObj.Program, allBanksSentinel));

        // A Set List is always effectively "top-level" — nothing else in this reference
        // graph ever points at one (see ObjectReferenceWalker) — so every staged Set List
        // shows up here, fully expanded.
        foreach (var e in _cache.Entries.Where(e => e.ObjType == LibObj.SetList))
            setListsRoot.Children.Add(MakeNodeWithChildren(e, byHash));

        // Combis/Programs show at THEIR OWN top-level section when the user explicitly pulled
        // them, OR when nothing still staged references them anymore. That second case is what
        // keeps a Set List's own dependencies from silently vanishing the moment the Set List
        // itself gets placed and removed — they were never "top-level pulls," so without this
        // they'd only ever have been reachable by nesting under the Set List, which no longer
        // exists in the tree at all once it's placed: still fully staged, but with no way for
        // the user to find or place them. A dependency that's still nested under something ELSE
        // still staged is deliberately left nested-only here (no duplicate flat entry) — that's
        // still a real referrer, and is what makes a genuinely-shared dependency's yellow
        // marker show up wherever it's actually used.
        //
        // Grouped by SOURCE bank (requirement 4) so a whole bank is a single selectable unit
        // that can be placed — or, for Programs, copied across an EXi/HD-1 boundary — in one
        // action. See AddBankGroups.
        AddBankGroups(combisRoot, LibObj.Combi, byHash);
        AddBankGroups(programsRoot, LibObj.Program, byHash);

        if (setListsRoot.Children.Count > 0) Roots.Add(setListsRoot);
        if (combisRoot.Children.Count > 0) Roots.Add(combisRoot);
        if (programsRoot.Children.Count > 0) Roots.Add(programsRoot);
        ObjectTreeNode.RestoreExpandedKeys(Roots, expandedKeys);
        TreeRefreshed?.Invoke();
    }

    // Top-level Combis/Programs grouped by their SOURCE bank (requirement 4) so a whole bank is
    // a single selectable/draggable unit — its BankRef expands to every content hash under it,
    // exactly like a Local/PCG bank does, and a whole bank can be placed (or, for Programs,
    // copied across an EXi/HD-1 boundary) at once. A Program bank's label carries the
    // "(EXi)"/"(HD-1)" format suffix (a real Program bank is homogeneously one format — see
    // ProgramFormatConverter), mirroring the Local pane's own bank labels and making an
    // unexpected format immediately visible. Grouped by the entry's FIRST origin's bank.
    void AddBankGroups(ObjectTreeNode root, int objType, Dictionary<string, MergeEntry> byHash)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        // "Top-level pull, OR nothing still staged references it anymore" — an entry counts as
        // still-referenced only if that referrer is ITSELF still staged (Remove/CommitPlacement
        // never retroactively clean up a placed/removed entry's ReferencedBy bookkeeping, so it's
        // recomputed fresh against the CURRENT snapshot here rather than trusted at face value).
        var groups = _cache.Entries
            .Where(e => e.ObjType == objType && (e.IsTopLevelPull || !e.ReferencedBy.Any(byHash.ContainsKey)))
            .GroupBy(PrimaryBank)
            .OrderBy(g => g.Key);
        foreach (var group in groups)
        {
            string label = objType == LibObj.Program
                ? $"{descriptor.BankLabel(group.Key)} ({(group.First().Body.Length == ProgramFormatConverter.WireSizeExi ? "EXi" : "HD-1")})"
                : descriptor.BankLabel(group.Key);
            var bankNode = new ObjectTreeNode(label, bankRef: (objType, group.Key));
            foreach (var e in group)
                bankNode.Children.Add(objType == LibObj.Combi ? MakeNodeWithChildren(e, byHash) : MakeNode(e, byHash));
            root.Children.Add(bankNode);
        }
    }

    // The bank a merge entry is grouped under — its first origin's source bank (0 if, somehow,
    // it has no origin at all). Dedup can give one entry multiple origins; the first is a stable,
    // good-enough choice for grouping (the common case is a single-source pull anyway).
    static int PrimaryBank(MergeEntry entry) => entry.Origins.Count > 0 ? entry.Origins[0].SourceLoc.Bank : 0;

    ObjectTreeNode MakeNodeWithChildren(MergeEntry entry, Dictionary<string, MergeEntry> byHash)
    {
        var node = MakeNode(entry, byHash);
        foreach (var site in entry.RefSites)
        {
            if (site.ResolvedContentHash == null || !byHash.TryGetValue(site.ResolvedContentHash, out var dep)) continue;
            // A Combi child may itself have Program children (folder-within-folder); a
            // Program child never does (ObjectReferenceWalker never yields refs for one).
            node.Children.Add(dep.ObjType == LibObj.Combi ? MakeNodeWithChildren(dep, byHash) : MakeNode(dep, byHash));
        }
        return node;
    }

    ObjectTreeNode MakeNode(MergeEntry entry, Dictionary<string, MergeEntry> byHash)
    {
        string name = string.IsNullOrEmpty(entry.DisplayName) ? "(unnamed)" : entry.DisplayName;
        string originSummary = entry.Origins.Count == 1
            ? entry.Origins[0].PcgFileName
            : $"{entry.Origins.Count} source(s)";
        // Counted against the CURRENT snapshot, not entry.ReferencedBy.Count directly — a
        // referrer that's since been placed and removed must not keep this marked "shared"
        // (same staleness reasoning as RefreshTree's own HasCurrentReferrer).
        int currentReferrers = entry.ReferencedBy.Count(byHash.ContainsKey);
        return new ObjectTreeNode($"{name}  [{originSummary}]", mergeContentHash: entry.ContentHash)
        {
            SharedTooltip = currentReferrers > 1 ? "Shared by multiple Combis/Songs" : null,
        };
    }
}
