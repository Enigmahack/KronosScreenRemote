using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;   // Dispatcher.Yield - see AutoFillToLibraryAsync on why the priority matters
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KronosScreenRemote.ViewModels;
// Cross-pane placement for the Librarian shell - PCG -> Local, Merge Window -> Local, the
// Auto-Fill sweeps, whole-bank copies with an HD-1/EXi type change, and the type-root drop
// targets that feed them. Split out of LibrarianShellViewModel.cs purely for size: this is the
// half of the shell that MOVES objects between panes, as opposed to the half that DESCRIBES
// what they reference (LibrarianShellViewModel.Dependencies.cs).
//
// Still a partial of the same ViewModel rather than a standalone coordinator: every method here
// reads or writes shell state that XAML binds to (both panes, the session clipboard, IsBusy,
// the pane status lines), so a separate class would take the ViewModel as its only constructor
// argument and add indirection without moving a single dependency.
partial class LibrarianShellViewModel
{
    // ── Cross-pane placement (PCG -> local), requirement 12 ──────────────────────────
    // Drop on a specific slot = exact placement. HW-write never happens here - this only
    // ever touches the local cache via LocalEditOps, exactly like every other local edit.

    public (bool Ok, string? Error) PlaceFromPcg(ObjLoc pcgLoc, ObjLoc destLoc)
    {
        // One undo step for the whole placement, including whatever occupant it displaces. A
        // guard below that returns before writing anything captures nothing, so no empty step
        // is pushed (see LibrarianUndoStep.CapturedNothing).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedAt(pcgLoc.Label(), destLoc.Label()));

        // Cross-type guard, same as BatchPlaceFromPcg's per-item check: the single-item drop
        // path has no upstream type check (OnLocalDrop filters on drag format only), and combi
        // bank numbers are a numeric subset of program bank numbers - a mismatched drop would
        // otherwise land silently in a valid-looking slot of the wrong type.
        if (pcgLoc.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(pcgLoc.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");

        var entry = PcgPane.Get(pcgLoc);
        if (entry == null) return (false, "not found in the loaded PCG file");
        var rawBody = ProgramFormatConverter.WireBodyFromPcgEntry(pcgLoc.ObjType, entry);
        if (rawBody == null) return (false, "malformed Program record in the loaded PCG file");

        // Repoint whatever of this object's OWN references already resolve somewhere in Local
        // Library (by content, not just the raw address the PCG encoded) before ever writing
        // it - see DependencyScanner.RepointPcgReferences's own comment. `entry != null` above
        // already guarantees PcgPane.View isn't null; the pattern-match is just defensive.
        var (body, unresolved) = PcgPane.View is { } view
            ? DependencyScanner.RepointPcgReferences(_cache, view, pcgLoc.ObjType, rawBody)
            : (rawBody, new List<(RefKind RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)>());

        var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(
            _cache, destLoc, pcgLoc.ObjType, LibObj.CurrentObjectVersion(pcgLoc.ObjType) ?? 0, body, entry.Name,
            divertDisplacedToClipboard: true, DateTime.UtcNow, BankTypeOf);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        StageAndTrackPcgDependencies(unresolved, destLoc);
        LocalPane.RefreshTree();
        NotifyLocalEditMade();
        return (true, null);
    }

    // ── PCG -> Merge Window (fully automatic, transitive - see MergeCache.PullFromPcg) ──

    public void PullIntoMerge(ObjLoc pcgLoc) => PullIntoMerge(new[] { pcgLoc });

    // Multi-item entry point (a multi-select or a whole-bank drag/context-menu action). Handed to
    // the pane as ONE list rather than looped here so the whole gesture is one undo step and one
    // status line - see MergePaneViewModel.PullFromPcg's list overload for why the loop can't live
    // on this side any more.
    public void PullIntoMerge(IReadOnlyList<ObjLoc> pcgLocs)
    {
        if (pcgLocs.Count == 0) return;
        if (PcgPane.View is not { } view) return;   // nothing loaded - nothing to pull from
        MergePane.PullFromPcg(view, PcgPane.LoadedFileName ?? "(unknown)", pcgLocs);
    }

    // ── Local -> Merge Window (requirement 3, transitive - see MergeCache.PullFromLocal) ──
    // The Merge Window as a general scratchpad: stage an already-placed Local object (plus its
    // local dependencies) back in so it can be moved/rearranged and pushed somewhere else.
    // A read-only GM/g row has no body in the library to stage - it is a name from the shared
    // name cache and nothing more (see ReadOnlyBankNames), so staging it would add an empty or
    // phantom Merge entry.
    public void PullLocalIntoMerge(ObjLoc localLoc) => PullLocalIntoMerge(new[] { localLoc });

    // Same one-gesture-one-step-one-status-line reasoning as PullIntoMerge's list overload above.
    public void PullLocalIntoMerge(IReadOnlyList<ObjLoc> localLocs)
    {
        localLocs = localLocs.Where(l => !ObjectTypeRegistry.IsReadOnly(l)).ToList();
        if (localLocs.Count == 0) return;
        MergePane.PullFromLocal(_cache, localLocs);
    }

    // ── Merge Window group -> Local (bulk placement of a multi-item Merge selection) ─────
    // Dragging a multi-item Merge Window selection - typically the whole "Set Lists"/"Combis"/
    // "Programs" group node (LibrarianShellWindow's Merge pane bank-equivalent selection), but
    // works equally for any multi-leaf Ctrl+click selection sharing one type - onto a local
    // bank or a specific slot within one, instead of placing one staged item at a time. If the
    // drop landed on a specific slot, destSlot is that slot's index and the fill starts EXACTLY
    // there - the user pointed at it, so that's where placement begins, not wherever the first
    // free slot happens to be. Dropping on the bank/group node itself instead (destSlot null)
    // falls back to destBank's own first free slot (LocalEditOps.FindNextFreeSlot). Either way,
    // fill is sequential, not "must be completely empty" (an earlier, more conservative version
    // of this method required that; real use showed a partially-filled bank with plenty of room
    // left was the common case, not the exception). An occupied-but-unreferenced slot in the
    // way is overwritten with its occupant diverted to the persisted clipboard (never lost) -
    // the same safety net every other batch placement in this app already relies on; a
    // referenced occupant still REFUSEs via LocalEditOps.BatchPlace's own orphan gate. Only
    // entries matching destBank's own type are placed (silently drops anything else, e.g. a
    // stray different-type hash) - nested dependency Programs/Combis stay staged for individual
    // placement afterward, exactly like PlaceFromMerge above already works (this doesn't
    // cascade into placing dependencies either). Anything beyond the bank's remaining room
    // stays staged too (never lost), same "flag what didn't fit" convention BatchPlaceFromPcg
    // uses.
    // Duplicate-content guard (same as PlaceFromMerge's single-item path): anything whose
    // content already lives elsewhere in Keyboard Library is repointed there instead of consuming
    // a destination slot for a second copy - never needs a bank, let alone a free slot in one.
    // Honours the per-type preserve-duplication toggles (FindExistingLocalCopy). Shared by
    // PlaceMergeGroupSequentially and AutoFillFromMergeAsync so a duplicate is caught the same
    // way regardless of which one is asking - see AutoFillFromMergeAsync's own comment for the
    // real bug this split fixes (a fully-packed bank used to skip this check entirely for
    // Auto-Fill, stranding pure duplicates in the Merge Window that a manual drag deduped fine).
    (List<MergeEntry> ToPlace, int Deduped) DedupMergeGroup(int objType, IReadOnlyList<string> contentHashes)
    {
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e != null && e!.ObjType == objType).Select(e => e!).ToList();
        var toPlace = new List<MergeEntry>();
        int deduped = 0;
        // Each CommitPlacement still lands immediately - the NEXT entry's FindExistingLocalCopy
        // resolves against the placements this sweep has already recorded. The scope only
        // collapses the persistence writes and tree rebuilds, which a whole-bank re-copy would
        // otherwise pay once per duplicate.
        using (MergePane.DeferCommits())
            foreach (var entry in group)
            {
                if (FindExistingLocalCopy(entry) is { } existingLoc)
                {
                    MergePane.CommitPlacement(entry.ContentHash, existingLoc);
                    deduped++;
                }
                else toPlace.Add(entry);
            }
        return (toPlace, deduped);
    }

    public (bool Ok, string? Message) PlaceMergeGroupSequentially(int objType, int destBank, IReadOnlyList<string> contentHashes, int? destSlot = null)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        // The exact gesture this feature exists for: one accidental whole-bank drag out of the
        // Merge Window is one Ctrl+Z, restoring both the staged entries and every local slot the
        // batch wrote (plus any occupant it overwrote).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedGroup(contentHashes.Count, descriptor.BankLabel(destBank)));
        var (toPlace, dedupedCount) = DedupMergeGroup(objType, contentHashes);
        if (toPlace.Count + dedupedCount == 0) return (false, "nothing to place for this bank's type");
        string dedupNote = dedupedCount > 0 ? AppMessages.Librarian.Shell.ReusedExistingContentCount(dedupedCount) : "";

        if (toPlace.Count == 0) return (true, dedupNote);

        // A drop on a SPECIFIC slot fills contiguously from exactly there - the user pointed at it,
        // so overwriting whatever follows is their explicit intent. A drop on a bank/header is an
        // auto-fill, and must land only on slots free of real content: init placeholders count as
        // free, which makes those holes scattered rather than one contiguous tail, so a plain
        // startSlot+i walk would write over real patches sitting past the first placeholder.
        int startSlot = destSlot ?? FindNextFreeSlot(objType, destBank);
        var targetSlots = destSlot is { } fixedStart
            ? Enumerable.Range(fixedStart, Math.Max(0, Math.Min(toPlace.Count, descriptor.SlotCount(destBank) - fixedStart))).ToList()
            : LocalEditOps.AvailableSlotsFrom(_cache, objType, destBank, startSlot, toPlace.Count);
        int take = targetSlots.Count;
        if (take <= 0)
        {
            return (false, destSlot is { } s
                ? $"not enough room in {descriptor.BankLabel(destBank)} from {new ObjLoc(objType, destBank, s).Label()} onward."
                : $"{descriptor.BankLabel(destBank)} is full - no free slots left.");
        }

        var bodies = new byte[take][];
        var unresolvedPerItem = new List<MergeRefSite>[take];
        var placements = new List<BatchPlacement>();
        for (int i = 0; i < take; i++)
        {
            var entry = toPlace[i];
            (bodies[i], unresolvedPerItem[i]) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);
            placements.Add(new BatchPlacement(null, new ObjLoc(objType, destBank, targetSlots[i]),
                new ObjectDump(objType, destBank, targetSlots[i], entry.Version, bodies[i]), entry.DisplayName));
        }

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow, MergePane.ForceOverwrite);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        var committed = new List<(string ContentHash, ObjLoc Dest)>(take);
        for (int i = 0; i < take; i++)
        {
            var destLoc = new ObjLoc(objType, destBank, targetSlots[i]);
            committed.Add((toPlace[i].ContentHash, destLoc));
            TrackMergeDependencies(unresolvedPerItem[i], destLoc);
        }
        MergePane.CommitPlacements(committed);
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        string msg = take < toPlace.Count
            ? $"Placed {take}; {toPlace.Count - take} didn't fit ({descriptor.BankLabel(destBank)} is full) - still staged in the Merge Window"
            : $"Placed {take}";
        if (dedupNote.Length > 0) msg += $"; {dedupNote}";
        return (true, msg);
    }

    // ── Auto-Fill: place EVERYTHING staged into the next free slots ──────────────────────
    // One button for what the Merge Window otherwise costs a drag per type per bank: take every
    // staged Set List / Combi / Program / Drum Kit / Wave Sequence (top-level pulls AND the
    // dependencies that came with them) and fill them into Keyboard Library's next free slots of
    // their own type. Purely LOCAL - it stages, exactly like every other placement in this pane;
    // nothing reaches the instrument until Commit Changes. One Ctrl+Z undoes the whole sweep
    // (LibrarianUndo's nested Begins join the outer step, so the per-bank scopes inside
    // PlaceMergeGroupSequentially fold in).
    //
    // DEPENDENCIES FIRST, and that ordering is the whole reason this isn't just five loops in
    // any order: PlaceMergeGroupSequentially resolves each entry's outgoing references against
    // what is local AT PLACEMENT TIME (MergeCache.ResolveReferencesForPlacement), so a Combi
    // placed after its Programs gets repointed at where they actually landed, while a Combi
    // placed first can only be tracked as pending and repaired later, lazily, at Commit. Both
    // paths are correct - one just leaves nothing to repair. Hence Drum Kits/Wave Sequences
    // (referenced by a Program's oscillator zones, never referrers themselves), then Programs
    // (also referenced by another Program's Drum Track), then Combis, then Set Lists: strictly
    // referenced-before-referrer.
    //
    // Programs are additionally partitioned by WIRE FORMAT, and each partition placed into the
    // next free slots of a bank of ITS OWN format. Without that, a mixed EXi+HD-1 group makes
    // MergeGroupIsExi answer null, and a null format turns FindBankWithFreeSlot's format filter
    // OFF - so it returns the first bank with room whatever its type, and BatchPlace then refuses
    // the whole thing with Move.WrongFormatForBank. Nothing is destroyed by that (the refusal is
    // the gate doing its job), but it aborts the entire Program pass over a bank the user never
    // chose, leaving everything staged. Partitioning means the question never comes up.
    //
    // Note this path never calls BankTypeChangeNeeded - that's the drag handler's escalation
    // (LibrarianShellWindow.OnMergeToLocalDrop), and a func-0x7C bank reformat is not something
    // "fill the free slots" should ever reach.
    // `pump`, when supplied, is awaited before each bank's placement: the UI passes one that
    // reports the message and then yields to the Dispatcher, which is what lets the button's
    // progress indicator paint and animate instead of the whole sweep landing as one frozen
    // block. Pass null (the self-tests do) and NOTHING suspends - every await completes
    // synchronously, so this stays safe to drive from App.xaml.cs's GetAwaiter().GetResult() on
    // the UI thread, which a genuinely-async version would deadlock.
    public async Task<(bool Ok, string Message)> AutoFillFromMergeAsync(Func<string, Task>? pump = null)
    {
        // EntriesInDisplayOrder, NEVER raw Entries: MergeCache's backing Dictionary recycles
        // the array slots this sweep's own CommitPlacement removals freed, LIFO, so a re-copied
        // PCG pulled in afterwards enumerates SCRAMBLED (a whole-bank re-copy comes out exactly
        // backwards) - and anything enumerated here is the order slots are filled in. Display
        // order (source bank, then source slot) is the order the Merge Window itself shows, so
        // every Auto-Fill lands in the same order the user sees, first run or fifth.
        var staged = MergePane.EntriesInDisplayOrder;
        if (staged.Count == 0) return (false, AppMessages.Librarian.Merge.AutoFillNothingStaged);

        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoAutoFilled(staged.Count));

        int startCount = staged.Count;
        string? refusal = null, refusedWhat = null;
        var noRoom = new List<(string What, int Count)>();

        foreach (var objType in new[] { LibObj.DrumKit, LibObj.WaveSequence, LibObj.Program, LibObj.Combi, LibObj.SetList })
        {
            var ofType = staged.Where(e => e.ObjType == objType).ToList();
            if (ofType.Count == 0) continue;

            // Programs: one run per wire format. Everything else is a single run (format is not a
            // concept for Combis/Set Lists, so the lone partition carries a null "isExi").
            var partitions = objType == LibObj.Program
                ? ofType.GroupBy(e => e.Body.Length == ProgramFormatConverter.WireSizeExi)
                        .Select(g => ((bool?)g.Key, g.Select(e => e.ContentHash).ToList()))
                        .ToList()
                : new List<(bool?, List<string>)> { (null, ofType.Select(e => e.ContentHash).ToList()) };

            foreach (var (isExi, hashes) in partitions)
            {
                // Dedup FIRST, before ever asking for a free bank: a pure duplicate is repointed
                // to its existing address and needs no slot at all, so gating this behind "is
                // there room somewhere" (as PlaceMergeGroupSequentially alone would, called only
                // from inside the loop below) stranded every duplicate in a fully-packed bank -
                // the exact bug report this fixes. A manual drag-drop already deduped fine
                // (PlaceFromMerge checks unconditionally); Auto-Fill just never got there.
                var (toPlace, _) = DedupMergeGroup(objType, hashes);
                var remaining = toPlace.Select(e => e.ContentHash).ToList();
                while (remaining.Count > 0)
                {
                    // Re-asked every pass: the previous pass filled that bank up, so the next one
                    // has to resolve to a different bank (or to null = nothing of this format has
                    // room left, and the rest stays staged rather than being lost).
                    if (LocalEditOps.FindBankWithFreeSlot(_cache, objType, isExi, BankTypeOf) is not { } bank)
                    {
                        // Nothing of this kind has room anywhere. This used to end the pass
                        // silently, leaving the items staged with only a clause at the end of the
                        // Merge pane's status line to say why - which is not where anyone looks
                        // after clicking Auto-Fill. Collected per type/format so the warning can
                        // name exactly what has nowhere to go (an EXi Program can only ever land
                        // in an EXi bank, so "Programs are full" would be the wrong thing to say
                        // when the HD-1 banks are half empty).
                        noRoom.Add((DescribeAutoFillKind(objType, isExi), remaining.Count));
                        break;
                    }

                    // One yield per bank - the finest granularity available without splitting a
                    // BatchPlace, which has to stay atomic. Placing a full 128-slot bank is
                    // therefore still one un-interruptible step; this makes the sweep BETWEEN
                    // banks visible, it doesn't make any single bank's write interruptible.
                    var descriptor = ObjectTypeRegistry.Get(objType);
                    if (pump != null)
                        await pump(AppMessages.Librarian.Merge.AutoFillProgress(
                            descriptor.DisplayName, descriptor.BankLabel(bank), remaining.Count));

                    int before = remaining.Count;
                    var (ok, message) = PlaceMergeGroupSequentially(objType, bank, remaining);
                    // Survivors, straight from the Merge Window itself rather than from the count
                    // in `message`: PlaceMergeGroupSequentially both PLACES and DEDUPES (content
                    // already elsewhere locally is reused, not written), and CommitPlacement
                    // removes an entry either way. "Still staged" is the only definition of
                    // "still needs a slot" that covers both.
                    remaining = remaining.Where(h => MergePane.TryGet(h) != null).ToList();

                    if (!ok)
                    {
                        // A REFUSE (orphan gate, wrong format, ...) is reported, not swallowed,
                        // and not retried against the same bank - but the other types still get
                        // their turn, so one bad group can't strand the rest.
                        refusal ??= message;
                        refusedWhat ??= ObjectTypeRegistry.Get(objType).DisplayName;
                        break;
                    }
                    // Progress guard: a bank that accepted nothing would otherwise be chosen
                    // again forever, since FindBankWithFreeSlot would keep returning it. Reported
                    // through the same warning as a genuinely full library - a different cause,
                    // but from the user's side the identical outcome (these items are still
                    // staged and nothing said so).
                    if (remaining.Count >= before)
                    {
                        noRoom.Add((DescribeAutoFillKind(objType, isExi), remaining.Count));
                        break;
                    }
                }
            }
        }

        LocalPane.RefreshTree();
        int stillStaged = MergePane.Entries.Count;
        int resolved = startCount - stillStaged;

        // The banner, not a pop-up: Auto-Fill is a sweep the user watches, and a modal that has
        // to be dismissed before the result can even be looked at is the wrong shape for "some of
        // this didn't fit". Left set until dismissed or until the next Sync/Commit clears it.
        if (noRoom.Count > 0) WarningText = AppMessages.Librarian.Merge.AutoFillNoRoom(noRoom);

        if (refusal != null)
            return (false, AppMessages.Librarian.Merge.AutoFillRefused(resolved, refusedWhat ?? "a staged group", refusal));

        return (resolved > 0, AppMessages.Librarian.Merge.AutoFillResult(resolved, stillStaged));
    }

    // What kind of thing ran out of room, as the warning names it. Programs carry their wire
    // format because that's what actually constrains them - an EXi Program is only ever placeable
    // in an EXi bank (see LocalEditOps.FindBankWithFreeSlot's own isExi argument).
    static string DescribeAutoFillKind(int objType, bool? isExi)
    {
        string name = ObjectTypeRegistry.Get(objType).DisplayName;
        return isExi is { } exi ? $"{(exi ? "EXi" : "HD-1")} {name}" : name;
    }

    // ── Whole Program bank copy with EXi/HD-1 type change (requirement 4) ────────────────
    // The func 0x7C "Change Program Bank Type" the changeset emits reformats+ERASES the whole
    // destination bank, so a type change is inherently a copy-the-entire-bank operation.

    // If placing this Merge-Window Program group into destBank would require changing the
    // destination bank's HD-1/EXi type, returns the target IsExi; otherwise null (not a Program
    // group, mixed formats, destination type unknown, or already the right type). The caller
    // (code-behind) uses this to prompt before the destructive reformat.
    public bool? BankTypeChangeNeeded(int objType, int destBank, IReadOnlyList<string> contentHashes)
    {
        if (objType != LibObj.Program) return null;
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e is { ObjType: LibObj.Program }).Select(e => e!).ToList();
        if (group.Count == 0) return null;
        bool allExi = group.All(e => e.Body.Length == ProgramFormatConverter.WireSizeExi);
        bool allHd1 = group.All(e => e.Body.Length != ProgramFormatConverter.WireSizeExi);
        if (allExi == allHd1) return null;   // mixed formats - not a clean single-type bank
        bool groupIsExi = allExi;
        // Destination bank's current type: the live func-0x61 answer if we have it, ELSE the
        // format of whatever Programs already sit in that bank locally (a real bank is
        // homogeneous). The fallback is what makes the type-change prompt fire right after the
        // window opens or offline, instead of the drop slipping through and only being caught as
        // a per-item REFUSE at Commit - the exact failure the user hit copying EXi into an HD-1
        // bank whose live type hadn't been warmed yet.
        bool? destIsExi = BankTypeOf(destBank) ?? LocalProgramBankFormat(destBank);
        return destIsExi is bool d && d != groupIsExi ? groupIsExi : null;
    }

    // The HD-1/EXi format of a destination Program bank as Keyboard Library currently sees it. Null if
    // the bank is empty locally (nothing to infer a type from) - see LocalEditOps' own comment.
    bool? LocalProgramBankFormat(int bank) => LocalEditOps.LocalProgramBankFormat(_cache, bank);

    // ── Type-root ("Programs"/"Combis"/"Set Lists" header) drop targets, requirement 6 ──────
    // A drop on the header names a TYPE but no bank, so each entry point below resolves it to the
    // first bank with room - passing the incoming Programs' own HD-1/EXi format so the chosen bank
    // can't be one the placement would then REFUSE as wrong-format (see
    // LocalEditOps.FindBankWithFreeSlot). Null means every writable bank of that type is full.

    public int? FindBankForPcgDrop(int objType, IReadOnlyList<ObjLoc> pcgLocs) =>
        LocalEditOps.FindBankWithFreeSlot(_cache, objType, PcgGroupIsExi(objType, pcgLocs), BankTypeOf);

    public int? FindBankForMergeDrop(int objType, IReadOnlyList<string> contentHashes) =>
        LocalEditOps.FindBankWithFreeSlot(_cache, objType, MergeGroupIsExi(objType, contentHashes), BankTypeOf);

    // The first free slot in one specific bank, or null if it's full - for a SINGLE item dropped on
    // a bank/header, where a "slot 0" fallback would silently overwrite whatever sits there.
    public int? NextFreeSlotIn(int objType, int bank) => LocalEditOps.TryFindNextFreeSlot(_cache, objType, bank);

    // The wire format shared by a group of incoming Programs, or null when they're mixed, not
    // Programs, or unreadable - the body's own length says EXi vs HD-1 deterministically, the same
    // primitive BankTypeChangeNeeded already relies on. Public because the View needs the same
    // answer a moment later to word the "no bank of THIS format has room" refusal.
    public bool? PcgGroupIsExi(int objType, IReadOnlyList<ObjLoc> pcgLocs)
    {
        if (objType != LibObj.Program) return null;
        var lengths = pcgLocs
            .Select(l => PcgPane.Get(l))
            .Where(e => e != null)
            .Select(e => ProgramFormatConverter.WireBodyFromPcgEntry(LibObj.Program, e!)?.Length)
            .Where(len => len != null)
            .Distinct()
            .ToList();
        return lengths.Count == 1 ? lengths[0] == ProgramFormatConverter.WireSizeExi : null;
    }

    public bool? MergeGroupIsExi(int objType, IReadOnlyList<string> contentHashes)
    {
        if (objType != LibObj.Program) return null;
        var lengths = contentHashes
            .Select(h => MergePane.TryGet(h))
            .Where(e => e is { ObjType: LibObj.Program })
            .Select(e => e!.Body.Length)
            .Distinct()
            .ToList();
        return lengths.Count == 1 ? lengths[0] == ProgramFormatConverter.WireSizeExi : null;
    }

    // Copies a whole Program bank from the Merge Window into destBank, changing destBank's
    // HD-1/EXi type to match. Because the func 0x7C emitted at Commit ERASES the whole
    // destination bank, this REPLACES it: every existing local Program in destBank is dropped
    // first (it would be erased on hardware regardless), the group is placed from slot 0, and
    // the type-change intent is recorded for the next Commit. Placement bypasses the normal
    // format REFUSE (bankTypeOf: null) precisely because the reformat is intentional.
    public (bool Ok, string? Message) PlaceMergeBankWithTypeChange(int destBank, IReadOnlyList<string> contentHashes, bool targetIsExi)
    {
        var descriptor = ObjectTypeRegistry.Get(LibObj.Program);
        // The most destructive placement in the Librarian (it drops every local Program in destBank
        // before writing) and the one where a mid-way REFUSE from BatchPlace would otherwise leave
        // the bank wiped with no way back: the scope captures those removals as they happen, so the
        // step is pushed - and Ctrl+Z recovers the bank - even on the failure path.
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoCopiedBankWithTypeChange(descriptor.BankLabel(destBank)));
        var group = contentHashes.Select(h => MergePane.TryGet(h)).Where(e => e is { ObjType: LibObj.Program }).Select(e => e!).ToList();
        if (group.Count == 0) return (false, "nothing to place for this bank");
        if (group.Count > descriptor.SlotCount(destBank)) group = group.Take(descriptor.SlotCount(destBank)).ToList();

        // Replace the destination bank - the 0x7C erases everything in it on hardware anyway.
        for (int n = 0; n < descriptor.SlotCount(destBank); n++)
            if (_cache.Exists(LibObj.Program, destBank, n))
                _cache.RemoveObject(LibObj.Program, destBank, n, DateTime.UtcNow);

        var placements = new List<BatchPlacement>();
        for (int i = 0; i < group.Count; i++)
        {
            var (body, _) = MergePane.ResolveReferencesForPlacement(group[i], LocalLookup);   // Programs have no refs
            placements.Add(new BatchPlacement(null, new ObjLoc(LibObj.Program, destBank, i),
                new ObjectDump(LibObj.Program, destBank, i, group[i].Version, body), group[i].DisplayName));
        }

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, LibObj.Program, placements, divertDisplacedToClipboard: true, bankTypeOf: null, DateTime.UtcNow, MergePane.ForceOverwrite);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        MergePane.CommitPlacements(Enumerable.Range(0, group.Count)
            .Select(i => (group[i].ContentHash, new ObjLoc(LibObj.Program, destBank, i))).ToList());
        // Index metadata, not a slot write - the undo recorder's slot-level observation can't see
        // this one, so the prior intent (often "none at all") is captured explicitly here.
        _undo.CapturePendingBankTypeChange(destBank);
        _cache.SetPendingBankTypeChange(destBank, targetIsExi);
        _cache.Save();
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        return (true, $"Copied {group.Count} program(s) into {descriptor.BankLabel(destBank)} and set it to {(targetIsExi ? "EXi" : "HD-1")} - the bank reformats on Commit.");
    }

    // ── Merge Window -> Local (manual, per-item - the user picks every destination,
    // including a dependency's, since only they know whether a bank should stay empty or a
    // partially-filled one should be continued; see this feature's own design conversation). ──

    // The duplicate-content guard below only applies to genuinely NEW content (pulled from a
    // .pcg file) - an entry the Merge Window staged FROM Keyboard Library itself (PullLocalIntoMerge,
    // "Move to Merge Window") already has a known local home; placing it elsewhere is the whole
    // point of that feature (an intentional copy/rearrange), not an accidental duplicate to warn
    // about or redirect. Without this exclusion, FindByContentHash would always find the entry's
    // own origin and silently no-op the placement.
    static bool WasStagedFromLocal(MergeEntry entry) =>
        entry.Origins.Any(o => o.PcgFileName == MergeCache.LocalSourceLabel);

    // The per-type "preserve duplication" policy (Settings > Librarian; Merge Window toolbar
    // quick toggles). Set Lists have no toggle and always take the duplicate-reuse path below.
    bool PreserveDuplicationFor(int objType) => objType switch
    {
        LibObj.Program => MergePreserveDuplicatePrograms,
        LibObj.Combi   => MergePreserveDuplicateCombis,
        _              => false,
    };

    // The duplicate-content guard shared by PlaceFromMerge and PlaceMergeGroupSequentially:
    // returns the Keyboard Library location whose content already IS this entry (so the placement
    // should repoint at it instead of writing a second copy), or null when the entry must be
    // written - because it was staged FROM Local (its placement elsewhere is the whole point),
    // because the user asked for duplication to be preserved for its type, or because nothing
    // byte-identical exists anywhere locally.
    //
    // A Combi is compared TWICE when duplication isn't being preserved. Its staged body still
    // holds the SOURCE (PCG) addresses for its timbres, so the raw content hash almost never
    // matches a previously-placed copy - that copy was repointed at where its Programs actually
    // landed before being written. Comparing the RESOLVED body (references repointed at local
    // reality, exactly as placement itself would write it) is what makes "scan duplicates and
    // reuse" actually fire for the re-copied-PCG case: the second copy resolves to the very
    // same Program destinations the first copy was written with, so the bodies hash equal.
    // Programs have no outgoing references (resolved == raw), and Set Lists deliberately keep
    // the historical raw-hash-only comparison.
    ObjLoc? FindExistingLocalCopy(MergeEntry entry)
    {
        if (WasStagedFromLocal(entry)) return null;
        if (PreserveDuplicationFor(entry.ObjType)) return null;
        if (_cache.FindByContentHash(entry.ObjType, entry.ContentHash) is { } raw) return raw;
        // A Program can carry RefSites too now (Drum Track; HD-1 Wave Sequence/Drum Kit
        // oscillator zones) - same reasoning as Combi: its RAW content may differ from an
        // already-local twin only in an unresolved reference address, which resolving before
        // re-hashing corrects for.
        if (entry.ObjType is LibObj.Combi or LibObj.Program && entry.RefSites.Count > 0)
        {
            var (resolved, _) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);
            string resolvedHash = LocalObjectStore.ComputeHash(resolved);
            if (resolvedHash != entry.ContentHash &&
                _cache.FindByContentHash(entry.ObjType, resolvedHash) is { } found)
                return found;
        }
        return null;
    }

    public (bool Ok, string? Error) PlaceFromMerge(string mergeContentHash, ObjLoc destLoc)
    {
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedMergeItemAt(destLoc.Label()));
        var entry = MergePane.TryGet(mergeContentHash);
        if (entry == null) return (false, "not found in the Merge Window");
        // Cross-type guard - see PlaceFromPcg's identical check for why this can't be
        // left to the drop handlers.
        if (entry.ObjType != destLoc.ObjType)
            return (false, $"can't place a {ObjectTypeRegistry.Get(entry.ObjType).DisplayName} on a {ObjectTypeRegistry.Get(destLoc.ObjType).DisplayName} slot");

        // Duplicate-content guard: this entry's OWN content (not just its references - see
        // ResolveReferencesForPlacement below for that) may already be sitting somewhere else
        // in Keyboard Library, byte-identical. Rather than writing a second copy, repoint this
        // hash at that existing location the same way a dependency would (RecordPlacement),
        // so any Merge-staged sibling that references it resolves to the ONE copy. Skipped
        // when the match IS the requested destination - that's just re-placing onto its own
        // slot, not a duplicate elsewhere - or when the user preserves duplicates for this
        // type (FindExistingLocalCopy's own policy check).
        if (FindExistingLocalCopy(entry) is { } existingLoc && !existingLoc.Equals(destLoc))
        {
            MergePane.CommitPlacement(mergeContentHash, existingLoc);
            return (true, AppMessages.Librarian.Shell.ReusedExistingContent(existingLoc.Label()));
        }

        // Patches whatever of this entry's OWN dependency references resolve - either because
        // the dependency was ALSO placed via Merge this session (_placedAddresses), or because
        // it already exists ANYWHERE in Keyboard Library (LocalLookup, by content) - the
        // many-to-one dedup payoff, generalized beyond just this-session Merge placements.
        // Anything still unresolved is tracked for a later retry (TrackMergeDependencies).
        var (body, unresolved) = MergePane.ResolveReferencesForPlacement(entry, LocalLookup);

        // Per-phase timings: this is the one drop path the user feels, and every phase below
        // writes a whole file to a DataDir that is routinely an SMB share, so a regression here
        // reads as a multi-second stall with no other symptom. Debug-level, so it costs nothing
        // unless someone is looking.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, error, clipboardAdds) = LocalEditOps.PlaceObject(
            _cache, destLoc, entry.ObjType, entry.Version, body, entry.DisplayName,
            divertDisplacedToClipboard: true, DateTime.UtcNow, BankTypeOf, MergePane.ForceOverwrite);
        if (!ok) return (false, error);
        long tPlace = sw.ElapsedMilliseconds;

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        long tClip = sw.ElapsedMilliseconds;
        MergePane.CommitPlacement(mergeContentHash, destLoc);
        TrackMergeDependencies(unresolved, destLoc);
        long tMerge = sw.ElapsedMilliseconds;
        LocalPane.RefreshTree();
        NotifyLocalEditMade();
        AppLog.Debug($"[librarian] place-from-merge {destLoc.Label()}: place {tPlace}ms, clipboard(+{clipboardAdds.Count}) {tClip - tPlace}ms, " +
                     $"merge {tMerge - tClip}ms, tree+history {sw.ElapsedMilliseconds - tMerge}ms, total {sw.ElapsedMilliseconds}ms");
        return (true, null);
    }

    ObjLoc? LocalLookup(int objType, string contentHash) => _cache.FindByContentHash(objType, contentHash);

    // Drop on a bank/root = auto-fill starting at the next free local slot in that bank,
    // reusing BatchLibrarian.ResolveSequentialFill - the same sequential-fill-with-clipboard-
    // overflow logic the persisted clipboard's Paste Multi/All already uses.
    public (bool Ok, string? Message) BatchPlaceFromPcg(int objType, IReadOnlyList<ObjLoc> pcgLocs, int destBank)
    {
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoPlacedGroup(
            pcgLocs.Count, ObjectTypeRegistry.Get(objType).BankLabel(destBank)));
        var pending = new List<ClipboardEntry>();
        foreach (var loc in pcgLocs)
        {
            // A loc whose own type doesn't match objType would otherwise get force-fed through
            // the WRONG type's WireBodyFromPcgEntry converter below (it trusts objType, not
            // loc) - skip rather than risk decoding e.g. a Combi body as a Program. The Local
            // tree's own UI-level selection guard (LibrarianShellWindow.OnPcgPreviewMouseDown)
            // already prevents building a mixed-type selection in the first place; this is
            // defense in depth for this method's other/future callers.
            if (loc.ObjType != objType) continue;
            if (PcgPane.Get(loc) is not { } e) continue;
            var body = ProgramFormatConverter.WireBodyFromPcgEntry(objType, e);
            if (body == null) continue;   // malformed Program record - skip rather than fail the whole batch
            pending.Add(new ClipboardEntry { ObjType = objType, Origin = loc, Version = LibObj.CurrentObjectVersion(objType) ?? 0, Body = body, Provenance = ClipboardProvenance.UserCopy, CutAt = DateTime.UtcNow });
        }
        if (pending.Count == 0) return (false, "nothing to place");

        int startSlot = FindNextFreeSlot(objType, destBank);
        var (placed, stillPending) = BatchLibrarian.ResolveSequentialFill(pending, objType, destBank, startSlot, bankTypeOf: null,
            slotAvailable: s => !_cache.HasContent(objType, destBank, s));
        if (placed.Count == 0) return (false, "nothing could be placed (bank full or type mismatch)");

        // Repoint each placed item's OWN references before writing, same as the single-item
        // path - every dependency that already resolves somewhere in Keyboard Library gets
        // pointed there; whatever doesn't is tracked per item below.
        var view = PcgPane.View;
        var bodies = new byte[placed.Count][];
        var unresolvedPerItem = new List<(RefKind RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)>[placed.Count];
        for (int i = 0; i < placed.Count; i++)
        {
            (bodies[i], unresolvedPerItem[i]) = view != null
                ? DependencyScanner.RepointPcgReferences(_cache, view, objType, placed[i].Entry.Body)
                : (placed[i].Entry.Body, new List<(RefKind, int, ObjLoc, string?)>());
        }

        var placements = new List<BatchPlacement>();
        for (int i = 0; i < placed.Count; i++)
            placements.Add(new BatchPlacement(null, new ObjLoc(objType, destBank, placed[i].Slot),
                new ObjectDump(objType, destBank, placed[i].Slot, placed[i].Entry.Version, bodies[i]), placed[i].Entry.Origin.Label()));

        var (ok, error, clipboardAdds) = LocalEditOps.BatchPlace(_cache, objType, placements, divertDisplacedToClipboard: true, BankTypeOf, DateTime.UtcNow);
        if (!ok) return (false, error);

        MergeDisplacedIntoPersistentClipboard(clipboardAdds);
        for (int i = 0; i < placed.Count; i++)
            StageAndTrackPcgDependencies(unresolvedPerItem[i], new ObjLoc(objType, destBank, placed[i].Slot));
        LocalPane.RefreshTree();
        NotifyLocalEditMade();

        string msg = stillPending.Count > 0
            ? $"Placed {placed.Count}; {stillPending.Count} didn't fit (bank full or type mismatch)"
            : $"Placed {placed.Count}";
        return (true, msg);
    }

    int FindNextFreeSlot(int objType, int bank) => LocalEditOps.FindNextFreeSlot(_cache, objType, bank);

    void MergeDisplacedIntoPersistentClipboard(List<ClipboardEntry> newEntries) =>
        BatchLibrarian.AppendClipboardGlobal(newEntries);

    // Requirement 14's dependency-completeness gate feeds off this: whatever
    // ResolveReferencesForPlacement (Merge path) couldn't resolve gets tracked so
    // ResolvePendingDependencies can retry it later, by content, against Keyboard Library's
    // then-current state - not just re-checking the one address it currently encodes.
    void TrackMergeDependencies(List<MergeRefSite> stillUnresolved, ObjLoc placedAt)
    {
        if (stillUnresolved.Count == 0) return;
        foreach (var site in stillUnresolved)
            _sessionClipboard.Add(new SessionDependencyEntry(site.TargetLoc, site.RefKind, site.Site, placedAt, site.ResolvedContentHash));
        RefreshSessionClipboard();
    }

    // Same tracking, for the direct-PCG path - plus auto-staging: a reference RepointPcgReferences
    // couldn't resolve locally, but whose expected content the loaded PCG DOES have, gets pulled
    // into the Merge Window right away (reusing the existing transitive pull) so the user has a
    // clear, visible next step instead of a silently wrong/missing reference. A null expected
    // hash (the PCG doesn't have it either - a true gap) is left alone; nothing to stage.
    void StageAndTrackPcgDependencies(List<(RefKind RefKind, int Site, ObjLoc OriginalTarget, string? ExpectedHash)> unresolved, ObjLoc placedAt)
    {
        if (unresolved.Count == 0) return;
        if (PcgPane.View is { } view)
        {
            var toStage = unresolved.Where(u => u.ExpectedHash != null).Select(u => u.OriginalTarget).ToList();
            MergePane.PullFromPcg(view, PcgPane.LoadedFileName ?? "(unknown)", toStage);
        }
        foreach (var (refKind, site, originalTarget, expectedHash) in unresolved)
            _sessionClipboard.Add(new SessionDependencyEntry(originalTarget, refKind, site, placedAt, expectedHash));
        RefreshSessionClipboard();
    }

    // Runs right before every Sync/Commit (see PrepareForPushAsync) - retries every pending
    // dependency against Keyboard Library's CURRENT state (time has passed; the dependency may
    // now exist anywhere, not necessarily at the address it was originally tracked against),
    // and repatches whatever's found via a REAL edit (LocalEditOps.RepatchReference -
    // re-dirties the referrer, appears in History, feeds the next push changeset; never a
    // silent byte mutation, since the referrer may already be dirty or previously pushed).
    void ResolvePendingDependencies()
    {
        bool anyResolved = false;
        foreach (var entry in _sessionClipboard.Pending.ToList())
        {
            if (entry.ExpectedContentHash is not { } hash) continue;   // a true gap - nothing to search for
            if (_cache.FindByContentHash(entry.MissingRef.ObjType, hash) is not { } foundLoc) continue;
            if (LocalEditOps.RepatchReference(_cache, entry.RequiredBy, entry.Site, entry.RefKind, foundLoc, DateTime.UtcNow))
            {
                _sessionClipboard.Remove(entry);
                anyResolved = true;
            }
        }
        RefreshSessionClipboard();
        if (anyResolved) { LocalPane.RefreshTree(); NotifyLocalEditMade(); }
    }

    void RefreshSessionClipboard()
    {
        SessionClipboardRows.Clear();
        foreach (var e in _sessionClipboard.Pending) SessionClipboardRows.Add(new SessionClipboardRow(e));
    }
}
