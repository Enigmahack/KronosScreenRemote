using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// Bindable tree node shared by both Librarian panes (Views/LibrarianShellWindow.xaml's
// TV_Local and TV_Pcg), using CommunityToolkit.Mvvm (requirement 4) for the observable
// properties. Git-style dirty/conflicted coloring is driven by IsDirty/IsConflicted via a
// content-level Border DataTrigger in the View, NOT TreeViewItem.Background — the stock
// TreeViewItem ControlTemplate's own IsSelected trigger paints its header border directly
// and wins over any TemplateBinding-sourced Background for whichever node WPF considers
// natively selected, so a Style-level trigger on TreeViewItem.Background is a no-op for
// that node; a Border inside the template's own content has no such conflict.
partial class ObjectTreeNode : ObservableObject
{
    public string Label { get; }
    public ObjLoc? Loc { get; }   // set for a LEAF node (an actual object)

    // Set for a BANK-level (or, for Set Lists — which have no real bank concept — the type
    // root) grouping node, so a drag-drop landing here (not on a specific leaf) knows which
    // (type, bank) to auto-fill into. Null for a leaf node or an unaddressable grouping.
    public (int ObjType, int Bank)? BankRef { get; }

    // Set for a Merge Window leaf node instead of Loc — identifies a MergeEntry by content
    // hash, since staged objects have no real bank/slot address yet (MergeCache is
    // deliberately bag-based; see its own class doc). Never set alongside Loc.
    public string? MergeContentHash { get; }

    [ObservableProperty] bool isDirty;
    [ObservableProperty] bool isConflicted;
    [ObservableProperty] bool isExpanded;
    [ObservableProperty] bool isSelected;

    // Local-only "marked for removal, pending Commit" flag (see LocalLibraryCache.
    // SetPendingDelete) — a LEAF-only signal, distinct from IsDirty/IsConflicted, that drives
    // the fade look instead of the row disappearing outright.
    [ObservableProperty] bool isPendingDelete;

    // Local Library's dependency-completeness dot — a SEPARATE signal from IsDirty/
    // IsConflicted above (not a replacement for either), only ever set for a dirty Combi/Set
    // List: null = no dot shown; true = green (every reference resolves locally, still
    // pending Sync/Commit); false = red (at least one reference is still missing).
    [ObservableProperty] bool? dependencyStatus;

    // For the Merge Window's folder tree (ViewModels/MergePaneViewModel.cs): a Program/Combi
    // used by more than one referrer gets a "shared" marker — a distinct signal from
    // DependencyStatus above (that one's Local-Library-only). Null everywhere else.
    [ObservableProperty] string? sharedTooltip;

    public System.Collections.ObjectModel.ObservableCollection<ObjectTreeNode> Children { get; } = new();

    public ObjectTreeNode(string label, ObjLoc? loc = null, (int ObjType, int Bank)? bankRef = null, string? mergeContentHash = null)
    {
        Label = label;
        Loc = loc;
        BankRef = bankRef;
        MergeContentHash = mergeContentHash;
    }

    // Every Loc among this node's own descendants (this node included) — the "expand a bank
    // selection to its leaves" primitive a bank/root selection needs once it feeds the same
    // Cut/Copy/Delete/drag/Move-to-Merge-Window pipelines a leaf selection already does (Local
    // and PCG panes only; Program/Combi banks and Set Lists are exactly one level deep, but this
    // recurses regardless so it stays correct if that ever changes).
    public IEnumerable<ObjLoc> LeafLocs()
    {
        if (Loc is { } loc) { yield return loc; yield break; }
        foreach (var child in Children)
            foreach (var descendantLoc in child.LeafLocs())
                yield return descendantLoc;
    }

    // A stable identity for expansion-state tracking across a RefreshTree() rebuild — every
    // node has SOME distinguishing key: a leaf's Loc, a bank/group's BankRef, a Merge leaf's
    // MergeContentHash, or (for a type-root like "Programs"/"Combis", which has none of the
    // above) its own Label, which is unique among siblings at that level.
    object IdentityKey
    {
        get
        {
            if (Loc is { } l) return l;
            if (BankRef is { } b) return b;
            if (MergeContentHash is { } h) return h;
            return Label;
        }
    }

    // Captured BEFORE a RefreshTree() rebuild — a fresh rebuild otherwise collapses everything
    // back to IsExpanded's default (false), since every RefreshTree() call replaces every
    // ObjectTreeNode with a brand new instance (same reason selection needs PaneSelection.
    // ReconcileAfterRefresh in LibrarianShellWindow.xaml.cs).
    public static HashSet<object> CollectExpandedKeys(IEnumerable<ObjectTreeNode> roots)
    {
        var keys = new HashSet<object>();
        void Walk(ObjectTreeNode n)
        {
            if (n.IsExpanded) keys.Add(n.IdentityKey);
            foreach (var child in n.Children) Walk(child);
        }
        foreach (var root in roots) Walk(root);
        return keys;
    }

    // Re-applies expansion state to the NEW tree built right after CollectExpandedKeys ran on
    // the old one — matched by identity, never by object reference (which RefreshTree just
    // discarded).
    public static void RestoreExpandedKeys(IEnumerable<ObjectTreeNode> roots, HashSet<object> keys)
    {
        if (keys.Count == 0) return;
        void Walk(ObjectTreeNode n)
        {
            if (keys.Contains(n.IdentityKey)) n.IsExpanded = true;
            foreach (var child in n.Children) Walk(child);
        }
        foreach (var root in roots) Walk(root);
    }
}
