using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace KronosScreenRemote;

// Clipboard panel (TV_Clipboard in LibrarianWindow.xaml) + the paste flow. Copy to Clipboard
// (right-click a tree leaf) dumps and adds entries here; the four paste variants (right-click a
// destination leaf) stage directly into _stagedPastes and mark the destination red immediately —
// no separate "select then click a button" step. Staged pastes aren't written anywhere yet —
// they're folded into the next Verify/Commit exactly like a swap, through the same orphan gate,
// staleness gate, and backup discipline (see LibrarianWindow.Batch.cs).

// A leaf reference row nested under an expanded Combi/Set List ClipboardRow — read-only, no
// children of its own. Present as a class (not a plain string) only so the shared
// TreeView.ItemContainerStyle's IsExpanded binding in LibrarianWindow.xaml resolves cleanly
// against every node type in the tree, not just ClipboardRow.
sealed class ClipboardRefRow
{
    public string Label { get; }
    public bool IsExpanded { get; set; }
    public ClipboardRefRow(string label) => Label = label;
}

// One top-level clipboard entry. IsExpanded and IsBatchSelected are bindable
// (INotifyPropertyChanged) and — same lesson as ObjectBrowserNode — reapplied by
// RefreshClipboardUi() across rebuilds by matching on the underlying ClipboardEntry's identity
// (which IS stable across rebuilds — _batchClipboard.Entries itself is never recreated, only
// re-wrapped in fresh ClipboardRow objects), so an unrelated new entry arriving doesn't silently
// collapse/deselect a row the user already expanded or Ctrl-selected.
sealed class ClipboardRow : INotifyPropertyChanged
{
    public ClipboardEntry Entry { get; }
    public string Label { get; }
    public List<ClipboardRefRow> Children { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    bool _isBatchSelected;
    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set { if (_isBatchSelected == value) return; _isBatchSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBatchSelected))); }
    }

    public ClipboardRow(ClipboardEntry entry, string label, List<ClipboardRefRow> children)
    {
        Entry = entry;
        Label = label;
        Children = children;
    }
}

partial class LibrarianWindow
{
    void RefreshClipboardUi()
    {
        var prevExpanded = (TV_Clipboard.ItemsSource as IEnumerable<ClipboardRow>)?
            .ToDictionary(r => r.Entry, r => r.IsExpanded) ?? new Dictionary<ClipboardEntry, bool>();

        var rows = _batchClipboard.Entries
            .OrderByDescending(e => e.CutAt)
            .Select(e => BuildClipboardRow(e, prevExpanded.TryGetValue(e, out var exp) && exp))
            .ToList();
        TV_Clipboard.ItemsSource = rows;
    }

    ClipboardRow BuildClipboardRow(ClipboardEntry e, bool wasExpanded)
    {
        string status;
        if (!e.Pending)
        {
            status = $"Pasted -> {e.PastedTo!.Value.Label()} ({e.PastedAt:yyyy-MM-dd HH:mm})";
        }
        else
        {
            var staged = _stagedPastes.FirstOrDefault(sp => ReferenceEquals(sp.Entry, e));
            status = staged.Entry != null ? $"Staged -> {staged.To.Label()}" : "Pending";
        }
        string groupTag = e.BankCopyGroup != null ? "  [bank copy]" : "";
        string label = $"{e.Origin.Label()}{groupTag}  —  {e.Reason}   cut {e.CutAt:yyyy-MM-dd HH:mm}   [{status}]";
        return new ClipboardRow(e, label, BuildRefChildren(e)) { IsExpanded = wasExpanded, IsBatchSelected = _clipboardSelection.Contains(e) };
    }

    // Decodes a Combi's 16 timbre->Program refs or a Set List's up to 128 slot->Program/Combi
    // refs straight from the entry's own already-dumped Body — zero extra hardware access, using
    // the same LibRefs iterators PlanMove/PlanBatchMove use to find referrer patch sites. Every
    // slot always resolves to SOME object (no "empty slot" concept anywhere else in this window),
    // so every timbre/slot gets a row, not just the ones that look "set".
    List<ClipboardRefRow> BuildRefChildren(ClipboardEntry e)
    {
        var kids = new List<ClipboardRefRow>();
        if (e.ObjType == LibObj.Combi)
        {
            foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(e.Body))
            {
                int objBank = KronosBanks.Func33ToObjBank(1, fbank);   // refType 1 = program
                kids.Add(new ClipboardRefRow(objBank < 0
                    ? $"Timbre {t + 1}: (unresolvable bank {fbank})"
                    : $"Timbre {t + 1}: {SlotLabel(new ObjLoc(LibObj.Program, objBank, num))}"));
            }
        }
        else if (e.ObjType == LibObj.SetList)
        {
            foreach (var (s, type, fbank, idx) in LibRefs.IterSetListSlotRefs(e.Body))
            {
                if (type == 2) { kids.Add(new ClipboardRefRow($"Slot {s + 1}: (Song — unsupported)")); continue; }
                int objBank = KronosBanks.Func33ToObjBank(type, fbank);   // type: 0=Combi, 1=Program
                kids.Add(new ClipboardRefRow(objBank < 0
                    ? $"Slot {s + 1}: (unresolvable bank {fbank})"
                    : $"Slot {s + 1}: {SlotLabel(new ObjLoc(type == 1 ? LibObj.Program : LibObj.Combi, objBank, idx))}"));
            }
        }
        return kids;
    }

    // ── TV_Clipboard multi-select ───────────────────────────────────────────────
    // Mirrors OnLeafPreviewMouseDown's Ctrl/Shift-select pattern exactly (see its doc comment for
    // why this must never rebuild TV_Clipboard.ItemsSource from a click) — plain click selects
    // just this row, Ctrl toggles, Shift range-selects over the currently displayed
    // (CutAt-descending) order. Feeds Paste Multi.
    void OnClipboardRowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipboardRow row) return;

        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (!ctrl && !shift)
        {
            ClearClipboardRowSelection();
            _clipboardSelection.Clear();
            _clipboardSelection.Add(row.Entry);
            row.IsBatchSelected = true;
            _lastClipboardTouch = row.Entry;
            return;
        }

        if (shift && _lastClipboardTouch is { } anchor)
        {
            var order = (TV_Clipboard.ItemsSource as IEnumerable<ClipboardRow>)?.ToList() ?? new();
            int ai = order.FindIndex(r => ReferenceEquals(r.Entry, anchor));
            int bi = order.FindIndex(r => ReferenceEquals(r.Entry, row.Entry));
            if (ai >= 0 && bi >= 0)
            {
                int lo = Math.Min(ai, bi), hi = Math.Max(ai, bi);
                for (int i = lo; i <= hi; i++)
                {
                    _clipboardSelection.Add(order[i].Entry);
                    order[i].IsBatchSelected = true;
                }
            }
            else
            {
                _clipboardSelection.Add(row.Entry);
                row.IsBatchSelected = true;
            }
        }
        else if (!_clipboardSelection.Add(row.Entry))
        {
            _clipboardSelection.Remove(row.Entry);
            row.IsBatchSelected = false;
        }
        else
        {
            row.IsBatchSelected = true;
        }

        _lastClipboardTouch = row.Entry;
        e.Handled = true;
    }

    void ClearClipboardRowSelection()
    {
        if (TV_Clipboard.ItemsSource is not IEnumerable<ClipboardRow> rows) return;
        foreach (var r in rows)
            if (_clipboardSelection.Contains(r.Entry)) r.IsBatchSelected = false;
    }

    // ── Copy to Clipboard ────────────────────────────────────────────────────
    // Right-click "Copy to Clipboard" — acts on the CURRENT _batchSelection (whichever node was
    // right-clicked is irrelevant; Copy always copies "whatever is selected", matching how
    // Verify/Commit already read _batchSelection-derived state rather than the click target).
    // Pure reads (DumpObjectAsync only) — no backup/staleness gate needed here, that's enforced
    // later at Verify/Commit time once a copied entry is actually pasted somewhere.
    async void OnCopyToClipboard(object sender, RoutedEventArgs e) => await CopyToClipboardAsync();

    async Task CopyToClipboardAsync()
    {
        if (_busy) return;
        if (_batchSelection.Count == 0) { Log("Copy: select one or more Program/Combi/Set List slots first (Ctrl/Shift-click in the list above)."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }

        var locs = _batchSelection.ToList();
        _busy = true;
        RefreshEnable();
        try
        {
            int copied = 0;
            foreach (var loc in locs)
            {
                var dump = await _sysEx.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                if (dump == null) { Log($"  Copy failed: could not dump {loc.Label()}."); continue; }
                _batchClipboard.Entries.Add(new ClipboardEntry
                {
                    ObjType = loc.ObjType, Origin = loc, Version = dump.Version, Body = dump.Body,
                    Provenance = ClipboardProvenance.UserCopy, Reason = "copied by user", CutAt = DateTime.Now,
                });
                copied++;
            }
            if (copied > 0)
            {
                await Task.Run(() => BatchLibrarian.SaveClipboard(_host, _batchClipboard));
                Log($"Copied {copied} item(s) to clipboard. Right-click a destination slot to paste.");
                ClearNodeSelection();
                _batchSelection.Clear();
                RefreshBatchSelectionUi();
                RefreshClipboardUi();
            }
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // ── Paste variants ───────────────────────────────────────────────────────
    // All four are pure staging — no hardware access, nothing written until Commit. Each marks
    // its destination(s) red (PasteState.Staged) immediately, matching "once pasted from the
    // clipboard, the object changes color" — Verify re-confirms feasibility afterward, it doesn't
    // change what's staged.

    static string TypeNoun(int objType) => objType switch { LibObj.Program => "Program", LibObj.Combi => "Combi", _ => "Set List" };

    bool StagePaste(ClipboardEntry entry, ObjLoc to)
    {
        if (_stagedPastes.Any(sp => sp.To.Equals(to)))
        {
            Log($"  Skipped {entry.Origin.Label()} — {to.Label()} already has a staged paste.");
            return false;
        }
        _stagedPastes.Add((entry, to));
        SetPasteState(to, PasteState.Staged);
        _plan = null;   // staging changed — any previously-armed plan no longer reflects it
        _batchPlan = null;
        return true;
    }

    void OnPasteSingle(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) return;
        if (_refIndex == null) { Log("Paste Single: Scan the library first (needed to check references) — nothing staged."); return; }

        var selected = _clipboardSelection.Where(x => x.Pending && x.ObjType == loc.ObjType).ToList();
        var candidates = selected.Count > 0 ? selected : _batchClipboard.Pending.Where(x => x.ObjType == loc.ObjType).ToList();

        if (candidates.Count == 0) { Log($"Paste Single: no pending clipboard entry matches a {TypeNoun(loc.ObjType)} destination."); return; }
        if (candidates.Count > 1) { Log($"Paste Single: {candidates.Count} candidates — click one entry in the Clipboard list to pick which, then try again."); return; }

        if (StagePaste(candidates[0], loc))
            Log($"Staged: {candidates[0].Origin.Label()} (clipboard)  ->  {loc.Label()}.");
        RefreshClipboardUi();
        RefreshEnable();
    }

    // Shared by Paste Multi/All — assigns sequential slots starting exactly at `dest` (not
    // always slot 0 — see BatchLibrarian.ResolveSequentialFill's doc comment) to `sourceEntries`.
    void PasteSequential(ObjLoc dest, List<ClipboardEntry> sourceEntries, string actionLabel)
    {
        if (sourceEntries.Count == 0) { Log($"{actionLabel}: nothing pending/selected matches a {TypeNoun(dest.ObjType)} destination."); return; }
        Func<int, bool?>? bankTypeOf = dest.ObjType == LibObj.Program ? BankTypeOf : null;
        if (dest.ObjType == LibObj.Program && !_programBankTypesLive)
        {
            Log($"{actionLabel}: program bank HD-1/EXi types haven't been fetched yet — run Scan Library first.");
            return;
        }

        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(sourceEntries, dest.ObjType, dest.Bank, dest.Number, bankTypeOf);
        int staged = 0;
        foreach (var (entry, slot) in placed)
            if (StagePaste(entry, new ObjLoc(dest.ObjType, dest.Bank, slot)))
                staged++;

        Log($"{actionLabel}: staged {staged} of {sourceEntries.Count} entr{(sourceEntries.Count == 1 ? "y" : "ies")} starting at {dest.Label()}.");
        foreach (var (entry, reason) in stillPending) Log($"  {entry.Origin.Label()}  ->  still pending ({reason})");
        RefreshClipboardUi();
        RefreshEnable();
    }

    void OnPasteMulti(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) return;
        if (_refIndex == null) { Log("Paste Multi: Scan the library first (needed to check references) — nothing staged."); return; }

        var selected = _clipboardSelection.Where(x => x.Pending && x.ObjType == loc.ObjType).OrderBy(x => x.CutAt).ToList();
        if (selected.Count == 0) { Log("Paste Multi: Ctrl/Shift-click two or more pending, matching-type entries in the Clipboard list first."); return; }
        PasteSequential(loc, selected, "Paste Multi");
    }

    void OnPasteAll(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;
        if (loc.ObjType != LibObj.Program && loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) return;
        if (_refIndex == null) { Log("Paste All: Scan the library first (needed to check references) — nothing staged."); return; }

        var pending = _batchClipboard.Pending.Where(x => x.ObjType == loc.ObjType).OrderBy(x => x.CutAt).ToList();
        PasteSequential(loc, pending, "Paste All");
    }

    // ── Copy Bank / Paste Bank ───────────────────────────────────────────────
    // A bank-level node has no Loc (only leaves do) but does carry a Rescan scope identifying
    // which bank (or, for Set Lists — which have no bank concept — the whole type) it is. Same
    // shared ContextMenu as everything else (see LibrarianWindow.xaml); these two self-guard with
    // a log message if right-clicked on a leaf, matching every other handler's convention.
    static bool IsBankLevelNode(ObjectBrowserNode node) =>
        node.Loc is null &&
        ((node.Rescan.ObjType is LibObj.Program or LibObj.Combi && node.Rescan.Bank is int && node.Rescan.Number is null) ||
         (node.Rescan.ObjType == LibObj.SetList && node.Rescan.Bank is null && node.Rescan.Number is null));

    async void OnCopyBank(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode node) return;
        if (!IsBankLevelNode(node)) { Log("Copy Bank to Clipboard: right-click a bank (or the Set Lists root), not a slot."); return; }
        await CopyBankAsync(node);
    }

    async Task CopyBankAsync(ObjectBrowserNode node)
    {
        if (_busy) return;
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }

        int objType = node.Rescan.ObjType;
        int bank = node.Rescan.Bank ?? 0;   // Set Lists have no bank — always 0
        int count = objType == LibObj.SetList ? SetListData.MaxCount : 128;

        _busy = true;
        RefreshEnable();
        Log($"\nCopying {node.Label} ({count} object(s)) to clipboard …");
        try
        {
            var group = Guid.NewGuid();
            int copied = 0;
            for (int number = 0; number < count; number++)
            {
                var dump = await _sysEx.DumpObjectAsync(objType, bank, number);
                if (dump == null) { Log($"  Failed to dump slot {number:D3} — skipped."); continue; }
                _batchClipboard.Entries.Add(new ClipboardEntry
                {
                    ObjType = objType, Origin = new ObjLoc(objType, bank, number), Version = dump.Version, Body = dump.Body,
                    Provenance = ClipboardProvenance.UserCopy, Reason = $"copied with {node.Label}", CutAt = DateTime.Now,
                    BankCopyGroup = group,
                });
                copied++;
            }
            if (copied > 0)
            {
                await Task.Run(() => BatchLibrarian.SaveClipboard(_host, _batchClipboard));
                Log($"Copied {copied} object(s) from {node.Label} to clipboard as one group. Right-click a destination bank and choose \"Paste Bank\".");
                RefreshClipboardUi();
            }
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // Direct 1:1 slot-preserving mapping, not ResolveSequentialFill — a bank-copy's 128 entries
    // already carry their original slot number in Origin.Number, and both source and destination
    // are always exactly 128 slots, so there's no overflow/skip case to handle. A Program
    // bank-type mismatch applies uniformly to the whole bank, so it REFUSEs the whole action with
    // one message rather than silently auto-clipboarding 128 individual items (they're already
    // safely in the clipboard either way).
    void OnPasteBank(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode node) return;
        if (!IsBankLevelNode(node)) { Log("Paste Bank: right-click a bank (or the Set Lists root), not a slot."); return; }
        if (_refIndex == null) { Log("Paste Bank: Scan the library first (needed to check references) — nothing staged."); return; }

        int objType = node.Rescan.ObjType;
        int destBank = node.Rescan.Bank ?? 0;

        var group = _batchClipboard.Pending
            .Where(x => x.ObjType == objType && x.BankCopyGroup != null)
            .GroupBy(x => x.BankCopyGroup!.Value)
            .OrderByDescending(g => g.Max(x => x.CutAt))
            .FirstOrDefault();

        if (group == null) { Log($"Paste Bank: no pending bank-copy group matches a {TypeNoun(objType)} destination — use Copy Bank to Clipboard on a bank first."); return; }

        if (objType == LibObj.Program)
        {
            if (!_programBankTypesLive) { Log("Paste Bank: program bank HD-1/EXi types haven't been fetched yet — run Scan Library first."); return; }
            bool? srcType = BankTypeOf(group.First().Origin.Bank);
            bool? dstType = BankTypeOf(destBank);
            if (srcType is bool st && dstType is bool dt && st != dt)
            {
                Log($"Paste Bank: REFUSED — source bank is {(st ? "EXi" : "HD-1")}, destination bank is {(dt ? "EXi" : "HD-1")}. Bank types must match.");
                return;
            }
        }

        int staged = 0;
        foreach (var entry in group)
            if (StagePaste(entry, new ObjLoc(objType, destBank, entry.Origin.Number)))
                staged++;

        Log($"Paste Bank: staged {staged} of {group.Count()} object(s) into {node.Label} (slot-for-slot).");
        RefreshClipboardUi();
        RefreshEnable();
    }

    // ── Unstage ──────────────────────────────────────────────────────────────
    // Right-click an already-staged (red) leaf to undo a paste or swap slot before Commit —
    // removes it from _stagedPastes (clipboard flow) or clears _src/_dst (swap flow) and reverts
    // its color. Not explicitly requested but necessary: pasting stages immediately now, so there
    // needs to be a way to correct a mistake without committing it.
    void OnUnstage(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Loc: { } loc }) return;

        bool did = _stagedPastes.RemoveAll(sp => sp.To.Equals(loc)) > 0;
        if (_src is { } s && s.Equals(loc)) { _src = null; did = true; }
        if (_dst is { } d && d.Equals(loc)) { _dst = null; did = true; }

        if (!did) { Log($"Unstage: {loc.Label()} has nothing staged."); return; }

        ClearPasteState(loc);
        _plan = null;   // staging changed — any previously-armed plan no longer reflects it
        _batchPlan = null;
        UpdateUsage();
        RefreshClipboardUi();
        RefreshEnable();
        Log($"Unstaged {loc.Label()}.");
    }

    // ── Clear Clipboard ─────────────────────────────────────────────────────
    // Wipes every clipboard entry (pending AND pasted-history rows) — a genuine reset, not just
    // "forget the pending ones". Any destination currently staged FROM a clipboard entry
    // (_stagedPastes) is unstaged first so a red slot never outlives the entry that justified it;
    // the swap flow's _src/_dst and any already-Committed (green) markers are untouched — those
    // reflect real hardware state, not clipboard contents. Irreversible, so it's gated on an
    // explicit confirm, same MessageBox.Show(...YesNo...) pattern FileManagerWindow uses for Delete.
    async Task OnClearClipboardAsync()
    {
        // A modal MessageBox pumps a nested message loop, so an in-flight Copy/Paste/Commit's
        // await continuations keep running while the confirm dialog is up — without this guard
        // (every other mutating handler in this file has one), Clear could race e.g. CopyBankAsync
        // still .Add()-ing slots after Entries.Clear() ran, leaving a corrupt partial bank behind.
        if (_busy) { Log("Busy — wait for the current operation to finish."); return; }
        if (_batchClipboard.Entries.Count == 0) { Log("Clipboard is already empty."); return; }

        int count = _batchClipboard.Entries.Count;
        if (MessageBox.Show(this,
                $"Clear all {count} clipboard entr{(count == 1 ? "y" : "ies")} (pending and pasted-history alike)?\n\nThis cannot be undone.",
                "Clear Clipboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        RefreshEnable();
        try
        {
            // _stagedPastes only ever holds clipboard-sourced pastes (the swap flow uses _src/_dst
            // instead — see StagePaste), so every staged entry here belongs to what's being cleared.
            foreach (var loc in _stagedPastes.Select(sp => sp.To).ToList()) ClearPasteState(loc);
            _stagedPastes.Clear();

            _batchClipboard.Entries.Clear();
            _clipboardSelection.Clear();
            _lastClipboardTouch = null;
            _plan = null;   // staging changed — any previously-armed plan no longer reflects it
            _batchPlan = null;

            await Task.Run(() => BatchLibrarian.SaveClipboard(_host, _batchClipboard));
            RefreshClipboardUi();
            Log($"Cleared {count} clipboard entr{(count == 1 ? "y" : "ies")}.");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }
}
