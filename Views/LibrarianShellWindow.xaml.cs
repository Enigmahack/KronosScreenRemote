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
internal partial class LibrarianShellWindow : Window
{
    readonly LibrarianShellViewModel _vm;

    // One PaneSelection per tree (see the PaneSelection class below) — replaces what used to
    // be two near-identical duplicated selection blocks (Local, PCG) plus a third pane (Merge)
    // with no selection tracking at all, which is exactly how issues #1/#2 (highlight doesn't
    // clean up on deselect / doesn't match across panes) happened in the first place.
    readonly PaneSelection _localSelection;
    readonly PaneSelection _pcgSelection;
    readonly PaneSelection _mergeSelection;

    public LibrarianShellWindow(ISysExService sysEx, LocalLibraryCache cache, AppSettings settings, string host)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        _vm = new LibrarianShellViewModel(sysEx, cache, settings, host);
        DataContext = _vm;

        // Step 4 of the auto-heal placement pipeline — the ViewModel stays free of WPF types
        // (same split as every other confirmation in this file), so it calls back into this
        // delegate only once ResolvePendingDependencies couldn't clear everything on its own.
        // A dedicated dialog, not MessageBox.Show — a plain MessageBox grew unboundedly tall
        // with a large number of entries until its own buttons scrolled off-screen (see
        // UnresolvedDependenciesDialog's own comment).
        _vm.ConfirmContinueWithPendingDependencies = pending =>
        {
            var dlg = UnresolvedDependenciesDialog.For(pending);
            dlg.Owner = this;
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

        UpdateToolbarEnabled();
    }

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
                "Permanently delete the local edit history log?\n\nThis does not affect your current local library, pending edits, or hardware — only the History panel's audit trail, which can't be recovered afterward.",
                "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.ClearHistory();
    }

    // Confirmation lives here, same split as OnClearHistoryButton — this discards every
    // pending local edit and pending deletion at once.
    void OnClearChangesButton(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Revert every pending local edit back to baseline and un-mark every pending deletion?\n\nHardware is unaffected either way — this only reverts what hasn't been pushed yet. A fresh Pull would leave the library looking the same as this does.",
                "Clear Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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

    void OnLocalNodePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.Loc == null && node.BankRef == null) return;   // type-root itself isn't selectable

        _localSelection.HandleClick(node, Keyboard.Modifiers);

        _localDragStart = e.GetPosition(null);
        _localDragArmed = true;
        UpdateToolbarEnabled();
        // Deliberately leaves e.Handled unset — double-click-to-open-properties must still fire.
    }

    void OnLocalNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_localDragArmed && sender is FrameworkElement { DataContext: ObjectTreeNode node })
        {
            _localSelection.HandleMouseUpWithoutDrag(node, Keyboard.Modifiers);
            UpdateToolbarEnabled();
        }
        _localDragArmed = false;
    }

    // Right-click selects first (Explorer convention), same as a left-click, before the
    // ContextMenu opens (OnLocalContextMenuOpening) — so Cut/Copy/Delete/etc. always act on
    // whatever was actually right-clicked instead of a stale, unrelated prior selection.
    void OnLocalNodePreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.Loc == null && node.BankRef == null) return;
        _localSelection.HandleRightClick(node);
        UpdateToolbarEnabled();
    }

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
            var setListDlg = PropertiesDialog.ForSetList($"{loc.Label()} Properties", currentName, data);
            setListDlg.Owner = this;
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
        var propDlg = PropertiesDialog.ForProgramOrCombi($"{loc.Label()} Properties", currentName, category, subCategory);
        propDlg.Owner = this;
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
            _vm.LocalPane.StatusText = msg ?? (ok ? "Pasted." : "Paste failed.");
        }
        else if (target?.BankRef is { } bankRef)
        {
            var (ok, msg) = _vm.LocalPane.PasteIntoBank(bankRef.ObjType, bankRef.Bank);
            _vm.LocalPane.StatusText = msg ?? (ok ? "Pasted." : "Paste failed.");
        }
        else
        {
            _vm.LocalPane.StatusText = "Select a slot or bank to paste into.";
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
        if (locs.Count == 1) _vm.LocalPane.ToggleDelete(locs[0]);
        else _vm.LocalPane.ToggleDeleteMany(locs);
        _vm.NotifyLocalEditMade();
    }

    void DoRename(ObjectTreeNode? target)
    {
        if (target?.Loc is not { } loc) return;
        string current = _vm.LocalPane.ReadDisplayName(loc);
        var dlg = new PromptDialog($"Rename {loc.Label()}:", current) { Owner = this };
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
                case MenuItem { Name: "MI_Cut" or "MI_Copy" } mi:
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

    Point _pcgDragStart;
    bool _pcgDragArmed;

    Point _localDragStart;
    bool _localDragArmed;

    Point _mergeDragStart;
    bool _mergeDragArmed;

    // ── PCG pane: selection (mirrors the Local pane's own — see its class-doc comment for
    // why this lives in code-behind, not a binding) ─────────────────────────────────────────
    // PCG placement is always effectively a Copy (the source never changes), so unlike Local
    // there's no Cut/vacate concern here — but LibrarianShellViewModel.BatchPlaceFromPcg/
    // PullIntoMerge still assume one object type per call (never mixing Program/Combi/Set
    // List), so PaneSelection's ExtraMixCheck (wired in the constructor) refuses to add a node
    // of a different type than what's already selected. A BANK node is a selectable citizen
    // here too, same as Local — see PaneSelection.

    List<ObjLoc> PcgSelectedLocs() => _pcgSelection.Items.SelectMany(n => n.LeafLocs()).ToList();

    void OnPcgPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.Loc == null && node.BankRef == null) return;   // type-root itself isn't selectable

        _pcgSelection.HandleClick(node, Keyboard.Modifiers);

        _pcgDragStart = e.GetPosition(null);
        _pcgDragArmed = true;
    }

    void OnPcgNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pcgDragArmed && sender is FrameworkElement { DataContext: ObjectTreeNode node })
            _pcgSelection.HandleMouseUpWithoutDrag(node, Keyboard.Modifiers);
        _pcgDragArmed = false;
    }

    // Right-click selects first (Explorer convention), before the ContextMenu opens
    // (OnPcgContextMenuOpening) — "Move to Merge Window" always acts on whatever was actually
    // right-clicked instead of a stale, unrelated prior selection.
    void OnPcgNodePreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.Loc == null && node.BankRef == null) return;
        _pcgSelection.HandleRightClick(node);
    }

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
        if (e.LeftButton != MouseButtonState.Pressed || !_pcgDragArmed) return;
        if (sender is not FrameworkElement { DataContext: ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } } }) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pcgDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pcgDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pcgDragArmed = false;
        var locs = PcgSelectedLocs();
        if (locs.Count == 0) return;
        var data = new DataObject(PcgDragFormat, new PcgDragPayload(locs));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    void OnLocalNodePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_localDragArmed) return;
        if (sender is not FrameworkElement { DataContext: ObjectTreeNode { Loc: { } } or ObjectTreeNode { BankRef: { } } }) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _localDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _localDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _localDragArmed = false;
        var locs = SelectedLocs();
        if (locs.Count == 0) return;
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
            _vm.StatusText = "Drop didn't carry a recognized library object.";
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));
        if (target == null)
        {
            _vm.StatusText = "Drop landed outside any bank/slot row — try dropping directly on one.";
            return;
        }

        if (target.Loc is { } destLoc && payload.Locs.Count == 1)
        {
            // Dropped one item on a specific slot -> exact placement (prompts-via-orphan-gate
            // if occupied, same as the existing paste-to-occupied-slot flow).
            var (ok, error) = _vm.PlaceFromPcg(payload.Locs[0], destLoc);
            _vm.StatusText = ok ? $"Placed {payload.Locs[0].Label()} at {destLoc.Label()}" : $"Place failed: {error}";
        }
        else if (target.Loc is { } slotLoc)
        {
            // Multiple items dropped on one specific slot — no single address applies to all
            // of them, so auto-fill starting at that slot's bank instead (same rationale as
            // the Local pane's own multi-item Paste onto a specific slot).
            var (ok, msg) = _vm.BatchPlaceFromPcg(slotLoc.ObjType, payload.Locs, slotLoc.Bank);
            _vm.StatusText = msg ?? (ok ? "Placed." : "Place failed.");
        }
        else if (target.BankRef is { } bankRef)
        {
            // Dropped on a bank (or the Set Lists root) -> auto-fill starting at the next free slot.
            var (ok, msg) = _vm.BatchPlaceFromPcg(bankRef.ObjType, payload.Locs, bankRef.Bank);
            _vm.StatusText = msg ?? (ok ? "Placed." : "Place failed.");
        }
        else
        {
            _vm.StatusText = "Drop onto a specific bank or slot.";
        }
    }

    void OnLocalInternalDrop(DragEventArgs e)
    {
        if (e.Data.GetData(LocalDragFormat) is not LocalDragPayload payload)
        {
            _vm.LocalPane.StatusText = "Drop didn't carry a recognized library object.";
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));
        if (target == null)
        {
            _vm.LocalPane.StatusText = "Drop landed outside any bank/slot row — try dropping directly on one.";
            return;
        }

        bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (!copy && payload.Locs.Count > 1)
        {
            // Don't touch Cut/Paste at all here: Cut() refusing a multi-item selection
            // leaves any existing clipboard armed (see its own comment), and unconditionally
            // calling PasteAt right after would silently act on that unrelated leftover
            // clipboard instead of failing cleanly.
            _vm.LocalPane.StatusText = "Drag-move works one item at a time — select a single item, or hold Ctrl to copy.";
            return;
        }

        if (copy) _vm.LocalPane.Copy(payload.Locs);
        else _vm.LocalPane.Cut(payload.Locs);
        PasteAt(target);
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
            _vm.MergePane.StatusText = "Drop didn't carry a recognized Merge Window object.";
            return;
        }
        var target = GetNodeAt(TV_Local, e.GetPosition(TV_Local));

        if (payload.ContentHashes.Count == 1)
        {
            if (target?.Loc is not { } destLoc)
            {
                _vm.MergePane.StatusText = "Drop directly onto a specific slot — pick exactly where this lands.";
                return;
            }
            var (ok, error) = _vm.PlaceFromMerge(payload.ContentHashes[0], destLoc);
            _vm.MergePane.StatusText = ok ? $"Placed at {destLoc.Label()}" : $"Place failed: {error}";
            return;
        }

        (int ObjType, int Bank)? destBank = target?.Loc is { } slotLoc ? (slotLoc.ObjType, slotLoc.Bank) : target?.BankRef;
        if (destBank is not { } db)
        {
            _vm.MergePane.StatusText = "Drop onto a specific slot or bank so the group has somewhere to land.";
            return;
        }
        var (bulkOk, msg) = _vm.PlaceMergeGroupSequentially(db.ObjType, db.Bank, payload.ContentHashes);
        _vm.MergePane.StatusText = msg ?? (bulkOk ? "Placed." : "Place failed.");
    }

    // ── Merge Window: selection + drag source (onto Local) + drop target (from PCG) ──────
    // Full multi-select parity with Local/PCG now (Ctrl+click, Shift-range, and a BankRef
    // "group" node — the type-root Set Lists/Combis/Programs headers, see
    // MergePaneViewModel.RefreshTree — selectable the same way a Local/PCG bank is). Dragging
    // a single leaf still means "place exactly here"; dragging 2+ (a multi-select or a whole
    // group) means "auto-fill sequentially from the target bank's first free slot" — see
    // OnMergeToLocalDrop.

    void OnMergePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.MergeContentHash == null && node.BankRef == null) return;

        _mergeSelection.HandleClick(node, Keyboard.Modifiers);

        _mergeDragStart = e.GetPosition(null);
        _mergeDragArmed = true;
    }

    void OnMergeNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mergeDragArmed && sender is FrameworkElement { DataContext: ObjectTreeNode node })
            _mergeSelection.HandleMouseUpWithoutDrag(node, Keyboard.Modifiers);
        _mergeDragArmed = false;
    }

    // Right-click selects first (Explorer convention), before the ContextMenu opens
    // (OnMergeContextMenuOpening) — "Remove" always acts on whatever was actually right-clicked
    // instead of a stale, unrelated prior selection.
    void OnMergeNodePreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ObjectTreeNode node) return;
        if (node.MergeContentHash == null && node.BankRef == null) return;
        _mergeSelection.HandleRightClick(node);
    }

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
        if (e.LeftButton != MouseButtonState.Pressed || !_mergeDragArmed) return;
        if (_mergeSelection.Items.Count == 0) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _mergeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _mergeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _mergeDragArmed = false;
        var hashes = _mergeSelection.Items.SelectMany(MergeContentHashes).Distinct().ToList();
        if (hashes.Count == 0) return;
        var data = new DataObject(MergeDragFormat, new MergeDragPayload(hashes));
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    void OnMergeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PcgDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // PCG -> Merge: every dropped item is pulled in fully automatically along with its own
    // dependencies (see LibrarianShellViewModel.PullIntoMerge) — no destination to pick, since
    // the Merge Window is bag-based (no addressing at all until placement into Local Library).
    void OnMergeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PcgDragFormat) is not PcgDragPayload payload)
        {
            _vm.MergePane.StatusText = "Drop didn't carry a recognized library object.";
            return;
        }
        foreach (var loc in payload.Locs) _vm.PullIntoMerge(loc);
    }

    // Confirmation lives here (not the ViewModel), same split as OnClearHistoryButton.
    void OnClearMergeButton(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Abandon everything currently staged in the Merge Window?\n\nAnything already placed into Local Library is unaffected — this only clears what's still pending.",
                "Clear Merge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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

// Selection tracking for one pane's tree (Local, PCG, or Merge) — click/Ctrl+click/Shift-range
// mechanics identical across all three, generalized to treat a BankRef node (a bank, or Merge
// Window's own type-grouping "bank-equivalent" — see MergePaneViewModel.RefreshTree) the same
// way a leaf node (Loc, or Merge's MergeContentHash) is treated. Kept as one shared class
// instead of three near-identical copies, which is exactly how the bug this replaces started:
// Merge Window used to have no selection tracking at all (nothing ever set IsSelected), while
// Local/PCG's own hand-duplicated copies quietly drifted — neither survived a RefreshTree()
// rebuild (which throws away every ObjectTreeNode and builds fresh ones), leaving a stale
// selection pointing at orphaned objects nothing on screen still represents.
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

    // The other two panes' selections — clearing THIS pane's selection also clears these
    // (cross-pane exclusivity: only one pane's selection is ever active at a time). Set once,
    // right after all three are constructed (see LibrarianShellWindow's own constructor).
    public PaneSelection[] Others = Array.Empty<PaneSelection>();

    // Fired at the end of every public entry point that can change Items — the "Object
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

    // Plain click / Ctrl+click / Shift-range — the click-down gesture. `modifiers` is passed in
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
            // Leave the multi-selection intact for now — dragging one of several selected
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
    // a multi-selection collapses to just that node (matches Explorer — mouse-down alone keeps
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
    // matched by stable identity (Loc/BankRef/MergeContentHash — never the old object
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

    // The tree is at most a few thousand leaves — a linear walk per Shift-click is fine and
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
