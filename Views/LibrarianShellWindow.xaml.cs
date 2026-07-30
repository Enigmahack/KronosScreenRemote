using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

// The Librarian UI (Views/LibrarianShellWindow.xaml) — the classic LibrarianWindow this
// replaced (and its SetListWindow/SetListSlotEditDialog satellites) were deleted in the
// Phase 7 cutover. Selection and context-menu wiring are plain code-behind (a per-item WPF
// ContextMenu inside a HierarchicalDataTemplate is a well-known MVVM binding-scope friction
// point — see LocalLibraryPaneViewModel's own comment) but every action itself is a call
// straight into the ViewModel; no hardware access or business logic lives in this file.
//
// The Local pane's interaction model is file-manager-style Cut/Copy/Paste + drag-drop +
// multi-select, replacing the old two-step "Set as Source/Destination + Swap" flow (see
// LocalLibraryPaneViewModel's Cut/Copy/PasteIntoSlot/PasteIntoBank). Selection itself
// (which nodes are highlighted right now) lives here, not in the ViewModel — same binding-
// scope reasoning as the ContextMenu — and, per PaneSelection below, a BANK-level node is now
// a selectable citizen too (not just a leaf), expanding to every item inside it wherever an
// action needs concrete ObjLocs (SelectedLocs/PcgSelectedLocs).
internal partial class LibrarianShellWindow : ThemedWindow
{
    readonly LibrarianShellViewModel _vm;

    // One PaneSelection per tree (see the PaneSelection class below) — replaces what used to
    // be two near-identical duplicated selection blocks (Local, PCG) plus a third pane (Merge)
    // with no selection tracking at all, which is exactly how issues #1/#2 (highlight doesn't
    // clean up on deselect / doesn't match across panes) happened in the first place.
    readonly PaneSelection _localSelection;
    readonly PaneSelection _pcgSelection;
    readonly PaneSelection _mergeSelection;

    // Per-pane mouse-gesture plumbing (click / right-click / mouse-up + drag-arm) — see
    // PaneInteraction, which holds the one shared copy of that logic. Each wraps its matching
    // selection above; both are kept because the rest of this file reads the selections directly
    // (SelectedLocs, toolbar/context-menu enabled-state).
    readonly PaneInteraction _local;
    readonly PaneInteraction _pcg;
    readonly PaneInteraction _merge;

    public LibrarianShellWindow(ILibrarianService sysEx, LocalLibraryCache cache, AppSettings settings, string host)
    {
        InitializeComponent();
        _vm = new LibrarianShellViewModel(sysEx, cache, settings, host);
        DataContext = _vm;

        // Break the Owner link before closing so WPF doesn't minimize the parent when this
        // window had focus (known WPF owner-activation bug) — without it, closing a maximized
        // Librarian would sometimes send MainWindow to the system tray. Same one-line fix
        // FileManagerWindow.OnClosing already uses for the identical reason.
        Closing += (_, _) => Owner = null;

        // Step 4 of the auto-heal placement pipeline — the ViewModel stays free of WPF types
        // (same split as every other confirmation in this file), so it calls back into this
        // delegate only once ResolvePendingDependencies couldn't clear everything on its own.
        // A dedicated dialog, not MessageBox.Show — a plain MessageBox grew unboundedly tall
        // with a large number of entries until its own buttons scrolled off-screen (see
        // UnresolvedDependenciesDialog's own comment).
        _vm.ConfirmContinueWithPendingDependencies = pending =>
        {
            var dlg = UnresolvedDependenciesDialog.For(pending).OwnedBy(this);
            return Task.FromResult(dlg.ShowDialog() == true);
        };

        _localSelection = new PaneSelection(n => PaneSelection.FindParent(_vm.LocalPane.Roots, n), msg => _vm.LocalPane.StatusText = msg);
        _pcgSelection = new PaneSelection(n => PaneSelection.FindParent(_vm.PcgPane.Roots, n), msg => _vm.PcgPane.StatusText = msg)
        {
            // PCG's own extra rule on top of the universal bank-vs-leaf check: BatchPlaceFromPcg/
            // PullIntoMerge assume one object type per call, so a mixed-type selection could only
            // ever fail (or worse, get decoded through the wrong type's converter) once acted on.
            ExtraMixCheck = (existing, candidate) => PcgKindObjType(existing) != PcgKindObjType(candidate)
                ? "Can't mix Programs, Combis, and Set Lists in one selection." : null,
        };
        _mergeSelection = new PaneSelection(n => PaneSelection.FindParent(_vm.MergePane.Roots, n), msg => _vm.MergePane.StatusText = msg);

        // Cross-pane exclusivity: selecting in one pane always clears the other two.
        _localSelection.Others = new[] { _pcgSelection, _mergeSelection };
        _pcgSelection.Others = new[] { _localSelection, _mergeSelection };
        _mergeSelection.Others = new[] { _localSelection, _pcgSelection };

        // Every RefreshTree() rebuilds Roots from scratch (brand new ObjectTreeNode instances) —
        // re-bind each pane's selection to the new nodes by identity right after, or a stale
        // selection would linger on orphaned objects nothing on screen still represents (the
        // exact bug behind "Delete doesn't fade, highlighting changes again on next select").
        _vm.LocalPane.TreeRefreshed += () => { _localSelection.ReconcileAfterRefresh(_vm.LocalPane.Roots); UpdateToolbarEnabled(); };
        _vm.PcgPane.TreeRefreshed += () => _pcgSelection.ReconcileAfterRefresh(_vm.PcgPane.Roots);
        _vm.MergePane.TreeRefreshed += () => _mergeSelection.ReconcileAfterRefresh(_vm.MergePane.Roots);

        // "Object Dependencies" panel — recompute from whichever pane currently holds the
        // selection every time any of the three changes (cross-pane exclusivity guarantees at
        // most one is ever non-empty by the time this runs).
        _localSelection.SelectionChanged += UpdateObjectDependencies;
        _pcgSelection.SelectionChanged += UpdateObjectDependencies;
        _mergeSelection.SelectionChanged += UpdateObjectDependencies;

        // Local/PCG share the same "leaf or bank" selectability rule; Merge's leaves are keyed by
        // content hash instead of Loc. Only the Local pane has a toolbar to refresh after a click.
        _local = new PaneInteraction(_localSelection, IsLibrarySelectable, UpdateToolbarEnabled);
        _pcg = new PaneInteraction(_pcgSelection, IsLibrarySelectable);
        _merge = new PaneInteraction(_mergeSelection, IsMergeSelectable);

        UpdateToolbarEnabled();
    }

    static bool IsLibrarySelectable(ObjectTreeNode n) => n.Loc != null || n.BankRef != null;
    static bool IsMergeSelectable(ObjectTreeNode n) => n.MergeContentHash != null || n.BankRef != null;

    void UpdateObjectDependencies()
    {
        if (_localSelection.Items.Count > 0) _vm.ShowLocalObjectDependencies(SelectedLocs());
        else if (_pcgSelection.Items.Count > 0) _vm.ShowPcgObjectDependencies(PcgSelectedLocs());
        else if (_mergeSelection.Items.Count > 0)
            _vm.ShowMergeObjectDependencies(_mergeSelection.Items.SelectMany(MergeContentHashes).Distinct().ToList());
        else _vm.ClearObjectDependencies();
    }

    static int PcgKindObjType(ObjectTreeNode n) => n.Loc?.ObjType ?? n.BankRef!.Value.ObjType;

    // Cut/Copy/Rename/Delete depend on "what's currently selected," which — like the
    // selection set itself — lives here, not in the ViewModel, so their enabled state is
    // pushed imperatively rather than bound. BTN_LocalPaste is the one exception: it depends
    // only on ViewModel clipboard state, so it's a plain XAML binding in the .xaml file.
    void UpdateToolbarEnabled()
    {
        bool hasSelection = _localSelection.Items.Count > 0;
        BTN_LocalCut.IsEnabled = hasSelection;
        BTN_LocalCopy.IsEnabled = hasSelection;
        BTN_LocalRename.IsEnabled = _localSelection.Items.Count == 1;
        BTN_LocalDelete.IsEnabled = hasSelection;
        BTN_LocalDelete.Content = hasSelection && _localSelection.Items.All(n => n.IsPendingDelete) ? "Restore" : "Delete";
    }

    // Confirmation lives here (not the ViewModel) since it's a WPF-specific concern — same
    // split FileManagerWindow's own Delete confirmations use.
    void OnClearHistoryButton(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                AppMessages.Librarian.Shell.ClearHistory,
                AppMessages.Librarian.Shell.ClearHistoryTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.ClearHistory();
    }

    // Confirmation lives here, same split as OnClearHistoryButton — this discards every
    // pending local edit and pending deletion at once.
    void OnClearChangesButton(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                AppMessages.Librarian.Shell.ClearChanges,
                AppMessages.Librarian.Shell.ClearChangesTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.LocalPane.ClearAllChanges();
        _vm.NotifyLocalEditMade();
    }

    // Local edits are already persisted as you make them (that's the whole "local edits never
    // touch hardware until Sync/Commit" model) — there's nothing for Cancel to roll back beyond
    // what Clear Changes above already does explicitly, so this is just a close.
    void OnCancelButton(object sender, RoutedEventArgs e) => Close();

    // ── Local pane: multi-select ─────────────────────────────────────────────────
    // Ctrl+Click toggles one; Shift+Click extends a contiguous range among the anchor's
    // siblings (same bank, or same type-root if the anchor/target are BANK nodes) — cross-
    // bank/cross-type-root range-select is out of scope; plain click on a node already part
    // of a multi-selection keeps the group intact until mouse-up (so dragging one of several
    // selected items moves the whole group, matching Explorer), collapsing to just that node
    // on mouse-up only if no drag happened meanwhile. A BANK node (not just a leaf) is now a
    // selectable citizen too — see PaneSelection — never mixed with a leaf selection.

    // Expands any selected BANK node to every leaf ObjLoc underneath it (a bank move = every
    // item in the bank) — a plain leaf selection passes through unchanged.
    List<ObjLoc> SelectedLocs() => _localSelection.Items.SelectMany(n => n.LeafLocs()).ToList();

    void ClearSelection()
    {
        _localSelection.Clear();
        UpdateToolbarEnabled();
    }

    // Click / mouse-up / right-click all delegate to the shared PaneInteraction (which selects,
    // arms a drag, and refreshes the toolbar) — see its own doc comment. Right-click selecting
    // first (Explorer convention) is what makes Cut/Copy/Delete act on the actually-clicked node,
    // not a stale prior selection, by the time OnLocalContextMenuOpening runs.
    void OnLocalNodePreviewMouseDown(object sender, MouseButtonEventArgs e) => _local.OnPreviewMouseDown(sender, e);
    void OnLocalNodeMouseUp(object sender, MouseButtonEventArgs e) => _local.OnMouseUp(sender, e);
    void OnLocalNodePreviewRightDown(object sender, MouseButtonEventArgs e) => _local.OnPreviewRightDown(sender, e);

    void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode { Loc: { } loc }) return;
        OpenProperties(loc);
    }

    void OnOpenProperties(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).DataContext is ObjectTreeNode { Loc: { } loc }) OpenProperties(loc);
    }

    // Program/Combi: Name + Category/Sub-Category. Set List: Name + a browsable slot list
    // (Name/Color/Comments per slot) — see PropertiesDialog's own doc comment for why this
    // absorbs the retired SetListWindow/SetListSlotEditDialog into one dialog.
    void OpenProperties(ObjLoc loc)
    {
        string currentName = _vm.LocalPane.ReadDisplayName(loc);
        var dump = _vm.LocalPane.GetObjectDump(loc);
        if (dump == null) return;   // not found locally — nothing to show

        if (loc.ObjType == LibObj.SetList)
        {
            var data = SetListBody.FromRawBody(loc.Number, dump.Body);
            if (data == null) return;
            var setListDlg = PropertiesDialog.ForSetList($"{loc.Label()} Properties", currentName, data).OwnedBy(this);
            if (setListDlg.ShowDialog() != true) return;

            bool changed = false;
            if (setListDlg.NewName != null && setListDlg.NewName != currentName)
            {
                _vm.LocalPane.EditProperties(loc, setListDlg.NewName, null, null);
                changed = true;
            }
            if (setListDlg.EditedSlotNumber is int slot)
            {
                _vm.LocalPane.EditSetListSlot(loc, slot, setListDlg.NewSlotName, setListDlg.NewSlotColor, setListDlg.NewSlotComments);
                changed = true;
            }
            if (changed) _vm.NotifyLocalEditMade();
            return;
        }

        var (category, subCategory) = loc.ObjType == LibObj.Program
            ? ProgramBody.ReadCategory(dump.Body)
            : CombiBody.ReadCategory(dump.Body);
        var propDlg = PropertiesDialog.ForProgramOrCombi($"{loc.Label()} Properties", currentName, category, subCategory).OwnedBy(this);
        if (propDlg.ShowDialog() != true) return;

        string? name = propDlg.NewName != null && propDlg.NewName != currentName ? propDlg.NewName : null;
        int? newCategory = null, newSubCategory = null;
        if (propDlg.NewCategory is { } nc && (nc.Category != category || nc.SubCategory != subCategory))
        {
            newCategory = nc.Category;
            newSubCategory = nc.SubCategory;
        }
        if (name == null && newCategory == null) return;

        _vm.LocalPane.EditProperties(loc, name, newCategory, newSubCategory);
        _vm.NotifyLocalEditMade();
    }

    // ── Local pane: Cut / Copy / Paste / Rename / Delete ─────────────────────────
    // Shared by the context menu, the toolbar buttons, and keyboard shortcuts (Ctrl+X/C/V,
    // F2, Delete) — one implementation per action, several ways to trigger it.

    void DoCut()
    {
        var locs = SelectedLocs();
        if (locs.Count > 0) _vm.LocalPane.Cut(locs);
    }

    void DoCopy()
    {
        var locs = SelectedLocs();
        if (locs.Count > 0) _vm.LocalPane.Copy(locs);
    }

    void PasteAt(ObjectTreeNode? target)
    {
        if (target?.Loc is { } destLoc)
        {
            var (ok, msg) = _vm.LocalPane.PasteIntoSlot(destLoc);
            _vm.LocalPane.StatusText = msg ?? (ok ? AppMessages.Librarian.Pasted : AppMessages.Librarian.PasteFailed);
        }
        else if (target?.BankRef is { } bankRef)
        {
            var (ok, msg) = _vm.LocalPane.PasteIntoBank(bankRef.ObjType, bankRef.Bank);
            _vm.LocalPane.StatusText = msg ?? (ok ? AppMessages.Librarian.Pasted : AppMessages.Librarian.PasteFailed);
        }
        else
        {
            _vm.LocalPane.StatusText = AppMessages.Librarian.Shell.SelectSlotOrBankToPasteInto;
        }
    }

    // Local-only "mark for deletion, fade in place" (or, toggled again, "restore") — see
    // LocalLibraryPaneViewModel.ToggleDelete/ToggleDeleteMany's own comment. Deliberately does
    // NOT clear the selection afterward (unlike the old Discard-based Delete): the same node
    // stays selected through the tree rebuild (PaneSelection.ReconcileAfterRefresh re-binds it
    // to the fresh, now-faded instance), so clicking Delete/Restore again immediately toggles
    // the same item back without re-selecting it first. Visually the row shows the pending-
    // delete grey, not the blue selection color, while both are true — IsPendingDelete's
    // DataTrigger is declared after IsSelected's in LocalNodeTemplate's Border style, so it
    // wins on conflict, same precedence IsDirty/IsConflicted already use over a selected row.
    void DoDelete()
    {
        var locs = SelectedLocs();
        if (locs.Count == 0) return;

        // Issue 1: warn before deleting something other Combis/Set Lists depend on — only on the
        // DELETE direction (a Restore toggles the flag back and can't dangle anything), and only
        // when there actually are referrers. The direction matches DoDelete's own toggle
        // (ToggleDelete[Many]): Restore when EVERY selected node is already pending-delete.
        bool restoring = _localSelection.Items.Count > 0 && _localSelection.Items.All(n => n.IsPendingDelete);
        if (!restoring)
        {
            var dependents = locs.SelectMany(l => _vm.LocalPane.DescribeReferrers(l).Select(r => (Loc: l, Ref: r))).ToList();
            if (dependents.Count > 0 && !ConfirmDeleteDependency(dependents)) return;
        }

        if (locs.Count == 1) _vm.LocalPane.ToggleDelete(locs[0]);
        else _vm.LocalPane.ToggleDeleteMany(locs);
        _vm.NotifyLocalEditMade();
    }

    // The dependency-delete warning (issue 1). Confirmation lives here (WPF concern), same split
    // as every other destructive prompt in this file. Caps the listed referrers so a heavily-used
    // Program doesn't grow the dialog off-screen.
    bool ConfirmDeleteDependency(List<(ObjLoc Loc, string Ref)> dependents)
    {
        const int maxLines = 8;
        var lines = dependents.Take(maxLines).Select(d => AppMessages.Librarian.Shell.DeleteDependencyLine(d.Loc.Label(), d.Ref));
        string list = string.Join("\n", lines);
        if (dependents.Count > maxLines) list += AppMessages.Librarian.Shell.DeleteDependencyMore(dependents.Count - maxLines);
        return MessageBox.Show(this,
            AppMessages.Librarian.Shell.DeleteDependency(list),
            AppMessages.Librarian.Shell.DeleteDependencyTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    void DoRename(ObjectTreeNode? target)
    {
        if (target?.Loc is not { } loc) return;
        string current = _vm.LocalPane.ReadDisplayName(loc);
        var dlg = new PromptDialog(AppMessages.Prompts.Rename(loc.Label()), current).OwnedBy(this);
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result) || dlg.Result == current) return;
        _vm.LocalPane.Rename(loc, dlg.Result);
    }

    // The ContextMenu's own DataContext is rebound to the clicked node (see the XAML), which
    // is enough for Click handlers — but a ContextMenu/Popup isn't part of the main visual
    // tree, so a MenuItem inside it can't reach back up to the Window's DataContext via a
    // normal RelativeSource binding (the same friction LocalLibraryPaneViewModel's own doc
    // comment calls out). Setting Visibility/IsEnabled here, just before the menu opens, is
    // the established workaround already used throughout this file.
    void OnLocalContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } fe) return;
        bool isLeaf = fe.DataContext is ObjectTreeNode { Loc: { } };
        bool isBank = fe.DataContext is ObjectTreeNode { BankRef: { } };
        bool canPaste = _vm.LocalPane.HasClipboard;

        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case MenuItem { Name: "MI_Paste" } mi: mi.IsEnabled = canPaste; break;
                // Rename/Properties are single-object concepts — a bank has neither a name nor
                // properties of its own, so those stay leaf-only; Cut/Copy/Delete now expand a
                // bank selection to every item inside it (SelectedLocs), so they show for both.
                case MenuItem { Name: "MI_Rename" or "MI_Properties" } mi:
                    mi.Visibility = isLeaf ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case MenuItem { Name: "MI_Cut" or "MI_Copy" or "MI_MoveToMerge" } mi:
                    mi.Visibility = isLeaf || isBank ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case MenuItem { Name: "MI_Delete" } mi:
                    mi.Visibility = isLeaf || isBank ? Visibility.Visible : Visibility.Collapsed;
                    mi.Header = _localSelection.Items.Count > 0 && _localSelection.Items.All(n => n.IsPendingDelete) ? "Restore" : "Delete";
                    break;
                case Separator sep:
                    sep.Visibility = isLeaf || isBank ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }
    }

    // Context menu handlers — target is the right-clicked node (MenuItem's DataContext).
    // Cut/Copy/Delete now also fire from a bank node (DoCut/DoCopy/DoDelete all go through
    // SelectedLocs(), which expands a bank to every leaf inside it) — Rename stays leaf-only.
    void OnCutMenuItem(object sender, RoutedEventArgs e) { if (((MenuItem)sender).DataContext is ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } }) DoCut(); }
    void OnCopyMenuItem(object sender, RoutedEventArgs e) { if (((MenuItem)sender).DataContext is ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } }) DoCopy(); }
    void OnPasteMenuItem(object sender, RoutedEventArgs e) => PasteAt(((MenuItem)sender).DataContext as ObjectTreeNode);
    void OnRenameMenuItem(object sender, RoutedEventArgs e) => DoRename(((MenuItem)sender).DataContext as ObjectTreeNode);
    void OnDeleteMenuItem(object sender, RoutedEventArgs e) { if (((MenuItem)sender).DataContext is ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } }) DoDelete(); }

    // Requirement 3: stage the selected local object(s) (a leaf, a multi-select, or a whole bank
    // via SelectedLocs()'s LeafLocs expansion) into the Merge Window — the same effective action
    // as dragging them onto it (OnMergeDrop's LocalDragFormat branch).
    void OnMoveLocalToMergeMenuItem(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).DataContext is ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } })
            foreach (var loc in SelectedLocs()) _vm.PullLocalIntoMerge(loc);
    }

    // Toolbar handlers — act on the current selection (Paste/Rename need exactly one leaf).
    void OnCutButton(object sender, RoutedEventArgs e) => DoCut();
    void OnCopyButton(object sender, RoutedEventArgs e) => DoCopy();
    void OnPasteButton(object sender, RoutedEventArgs e) => PasteAt(_localSelection.Items.Count == 1 ? _localSelection.Items.First() : null);
    void OnRenameButton(object sender, RoutedEventArgs e) => DoRename(_localSelection.Items.Count == 1 ? _localSelection.Items.First() : null);
    void OnDeleteButton(object sender, RoutedEventArgs e) => DoDelete();

    void OnLocalTreeKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.X) { DoCut(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { DoCopy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PasteAt(_localSelection.Items.Count == 1 ? _localSelection.Items.First() : null); e.Handled = true; }
        else if (e.Key == Key.Delete) { DoDelete(); e.Handled = true; }
        else if (e.Key == Key.F2) { DoRename(_localSelection.Items.Count == 1 ? _localSelection.Items.First() : null); e.Handled = true; }
    }

    // ── Drag-drop ─────────────────────────────────────────────────────────────────
    // Two independent drag sources land on the same TV_Local drop target, distinguished by
    // format string: PcgDragFormat (PCG pane, always a copy-in, unchanged from before) and
    // LocalDragFormat (the Local pane dragging onto itself — new). A Local-sourced drop is
    // sugar over Cut/Copy + Paste: Ctrl-held-during-drop means Copy, otherwise Cut, exactly
    // reusing the same LocalLibraryPaneViewModel methods the menu/toolbar/keyboard paths do.
    // MergeDragFormat: the Merge Window dragging OUT onto Local Library — a single item goes
    // through exact-slot placement, a multi-item/group drag instead auto-fills sequentially
    // starting at the target bank's first free slot (see OnMergeToLocalDrop/
    // LibrarianShellViewModel.PlaceMergeGroupSequentially).
    const string PcgDragFormat = "KronosScreenRemote.LibraryObjectEntry";
    const string LocalDragFormat = "KronosScreenRemote.LocalLibraryObjectEntry";
    const string MergeDragFormat = "KronosScreenRemote.MergeLibraryObjectEntry";
    sealed record PcgDragPayload(IReadOnlyList<ObjLoc> Locs);
    sealed record LocalDragPayload(IReadOnlyList<ObjLoc> Locs);

    // Generalized from a single hash to a list: one hash for a plain leaf drag (still goes
    // through the exact-slot PlaceFromMerge path below), several for a multi-select or a whole
    // bank-equivalent group drag (goes through LibrarianShellViewModel.
    // PlaceMergeGroupSequentially instead — see OnMergeToLocalDrop).
    sealed record MergeDragPayload(IReadOnlyList<string> ContentHashes);


    // ── PCG pane: selection (mirrors the Local pane's own — see its class-doc comment for
    // why this lives in code-behind, not a binding) ─────────────────────────────────────────
    // PCG placement is always effectively a Copy (the source never changes), so unlike Local
    // there's no Cut/vacate concern here — but LibrarianShellViewModel.BatchPlaceFromPcg/
    // PullIntoMerge still assume one object type per call (never mixing Program/Combi/Set
    // List), so PaneSelection's ExtraMixCheck (wired in the constructor) refuses to add a node
    // of a different type than what's already selected. A BANK node is a selectable citizen
    // here too, same as Local — see PaneSelection.

    List<ObjLoc> PcgSelectedLocs() => _pcgSelection.Items.SelectMany(n => n.LeafLocs()).ToList();

    // Delegated to the shared PaneInteraction (no toolbar hook — only the Local pane has one).
    // Right-click selects first (Explorer convention) so "Move to Merge Window" acts on whatever
    // was actually right-clicked, not a stale prior selection, by OnPcgContextMenuOpening.
    void OnPcgPreviewMouseDown(object sender, MouseButtonEventArgs e) => _pcg.OnPreviewMouseDown(sender, e);
    void OnPcgNodeMouseUp(object sender, MouseButtonEventArgs e) => _pcg.OnMouseUp(sender, e);
    void OnPcgNodePreviewRightDown(object sender, MouseButtonEventArgs e) => _pcg.OnPreviewRightDown(sender, e);

    void OnPcgContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } fe) return;
        bool hasTarget = (fe.DataContext is ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } })
            && _pcgSelection.Items.Count > 0;
        foreach (var item in menu.Items)
            if (item is MenuItem { Name: "MI_MoveToMerge" } mi)
                mi.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    // Works for a single item, a multi-select, or a whole bank (BankRef expands to every leaf
    // underneath via SelectedLocs()'s own LeafLocs() — same primitive PcgSelectedLocs uses) —
    // the exact per-loc loop OnMergeDrop already uses for a multi-item drag payload, just
    // triggered from the context menu instead of a drop.
    void OnMoveToMergeMenuItem(object sender, RoutedEventArgs e)
    {
        foreach (var loc in PcgSelectedLocs()) _vm.PullIntoMerge(loc);
    }

    void OnPcgPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_pcg.DragArmed) return;
        if (sender is not FrameworkElement { DataContext: ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } } }) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pcg.DragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pcg.DragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pcg.DragArmed = false;
        var locs = PcgSelectedLocs();
        if (locs.Count == 0) return;
        var data = new DataObject(PcgDragFormat, new PcgDragPayload(locs));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    void OnLocalNodePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_local.DragArmed) return;
        if (sender is not FrameworkElement { DataContext: ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } } }) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _local.DragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _local.DragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _local.DragArmed = false;
        var locs = SelectedLocs();
        if (locs.Count == 0) return;
        AppLog.Debug($"[librarian] local drag start: {locs.Count} item(s) [{string.Join(", ", locs.Select(l => l.Label()))}]");
        var data = new DataObject(LocalDragFormat, new LocalDragPayload(locs));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    void OnLocalDragOver(object sender, DragEventArgs e)
    {
        // Must match each source's own DoDragDrop allowed-effects bitmask exactly (Merge's own
        // drag start above only allows Move) — requesting an effect the source didn't allow
        // makes WPF show the "drop not allowed" cursor for the whole drag, even though
        // OnLocalDrop below is fully able to handle it.
        e.Effects = e.Data.GetDataPresent(MergeDragFormat) ? DragDropEffects.Move
            : e.Data.GetDataPresent(PcgDragFormat) || e.Data.GetDataPresent(LocalDragFormat) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnLocalDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LocalDragFormat)) { OnLocalInternalDrop(e); return; }
        if (e.Data.GetDataPresent(MergeDragFormat)) { OnMergeToLocalDrop(e); return; }

        if (e.Data.GetData(PcgDragFormat) is not PcgDragPayload payload)
        {
            _vm.StatusText = AppMessages.Librarian.Shell.DropNotRecognizedLibraryObject;
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));
        if (target == null)
        {
            _vm.StatusText = AppMessages.Librarian.Shell.DropOutsideRow;
            return;
        }

        if (target.Loc is { } destLoc && payload.Locs.Count == 1)
        {
            // Dropped one item on a specific slot -> exact placement (prompts-via-orphan-gate
            // if occupied, same as the existing paste-to-occupied-slot flow).
            var (ok, error) = _vm.PlaceFromPcg(payload.Locs[0], destLoc);
            _vm.StatusText = ok ? AppMessages.Librarian.Shell.PlacedAt(payload.Locs[0].Label(), destLoc.Label()) : AppMessages.Librarian.Shell.PlaceFailedDetail(error);
        }
        else if (target.Loc is { } slotLoc)
        {
            // Multiple items dropped on one specific slot — no single address applies to all
            // of them, so auto-fill starting at that slot's bank instead (same rationale as
            // the Local pane's own multi-item Paste onto a specific slot).
            var (ok, msg) = _vm.BatchPlaceFromPcg(slotLoc.ObjType, payload.Locs, slotLoc.Bank);
            _vm.StatusText = msg ?? (ok ? AppMessages.Librarian.Placed : AppMessages.Librarian.PlaceFailed);
        }
        else if (target.BankRef is { } bankRef)
        {
            // Dropped on a bank (or the Set Lists root) -> auto-fill starting at the next free slot.
            var (ok, msg) = _vm.BatchPlaceFromPcg(bankRef.ObjType, payload.Locs, bankRef.Bank);
            _vm.StatusText = msg ?? (ok ? AppMessages.Librarian.Placed : AppMessages.Librarian.PlaceFailed);
        }
        else
        {
            _vm.StatusText = AppMessages.Librarian.Shell.DropOntoBankOrSlot;
        }
    }

    void OnLocalInternalDrop(DragEventArgs e)
    {
        if (e.Data.GetData(LocalDragFormat) is not LocalDragPayload payload)
        {
            _vm.LocalPane.StatusText = AppMessages.Librarian.Shell.DropNotRecognizedLibraryObject;
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));
        AppLog.Debug($"[librarian] local internal drop: {payload.Locs.Count} item(s); target={(target?.Loc?.Label() ?? (target?.BankRef is { } br ? $"bank {br.ObjType:X2}:{br.Bank:X2}" : "(none)"))}");
        if (target == null)
        {
            _vm.LocalPane.StatusText = AppMessages.Librarian.Shell.DropOutsideRow;
            return;
        }

        bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (!copy && payload.Locs.Count > 1)
        {
            // Don't touch Cut/Paste at all here: Cut() refusing a multi-item selection
            // leaves any existing clipboard armed (see its own comment), and unconditionally
            // calling PasteAt right after would silently act on that unrelated leftover
            // clipboard instead of failing cleanly.
            _vm.LocalPane.StatusText = AppMessages.Librarian.Shell.DragMoveOneAtATime;
            return;
        }

        if (copy) _vm.LocalPane.Copy(payload.Locs);
        else _vm.LocalPane.Cut(payload.Locs);
        PasteAt(target);
        AppLog.Debug($"[librarian] local internal drop result ({(copy ? "copy" : "cut/swap")}): {_vm.LocalPane.StatusText}");
    }

    // Merge Window -> Local: exact-slot placement for a single item (manual, per-item — the
    // user picks the destination, since only they know whether a bank should stay empty or
    // continue a partially-filled one — see the Merge Window GroupBox's own XAML comment); a
    // multi-item drag (a Ctrl+click multi-select, or a whole bank-equivalent group) instead
    // auto-fills sequentially starting at that bank's first free slot — dropping on a specific
    // slot or the bank/group node both just identify WHICH bank, same as the PCG pane's own
    // multi-item drop (OnLocalDrop's BatchPlaceFromPcg branch) — see LibrarianShellViewModel.
    // PlaceMergeGroupSequentially's own comment.
    void OnMergeToLocalDrop(DragEventArgs e)
    {
        if (e.Data.GetData(MergeDragFormat) is not MergeDragPayload payload || payload.ContentHashes.Count == 0)
        {
            _vm.MergePane.StatusText = AppMessages.Librarian.Shell.DropNotRecognizedMergeObject;
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));

        if (payload.ContentHashes.Count == 1)
        {
            if (target?.Loc is not { } destLoc)
            {
                _vm.MergePane.StatusText = AppMessages.Librarian.Shell.DropOntoSpecificSlot;
                return;
            }
            var (ok, error) = _vm.PlaceFromMerge(payload.ContentHashes[0], destLoc);
            _vm.MergePane.StatusText = ok ? AppMessages.Librarian.Shell.PlacedAtWhere(destLoc.Label()) : AppMessages.Librarian.Shell.PlaceFailedDetail(error);
            return;
        }

        (int ObjType, int Bank)? destBank = target?.Loc is { } slotLoc ? (slotLoc.ObjType, slotLoc.Bank) : target?.BankRef;
        if (destBank is not { } db)
        {
            _vm.MergePane.StatusText = AppMessages.Librarian.Shell.DropOntoSlotOrBankForGroup;
            return;
        }

        // Whole Program bank crossing an EXi/HD-1 boundary (requirement 4): copying it requires
        // reformatting the destination bank (func 0x7C), which ERASES it — confirm first.
        if (_vm.BankTypeChangeNeeded(db.ObjType, db.Bank, payload.ContentHashes) is bool targetIsExi)
        {
            var descriptor = ObjectTypeRegistry.Get(db.ObjType);
            string curType = targetIsExi ? "HD-1" : "EXi", newType = targetIsExi ? "EXi" : "HD-1";
            if (MessageBox.Show(this,
                    AppMessages.Librarian.Shell.ChangeBankType(descriptor.BankLabel(db.Bank), curType, newType),
                    AppMessages.Librarian.Shell.ChangeBankTypeTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                _vm.MergePane.StatusText = AppMessages.Librarian.Shell.BankTypeChangeCancelled;
                return;
            }
            var (tcOk, tcMsg) = _vm.PlaceMergeBankWithTypeChange(db.Bank, payload.ContentHashes, targetIsExi);
            _vm.MergePane.StatusText = tcMsg ?? (tcOk ? AppMessages.Librarian.Placed : AppMessages.Librarian.PlaceFailed);
            return;
        }

        var (bulkOk, msg) = _vm.PlaceMergeGroupSequentially(db.ObjType, db.Bank, payload.ContentHashes);
        _vm.MergePane.StatusText = msg ?? (bulkOk ? AppMessages.Librarian.Placed : AppMessages.Librarian.PlaceFailed);
    }

    // ── Merge Window: selection + drag source (onto Local) + drop target (from PCG) ──────
    // Full multi-select parity with Local/PCG now (Ctrl+click, Shift-range, and a BankRef
    // "group" node — the type-root Set Lists/Combis/Programs headers, see
    // MergePaneViewModel.RefreshTree — selectable the same way a Local/PCG bank is). Dragging
    // a single leaf still means "place exactly here"; dragging 2+ (a multi-select or a whole
    // group) means "auto-fill sequentially from the target bank's first free slot" — see
    // OnMergeToLocalDrop.

    // Delegated to the shared PaneInteraction (no toolbar hook). Right-click selects first so
    // "Remove" acts on the actually-clicked node, not a stale prior selection, by
    // OnMergeContextMenuOpening.
    void OnMergePreviewMouseDown(object sender, MouseButtonEventArgs e) => _merge.OnPreviewMouseDown(sender, e);
    void OnMergeNodeMouseUp(object sender, MouseButtonEventArgs e) => _merge.OnMouseUp(sender, e);
    void OnMergeNodePreviewRightDown(object sender, MouseButtonEventArgs e) => _merge.OnPreviewRightDown(sender, e);

    void OnMergeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } fe) return;
        bool hasTarget = (fe.DataContext is ObjectTreeNode { MergeContentHash: { } } or ObjectTreeNode { BankRef: { } })
            && _mergeSelection.Items.Count > 0;
        foreach (var item in menu.Items)
            if (item is MenuItem { Name: "MI_RemoveFromMerge" } mi)
                mi.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    // Works for a single item, a multi-select, or a whole group (expands to every DIRECT child
    // content hash via MergeContentHashes — same primitive the drag payload uses).
    void OnRemoveFromMergeMenuItem(object sender, RoutedEventArgs e)
    {
        var hashes = _mergeSelection.Items.SelectMany(MergeContentHashes).Distinct().ToList();
        if (hashes.Count > 0) _vm.MergePane.Remove(hashes);
    }

    // A group node (BankRef set — one of the type-root headers, or a pure sub-grouping like
    // Programs' HD-1/EXi split) recurses into its children to collect their content hashes.
    // This stops the instant it reaches a node that already has its own MergeContentHash — a
    // top-level Combi/Set List entry has one despite also having Children (its own nested
    // dependency Programs), so those never get swept in here; only the dependency-free
    // grouping levels above it (type root, HD-1/EXi) get walked through. See
    // LibrarianShellViewModel.PlaceMergeGroupSequentially's own comment for why nested
    // dependencies stay staged for individual placement instead.
    static IEnumerable<string> MergeContentHashes(ObjectTreeNode node) => node.MergeContentHash != null
        ? new[] { node.MergeContentHash }
        : node.Children.SelectMany(MergeContentHashes);

    void OnMergePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_merge.DragArmed) return;
        if (_mergeSelection.Items.Count == 0) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _merge.DragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _merge.DragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _merge.DragArmed = false;
        var hashes = _mergeSelection.Items.SelectMany(MergeContentHashes).Distinct().ToList();
        if (hashes.Count == 0) return;
        var data = new DataObject(MergeDragFormat, new MergeDragPayload(hashes));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    // Accepts drags from the PCG pane (copy-in) and, now, the Local pane (requirement 3 —
    // stage an already-placed object back in to rearrange/re-push it). Both are a pull-in, so
    // both request Copy — the Local drag's own DoDragDrop allows Copy|Move, so Copy is fine.
    void OnMergeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PcgDragFormat) || e.Data.GetDataPresent(LocalDragFormat)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // PCG/Local -> Merge: every dropped item is pulled in fully automatically along with its own
    // dependencies (see LibrarianShellViewModel.PullIntoMerge/PullLocalIntoMerge) — no destination
    // to pick, since the Merge Window is bag-based (no addressing at all until placement into
    // Local Library).
    void OnMergeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(LocalDragFormat) is LocalDragPayload localPayload)
        {
            foreach (var loc in localPayload.Locs) _vm.PullLocalIntoMerge(loc);
            return;
        }
        if (e.Data.GetData(PcgDragFormat) is not PcgDragPayload payload)
        {
            _vm.MergePane.StatusText = AppMessages.Librarian.Shell.DropNotRecognizedLibraryObject;
            return;
        }
        foreach (var loc in payload.Locs) _vm.PullIntoMerge(loc);
    }

    // Confirmation lives here (not the ViewModel), same split as OnClearHistoryButton.
    void OnClearMergeButton(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                AppMessages.Librarian.Shell.ClearMerge,
                AppMessages.Librarian.Shell.ClearMergeTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.MergePane.Clear();
    }

    static ObjectTreeNode? GetNodeAt(TreeView tv, Point pt)
    {
        var hit = tv.InputHitTest(pt) as DependencyObject;
        while (hit != null)
        {
            if (hit is TreeViewItem tvi) return tvi.DataContext as ObjectTreeNode;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }
}
