namespace KronosScreenRemote;

// Snapshot of one multisample's full zone list - order plus every mutable per-zone
// field. Each entry pairs the LIVE KmpZone instance (the exact object that was in the
// list at snapshot time - never recreated) with a CLONE of its field values as they
// stood then.
//
// Keeping the live reference (not just cloned data at a position) is what makes
// ApplyTo correct for an edit that changes the list's ORDER, not just a field - a
// drag-reorder. A naive "copy field values back by position" restore is correct for
// MoveZoneBoundary (the list's order/count never changes there, so position == identity
// throughout), but for a reorder it would leave the list in whatever order the edit put
// it in while silently SWAPPING which object holds which zone's data - breaking every
// consumer that holds a direct reference to a specific KmpZone instance rather than
// re-deriving it by index each time: SampleEditorViewModel._selectedZone, the keymap's
// SelectedZone binding, and the tree's per-zone nodes (built once against these exact
// instances, not rebuilt on every edit). Restoring both the live objects' fields AND
// the list's order (via those SAME objects) keeps identity and position in sync the
// same way the live edit itself does.
//
// This same shape also makes add/delete correct for free: a zone added after the
// snapshot was taken simply isn't in Entries, so rebuilding the list from Entries drops
// it; a zone deleted after the snapshot still has a live Entries reference to add back.
//
// A snapshot spans ONE OR MORE zone lists, not exactly one. That matters for a stereo
// pair: every key-range edit is mirrored onto the sibling multisample's own zone list
// (see SampleEditorViewModel.FindLiveStereoSibling and doc §2.2 - the two halves are
// matched by EXACT (OriginalKey, TopKey), so they must move together or the pair stops
// resolving). Undoing only the half that was actually clicked would re-introduce
// exactly the divergence the mirroring exists to prevent, so both lists are captured
// and restored as one atomic step. Each list carries its own target reference, so
// ApplyTo() needs no argument - it always restores precisely what was captured.
sealed class ZoneListSnapshot
{
    public required List<(List<KmpZone> Target, List<(KmpZone Live, KmpZone Snapshot)> Entries)> Lists { get; init; }

    // Nulls are skipped rather than rejected - the sibling list is legitimately absent
    // for a mono multisample, and every call site would otherwise need the same branch.
    public static ZoneListSnapshot Of(params List<KmpZone>?[] zoneLists) =>
        new()
        {
            Lists = zoneLists.Where(l => l != null)
                .Select(l => (l!, l!.Select(z => (z, Clone(z))).ToList()))
                .ToList(),
        };

    static KmpZone Clone(KmpZone z) => new()
    {
        OriginalKey = z.OriginalKey,
        TopKey = z.TopKey,
        Filename = z.Filename,
        Unknown4 = (byte[])z.Unknown4.Clone(),
        Rlp3 = (byte[])z.Rlp3.Clone(),
        Rlp2 = (byte[])z.Rlp2.Clone(),
    };

    // Restores each live object's fields from its own paired snapshot clone, THEN
    // rebuilds each list's order from its Entries (using those same live objects) -
    // never creates a new KmpZone instance for anything that existed at snapshot time.
    public void ApplyTo()
    {
        foreach (var (target, entries) in Lists)
        {
            foreach (var (live, snap) in entries) CopyInto(snap, live);
            target.Clear();
            target.AddRange(entries.Select(e => e.Live));
        }
    }

    static void CopyInto(KmpZone src, KmpZone dst)
    {
        dst.OriginalKey = src.OriginalKey;
        dst.TopKey = src.TopKey;
        dst.Filename = src.Filename;
        dst.Unknown4 = (byte[])src.Unknown4.Clone();
        dst.Rlp3 = (byte[])src.Rlp3.Clone();
        dst.Rlp2 = (byte[])src.Rlp2.Clone();
    }
}

// Bounded linear undo/redo for one multisample's zone list - boundary drags today,
// add/delete/skip/reorder in later rounds. Deliberately NOT LibrarianUndo.cs's
// event-driven observer shape (built around LocalLibraryCache's own mutation events,
// nothing like that exists for KmpZone) and NOT SampleEditUndo's byte-capped design
// (that cap exists because a PCM snapshot can be multi-MB; a zone list is a handful of
// small structs, so a plain step-count cap is the right weight here).
sealed class SampleZoneUndo(int stepCap = 50)
{
    readonly LinkedList<ZoneListSnapshot> _undoStack = new();
    readonly LinkedList<ZoneListSnapshot> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void RecordBeforeEdit(ZoneListSnapshot preEditState)
    {
        _redoStack.Clear();
        _undoStack.AddLast(preEditState);
        if (_undoStack.Count > stepCap) _undoStack.RemoveFirst();
    }

    public ZoneListSnapshot? Undo(ZoneListSnapshot currentState)
    {
        if (_undoStack.Count == 0) return null;
        var restored = _undoStack.Last!.Value;
        _undoStack.RemoveLast();
        _redoStack.AddLast(currentState);
        return restored;
    }

    public ZoneListSnapshot? Redo(ZoneListSnapshot currentState)
    {
        if (_redoStack.Count == 0) return null;
        var restored = _redoStack.Last!.Value;
        _redoStack.RemoveLast();
        _undoStack.AddLast(currentState);
        return restored;
    }
}
