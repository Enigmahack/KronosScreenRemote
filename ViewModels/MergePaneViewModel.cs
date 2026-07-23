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
            ? $"Pulled {added.Count} object(s) into the Merge Window."
            : $"Pulled {added.Count} object(s); {gaps.Count} dependency reference(s) not found in this PCG — load another PCG that has them and pull it in.";
    }

    // Explicit "Clear Merge" — abandons everything still staged (see MergeCache.Clear's own
    // comment). Confirmation lives in code-behind, same split as ClearHistory.
    public void Clear()
    {
        _cache.Clear();
        RefreshTree();
        StatusText = "Merge Window cleared.";
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
        StatusText = removed == 1 ? "Removed 1 item from the Merge Window." : $"Removed {removed} item(s) from the Merge Window.";
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
        // already have rather than inventing a second concept for the same idea. Bank number is
        // always 0 — meaningless for Merge Window, just a placeholder so the tuple identity is
        // stable across rebuilds.
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, 0));
        var combisRoot = new ObjectTreeNode("Combis", bankRef: (LibObj.Combi, 0));
        var programsRoot = new ObjectTreeNode("Programs", bankRef: (LibObj.Program, 0));

        // An entry counts as "still referenced" only if that referrer is ITSELF still staged —
        // Remove/CommitPlacement never retroactively clean up a placed/removed entry's own
        // ReferencedBy bookkeeping on whatever IT referenced (harmless on its own — the same
        // "an unreferenced blob is accepted debris, not a correctness problem" spirit MergeCache's
        // class doc already applies to blobs), so this is computed fresh against the CURRENT
        // snapshot every refresh rather than trusted at face value.
        bool HasCurrentReferrer(MergeEntry e) => e.ReferencedBy.Any(byHash.ContainsKey);

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
        foreach (var e in _cache.Entries.Where(e => e.ObjType == LibObj.Combi && (e.IsTopLevelPull || !HasCurrentReferrer(e))))
            combisRoot.Children.Add(MakeNodeWithChildren(e, byHash));

        // Grouped by actual wire format rather than dumped flat — a real Program bank is
        // always homogeneously one format or the other (see ProgramFormatConverter's own
        // class comment), so mirroring that split here matches what the same Program will
        // look like once placed, and makes an unexpected format immediately visible.
        var hd1Root = new ObjectTreeNode("HD-1");
        var exiRoot = new ObjectTreeNode("EXi");
        foreach (var e in _cache.Entries.Where(e => e.ObjType == LibObj.Program && (e.IsTopLevelPull || !HasCurrentReferrer(e))))
            (e.Body.Length == ProgramFormatConverter.WireSizeExi ? exiRoot : hd1Root).Children.Add(MakeNode(e, byHash));
        if (hd1Root.Children.Count > 0) programsRoot.Children.Add(hd1Root);
        if (exiRoot.Children.Count > 0) programsRoot.Children.Add(exiRoot);

        if (setListsRoot.Children.Count > 0) Roots.Add(setListsRoot);
        if (combisRoot.Children.Count > 0) Roots.Add(combisRoot);
        if (programsRoot.Children.Count > 0) Roots.Add(programsRoot);
        ObjectTreeNode.RestoreExpandedKeys(Roots, expandedKeys);
        TreeRefreshed?.Invoke();
    }

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
