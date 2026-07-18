using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace KronosScreenRemote;

// One row in the Librarian's unified object browser (TV_Objects in LibrarianWindow.xaml).
// Every node — root, bank-group, and leaf — carries a Rescan scope, so "Rescan" in the
// context menu works at any level. Only leaves carry a Loc; Program/Combi leaves respond
// to "Set as Source/Destination", and every leaf (including Set List) responds to
// double-click (rename, or — for Set Lists — opening the Set List editor).
//
// IsBatchSelected is a bindable (INotifyPropertyChanged) property, NOT baked into Label, and
// deliberately so: reassigning TV_Objects.ItemsSource (a full BuildObjectTree() rebuild) drops
// every TreeViewItem's expansion state (IsExpanded binds Mode=OneTime, so a fresh node always
// starts collapsed). A batch-select click must only ever flip this property on the exact node(s)
// clicked — never call RefreshObjectTree() from a selection handler, or every bank the user has
// expanded to make a multi-selection collapses on every single click.
// Git-style staging state for a leaf: None (nothing pending), Staged (a paste/swap has queued a
// write here but Commit hasn't run yet — red), Committed (this session's Commit wrote it — green).
// Transient by design — never persisted, cleared only by a real Scan/Rescan (see ScanAsync/
// RescanScopeAsync), reapplied across RefreshObjectTree() rebuilds exactly like IsBatchSelected.
enum PasteState { None, Staged, Committed }

sealed class ObjectBrowserNode : INotifyPropertyChanged
{
    public string Label { get; }
    public ObjLoc? Loc { get; }
    public RescanScope Rescan { get; }
    public bool IsExpanded { get; }
    public List<ObjectBrowserNode> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    bool _isBatchSelected;
    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set
        {
            if (_isBatchSelected == value) return;
            _isBatchSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBatchSelected)));
        }
    }

    PasteState _pasteState;
    public PasteState PasteState
    {
        get => _pasteState;
        set
        {
            if (_pasteState == value) return;
            _pasteState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PasteState)));
        }
    }

    public ObjectBrowserNode(string label, RescanScope rescan, ObjLoc? loc = null, bool isExpanded = false)
    {
        Label = label;
        Loc = loc;
        Rescan = rescan;
        IsExpanded = isExpanded;
    }
}

partial class LibrarianWindow
{
    // Program/Combi banks a move can reference — INT + USER, same scope the picker used
    // before (excludes read-only GM/g, which can never be a move destination).
    static IEnumerable<int> ProgramMoveBanks() => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 14));
    static IEnumerable<int> CombiMoveBanks()   => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7));

    string SlotLabel(ObjLoc loc)
    {
        string name = NameOf(loc);
        string namePart = string.IsNullOrEmpty(name) ? "" : $"  {name}";
        string usagePart = "";
        if (_refIndex is { } ri)
        {
            int n = ri.UsageCount(loc);
            usagePart = $"  ({n} ref{(n == 1 ? "" : "s")})";
        }
        return $"{loc.Number:D3}{namePart}{usagePart}";
    }

    string SetListLabel(int number) =>
        _setListNames.TryGetValue(number, out var nm) && !string.IsNullOrWhiteSpace(nm)
            ? $"{number:D2}  {nm}"
            : $"{number:D2}";

    // Program bank labels only (Combis have no HD-1/EXi typing) — appends the type
    // once RequestProgramBankTypesAsync has answered; blank until then/if unknown
    // (e.g. I-G, which the func-0x61 bitmap doesn't cover — see KronosBanks).
    string ProgramBankLabel(int bank)
    {
        string label = KronosBanks.ProgramLabel(bank);
        if (_programBankTypes is not { } types) return label;
        if (KronosBanks.ProgramBankTypeBitIndex(bank) is not int bit || bit >= types.Length) return label;
        return $"{label}  ({(types[bit] ? "EXi" : "HD-1")})";
    }

    List<ObjectBrowserNode> BuildObjectTree()
    {
        var programsRoot = new ObjectBrowserNode("Programs", new RescanScope(LibObj.Program, null, null), isExpanded: true);
        foreach (var bank in ProgramMoveBanks())
        {
            var bankNode = new ObjectBrowserNode(ProgramBankLabel(bank), new RescanScope(LibObj.Program, bank, null));
            for (int number = 0; number < 128; number++)
            {
                var loc = new ObjLoc(LibObj.Program, bank, number);
                bankNode.Children.Add(new ObjectBrowserNode(SlotLabel(loc), new RescanScope(LibObj.Program, bank, number), loc));
            }
            programsRoot.Children.Add(bankNode);
        }

        var combisRoot = new ObjectBrowserNode("Combis", new RescanScope(LibObj.Combi, null, null), isExpanded: true);
        foreach (var bank in CombiMoveBanks())
        {
            var bankNode = new ObjectBrowserNode(KronosBanks.CombiLabel(bank), new RescanScope(LibObj.Combi, bank, null));
            for (int number = 0; number < 128; number++)
            {
                var loc = new ObjLoc(LibObj.Combi, bank, number);
                bankNode.Children.Add(new ObjectBrowserNode(SlotLabel(loc), new RescanScope(LibObj.Combi, bank, number), loc));
            }
            combisRoot.Children.Add(bankNode);
        }

        var setListsRoot = new ObjectBrowserNode("Set Lists", new RescanScope(LibObj.SetList, null, null), isExpanded: true);
        for (int number = 0; number < SetListData.MaxCount; number++)
        {
            var loc = new ObjLoc(LibObj.SetList, 0, number);
            setListsRoot.Children.Add(new ObjectBrowserNode(SetListLabel(number), new RescanScope(LibObj.SetList, null, number), loc));
        }

        return new List<ObjectBrowserNode> { programsRoot, combisRoot, setListsRoot };
    }

    // Full rebuild — legitimate here (Scan/Sync/Rescan/Rename all change underlying data), but
    // NEVER call this from a selection click (see ObjectBrowserNode's doc comment). Re-applies
    // the current _batchSelection AND _pasteState onto the freshly built nodes so a rebuild
    // triggered by one of those actions doesn't visually drop an in-progress batch selection or
    // (critically) the green "just committed" marker Commit's own tree refresh would otherwise
    // wipe out before it was ever visible — see _pasteState's doc comment in LibrarianWindow.xaml.cs.
    void RefreshObjectTree()
    {
        var tree = BuildObjectTree();
        TV_Objects.ItemsSource = tree;

        _nodeByLoc = new Dictionary<ObjLoc, ObjectBrowserNode>();
        void Walk(ObjectBrowserNode n)
        {
            if (n.Loc is { } loc) _nodeByLoc[loc] = n;
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var root in tree) Walk(root);

        foreach (var loc in _batchSelection)
            if (_nodeByLoc.TryGetValue(loc, out var node)) node.IsBatchSelected = true;
        foreach (var (loc, state) in _pasteState)
            if (_nodeByLoc.TryGetValue(loc, out var node)) node.PasteState = state;
    }

    // Mutates a live node in place (no rebuild) — same discipline as IsBatchSelected toggling.
    // Updates _pasteState (the source of truth, reapplied by RefreshObjectTree above) and, if
    // that node is currently realized, its bindable PasteState directly for immediate feedback.
    void SetPasteState(ObjLoc loc, PasteState state)
    {
        _pasteState[loc] = state;
        if (_nodeByLoc.TryGetValue(loc, out var node)) node.PasteState = state;
    }

    void ClearPasteState(ObjLoc loc)
    {
        _pasteState.Remove(loc);
        if (_nodeByLoc.TryGetValue(loc, out var node)) node.PasteState = PasteState.None;
    }

    void OnSetAsSource(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi) return;
        if (_src is { } old) ClearPasteState(old);
        _src = loc;
        SetPasteState(loc, PasteState.Staged);
        _plan = null;   // staging changed — any previously-armed plan no longer reflects it
        _batchPlan = null;
        UpdateUsage();
        RefreshEnable();
    }

    void OnSetAsDestination(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi) return;
        if (_dst is { } old) ClearPasteState(old);
        _dst = loc;
        SetPasteState(loc, PasteState.Staged);
        _plan = null;   // staging changed — any previously-armed plan no longer reflects it
        _batchPlan = null;
        UpdateUsage();
        RefreshEnable();
    }

    // Batch-move multi-select. Plain WPF TreeView has no native multi-select, so this is
    // tracked entirely in `_batchSelection` (the ObjLoc set of truth) alongside (not instead
    // of) the TreeView's own single SelectedItem — Ctrl/Shift-click never disturb that
    // (Handled=true stops the TreeViewItem from also processing the click), so
    // double-click-to-rename and the existing Set as Source/Destination flow are unaffected.
    //
    // Critical: this handler must NEVER call RefreshObjectTree() (see ObjectBrowserNode's doc
    // comment) — it only flips IsBatchSelected on the exact node(s) touched, via `node` itself
    // (the clicked leaf) or FindBankChildren (Shift-range) / ClearNodeSelection (deselecting
    // everything previously selected).
    void OnLeafPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc } node) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) return;

        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (!ctrl && !shift)
        {
            // Plain click: standard multi-select convention — reset to just this one item.
            // Leave the click unhandled so normal TreeView single-selection still applies.
            ClearNodeSelection();
            _batchSelection.Clear();
            _batchSelection.Add(loc);
            node.IsBatchSelected = true;
            _lastBatchTouch = loc;
            RefreshBatchSelectionUi();
            return;
        }

        if (_batchSelection.Count > 0 && _batchSelection.First().ObjType != loc.ObjType)
        {
            Log("Batch selection cleared: Programs and Combis can't be batched together.");
            ClearNodeSelection();
            _batchSelection.Clear();
        }

        if (shift && _lastBatchTouch is { } anchor && anchor.ObjType == loc.ObjType)
        {
            var bankChildren = FindBankChildren(loc);
            int ai = bankChildren.FindIndex(n => n.Loc == anchor);
            int bi = bankChildren.FindIndex(n => n.Loc == loc);
            if (ai >= 0 && bi >= 0)
            {
                int lo = Math.Min(ai, bi), hi = Math.Max(ai, bi);
                for (int i = lo; i <= hi; i++)
                    if (bankChildren[i].Loc is { } l)
                    {
                        _batchSelection.Add(l);
                        bankChildren[i].IsBatchSelected = true;
                    }
            }
            else
            {
                _batchSelection.Add(loc);   // anchor's in a different bank — just add this one
                node.IsBatchSelected = true;
            }
        }
        else if (!_batchSelection.Add(loc))
        {
            _batchSelection.Remove(loc);    // Ctrl-click toggles
            node.IsBatchSelected = false;
        }
        else
        {
            node.IsBatchSelected = true;
        }

        _lastBatchTouch = loc;
        e.Handled = true;
        RefreshBatchSelectionUi();
    }

    // Clears IsBatchSelected on every currently-selected node (looked up via _nodeByLoc, O(1)
    // each) WITHOUT touching _batchSelection itself — callers clear that set separately.
    void ClearNodeSelection()
    {
        foreach (var l in _batchSelection)
            if (_nodeByLoc.TryGetValue(l, out var n)) n.IsBatchSelected = false;
    }

    List<ObjectBrowserNode> FindBankChildren(ObjLoc loc)
    {
        if (TV_Objects.ItemsSource is not List<ObjectBrowserNode> roots) return new();
        var typeRoot = roots.FirstOrDefault(r => r.Rescan.ObjType == loc.ObjType && r.Rescan.Bank is null && r.Rescan.Number is null);
        // Set Lists have no intermediate bank-group level (BuildObjectTree puts all 128 leaves
        // directly under the type root), unlike Program/Combi's type -> bank -> leaf shape.
        if (loc.ObjType == LibObj.SetList) return typeRoot?.Children ?? new();
        var bankNode = typeRoot?.Children.FirstOrDefault(b => b.Rescan.ObjType == loc.ObjType && b.Rescan.Bank == loc.Bank);
        return bankNode?.Children ?? new();
    }
}
