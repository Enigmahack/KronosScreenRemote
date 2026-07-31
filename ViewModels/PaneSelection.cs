using System.Windows.Input;

namespace KronosScreenRemote.ViewModels;

// Selection tracking for one pane's tree (Local, PCG, or Merge) - click/Ctrl+click/Shift-range
// mechanics identical across all three, generalized to treat a BankRef node (a bank, or Merge
// Window's own type-grouping "bank-equivalent" - see MergePaneViewModel.RefreshTree) the same
// way a leaf node (Loc, or Merge's MergeContentHash) is treated. Kept as one shared class
// instead of three near-identical copies, which is exactly how the bug this replaces started:
// Merge Window used to have no selection tracking at all (nothing ever set IsSelected), while
// Local/PCG's own hand-duplicated copies quietly drifted - neither survived a RefreshTree()
// rebuild (which throws away every ObjectTreeNode and builds fresh ones), leaving a stale
// selection pointing at orphaned objects nothing on screen still represents.
//
// Lives in ViewModels/ (not the LibrarianShellWindow code-behind it's driven from) precisely
// because it's WPF-decoupled - its only System.Windows dependency is the ModifierKeys enum
// passed in by the caller - so PaneSelectionSelfTests can exercise the whole click/range/
// reconcile state machine off-hardware, closing what was the largest untested-logic gap in the
// new librarian stack.
sealed class PaneSelection
{
    public readonly HashSet<ObjectTreeNode> Items = new();
    public ObjectTreeNode? Anchor;

    readonly Func<ObjectTreeNode, ObjectTreeNode?> _findParent;
    readonly Action<string> _reportStatus;

    // Extra pane-specific refusal layered on top of the universal bank-vs-leaf check below
    // (PCG's own "can't mix Programs, Combis, and Set Lists"). Given (any current member,
    // candidate) -> a refusal message, or null to allow.
    public Func<ObjectTreeNode, ObjectTreeNode, string?>? ExtraMixCheck;

    // The other two panes' selections - clearing THIS pane's selection also clears these
    // (cross-pane exclusivity: only one pane's selection is ever active at a time). Set once,
    // right after all three are constructed (see LibrarianShellWindow's own constructor).
    public PaneSelection[] Others = Array.Empty<PaneSelection>();

    // Fired at the end of every public entry point that can change Items - the "Object
    // Dependencies" panel subscribes on all three panes' instances (see LibrarianShellWindow's
    // own constructor) to recompute from whichever pane currently holds the selection.
    public event Action? SelectionChanged;

    public PaneSelection(Func<ObjectTreeNode, ObjectTreeNode?> findParent, Action<string> reportStatus)
    {
        _findParent = findParent;
        _reportStatus = reportStatus;
    }

    static bool IsBank(ObjectTreeNode n) => n.BankRef != null;

    public void Clear()
    {
        if (Items.Count == 0) return;
        foreach (var n in Items) n.IsSelected = false;
        Items.Clear();
        Anchor = null;
        SelectionChanged?.Invoke();
    }

    void ClearOthers()
    {
        foreach (var other in Others) other.Clear();
    }

    void ReplaceWith(ObjectTreeNode node)
    {
        Clear();
        Items.Add(node);
        node.IsSelected = true;
        Anchor = node;
    }

    // Plain click / Ctrl+click / Shift-range - the click-down gesture. `modifiers` is passed in
    // (rather than read from Keyboard.Modifiers directly) so this class stays independent of
    // WPF's global input state.
    public void HandleClick(ObjectTreeNode node, ModifierKeys modifiers)
    {
        ClearOthers();

        if (modifiers.HasFlag(ModifierKeys.Shift) && Anchor != null)
        {
            SelectRange(Anchor, node);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Items.Contains(node)) { Items.Remove(node); node.IsSelected = false; }
            else if (Items.Count > 0 && IsBank(Items.First()) != IsBank(node))
                _reportStatus("Can't mix a bank with individual items in one selection.");
            else if (Items.Count > 0 && ExtraMixCheck?.Invoke(Items.First(), node) is { } refusal)
                _reportStatus(refusal);
            else { Items.Add(node); node.IsSelected = true; }
            Anchor = node;
        }
        else if (Items.Contains(node) && Items.Count > 1)
        {
            // Leave the multi-selection intact for now - dragging one of several selected
            // items should move/copy the whole group; HandleMouseUpWithoutDrag collapses to
            // just this node if no drag actually happened.
        }
        else
        {
            ReplaceWith(node);
        }
        SelectionChanged?.Invoke();
    }

    // Mouse-up without an intervening drag: a plain click-and-release on a node already part of
    // a multi-selection collapses to just that node (matches Explorer - mouse-down alone keeps
    // the group armed in case you drag it; releasing without dragging narrows to just this one).
    public void HandleMouseUpWithoutDrag(ObjectTreeNode node, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift)
            && Items.Count > 1 && Items.Contains(node))
            ReplaceWith(node);
        SelectionChanged?.Invoke();
    }

    // Right-click: selects first (Explorer convention), then the caller opens its ContextMenu.
    // Keeps an existing multi-selection intact if the target is already part of it; otherwise
    // replaces the selection with just the target.
    public void HandleRightClick(ObjectTreeNode node)
    {
        ClearOthers();
        if (Items.Contains(node)) { SelectionChanged?.Invoke(); return; }
        ReplaceWith(node);
        SelectionChanged?.Invoke();
    }

    void SelectRange(ObjectTreeNode anchor, ObjectTreeNode target)
    {
        var parent = _findParent(anchor);
        if (parent == null || _findParent(target) != parent)
        {
            ReplaceWith(target);
            return;
        }

        Clear();
        int ai = parent.Children.IndexOf(anchor), ti = parent.Children.IndexOf(target);
        int lo = Math.Min(ai, ti), hi = Math.Max(ai, ti);
        for (int i = lo; i <= hi; i++)
        {
            var n = parent.Children[i];
            Items.Add(n);
            n.IsSelected = true;
        }
        Anchor = anchor;
    }

    // Re-binds this selection to the tree's NEW node instances after a RefreshTree() rebuild,
    // matched by stable identity (Loc/BankRef/MergeContentHash - never the old object
    // reference, which RefreshTree just discarded). This is what actually fixes selection
    // surviving an edit: without it, a deleted-then-reselected row (or any post-edit click)
    // would silently act on stale, orphaned node objects nothing on screen still represents.
    public void ReconcileAfterRefresh(IEnumerable<ObjectTreeNode> newRoots)
    {
        if (Items.Count == 0) return;

        var wantedLocs = new HashSet<ObjLoc>(Items.Where(n => n.Loc != null).Select(n => n.Loc!.Value));
        var wantedBanks = new HashSet<(int, int)>(Items.Where(n => n.BankRef != null).Select(n => n.BankRef!.Value));
        var wantedHashes = new HashSet<string>(Items.Where(n => n.MergeContentHash != null).Select(n => n.MergeContentHash!));
        var anchorLoc = Anchor?.Loc;
        var anchorBank = Anchor?.BankRef;
        var anchorHash = Anchor?.MergeContentHash;

        Items.Clear();
        Anchor = null;
        foreach (var root in newRoots) Walk(root);

        void Walk(ObjectTreeNode node)
        {
            bool matches = (node.Loc is { } l && wantedLocs.Contains(l))
                || (node.BankRef is { } b && wantedBanks.Contains(b))
                || (node.MergeContentHash is { } h && wantedHashes.Contains(h));
            if (matches)
            {
                Items.Add(node);
                node.IsSelected = true;
                bool wasAnchor = (node.Loc is { } l2 && anchorLoc == l2)
                    || (node.BankRef is { } b2 && anchorBank == b2)
                    || (node.MergeContentHash is { } h2 && anchorHash == h2);
                if (wasAnchor) Anchor = node;
            }
            foreach (var child in node.Children) Walk(child);
        }
        SelectionChanged?.Invoke();
    }

    // The tree is at most a few thousand leaves - a linear walk per Shift-click is fine and
    // avoids needing a back-reference on ObjectTreeNode just for this.
    public static ObjectTreeNode? FindParent(IEnumerable<ObjectTreeNode> roots, ObjectTreeNode node)
    {
        foreach (var root in roots)
        {
            var found = FindParentRecursive(root, node);
            if (found != null) return found;
        }
        return null;
    }

    static ObjectTreeNode? FindParentRecursive(ObjectTreeNode candidate, ObjectTreeNode target)
    {
        if (candidate.Children.Contains(target)) return candidate;
        foreach (var child in candidate.Children)
        {
            var found = FindParentRecursive(child, target);
            if (found != null) return found;
        }
        return null;
    }
}
