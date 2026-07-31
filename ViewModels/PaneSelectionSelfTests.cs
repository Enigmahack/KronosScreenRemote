using System.Windows.Input;

namespace KronosScreenRemote.ViewModels;

// Off-hardware self-test for PaneSelection - the librarian's historically most bug-prone logic
// (click / Ctrl+click / Shift-range multi-select, cross-pane exclusivity, and survive-a-tree-
// rebuild reconciliation), which used to live untested in the LibrarianShellWindow code-behind.
// PaneSelection only depends on WPF for the ModifierKeys enum passed into it, so the whole
// state machine is exercisable here against a synthetic ObjectTreeNode tree, no Window needed.
// Wired into App.xaml.cs's --librarian-selftest; returns failing check names (empty == pass).
static class PaneSelectionSelfTests
{
    const ModifierKeys None = ModifierKeys.None;
    const ModifierKeys Ctrl = ModifierKeys.Control;
    const ModifierKeys Shift = ModifierKeys.Shift;

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── Plain click replaces; Ctrl+click adds then toggles off ──
        {
            var t = BuildTree();
            var sel = Make(t.Roots);
            sel.HandleClick(t.Leaf0, None);
            Check("plain-click-selects-one", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf0) && t.Leaf0.IsSelected);
            Check("plain-click-sets-anchor", sel.Anchor == t.Leaf0);

            sel.HandleClick(t.Leaf1, Ctrl);
            Check("ctrl-click-adds", sel.Items.Count == 2 && t.Leaf1.IsSelected);

            sel.HandleClick(t.Leaf0, Ctrl);
            Check("ctrl-click-toggles-off", sel.Items.Count == 1 && !t.Leaf0.IsSelected && sel.Items.Contains(t.Leaf1));

            sel.HandleClick(t.Leaf3, None);
            Check("plain-click-replaces", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf3) && !t.Leaf1.IsSelected);
        }

        // ── Shift-range across siblings; falls back to a plain replace across parents ──
        {
            var t = BuildTree();
            var sel = Make(t.Roots);
            sel.HandleClick(t.Leaf0, None);
            sel.HandleClick(t.Leaf2, Shift);
            Check("shift-range-spans-siblings",
                sel.Items.Count == 3 && sel.Items.Contains(t.Leaf0) && sel.Items.Contains(t.Leaf1) && sel.Items.Contains(t.Leaf2));

            var t2 = BuildTree();
            var sel2 = Make(t2.Roots);
            sel2.HandleClick(t2.Leaf0, None);      // anchor in Bank A
            sel2.HandleClick(t2.Leaf3, Shift);     // target in Bank B → not siblings → replace
            Check("shift-range-cross-parent-replaces", sel2.Items.Count == 1 && sel2.Items.Contains(t2.Leaf3));
        }

        // ── Bank-vs-leaf mix refusal, and the pane-specific ExtraMixCheck on top of it ──
        {
            var t = BuildTree();
            string last = "";
            var sel = Make(t.Roots, m => last = m);
            sel.HandleClick(t.Leaf0, None);
            last = "";
            sel.HandleClick(t.BankA, Ctrl);        // bank + leaf can't mix
            Check("mix-bank-leaf-refused", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf0) && !t.BankA.IsSelected);
            Check("mix-bank-leaf-message", last.Contains("mix a bank"));

            var t3 = BuildTree();
            string last3 = "";
            var sel3 = Make(t3.Roots, m => last3 = m);
            sel3.ExtraMixCheck = (_, _) => "custom refusal";
            sel3.HandleClick(t3.Leaf0, None);
            last3 = "";
            sel3.HandleClick(t3.Leaf1, Ctrl);      // both leaves → ExtraMixCheck decides
            Check("extra-mix-refused", sel3.Items.Count == 1 && sel3.Items.Contains(t3.Leaf0) && !t3.Leaf1.IsSelected);
            Check("extra-mix-message", last3 == "custom refusal");
        }

        // ── Mouse-up without a drag collapses a multi-selection to just the clicked node ──
        {
            var t = BuildTree();
            var sel = Make(t.Roots);
            sel.HandleClick(t.Leaf0, None);
            sel.HandleClick(t.Leaf1, Ctrl);        // {Leaf0, Leaf1}
            sel.HandleClick(t.Leaf0, None);        // plain click on a member: keep group armed for a drag
            Check("multi-stays-armed-on-mousedown", sel.Items.Count == 2);
            sel.HandleMouseUpWithoutDrag(t.Leaf0, None);
            Check("no-drag-collapses-to-one", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf0) && !t.Leaf1.IsSelected);
        }

        // ── Right-click selects, keeps an existing multi it's part of, replaces otherwise ──
        {
            var t = BuildTree();
            var sel = Make(t.Roots);
            sel.HandleRightClick(t.Leaf0);
            Check("right-click-selects", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf0));
            sel.HandleClick(t.Leaf1, Ctrl);        // {Leaf0, Leaf1}
            sel.HandleRightClick(t.Leaf0);         // already selected → keep the group
            Check("right-click-keeps-multi", sel.Items.Count == 2);
            sel.HandleRightClick(t.Leaf3);         // outside → replace
            Check("right-click-replaces-outside", sel.Items.Count == 1 && sel.Items.Contains(t.Leaf3));
        }

        // ── Cross-pane exclusivity: selecting in one pane clears the others ──
        {
            var a = BuildTree();
            var b = BuildTree();
            var selA = Make(a.Roots);
            var selB = Make(b.Roots);
            selA.Others = new[] { selB };
            selB.Others = new[] { selA };
            selA.HandleClick(a.Leaf0, None);
            Check("xpane-A-selected", selA.Items.Count == 1);
            selB.HandleClick(b.Leaf0, None);
            Check("xpane-selecting-B-clears-A", selA.Items.Count == 0 && !a.Leaf0.IsSelected);
            Check("xpane-B-holds-selection", selB.Items.Count == 1);
        }

        // ── Reconcile after a RefreshTree() rebuild: re-bind by identity to the fresh nodes ──
        {
            var old = BuildTree();
            var sel = Make(old.Roots);
            sel.HandleClick(old.Leaf0, None);
            sel.HandleClick(old.Leaf1, Ctrl);      // {Leaf0, Leaf1}, anchor = Leaf1

            var fresh = BuildTree();               // brand new node instances, same Loc identities
            sel.ReconcileAfterRefresh(fresh.Roots);
            Check("reconcile-preserves-count", sel.Items.Count == 2);
            Check("reconcile-rebinds-to-fresh-nodes", sel.Items.Contains(fresh.Leaf0) && sel.Items.Contains(fresh.Leaf1));
            Check("reconcile-drops-stale-nodes", !sel.Items.Contains(old.Leaf0) && !sel.Items.Contains(old.Leaf1));
            Check("reconcile-selects-fresh-nodes", fresh.Leaf0.IsSelected && fresh.Leaf1.IsSelected);
            Check("reconcile-rebinds-anchor", sel.Anchor == fresh.Leaf1);
        }

        // ── Clear empties the set, clears IsSelected, drops the anchor, and notifies ──
        {
            var t = BuildTree();
            var sel = Make(t.Roots);
            int changes = 0;
            sel.SelectionChanged += () => changes++;
            sel.HandleClick(t.Leaf0, None);
            sel.HandleClick(t.Leaf1, Ctrl);
            Check("selection-changed-fires", changes > 0);
            sel.Clear();
            Check("clear-empties", sel.Items.Count == 0 && !t.Leaf0.IsSelected && !t.Leaf1.IsSelected && sel.Anchor == null);
        }

        return fails;
    }

    static PaneSelection Make(List<ObjectTreeNode> roots, Action<string>? reportStatus = null) =>
        new(n => PaneSelection.FindParent(roots, n), reportStatus ?? (_ => { }));

    // A minimal two-bank Program tree - Bank A holds three leaves (for Shift-range), Bank B one
    // (for the cross-parent case). Each call yields FRESH ObjectTreeNode instances with the same
    // stable Loc identities, so a second BuildTree() stands in for a RefreshTree() rebuild.
    static TreeFixture BuildTree()
    {
        var leaf0 = new ObjectTreeNode("P000", new ObjLoc(LibObj.Program, 0x00, 0));
        var leaf1 = new ObjectTreeNode("P001", new ObjLoc(LibObj.Program, 0x00, 1));
        var leaf2 = new ObjectTreeNode("P002", new ObjLoc(LibObj.Program, 0x00, 2));
        var bankA = new ObjectTreeNode("Bank A", bankRef: (LibObj.Program, 0x00));
        bankA.Children.Add(leaf0);
        bankA.Children.Add(leaf1);
        bankA.Children.Add(leaf2);

        var leaf3 = new ObjectTreeNode("P100", new ObjLoc(LibObj.Program, 0x01, 0));
        var bankB = new ObjectTreeNode("Bank B", bankRef: (LibObj.Program, 0x01));
        bankB.Children.Add(leaf3);

        var root = new ObjectTreeNode("Programs");
        root.Children.Add(bankA);
        root.Children.Add(bankB);

        return new TreeFixture(new List<ObjectTreeNode> { root }, bankA, bankB, leaf0, leaf1, leaf2, leaf3);
    }

    sealed record TreeFixture(
        List<ObjectTreeNode> Roots,
        ObjectTreeNode BankA, ObjectTreeNode BankB,
        ObjectTreeNode Leaf0, ObjectTreeNode Leaf1, ObjectTreeNode Leaf2, ObjectTreeNode Leaf3);
}
