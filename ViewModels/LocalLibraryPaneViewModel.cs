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

    // True while the referrer catalog is (re)building (see LibrarianShellViewModel.WarmCatalogAsync).
    // The view hides the tree and disables the toolbar until this clears, so a move/edit can't run
    // against a half-built index. Defaults true so the pane starts hidden until indexing completes.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    bool isIndexing = true;

    // Binding convenience: the tree/toolbar are shown/enabled only when NOT indexing.
    public bool IsReady => !IsIndexing;

    // The centered placeholder shown in the tree's place while indexing (see AppMessages).
    public string IndexingPlaceholder => AppMessages.Librarian.Local.IndexingPlaceholder;

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

    // The Programs/Combis/Set Lists tree SHAPE is shared with the PCG pane (ObjectTreeScaffold);
    // this pane supplies only what's local-specific — which slots the cache holds, and the rich
    // per-leaf decoration (dirty/conflicted/pending-delete/dependency dot). keepEmptyRoots: true
    // keeps all three type roots visible even when empty (a Set List root, for instance, is a
    // valid auto-fill drop target regardless), the behavior this pane has always had.
    public void RefreshTree()
    {
        ObjectTreeScaffold.Rebuild(
            Roots,
            banksFor: BanksFor,
            setListLocs: SetListLocs(),
            makeLeaf: MakeLeafNode,
            bankLabel: (objType, bank) => BankNodeLabel(objType, ObjectTypeRegistry.Get(objType), bank),
            keepEmptyRoots: true);
        TreeRefreshed?.Invoke();
    }

    // The populated banks of one Program/Combi object type, in EditableBanks() order — a bank's
    // Locs are only the slots the cache actually holds (index-only Exists check, no blob read).
    IReadOnlyList<ObjectTreeScaffold.Bank> BanksFor(int objType)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        var banks = new List<ObjectTreeScaffold.Bank>();
        foreach (var bank in descriptor.EditableBanks())
        {
            var locs = new List<ObjLoc>();
            for (int number = 0; number < descriptor.SlotCount; number++)
                if (_cache.Exists(objType, bank, number))
                    locs.Add(new ObjLoc(objType, bank, number));
            banks.Add(new ObjectTreeScaffold.Bank(bank, locs));
        }
        return banks;
    }

    // The populated Set List slots (flat, all bank 0), in numeric order.
    IReadOnlyList<ObjLoc> SetListLocs()
    {
        var locs = new List<ObjLoc>();
        for (int number = 0; number < SetListData.MaxCount; number++)
        {
            var loc = new ObjLoc(LibObj.SetList, 0, number);
            if (_cache.Exists(loc.ObjType, loc.Bank, loc.Number)) locs.Add(loc);
        }
        return locs;
    }

    // Mirrors PcgPaneViewModel.BankNodeLabel's own "(EXi)"/"(HD-1)" suffix exactly — which
    // wire format a bank holds matters just as much once it's local as it did in a loaded
    // .pcg file (a real prior gap: Local Library showed no format indicator at all). Derived
    // from the first occupied slot's cached IsExi bit (LocalLibraryCache.IsExi — index-only,
    // no blob read), since every Program in one bank shares the same format. Only ever called
    // for a populated bank (the scaffold skips empty ones), so bank.Locs is never empty here.
    string BankNodeLabel(int objType, IObjectTypeDescriptor descriptor, ObjectTreeScaffold.Bank bank)
    {
        string label = descriptor.BankLabel(bank.Number);
        if (objType != LibObj.Program) return label;
        var first = bank.Locs[0];
        return label + (_cache.IsExi(first.ObjType, first.Bank, first.Number) ? " (EXi)" : " (HD-1)");
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
    //
    // Set Lists are eligible now (requirement 1): a Set-List swap is a pure body-swap — nothing
    // ever references a Set List (LibraryCatalog.ReferrersOf returns empty for it), so
    // Librarian.PlanMove just writes the two bodies swapped, with no referrer patching. The
    // earlier Set-List exclusion here was conservatism, not a correctness guard.
    public void Cut(IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) { ClearClipboard(); StatusText = AppMessages.Librarian.Local.NothingToCut; return; }
        if (locs.Count > 1) { StatusText = AppMessages.Librarian.Local.CutOneAtATime; return; }
        _clipItems = locs.ToList();
        Mode = ClipboardMode.Cut;
        StatusText = AppMessages.Librarian.Local.Cut(locs[0].Label());
    }

    public void Copy(IReadOnlyList<ObjLoc> locs)
    {
        if (locs.Count == 0) return;
        _clipItems = locs.ToList();
        Mode = ClipboardMode.Copy;
        StatusText = locs.Count == 1
            ? AppMessages.Librarian.Local.CopiedOne(locs[0].Label())
            : AppMessages.Librarian.Local.CopiedMany(locs.Count);
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
        if (!HasClipboard) return (false, AppMessages.Librarian.Local.NothingCutOrCopied);
        if (_clipItems.Any(l => l.ObjType != dest.ObjType)) return (false, AppMessages.Librarian.Local.TypeMismatch);

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
        if (!HasClipboard) return (false, AppMessages.Librarian.Local.NothingCutOrCopied);
        if (_clipItems.Any(l => l.ObjType != objType)) return (false, AppMessages.Librarian.Local.TypeMismatch);
        if (Mode == ClipboardMode.Cut)
            return (false, AppMessages.Librarian.Local.CutNeedsOccupiedSlot);

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
        if (src.Equals(dest)) return (false, AppMessages.Librarian.Local.SameLocation);

        if (!cut)
        {
            var dump = LocalEditOps.GetObjectDump(_cache, src);
            if (dump == null) return (false, AppMessages.Librarian.Local.NotFoundLocally(src.Label()));
            var label = _cache.GetDisplayName(src.ObjType, src.Bank, src.Number);
            var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(_cache, dest, src.ObjType, dump.Version, dump.Body, label, divertDisplacedToClipboard: true, DateTime.UtcNow);
            if (ok) MergeIntoPersistentClipboard(clipboardAdds);
            return (ok, ok ? AppMessages.Librarian.Local.CopiedTo(src.Label(), dest.Label()) : AppMessages.Librarian.Local.CopyFailed(error));
        }

        if (_cache.Exists(dest.ObjType, dest.Bank, dest.Number))
        {
            var (ok, error) = LocalEditOps.Move(_cache, src, dest, DateTime.UtcNow);
            return (ok, ok ? AppMessages.Librarian.Local.Swapped(src.Label(), dest.Label()) : AppMessages.Librarian.Local.MoveFailed(error));
        }
        else
        {
            // No move-to-empty here: this cache has no primitive that vacates a source slot
            // (Discard only reverts a pending edit back to baseline — a no-op on a clean,
            // just-pulled object) and no way to push "this slot is now empty" to hardware
            // either. A real move is only ever a true swap (LocalEditOps.Move, both
            // directions written) — swap onto an occupied slot instead, or use Copy.
            return (false, AppMessages.Librarian.Local.EmptySlotCut(dest.Label()));
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
        if (pending.Count == 0) return (false, AppMessages.Librarian.Local.NothingToPaste);

        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(pending, objType, destBank, startSlot, bankTypeOf: null);
        if (placed.Count == 0) return (false, AppMessages.Librarian.Local.NothingCouldBePlaced);

        var placements = placed
            .Select(p => new BatchPlacement(null, new ObjLoc(objType, destBank, p.Slot),
                new ObjectDump(objType, destBank, p.Slot, p.Entry.Version, p.Entry.Body), p.Entry.Origin.Label()))
            .ToList();

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow);
        if (!ok) return (false, error);
        MergeIntoPersistentClipboard(clipboardAdds);

        string msg = AppMessages.Librarian.Local.PlacedCount(placed.Count, stillPending.Count);
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
        StatusText = ok ? AppMessages.Librarian.Local.Renamed(loc.Label(), newName) : AppMessages.Librarian.Local.RenameFailed(error);
        if (ok) RefreshTree();
    }

    public void Discard(ObjLoc loc)
    {
        var (ok, error) = LocalEditOps.Discard(_cache, loc, DateTime.UtcNow);
        StatusText = ok ? AppMessages.Librarian.Local.Discarded(loc.Label()) : AppMessages.Librarian.Local.DiscardFailed(error);
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
        StatusText = AppMessages.Librarian.Local.DiscardedCount(ok, locs.Count);
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
            ? (markForDeletion ? AppMessages.Librarian.Local.MarkedForDeletion(loc.Label()) : AppMessages.Librarian.Local.Restored(loc.Label()))
            : AppMessages.Librarian.Local.DeleteRestoreFailed(markForDeletion, error);
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
        StatusText = AppMessages.Librarian.Local.MarkedRestoredCount(markForDeletion, ok, locs.Count);
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
        if (locs.Count == 0) { StatusText = AppMessages.Librarian.Local.NothingToClear; return; }
        int ok = 0;
        foreach (var loc in locs)
        {
            bool didDiscard = LocalEditOps.Discard(_cache, loc, DateTime.UtcNow).Ok;
            bool didRestore = LocalEditOps.SetPendingDelete(_cache, loc, false, DateTime.UtcNow).Ok;
            if (didDiscard || didRestore) ok++;
        }
        StatusText = AppMessages.Librarian.Local.ClearedChanges(ok);
        RefreshTree();
    }

    public ObjectDump? GetObjectDump(ObjLoc loc) => LocalEditOps.GetObjectDump(_cache, loc);

    // Human-readable descriptions of every Combi timbre / Set List slot that currently points at
    // `loc` (issue 1 — used to warn before deleting a dependency that would leave those referrers
    // dangling). Empty for a Set List (nothing ever references one) and for anything nothing
    // points at. Uses the memoized catalog, so the first call after the window opens may build it.
    public IReadOnlyList<string> DescribeReferrers(ObjLoc loc)
    {
        if (loc.ObjType == LibObj.SetList) return Array.Empty<string>();
        return _cache.BuildCatalog().ReferrersOf(loc).Select(r => r.Describe()).ToList();
    }

    public void EditProperties(ObjLoc loc, string? name, int? category, int? subCategory)
    {
        var (ok, error) = LocalEditOps.EditProperties(_cache, loc, name, category, subCategory, DateTime.UtcNow);
        StatusText = ok ? AppMessages.Librarian.Local.Edited(loc.Label()) : AppMessages.Librarian.Local.EditFailed(error);
        if (ok) RefreshTree();
    }

    public void EditSetListSlot(ObjLoc loc, int slot, string? name, int? color, string? comments)
    {
        var (ok, error) = LocalEditOps.EditSetListSlot(_cache, loc, slot, name, color, comments, DateTime.UtcNow);
        StatusText = ok ? AppMessages.Librarian.Local.EditedSlot(loc.Label(), slot) : AppMessages.Librarian.Local.EditFailed(error);
        if (ok) RefreshTree();
    }
}
