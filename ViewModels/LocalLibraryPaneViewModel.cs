using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// The left ("local copy") pane's view-state: builds/refreshes a Program/Combi/Set List
// tree from LocalLibraryCache, and exposes file-manager-style Cut/Copy/Paste plus per-node
// Rename/Discard/property-edit — all running against LocalEditOps (Core/LocalLibrary),
// never touching hardware directly. Cross-pane drag-drop placement (PCG -> local) lives on
// LibrarianShellViewModel instead, since it's the one thing that needs both panes at once —
// this pane doesn't know the PCG pane exists.
//
// Cut/Copy/Paste/Rename/Discard are plain public methods, not [RelayCommand]s — a per-node
// WPF ContextMenu inside a HierarchicalDataTemplate is a well-known MVVM binding-scope
// friction point (the ContextMenu isn't part of the visual tree, so it can't reach an
// ancestor's DataContext the normal way). Views/LibrarianShellWindow.xaml.cs's code-behind
// grabs the clicked node(s) and calls straight into these methods — the state/logic stays
// here and is exactly as testable, only the click-to-method wiring (and the toolbar's own
// enabled-state, since "what's currently selected" is code-behind's tree-selection state,
// not this ViewModel's) lives in code-behind.
//
// The Cut/Copy clipboard here is a small, session-only field (_clipItems/ClipboardMode) —
// deliberately NOT the persisted BatchClipboard/ClipboardEntry model in
// Core/BatchMoveModel.cs. That model exists for a different, already-solved problem (a
// durable safety net for occupants displaced by a batch placement) and stays exactly as-is;
// pasting still feeds it via the same displacement path a PCG-pane copy already uses.
partial class LocalLibraryPaneViewModel : ObservableObject
{
    readonly LocalLibraryCache _cache;

    public ObservableCollection<ObjectTreeNode> Roots { get; } = new();

    // Raised at the end of RefreshTree() — every edit rebuilds Roots from scratch (brand new
    // ObjectTreeNode instances), so code-behind's selection tracking (keyed by node reference)
    // would otherwise go stale the moment anything is Cut/Copy/Paste/Renamed/Deleted. Subscribers
    // re-walk the fresh Roots and re-apply IsSelected by identity (Loc/BankRef), not by the old
    // object reference.
    public event Action? TreeRefreshed;

    // Set once by LibrarianShellViewModel's constructor to its own BankTypeOf method — this
    // pane has no direct access to _sysEx/_host, so the live-queried Program bank-type lookup
    // is injected the same way ConfirmContinueWithPendingDependencies is elsewhere. Null (a
    // headless self-test, or before the first successful func-0x61 query) just means
    // PasteBatch's own bank-type check can't verify — advisory only, never blocks.
    public Func<int, bool?>? BankTypeOf { get; set; }

    public enum ClipboardMode { None, Cut, Copy }

    // Field named `mode` (not `clipMode`) deliberately — CommunityToolkit's generated
    // property from a `clipMode` field would itself be named `ClipMode`, colliding with the
    // `ClipboardMode` enum type name one letter away (a real CS0102 the first pass hit).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClipboard))]
    [NotifyPropertyChangedFor(nameof(ClipboardLabel))]
    ClipboardMode mode;

    List<ObjLoc> _clipItems = new();

    [ObservableProperty] string statusText = "";

    public bool HasClipboard => Mode != ClipboardMode.None && _clipItems.Count > 0;
    public string ClipboardLabel => Mode switch
    {
        ClipboardMode.Cut  => $"Cut: {_clipItems.Count} item(s)",
        ClipboardMode.Copy => $"Copy: {_clipItems.Count} item(s)",
        _ => "(nothing cut or copied)",
    };

    public LocalLibraryPaneViewModel(LocalLibraryCache cache)
    {
        _cache = cache;
        RefreshTree();
    }

    public void RefreshTree()
    {
        var expandedKeys = ObjectTreeNode.CollectExpandedKeys(Roots);
        Roots.Clear();

        var programsRoot = new ObjectTreeNode("Programs");
        var combisRoot = new ObjectTreeNode("Combis");
        // Set Lists have no bank concept (a flat 128 numbered slots) — the type root itself
        // is the valid auto-fill drop target, unlike Programs/Combis where only a specific
        // BANK sub-node is (dropping on "Programs" itself doesn't say which of 21 banks).
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, 0));

        BuildTypeSubtree(programsRoot, LibObj.Program);
        BuildTypeSubtree(combisRoot, LibObj.Combi);
        BuildSetListSubtree(setListsRoot);

        Roots.Add(programsRoot);
        Roots.Add(combisRoot);
        Roots.Add(setListsRoot);
        ObjectTreeNode.RestoreExpandedKeys(Roots, expandedKeys);
        TreeRefreshed?.Invoke();
    }

    void BuildTypeSubtree(ObjectTreeNode typeRoot, int objType)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        foreach (var bank in descriptor.EditableBanks())
        {
            var bankNode = new ObjectTreeNode(BankNodeLabel(objType, descriptor, bank), bankRef: (objType, bank));
            for (int number = 0; number < descriptor.SlotCount; number++)
            {
                if (!_cache.Exists(objType, bank, number)) continue;   // nothing local for this slot
                bankNode.Children.Add(MakeLeafNode(new ObjLoc(objType, bank, number)));
            }
            if (bankNode.Children.Count > 0) typeRoot.Children.Add(bankNode);
        }
    }

    // Mirrors PcgPaneViewModel.BankNodeLabel's own "(EXi)"/"(HD-1)" suffix exactly — which
    // wire format a bank holds matters just as much once it's local as it did in a loaded
    // .pcg file (a real prior gap: Local Library showed no format indicator at all). Derived
    // from the first occupied slot's cached IsExi bit (LocalLibraryCache.IsExi — index-only,
    // no blob read), since every Program in one bank shares the same format. An empty bank
    // gets no suffix, but BuildTypeSubtree above never adds an empty bank to the tree anyway.
    string BankNodeLabel(int objType, IObjectTypeDescriptor descriptor, int bank)
    {
        string label = descriptor.BankLabel(bank);
        if (objType != LibObj.Program) return label;
        for (int number = 0; number < descriptor.SlotCount; number++)
        {
            if (!_cache.Exists(objType, bank, number)) continue;
            return label + (_cache.IsExi(objType, bank, number) ? " (EXi)" : " (HD-1)");
        }
        return label;
    }

    void BuildSetListSubtree(ObjectTreeNode setListsRoot)
    {
        for (int number = 0; number < SetListData.MaxCount; number++)
        {
            var loc = new ObjLoc(LibObj.SetList, 0, number);
            if (!_cache.Exists(loc.ObjType, loc.Bank, loc.Number)) continue;
            setListsRoot.Children.Add(MakeLeafNode(loc));
        }
    }

    ObjectTreeNode MakeLeafNode(ObjLoc loc)
    {
        string name = ReadDisplayName(loc);
        var label = string.IsNullOrEmpty(name) ? loc.Label() : $"{loc.Label()}  {name}";
        bool isDirty = _cache.IsDirty(loc.ObjType, loc.Bank, loc.Number);

        // The dependency-completeness dot only means anything for a Combi/Set List that's
        // still pending Sync/Commit — a Program never has references to be missing, and a
        // clean/already-synced object isn't what this dot communicates. Backed by
        // LocalLibraryCache's own cached bit (see LocalIndexEntry's doc comment) — index-only,
        // no blob read, same "cheap on every tree refresh" discipline as everything else here.
        bool showsDependencyDot = isDirty && loc.ObjType is LibObj.Combi or LibObj.SetList;

        return new ObjectTreeNode(label, loc)
        {
            IsDirty = isDirty,
            IsConflicted = _cache.IsConflicted(loc.ObjType, loc.Bank, loc.Number),
            DependencyStatus = showsDependencyDot ? _cache.HasResolvedDependencies(loc.ObjType, loc.Bank, loc.Number) : null,
            IsPendingDelete = _cache.IsPendingDelete(loc.ObjType, loc.Bank, loc.Number),
        };
    }

    // Public so the View's double-click handler can pre-populate PropertiesDialog with the
    // current (unlabeled) name before showing it. Backed by LocalLibraryCache's cached
    // DisplayName — never reads a body from disk (see LocalIndexEntry's doc comment for
    // why that matters: this is called once per populated slot on every tree refresh).
    public string ReadDisplayName(ObjLoc loc) => _cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number);

    // ── Cut / Copy / Paste — replaces the old Set as Source/Destination + Swap flow ──────

    // Cut is capped at one item: the only correct "move" this app can perform is a true,
    // symmetric swap onto an already-occupied slot (LocalEditOps.Move, writing both
    // directions) — there is no way to vacate a source slot otherwise (see PasteSingle's own
    // comment), so a multi-item or move-to-empty Cut can never be completed correctly. Copy
    // has no such limit, since it never touches the source.
    public void Cut(IReadOnlyList<ObjLoc> locs)
    {
        var eligible = locs.Where(l => l.ObjType != LibObj.SetList).ToList();
        if (eligible.Count == 0) { ClearClipboard(); StatusText = "Set Lists can't be Cut — Copy instead."; return; }
        if (eligible.Count > 1) { StatusText = "Cut works on one item at a time — select a single item, or use Copy for multiple."; return; }
        _clipItems = eligible;
        Mode = ClipboardMode.Cut;
        StatusText = $"Cut {eligible[0].Label()} — select an occupied slot and Paste to swap.";
    }

    public void Copy(IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        _clipItems = locs.ToList();
        Mode = ClipboardMode.Copy;
        StatusText = locs.Count == 1
            ? $"Copied {locs[0].Label()} — select a destination and Paste."
            : $"Copied {locs.Count} item(s) — select a destination and Paste.";
    }

    public void ClearClipboard()
    {
        _clipItems = new();
        Mode = ClipboardMode.None;
    }

    // Paste onto one specific slot — the common case. Cut is always exactly one item here
    // (see Cut's own comment) and lands via a true swap if the slot is occupied, or refuses
    // if it's empty; Copy can be one or many items, auto-filling from dest onward if there's
    // more than one (same fill behavior as PasteIntoBank below).
    public (bool Ok, string? Message) PasteIntoSlot(ObjLoc dest)
    {
        if (!HasClipboard) return (false, "nothing cut or copied");
        if (_clipItems.Any(l => l.ObjType != dest.ObjType)) return (false, "can't paste here — object type doesn't match");

        bool cut = Mode == ClipboardMode.Cut;
        var result = _clipItems.Count == 1
            ? PasteSingle(_clipItems[0], dest, cut)
            : PasteBatch(_clipItems, dest.ObjType, dest.Bank, dest.Number);
        FinishPaste(result.Ok, cut);
        return result;
    }

    // Paste onto a bank (or the Set Lists root) — always auto-fill into free slots, same as
    // the PCG pane's own drop-on-a-bank behavior. Cut refuses here unconditionally: a bank
    // drop has no specific occupied slot to swap onto, and this app has no way to vacate a
    // source slot otherwise (see PasteSingle's comment) — drop directly on a specific
    // occupied slot instead, or use Copy.
    public (bool Ok, string? Message) PasteIntoBank(int objType, int bank)
    {
        if (!HasClipboard) return (false, "nothing cut or copied");
        if (_clipItems.Any(l => l.ObjType != objType)) return (false, "can't paste here — object type doesn't match");
        if (Mode == ClipboardMode.Cut)
            return (false, "Cut needs a specific occupied slot to swap into — drop directly onto one, or use Copy to fill empty slots.");

        int startSlot = LocalEditOps.FindNextFreeSlot(_cache, objType, bank);
        var result = PasteBatch(_clipItems, objType, bank, startSlot);
        FinishPaste(result.Ok, cut: false);
        return result;
    }

    void FinishPaste(bool ok, bool cut)
    {
        if (ok && cut) ClearClipboard();
        if (ok) RefreshTree();
    }

    (bool Ok, string? Message) PasteSingle(ObjLoc src, ObjLoc dest, bool cut)
    {
        if (src.Equals(dest)) return (false, "source and destination are the same location");

        if (!cut)
        {
            var dump = LocalEditOps.GetObjectDump(_cache, src);
            if (dump == null) return (false, $"{src.Label()} not found locally");
            var label = _cache.GetDisplayName(src.ObjType, src.Bank, src.Number);
            var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(_cache, dest, src.ObjType, dump.Version, dump.Body, label, divertDisplacedToClipboard: true, DateTime.UtcNow);
            if (ok) MergeIntoPersistentClipboard(clipboardAdds);
            return (ok, ok ? $"Copied {src.Label()} to {dest.Label()}" : $"Copy failed: {error}");
        }

        if (_cache.Exists(dest.ObjType, dest.Bank, dest.Number))
        {
            var (ok, error) = LocalEditOps.Move(_cache, src, dest, DateTime.UtcNow);
            return (ok, ok ? $"Moved {src.Label()} ↔ {dest.Label()}" : $"Move failed: {error}");
        }
        else
        {
            // No move-to-empty here: this cache has no primitive that vacates a source slot
            // (Discard only reverts a pending edit back to baseline — a no-op on a clean,
            // just-pulled object) and no way to push "this slot is now empty" to hardware
            // either. A real move is only ever a true swap (LocalEditOps.Move, both
            // directions written) — swap onto an occupied slot instead, or use Copy.
            return (false, $"{dest.Label()} is empty — Cut can only be pasted onto an occupied slot (to swap). Use Copy to place a copy there instead.");
        }
    }

    // N-item Copy, auto-filling free slots in destBank starting at startSlot. Copy-only:
    // Cut is capped at one item and never reaches this (see Cut's and PasteIntoBank's
    // comments), so there is no source to vacate and no `From` to repoint here.
    (bool Ok, string? Message) PasteBatch(IReadOnlyList<ObjLoc> srcs, int objType, int destBank, int startSlot)
    {
        var pending = new List<ClipboardEntry>();
        foreach (var src in srcs)
        {
            var dump = LocalEditOps.GetObjectDump(_cache, src);
            if (dump == null) continue;
            pending.Add(new ClipboardEntry
            {
                ObjType = objType, Origin = src, Version = dump.Version, Body = dump.Body,
                Provenance = ClipboardProvenance.UserCopy, CutAt = DateTime.UtcNow,
            });
        }
        if (pending.Count == 0) return (false, "nothing to paste");

        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(pending, objType, destBank, startSlot, bankTypeOf: null);
        if (placed.Count == 0) return (false, "nothing could be placed (bank full or type mismatch)");

        var placements = placed
            .Select(p => new BatchPlacement(null, new ObjLoc(objType, destBank, p.Slot),
                new ObjectDump(objType, destBank, p.Slot, p.Entry.Version, p.Entry.Body), p.Entry.Origin.Label()))
            .ToList();

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow);
        if (!ok) return (false, error);
        MergeIntoPersistentClipboard(clipboardAdds);

        string msg = $"Placed {placed.Count}" + (stillPending.Count > 0 ? $"; {stillPending.Count} didn't fit (bank full or type mismatch)" : "");
        return (true, msg);
    }

    void MergeIntoPersistentClipboard(List<ClipboardEntry> newEntries)
    {
        if (newEntries.Count == 0) return;
        var clip = BatchLibrarian.LoadClipboardGlobal();
        clip.Entries.AddRange(newEntries);
        BatchLibrarian.SaveClipboardGlobal(clip);
    }

    public void Rename(ObjLoc loc, string newName)
    {
        var (ok, error) = LocalEditOps.Rename(_cache, loc, newName, DateTime.UtcNow);
        StatusText = ok ? $"Renamed {loc.Label()} to \"{newName}\"" : $"Rename failed: {error}";
        if (ok) RefreshTree();
    }

    public void Discard(ObjLoc loc)
    {
        var (ok, error) = LocalEditOps.Discard(_cache, loc, DateTime.UtcNow);
        StatusText = ok ? $"Discarded {loc.Label()}" : $"Discard failed: {error}";
        if (ok) RefreshTree();
    }

    // Multi-select Delete — best-effort across the whole selection rather than all-or-
    // nothing, since a mid-selection failure (e.g. something already discarded elsewhere)
    // shouldn't block discarding the rest.
    public void DiscardMany(IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        int ok = 0;
        foreach (var loc in locs)
            if (LocalEditOps.Discard(_cache, loc, DateTime.UtcNow).Ok) ok++;
        StatusText = ok == locs.Count ? $"Discarded {ok} item(s)" : $"Discarded {ok}/{locs.Count} item(s)";
        if (ok > 0) RefreshTree();
    }

    // "Delete" (toolbar/context-menu/Del key) — local-only: abandons any pending edit (same as
    // Discard above) and marks the object PendingDelete so it fades in place instead of
    // vanishing; hardware is unaffected until Commit (which today simply pushes nothing for a
    // pending-delete with no other edit, same as it always has). Calling this again on an
    // already-pending item is the undo — just clears the flag, no re-Discard.
    public void ToggleDelete(ObjLoc loc)
    {
        bool markForDeletion = !_cache.IsPendingDelete(loc.ObjType, loc.Bank, loc.Number);
        if (markForDeletion) LocalEditOps.Discard(_cache, loc, DateTime.UtcNow);
        var (ok, error) = LocalEditOps.SetPendingDelete(_cache, loc, markForDeletion, DateTime.UtcNow);
        StatusText = ok
            ? (markForDeletion ? $"Marked {loc.Label()} for deletion" : $"Restored {loc.Label()}")
            : $"{(markForDeletion ? "Delete" : "Restore")} failed: {error}";
        if (ok) RefreshTree();
    }

    // Multi-select Delete/Restore — one direction for the whole selection (whichever the
    // toolbar/menu is currently showing, per LibrarianShellWindow's label logic: Restore only
    // when EVERY selected item is already pending-delete, Delete otherwise), best-effort like
    // DiscardMany above.
    public void ToggleDeleteMany(IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        bool markForDeletion = !locs.All(l => _cache.IsPendingDelete(l.ObjType, l.Bank, l.Number));
        int ok = 0;
        foreach (var loc in locs)
        {
            if (markForDeletion) LocalEditOps.Discard(_cache, loc, DateTime.UtcNow);
            if (LocalEditOps.SetPendingDelete(_cache, loc, markForDeletion, DateTime.UtcNow).Ok) ok++;
        }
        string verb = markForDeletion ? "Marked" : "Restored";
        StatusText = ok == locs.Count ? $"{verb} {ok} item(s)" : $"{verb} {ok}/{locs.Count} item(s)";
        if (ok > 0) RefreshTree();
    }

    // Backing data for PropertiesDialog — a single body read (not a bulk operation), so
    // this is fine to call once when the dialog opens (unlike the tree-building path,
    // which must never touch a blob per slot).
    // "Clear Changes" — reverts EVERY pending local edit back to baseline and clears every
    // pending-delete flag, in one action. Confirmation lives in code-behind (destructive, same
    // split as ClearHistory/Clear Merge). Each object still goes through the same Discard/
    // SetPendingDelete primitives every other local edit action uses, so it's auditable history
    // like everything else, not a silent bulk wipe.
    public void ClearAllChanges()
    {
        var locs = _cache.DirtyObjects().Concat(_cache.PendingDeleteObjects()).Distinct().ToList();
        if (locs.Count == 0) { StatusText = "Nothing to clear."; return; }
        int ok = 0;
        foreach (var loc in locs)
        {
            bool didDiscard = LocalEditOps.Discard(_cache, loc, DateTime.UtcNow).Ok;
            bool didRestore = LocalEditOps.SetPendingDelete(_cache, loc, false, DateTime.UtcNow).Ok;
            if (didDiscard || didRestore) ok++;
        }
        StatusText = $"Cleared {ok} pending change(s).";
        RefreshTree();
    }

    public ObjectDump? GetObjectDump(ObjLoc loc) => LocalEditOps.GetObjectDump(_cache, loc);

    public void EditProperties(ObjLoc loc, string? name, int? category, int? subCategory)
    {
        var (ok, error) = LocalEditOps.EditProperties(_cache, loc, name, category, subCategory, DateTime.UtcNow);
        StatusText = ok ? $"Edited {loc.Label()}" : $"Edit failed: {error}";
        if (ok) RefreshTree();
    }

    public void EditSetListSlot(ObjLoc loc, int slot, string? name, int? color, string? comments)
    {
        var (ok, error) = LocalEditOps.EditSetListSlot(_cache, loc, slot, name, color, comments, DateTime.UtcNow);
        StatusText = ok ? $"Edited {loc.Label()} slot {slot}" : $"Edit failed: {error}";
        if (ok) RefreshTree();
    }
}
