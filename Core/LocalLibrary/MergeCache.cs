namespace KronosScreenRemote;

// Where one piece of content in the merge cache was originally pulled from - kept for
// traceability even after dedup collapses multiple pulls of identical content into one
// MergeEntry (see MergeCache.PullRecursive).
readonly record struct MergeOrigin(string PcgFileName, ObjLoc SourceLoc);

// One outgoing reference site inside a Combi/Set List entry's own body (a timbre slot, or a
// Set List slot). RefKind/Site mirror ObjectReferenceWalker's own (RefKind, Site) pair exactly
// so MergeCache.ResolveReferencesForPlacement can patch the same bytes ObjectReferenceWalker
// read. ResolvedContentHash is the dependency's content hash if pulling it succeeded at the
// time OwnerHash's entry was itself pulled; null means it was a gap - MergeCache.ReconcileGaps
// can still backfill it later if the exact same ObjLoc is pulled successfully afterward (e.g.
// from a second PCG).
sealed class MergeRefSite
{
    public required string OwnerHash { get; init; }   // the entry this reference site belongs to
    public required string RefKind { get; init; }
    public required int Site { get; init; }
    public required ObjLoc TargetLoc { get; init; }   // the original reference, for gap reconciliation
    public string? ResolvedContentHash { get; set; }
}

// One piece of content staged in the Merge Window - the Merge Window's whole point is that
// IDENTICAL content pulled multiple times (same PCG twice, or two different PCGs with a
// byte-identical Program) collapses to exactly one of these, tracked by ContentHash.
sealed class MergeEntry
{
    public required string ContentHash { get; init; }
    public required int ObjType { get; init; }
    public required byte[] Body { get; init; }   // wire format - same convention LocalObjectStore uses
    public required byte Version { get; init; }
    public required string DisplayName { get; init; }
    public bool IsTopLevelPull { get; set; }     // the user explicitly pulled this, not just a dependency
    public List<MergeOrigin> Origins { get; } = new();
    public HashSet<string> ReferencedBy { get; } = new();   // >1 => shown as "shared" (yellow) in the UI
    public List<MergeRefSite> RefSites { get; } = new();    // this entry's OWN outgoing references

    public bool HasUnresolvedDependencies => RefSites.Any(s => s.ResolvedContentHash == null);
}

// The Librarian's Merge Window: a bag-based staging cache for objects pulled out of loaded
// PCG files, before they're placed into Local Library. Deliberately bag-based, not addressed -
// two different PCGs can each have something at the SAME (bank,number), so staging can't use
// Local Library's own address space without inventing conflicts that don't need to exist yet;
// address resolution happens only once, at placement time (LocalEditOps.PlaceObject, unchanged).
sealed class MergeCache
{
    readonly Dictionary<string, MergeEntry> _byHash = new();

    // Every RefSite still pointing at a specific ObjLoc that hasn't resolved yet - keyed by
    // that ObjLoc so a LATER pull of the exact same ObjLoc (e.g. from a second PCG that DOES
    // have it) can retroactively resolve it. See ReconcileGaps.
    readonly Dictionary<ObjLoc, List<MergeRefSite>> _pendingGapSites = new();

    // Where a content hash has already been placed in Local Library this batch - the
    // mechanism behind "two Combis sharing one deduped Program get patched to point at the
    // SAME destination slot" (see ResolveReferencesForPlacement).
    readonly Dictionary<string, ObjLoc> _placedAddresses = new();

    IMergeCachePersistence _persistence;

    public IReadOnlyCollection<MergeEntry> Entries => _byHash.Values;

    // Raised immediately BEFORE any change to what's staged (a pull, a removal, a clear, a
    // placement record). The Librarian's linear undo (Core/LocalLibrary/LibrarianUndo.cs) is the
    // only subscriber, and it uses this to snapshot the staging state LAZILY - only actions that
    // actually touch the Merge Window pay for copying it, so an unrelated rename doesn't.
    public event Action? Mutating;

    public MergeCache(IMergeCachePersistence persistence)
    {
        _persistence = persistence;
        if (_persistence.Load() is { } snapshot) LoadFrom(snapshot);
    }

    // Shared by the constructor (disk snapshot) and Restore (an undo step's snapshot).
    void LoadFrom(MergeCacheSnapshot snapshot)
    {
        foreach (var e in snapshot.Entries)
        {
            var entry = new MergeEntry
            {
                ContentHash = e.ContentHash, ObjType = e.ObjType, Body = e.Body, Version = e.Version,
                DisplayName = e.DisplayName, IsTopLevelPull = e.IsTopLevelPull,
            };
            entry.Origins.AddRange(e.Origins);
            foreach (var r in e.ReferencedBy) entry.ReferencedBy.Add(r);
            entry.RefSites.AddRange(e.RefSites);
            _byHash[entry.ContentHash] = entry;
        }
        foreach (var (hash, loc) in snapshot.PlacedAddresses) _placedAddresses[hash] = loc;
        RebuildPendingGapIndex();
    }

    // A snapshot safe to HOLD (unlike Save's, which is serialized immediately): MergeRefSite is a
    // mutable class whose ResolvedContentHash is written in place by ReconcileGaps, so a held
    // snapshot that shared those objects would silently change under the undo stack. Bodies are
    // shared deliberately - nothing ever mutates one in place (ResolveReferencesForPlacement
    // clones before patching), so copying them would only waste memory.
    public MergeCacheSnapshot Snapshot() => BuildSnapshot(deepCopyRefSites: true);

    // Replaces everything staged with `snapshot` - the Merge Window half of an undo step. Rebuilds
    // the pending-gap index from the restored entries (it's derived state) and persists, so an
    // undo survives a restart exactly like the placement it rolled back did.
    public void Restore(MergeCacheSnapshot snapshot)
    {
        _byHash.Clear();
        _pendingGapSites.Clear();
        _placedAddresses.Clear();
        LoadFrom(snapshot);
        Save();
    }

    void RebuildPendingGapIndex()
    {
        _pendingGapSites.Clear();
        foreach (var entry in _byHash.Values)
            foreach (var site in entry.RefSites)
                if (site.ResolvedContentHash == null)
                    RegisterGap(site);
    }

    // Switches which persistence strategy future mutations go through - e.g. the user flips
    // the "Merge behavior" setting while the Librarian is open. Temporary -> Local Storage
    // persists whatever is CURRENTLY staged to the new location immediately, so switching
    // mid-session doesn't silently lose it; Local Storage -> Temporary deletes the old
    // snapshot file but keeps everything already staged in memory for the rest of this
    // session - only future persistence stops.
    public void SetPersistence(IMergeCachePersistence newPersistence, bool wasFileBacked)
    {
        if (wasFileBacked) _persistence.Clear();
        _persistence = newPersistence;
        Save();
    }

    // Origin label used for anything pulled from Local Library rather than a loaded .pcg file
    // (requirement 3) - occupies the same MergeOrigin.PcgFileName slot a real filename would.
    public const string LocalSourceLabel = "Local Library";

    // A source of wire bodies + display names for a merge pull - a loaded PCG file (PullFromPcg)
    // or the Local Library (PullFromLocal). Returns null when this source has nothing at `loc`
    // (an empty slot, or a malformed record) - treated as a gap by the shared recursion below.
    delegate (byte[] Body, string Name)? MergePullSource(ObjLoc loc);

    // Pulls one object - and, fully automatically and transitively, everything it references
    // that resolves within `pcg` - into the merge cache. Byte-identical content already staged
    // (from this or any earlier pull) is recognized and NOT duplicated; only genuinely new
    // content is returned in Added. Gaps are references that don't resolve in `pcg` - the
    // caller decides how to surface them (same contract DependencyScanner.Scan already uses).
    public (List<MergeEntry> Added, List<(ObjLoc MissingRef, string RefKind)> Gaps) PullFromPcg(
        PcgLibraryView pcg, string pcgFileName, ObjLoc loc)
    {
        MergePullSource source = l =>
        {
            var e = pcg.Get(l);
            var body = e == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(l.ObjType, e);
            return body == null ? null : (body, e!.Name);
        };
        return Pull(source, pcgFileName, loc);
    }

    // Requirement 3: the same transitive, deduping pull, sourced from Local Library instead of a
    // loaded PCG - so an already-placed object (and everything it references locally) can be
    // staged back in the Merge Window to be rearranged and pushed somewhere else. Bodies come
    // from the cache's CURRENT state (cache.GetCurrentBody); references resolve against whatever
    // address they encode, exactly as a PCG pull resolves within its file.
    public (List<MergeEntry> Added, List<(ObjLoc MissingRef, string RefKind)> Gaps) PullFromLocal(
        LocalLibraryCache localCache, ObjLoc loc)
    {
        MergePullSource source = l =>
        {
            var body = localCache.GetCurrentBody(l.ObjType, l.Bank, l.Number);
            return body == null ? null : (body, localCache.GetDisplayName(l.ObjType, l.Bank, l.Number));
        };
        return Pull(source, LocalSourceLabel, loc);
    }

    (List<MergeEntry> Added, List<(ObjLoc MissingRef, string RefKind)> Gaps) Pull(
        MergePullSource source, string sourceLabel, ObjLoc loc)
    {
        Mutating?.Invoke();
        var added = new List<MergeEntry>();
        var gaps = new List<(ObjLoc, string)>();
        PullRecursive(source, sourceLabel, loc, isTopLevel: true, added, gaps);
        Save();
        return (added, gaps);
    }

    // Returns the content hash of whatever now represents `loc` (an existing deduped entry, a
    // freshly-added one, or null if it's a real gap - not found in `source`, or a malformed
    // record). No separate cycle guard needed even though a Program's Drum Track can reference
    // another Program (possibly itself, or two Programs pointing at each other): `_byHash[hash]
    // = entry` below is set BEFORE this recurses into that entry's own references, so a cycle's
    // second encounter always hits the dedup check at the top and returns immediately instead of
    // re-walking.
    string? PullRecursive(MergePullSource source, string sourceLabel, ObjLoc loc, bool isTopLevel,
                           List<MergeEntry> added, List<(ObjLoc, string)> gaps)
    {
        if (source(loc) is not { } got)
        {
            gaps.Add((loc, isTopLevel ? "pull" : "dependency"));
            return null;
        }
        var (wireBody, name) = got;

        string hash = LocalObjectStore.ComputeHash(wireBody);
        if (_byHash.TryGetValue(hash, out var existing))
        {
            if (isTopLevel) existing.IsTopLevelPull = true;
            if (!existing.Origins.Any(o => o.PcgFileName == sourceLabel && o.SourceLoc == loc))
                existing.Origins.Add(new MergeOrigin(sourceLabel, loc));
            ReconcileGaps(loc, hash);
            return hash;   // dedup - already walked its own deps when first added
        }

        var entry = new MergeEntry
        {
            ContentHash = hash, ObjType = loc.ObjType, Body = wireBody,
            Version = LibObj.CurrentObjectVersion(loc.ObjType) ?? 0, DisplayName = name, IsTopLevelPull = isTopLevel,
        };
        entry.Origins.Add(new MergeOrigin(sourceLabel, loc));
        _byHash[hash] = entry;
        added.Add(entry);
        ReconcileGaps(loc, hash);

        // WalkResolvable, not Walk: a reference into a read-only ROM Program bank (GM/g) needs no
        // staging and can never be satisfied by pulling - creating a RefSite for one would leave
        // HasUnresolvedDependencies permanently true and block every push. See
        // ObjectReferenceWalker.IsAlwaysAvailable.
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.WalkResolvable(loc.ObjType, wireBody))
        {
            string? depHash = PullRecursive(source, sourceLabel, refLoc, isTopLevel: false, added, gaps);
            var refSite = new MergeRefSite { OwnerHash = hash, RefKind = refKind, Site = site, TargetLoc = refLoc, ResolvedContentHash = depHash };
            entry.RefSites.Add(refSite);
            if (depHash != null) _byHash[depHash].ReferencedBy.Add(hash);
            else RegisterGap(refSite);
        }
        return hash;
    }

    void RegisterGap(MergeRefSite site)
    {
        if (!_pendingGapSites.TryGetValue(site.TargetLoc, out var list))
            _pendingGapSites[site.TargetLoc] = list = new();
        list.Add(site);
    }

    // A dependency that was missing when some earlier entry was pulled can become available
    // the moment the SAME ObjLoc is later pulled successfully - typically from a different PCG
    // that happens to have it. There's no address to re-check in a bag-based cache, only "was
    // this exact reference ever satisfied since" - which is exactly what "resolve later by
    // loading a different PCG and pulling it in" (from this feature's design discussion) means.
    void ReconcileGaps(ObjLoc loc, string resolvedHash)
    {
        if (!_pendingGapSites.Remove(loc, out var sites)) return;
        foreach (var site in sites)
        {
            site.ResolvedContentHash = resolvedHash;
            _byHash[resolvedHash].ReferencedBy.Add(site.OwnerHash);
        }
    }

    public MergeEntry? TryGet(string contentHash) => _byHash.GetValueOrDefault(contentHash);

    // Removes one entry - called after it's successfully placed into Local Library (move
    // semantics: the Merge Window only ever shows what's still pending placement) or when the
    // user abandons it without placing it. _placedAddresses is untouched: if this WAS placed,
    // RecordPlacement already captured where, which is exactly what lets a sibling entry still
    // staged resolve against it later.
    public bool Remove(string contentHash)
    {
        if (!_byHash.ContainsKey(contentHash)) return false;
        Mutating?.Invoke();
        if (!_byHash.Remove(contentHash)) return false;
        Save();
        return true;
    }

    // Explicit "Clear Merge" - abandons everything still staged, whether or not any of it was
    // ever placed. Placement bookkeeping is cleared too: once the whole batch is gone, nothing
    // remains that could ever look it up again.
    public void Clear()
    {
        Mutating?.Invoke();
        _byHash.Clear();
        _pendingGapSites.Clear();
        _placedAddresses.Clear();
        Save();
    }

    // Records that `contentHash` now lives at `destLoc` in Local Library - the mechanism
    // behind the "many-to-one" dependency dedup: every OTHER still-staged entry whose
    // RefSites resolved to this same hash will patch to point at exactly this address the
    // next time ResolveReferencesForPlacement runs on it.
    public void RecordPlacement(string contentHash, ObjLoc destLoc)
    {
        Mutating?.Invoke();
        _placedAddresses[contentHash] = destLoc;
        Save();
    }

    // Rewrites a COPY of `entry`'s own body so every dependency that can be resolved gets
    // repointed to its actual destination, and reports back whatever's left unresolved (see
    // MergeRefSite - used by the caller to track it for a later retry, e.g.
    // LibrarianShellViewModel.TrackMergeDependencies). A dependency resolves two ways, tried in
    // order:
    //   1. _placedAddresses - it was placed via THIS cache, this session (or a prior session
    //      recovered via Local Storage). Cheapest, most authoritative - always wins if present.
    //   2. localLookup (objType, contentHash) -> ObjLoc? - an optional caller-supplied search
    //      over Local Library as a WHOLE, by content identity, for a dependency that already
    //      exists there regardless of how it got there (a prior Pull, a prior Commit, a manual
    //      placement - anything). This is what lets a Combi's reference repoint correctly even
    //      when its dependency was never placed FROM this Merge Window at all. Null (the
    //      self-tests' default) skips this entirely, matching the old exact-_placedAddresses-
    //      only behavior.
    // Anything still unresolved after both - including a true gap where ResolvedContentHash was
    // already null - is left exactly as pulled (unchanged bytes) and reported in Unresolved.
    // Placement itself (choosing entry's OWN destination) is a separate, manual step the caller
    // drives via LocalEditOps - this method only patches OUTGOING references, never decides
    // where `entry` itself goes.
    public (byte[] Body, List<MergeRefSite> Unresolved) ResolveReferencesForPlacement(
        MergeEntry entry, Func<int, string, ObjLoc?>? localLookup = null)
    {
        var body = (byte[])entry.Body.Clone();
        var unresolved = new List<MergeRefSite>();
        foreach (var site in entry.RefSites)
        {
            ObjLoc? destLoc = null;
            if (site.ResolvedContentHash is { } hash)
            {
                if (_placedAddresses.TryGetValue(hash, out var placed)) destLoc = placed;
                else if (localLookup?.Invoke(site.TargetLoc.ObjType, hash) is { } found) destLoc = found;
            }

            if (destLoc is { } d)
            {
                LibRefs.ApplyResolvedRef(body, site.RefKind, site.Site, site.TargetLoc.ObjType, d.Bank, d.Number);
            }
            else
            {
                unresolved.Add(site);
            }
        }
        return (body, unresolved);
    }

    void Save() => _persistence.Save(BuildSnapshot(deepCopyRefSites: false));

    // deepCopyRefSites: false for Save (serialized or discarded immediately, so sharing the live
    // MergeRefSite objects is free and safe); true for Snapshot, which is held indefinitely - see
    // its own comment.
    MergeCacheSnapshot BuildSnapshot(bool deepCopyRefSites) => new(
        _byHash.Values.Select(e => new MergeEntrySnapshot(
            e.ContentHash, e.ObjType, e.Body, e.Version, e.DisplayName, e.IsTopLevelPull,
            e.Origins.ToList(), e.ReferencedBy.ToList(),
            deepCopyRefSites
                ? e.RefSites.Select(s => new MergeRefSite
                {
                    OwnerHash = s.OwnerHash, RefKind = s.RefKind, Site = s.Site,
                    TargetLoc = s.TargetLoc, ResolvedContentHash = s.ResolvedContentHash,
                }).ToList()
                : e.RefSites.ToList())).ToList(),
        new Dictionary<string, ObjLoc>(_placedAddresses));
}
