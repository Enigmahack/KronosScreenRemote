using System.Collections.ObjectModel;

namespace KronosScreenRemote.ViewModels;

// The shared Programs / Combis / Set Lists tree SHAPE for the Local and PCG panes' RefreshTree.
// Both panes present the identical layout — a bank sub-node per populated Program/Combi bank,
// with Set Lists flat under their own type root (no inner bank node, since a Set List has no
// real bank concept) — and differ only in WHERE the objects come from (a LocalLibraryCache vs. a
// loaded PcgLibraryView) and how each leaf/bank node is labelled and decorated. This holds that
// structure once, parameterized by those two concerns, so a layout rule (the Set-List "no inner
// bank node" case that regressed once, the bankRef identities, ordering, the empty-root policy)
// is fixed in a single place. The Merge pane's tree is genuinely different — dependency nesting,
// the HD-1/EXi split, graduation of orphaned dependencies — and keeps its own RefreshTree.
static class ObjectTreeScaffold
{
    // One Program/Combi bank's populated leaves, already in intended display order.
    public readonly record struct Bank(int Number, IReadOnlyList<ObjLoc> Locs);

    // Rebuild the three type roots into `roots`, preserving expansion state across the rebuild.
    //   banksFor       — the populated banks for a Program/Combi object type, in display order
    //   setListLocs    — the Set List leaves (flat, no bank grouping), in display order
    //   makeLeaf       — build a leaf node for one ObjLoc (pane-specific label/decoration)
    //   bankLabel      — label for a Program/Combi bank node, given its populated Bank
    //   keepEmptyRoots — Local shows all three type roots even when empty; PCG shows only the
    //                    non-empty ones (a type root with no populated bank has no children).
    public static void Rebuild(
        ObservableCollection<ObjectTreeNode> roots,
        Func<int, IReadOnlyList<Bank>> banksFor,
        IReadOnlyList<ObjLoc> setListLocs,
        Func<ObjLoc, ObjectTreeNode> makeLeaf,
        Func<int, Bank, string> bankLabel,
        bool keepEmptyRoots)
    {
        var expandedKeys = ObjectTreeNode.CollectExpandedKeys(roots);
        roots.Clear();

        var programsRoot = BuildTyped("Programs", LibObj.Program, banksFor, makeLeaf, bankLabel);
        var combisRoot   = BuildTyped("Combis", LibObj.Combi, banksFor, makeLeaf, bankLabel);

        // Set Lists have no bank concept (a flat, single group) — the type root itself carries
        // the bankRef identity and leaves nest directly under it, NOT through an inner bank node.
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, 0));
        foreach (var loc in setListLocs) setListsRoot.Children.Add(makeLeaf(loc));
        setListsRoot.IsDirty = setListsRoot.Children.Any(c => c.IsDirty);

        AddRoot(roots, programsRoot, keepEmptyRoots);
        AddRoot(roots, combisRoot, keepEmptyRoots);
        AddRoot(roots, setListsRoot, keepEmptyRoots);
        ObjectTreeNode.RestoreExpandedKeys(roots, expandedKeys);
    }

    static ObjectTreeNode BuildTyped(
        string rootLabel, int objType,
        Func<int, IReadOnlyList<Bank>> banksFor,
        Func<ObjLoc, ObjectTreeNode> makeLeaf,
        Func<int, Bank, string> bankLabel)
    {
        // typeRootObjType (not bankRef — this level has no bank): lets a drop landing on the
        // "Programs"/"Combis" header resolve to a bank with room instead of being refused. See
        // ObjectTreeNode.TypeRootObjType.
        var typeRoot = new ObjectTreeNode(rootLabel, typeRootObjType: objType);
        foreach (var bank in banksFor(objType))
        {
            if (bank.Locs.Count == 0) continue;   // an empty bank never becomes a node
            var bankNode = new ObjectTreeNode(bankLabel(objType, bank), bankRef: (objType, bank.Number));
            foreach (var loc in bank.Locs) bankNode.Children.Add(makeLeaf(loc));
            // Bubbled up from the leaves just added — a bank node otherwise defaults to
            // IsDirty=false forever (nothing else ever sets it), so a locally-changed leaf
            // sitting inside a COLLAPSED bank had no way to show its red dot (see
            // ObjectTreeNode's IsDirty doc) until the bank was expanded. PCG leaves never set
            // IsDirty (read-only pane), so this is a harmless no-op there — always false.
            bankNode.IsDirty = bankNode.Children.Any(c => c.IsDirty);
            typeRoot.Children.Add(bankNode);
        }
        // Same bubble-up, one level higher — a whole type root ("Programs"/"Combis") collapsed
        // at the window level should also flag that SOMETHING inside changed.
        typeRoot.IsDirty = typeRoot.Children.Any(c => c.IsDirty);
        return typeRoot;
    }

    static void AddRoot(ObservableCollection<ObjectTreeNode> roots, ObjectTreeNode root, bool keepEmpty)
    {
        if (keepEmpty || root.Children.Count > 0) roots.Add(root);
    }
}
