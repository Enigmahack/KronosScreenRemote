namespace KronosScreenRemote;

// A dependency an incoming placement (Combi/SetList) refers to that isn't present locally
// yet - surfaced so the user can place it before Commit will validate clean (requirement
// 14's dependency-completeness gate). Genuinely session-only: in-memory ONLY, Storage.* is
// never touched here - deliberately distinct from the persisted pending-change history and
// from the persisted BatchClipboard. Closing the app loses anything still unplaced here
// (accepted - see the plan's flagged tensions).
//
// Site (the numeric reference-site index ObjectReferenceWalker/MergeRefSite already use) and
// ExpectedContentHash are what let LibrarianShellViewModel.ResolvePendingDependencies actually
// REPAIR this later, not just display it: once the real dependency exists ANYWHERE locally
// (found via LocalLibraryCache.FindByContentHash, not necessarily at MissingRef's own address),
// RequiredBy's body gets repatched at Site to point there. ExpectedContentHash is null for a
// true gap (the source - a loaded PCG, or the Merge Window's own pull - never resolved this
// reference to any known content), which is not something this mechanism can ever auto-repair;
// it stays pending until the user does something about it manually.
sealed record SessionDependencyEntry(ObjLoc MissingRef, string RefKind, int Site, ObjLoc RequiredBy, string? ExpectedContentHash);

sealed class SessionDependencyClipboard
{
    readonly List<SessionDependencyEntry> _entries = new();
    public IReadOnlyList<SessionDependencyEntry> Pending => _entries;

    public void Add(SessionDependencyEntry entry)
    {
        if (!_entries.Contains(entry)) _entries.Add(entry);
    }

    // Called once the missing object has actually been placed locally AT EXACTLY MissingRef's
    // own address - clears every pending entry referencing it, regardless of which placement
    // originally flagged it. NOT what ResolvePendingDependencies uses (that repoints by content
    // hash, which can resolve an entry whose dependency landed at a DIFFERENT address than
    // MissingRef) - this stays for the narrower "landed at the exact original address" case.
    public void Resolve(ObjLoc missingRef) => _entries.RemoveAll(e => e.MissingRef.Equals(missingRef));

    // Exact-entry removal - for ResolvePendingDependencies, which repatches one entry at a time
    // and must remove only THAT entry, not every entry that happens to share an address (two
    // different referrers can each be tracked against the same expected content hash from two
    // different original addresses).
    public void Remove(SessionDependencyEntry entry) => _entries.Remove(entry);

    public void Clear() => _entries.Clear();

    // Puts the whole pending list back as an undo step captured it (Core/LocalLibrary/
    // LibrarianUndo.cs) - a placement that tracked new unresolved dependencies must stop
    // reporting them once that placement is rolled back.
    public void ReplaceAll(IReadOnlyList<SessionDependencyEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
    }
}
