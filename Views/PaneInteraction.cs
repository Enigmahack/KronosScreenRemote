using System.Windows;
using System.Windows.Input;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

// One pane's mouse-gesture plumbing, so the three librarian panes (Local, PCG, Merge) share a
// single copy of the click / right-click / mouse-up mechanics instead of three near-identical
// handler triples in the code-behind. The selection state machine itself already lives in
// PaneSelection (ViewModels); this is the thin WPF glue around it - arming a drag on mouse-down,
// the pane's own "is this node selectable" rule, and an optional post-gesture hook (the Local
// pane refreshes its toolbar's enabled-state after every selection change; PCG/Merge have no
// toolbar).
//
// Deliberately does NOT absorb the drag-START (PreviewMouseMove) or ContextMenu-opening handlers:
// those genuinely differ per pane (each has its own drag format/payload and its own menu items),
// so folding them in would add more special-casing than it removes. They stay in the code-behind.
sealed class PaneInteraction
{
    public readonly PaneSelection Selection;
    readonly Func<ObjectTreeNode, bool> _selectable;
    readonly Action _afterChange;

    // Armed on mouse-down, read by the code-behind's own PreviewMouseMove to decide whether the
    // gesture became a drag. Public so that drag-start handler can share this one piece of state.
    public Point DragStart;
    public bool DragArmed;

    public PaneInteraction(PaneSelection selection, Func<ObjectTreeNode, bool> selectable, Action? afterChange = null)
    {
        Selection = selection;
        _selectable = selectable;
        _afterChange = afterChange ?? (() => { });
    }

    // Click-down: select (plain / Ctrl / Shift via PaneSelection) and arm a potential drag.
    // Leaves e.Handled unset so a double-click (open properties) still fires.
    public void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryNode(sender, out var node)) return;
        Selection.HandleClick(node, Keyboard.Modifiers);
        DragStart = e.GetPosition(null);
        DragArmed = true;
        _afterChange();
    }

    // Mouse-up with no intervening drag: collapse a multi-selection to just the clicked node
    // (PaneSelection decides). No selectability guard - matches the original, and a non-
    // selectable node simply isn't in the selection, so the collapse is a no-op for it.
    public void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DragArmed && sender is FrameworkElement { DataContext: ObjectTreeNode node })
        {
            Selection.HandleMouseUpWithoutDrag(node, Keyboard.Modifiers);
            _afterChange();
        }
        DragArmed = false;
    }

    // Right-click selects first (Explorer convention) before the caller opens its ContextMenu.
    public void OnPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryNode(sender, out var node)) return;
        Selection.HandleRightClick(node);
        _afterChange();
    }

    bool TryNode(object sender, out ObjectTreeNode node)
    {
        node = null!;
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode n) return false;
        if (!_selectable(n)) return false;   // a type-root header isn't a selectable citizen
        node = n;
        return true;
    }
}
