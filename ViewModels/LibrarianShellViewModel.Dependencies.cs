using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;   // Dispatcher.Yield - see AutoFillToLibraryAsync on why the priority matters
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KronosScreenRemote.ViewModels;
// The Librarian shell's read-only dependency VIEWS - the "Object Dependencies" panel, the
// Properties dialog's per-object requirement/referrer lists, "Scan PCG for dependency", and the
// staged-gap rows. Nothing here mutates the library; it all answers "what does this object
// need, and is that need met here?".
//
// Partial of the same ViewModel for the same reason as the placement half - see
// LibrarianShellViewModel.Placement.cs.
partial class LibrarianShellViewModel
{
    // ── "Object Dependencies" panel (Views/LibrarianShellWindow.xaml's GroupBox, driven by
    // LibrarianShellWindow.xaml.cs's PaneSelection.SelectionChanged) - a live, read-only view
    // of what the CURRENTLY SELECTED Program(s)/Combi(s)/Set List(s) reference, transitively (a
    // Set List's Combis and their Programs; a Program's Drum Track and, for HD-1, its Wave
    // Sequence/Drum Kit oscillator zones). Distinct from _sessionClipboard above: that tracks a
    // placement's references that still need pushing; this is just "what does this object need,"
    // independent of placement history.

    public void ShowLocalObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        _refreshObjectDependencies = () => ShowLocalObjectDependencies(selectedLocs);
        var seen = new HashSet<ObjLoc>();
        var sampleSeen = new HashSet<string>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var loc in selectedLocs)
            CollectLocalDeps(loc, seen, sampleSeen, rows);
        ReplaceObjectDependencies(rows);
    }

    // Both walks over ONE object's body. Pure functions of (objType, body), which is what makes
    // the memo below safe.
    sealed record ObjectWalk(
        List<(RefKind RefKind, int Site, ObjLoc Ref)> Refs,
        List<SampleReferenceWalker.SampleDependencyRow> SampleRows);

    // Populating this panel walks the selection's dependencies TRANSITIVELY, and every step used
    // to re-read the referenced body off the CAS store - two filesystem round trips each
    // (LocalObjectStore.TryGet), over a DataDir that is routinely an SMB share. A Set List
    // selection is 128 slots x 16 timbres of that, so clicking one object took seconds, and a
    // plain click runs the walk twice anyway (PaneSelection.HandleMouseUpWithoutDrag fires
    // SelectionChanged unconditionally).
    //
    // Keyed on the body's CONTENT hash, read from the index with no blob read at all
    // (LocalLibraryCache.GetContentHash), so this is self-invalidating rather than something
    // that has to be flushed: any edit rewrites the body, which changes the hash, which misses.
    // Only the object actually edited is invalidated - the rest of the graph stays warm. Two
    // different slots sharing a hash (every INIT Program has an identical body) SHOULD share an
    // entry: the walkers return targets derived from the body, never the owner's own address.
    //
    // Deliberately memoizes ONLY the body-derived walk. Whether each referenced object is
    // present (_cache.Exists) and what it is called (_cache.GetDisplayName) stay live on every
    // population - both are index-only lookups, and they are exactly what must stay fresh so a
    // dependency that gets placed flips from missing to found with no flush anywhere.
    readonly Dictionary<(int ObjType, string Hash), ObjectWalk> _walkCache = new();

    // Bounded in practice by the distinct BODIES a session touches, which is small - but nothing
    // structural caps it, and a long session over a big library browsing every bank in turn has
    // no upper bound at all. Cleared wholesale rather than evicted LRU: entries are pure
    // body-derived memos, so a flush costs one re-walk each, and a cap this far above normal
    // working-set size means it effectively never fires.
    const int WalkCacheCap = 8192;

    // Test-only visibility into whether the memo is actually being HIT - a correctness test
    // passes identically when every lookup silently misses (see LibrarianDependencyCacheSelfTests).
    internal int WalkCacheMisses { get; private set; }

    ObjectWalk? LocalWalk(ObjLoc loc)
    {
        string? hash = _cache.GetContentHash(loc.ObjType, loc.Bank, loc.Number);
        // NoBaselineSentinel ("") is not an identity - keying on it would serve one object's
        // dependencies for every other local-only object of the same type.
        bool cacheable = !string.IsNullOrEmpty(hash);
        if (cacheable && _walkCache.TryGetValue((loc.ObjType, hash!), out var hit)) return hit;

        WalkCacheMisses++;
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return null;
        var walk = new ObjectWalk(
            ObjectReferenceWalker.Walk(loc.ObjType, body).ToList(),
            SampleReferenceWalker.Walk(loc.ObjType, body).ToList());
        if (cacheable)
        {
            if (_walkCache.Count >= WalkCacheCap) _walkCache.Clear();
            _walkCache[(loc.ObjType, hash!)] = walk;
        }
        return walk;
    }

    // `missing` is optional: the selection-driven panel only wants display rows, while
    // InspectDependencies wants the gaps from the SAME walk rather than a second one.
    void CollectLocalDeps(ObjLoc loc, HashSet<ObjLoc> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows,
                          List<MissingDependency>? missing = null)
    {
        if (LocalWalk(loc) is not { } walk) return;
        string parentName = _cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number);
        AddSampleRows(walk.SampleRows, DescribeParent(loc, parentName, "sample"), sampleSeen, rows);
        foreach (var (refKind, site, refLoc) in walk.Refs)
        {
            if (!seen.Add(refLoc)) continue;
            string parentInfo = DescribeParent(loc, parentName, RefKinds.Describe(refKind, site));
            // A ROM (GM/g) reference is shown, but never as missing - it resolves on the
            // instrument no matter what the keyboard library holds (ObjectReferenceWalker.
            // IsAlwaysAvailable), and nothing can be pulled or placed to "fix" it.
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc), parentInfo));
                continue;
            }
            // Cached at write time (LocalIndexEntry.DisplayName) - never a blob read, same
            // discipline as the tree's own labels (LocalLibraryPaneViewModel.MakeLeafNode).
            // AvailableLocally, not bare Exists: an object already marked pending-delete still has
            // an index entry, so Exists reported a Combi's timbre as satisfied by a Program the
            // very next Commit removes. Now consistent with the PCG/Merge rows, which use the same
            // test to decide whether a reference is really covered.
            bool found = AvailableLocally(refLoc);
            string name = found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "";
            // An INIT Program satisfies the reference technically but is a placeholder, not the
            // sound the referrer expects - worth saying so, since it's also the case that places
            // freely (see ProgramBody.IsInit and BatchLibrarian.PlanBatchMove's orphan gate).
            rows.Add(new ObjectDependencyRow(
                found && refLoc.ObjType == LibObj.Program && ProgramBody.IsInitName(name)
                    ? $"{TypeName(refLoc.ObjType)}: {refLoc.Label()} - {name} {AppMessages.Librarian.Shell.InitPlaceholderSuffix}"
                    : DescribeDependency(refLoc, name, found, "locally"),
                parentInfo,
                // "(references nothing)" - ObjectInfoDialog's default for a null callback - is the
                // wrong answer for a missing object: nothing local HAS it, so what it references
                // is unknowable, not empty.
                found ? () => DescribeLocalChildren(refLoc)
                      : () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
                // A "not found locally" row IS a missing dependency - it was already saying so in
                // words while rendering in the same color as every satisfied row beside it, which
                // read as "noted, and fine". Flagging it red also makes it right-clickable to
                // search a .pcg, the same recovery every other missing row now offers.
                missingRef: found ? null : refLoc));
            // A ROM reference is listed but is never a gap (IsAlwaysAvailable - it resolves on the
            // instrument and can't be searched for), so it never enters `missing`.
            if (!found && missing != null && !ObjectReferenceWalker.IsAlwaysAvailable(refLoc))
                missing.Add(new MissingDependency(refLoc, refKind, site, loc));
            if (found) CollectLocalDeps(refLoc, seen, sampleSeen, rows, missing);
        }
    }

    // SAMPLE dependencies (Sampling Mode/RAM, EXs, User Sample Banks) for one object's own
    // body - shared by the Local/PCG/Merge collectors below. Display-only rows: unlike an
    // object reference, a sample bank can never be "found"/resolved locally, pulled, or
    // repointed (SampleReferenceWalker's own header comment has the full reasoning), so there
    // is no recursion, no "missing" tracking, and no child-describe callback - just a flat
    // row per distinct bank this object references, deduped against `sampleSeen` the same way
    // `seen` dedupes object refs across one panel population.
    void AddSampleRows(IEnumerable<SampleReferenceWalker.SampleDependencyRow> walked, string parentInfo, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        foreach (var row in walked)
        {
            if (!sampleSeen.Add(row.Key)) continue;
            rows.Add(new ObjectDependencyRow(ResolveSampleDescription(row), parentInfo, sampleBucket: row.Bucket));
        }
    }

    // Appends a resolved friendly name from _exsIndex when one's available - never changes the
    // row's Bucket/color (that's fixed at classification time from the PCG bytes alone, so
    // coloring stays deterministic and identical whether or not the user ever resolves names -
    // see ExsOptionIndex's own header comment on why a raw-UUID bank found there isn't
    // reclassified from UserOrThirdParty to Exs even when it turns out to be an EXs127+ pack).
    // Falls back to the row's own description untouched when no index is loaded yet, or the
    // bank isn't in it (a user's own sample bank rather than a published EXs product, or
    // genuinely unresolvable - e.g. the live Sampling Mode/RAM bucket, which
    // SampleReferenceWalker's own comment explains has no persistent identity to look up at all).
    string ResolveSampleDescription(SampleReferenceWalker.SampleDependencyRow row)
    {
        if (_exsIndex == null) return row.Description;
        string? name = row.Bucket switch
        {
            SampleReferenceWalker.BankBucket.Exs when ParseExsNumber(row.Key) is { } n => _exsIndex.NameForExsNumber(n),
            SampleReferenceWalker.BankBucket.UserOrThirdParty => _exsIndex.NameForUuidHex(row.Key),
            _ => null,
        };
        return name == null ? row.Description : $"{row.Description} - {name}";
    }

    // Inverse of SampleReferenceWalker.ClassifyLegacyUuid's own "exs{exsNumber}" key format.
    static int? ParseExsNumber(string key) =>
        key.StartsWith("exs", StringComparison.Ordinal) && int.TryParse(key.AsSpan(3), out int n) ? n : null;

    public void ShowPcgObjectDependencies(IReadOnlyList<ObjLoc> selectedLocs)
    {
        _refreshObjectDependencies = () => ShowPcgObjectDependencies(selectedLocs);
        var rows = new List<ObjectDependencyRow>();
        if (PcgPane.View is { } view)
        {
            var seen = new HashSet<ObjLoc>();
            var sampleSeen = new HashSet<string>();
            foreach (var loc in selectedLocs)
                CollectPcgDeps(view, loc, seen, sampleSeen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    // The PCG walk had no memo of its own, unlike LocalWalk's _walkCache: every population
    // re-decoded every referenced body through WireBodyFromPcgEntry, and a Set List selection is
    // 128 slots x 16 timbres of that, on the UI thread. Tolerable off a click (the user asked for
    // it and waits once), but ApplyExsOptionIndex re-runs the SAME walk to pick up resolved names
    // - which is what made "Resolve Sample Bank Names..." lock the window the moment the index
    // was built.
    //
    // A loaded PcgLibraryView is immutable, so keying the memo on the instance makes it
    // self-invalidating: loading a different file is a different instance and misses everything.
    readonly Dictionary<(int ObjType, int Bank, int Number), byte[]?> _pcgBodyCache = new();
    PcgLibraryView? _pcgBodyCacheView;

    byte[]? PcgBody(PcgLibraryView view, ObjLoc loc)
    {
        if (!ReferenceEquals(view, _pcgBodyCacheView))
        {
            _pcgBodyCache.Clear();
            _pcgBodyCacheView = view;
        }
        var key = (loc.ObjType, loc.Bank, loc.Number);
        if (_pcgBodyCache.TryGetValue(key, out var hit)) return hit;
        var entry = view.Get(loc);
        return _pcgBodyCache[key] = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
    }

    void CollectPcgDeps(PcgLibraryView view, ObjLoc loc, HashSet<ObjLoc> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        var entry = view.Get(loc);
        if (entry == null || PcgBody(view, loc) is not { } body) return;
        AddSampleRows(SampleReferenceWalker.Walk(loc.ObjType, body), DescribeParent(loc, entry!.Name, "sample"), sampleSeen, rows);
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seen.Add(refLoc)) continue;
            string parentInfo = DescribeParent(loc, entry!.Name, RefKinds.Describe(refKind, site));
            if (ObjectReferenceWalker.IsAlwaysAvailable(refLoc))   // see CollectLocalDeps
            {
                rows.Add(new ObjectDependencyRow(DescribeRomDependency(refLoc), parentInfo));
                continue;
            }
            var depEntry = view.Get(refLoc);
            if (depEntry != null)
            {
                rows.Add(new ObjectDependencyRow(DescribeDependency(refLoc, depEntry.Name, true, "in this PCG"),
                    parentInfo, () => DescribePcgChildren(view, refLoc)));
                CollectPcgDeps(view, refLoc, seen, sampleSeen, rows);
                continue;
            }
            // Absent from the PCG is NOT the same as missing: the reference is an ADDRESS, so
            // whatever Keyboard Library already holds there is what the Kronos will play. Checking
            // that before calling it a gap is the difference between "you need to find this" and
            // "you already have this" - see DescribeAvailableLocally.
            rows.Add(DescribeGapOrLocal(refLoc, parentInfo, "in this PCG"));
        }
    }

    public void ShowMergeObjectDependencies(IReadOnlyList<string> selectedHashes)
    {
        _refreshObjectDependencies = () => ShowMergeObjectDependencies(selectedHashes);
        var seen = new HashSet<string>();
        var sampleSeen = new HashSet<string>();
        var rows = new List<ObjectDependencyRow>();
        foreach (var hash in selectedHashes)
        {
            var entry = MergePane.TryGet(hash);
            if (entry != null) CollectMergeDeps(entry, seen, sampleSeen, rows);
        }
        ReplaceObjectDependencies(rows);
    }

    // Merge entries are keyed by content hash, not address - RefSites already carry the
    // resolved-dependency lookup (or the original PCG address for a still-unresolved gap), so
    // this needs none of ObjectReferenceWalker's own byte-decoding, unlike the Local/PCG paths.
    // SAMPLE refs are the one thing still read straight off entry.Body (RefSites only carries
    // OBJECT references - see MergeEntry's own field comments) via the shared AddSampleRows.
    void CollectMergeDeps(MergeEntry entry, HashSet<string> seen, HashSet<string> sampleSeen, List<ObjectDependencyRow> rows)
    {
        string parentName = string.IsNullOrEmpty(entry.DisplayName) ? "(unnamed)" : entry.DisplayName;
        AddSampleRows(SampleReferenceWalker.Walk(entry.ObjType, entry.Body), $"{TypeName(entry.ObjType)}: {parentName} (via sample, staged - not yet placed)", sampleSeen, rows);
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string key = site.ResolvedContentHash ?? site.TargetLoc.Label();
            if (!seen.Add(key)) continue;
            string parentInfo = $"{TypeName(entry.ObjType)}: {parentName} (via {RefKinds.Describe(site.RefKind, site.Site)}, staged - not yet placed)";
            // No real address yet (Merge Window is bag-based, not addressed) - name is all
            // there is to show until it's actually placed.
            if (dep != null)
            {
                rows.Add(new ObjectDependencyRow(
                    $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}",
                    parentInfo, () => DescribeMergeChildren(dep)));
                CollectMergeDeps(dep, seen, sampleSeen, rows);
                continue;
            }
            rows.Add(DescribeGapOrLocal(site.TargetLoc, parentInfo, "in any loaded PCG"));
        }
    }

    // Empties the SELECTION half of the panel only - the staged-gap rows above it belong to the
    // Merge Window, not to whatever happens to be selected, and must not disappear when the user
    // clicks empty space (that list is the pre-Commit checklist; a checklist that vanishes on a
    // stray click is worse than none).
    // One reference that its own source (a loaded PCG, the Merge Window) doesn't satisfy - which
    // is only half the question. The reference is an ADDRESS, so if Keyboard Library already holds
    // something there, the Kronos resolves it on load and there is nothing for the user to go
    // find: that gets an ordinary row naming what's actually there, not a red one. Only an
    // address nothing local covers is a real gap.
    // Present locally AND still going to be there after the next Commit. _cache.Exists alone
    // counts an object already marked pending-delete, so reassuring the user that a dependency is
    // "already in your Keyboard Library" off Exists would be pointing at something Commit is about to
    // remove. Only used for the green-light direction - saying something IS satisfied is the claim
    // that has to be strict.
    bool AvailableLocally(ObjLoc loc) =>
        _cache.Exists(loc.ObjType, loc.Bank, loc.Number) && !_cache.IsPendingDelete(loc.ObjType, loc.Bank, loc.Number);

    ObjectDependencyRow DescribeGapOrLocal(ObjLoc refLoc, string parentInfo, string whereMissing)
    {
        if (AvailableLocally(refLoc))
        {
            string localName = _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number);
            return new ObjectDependencyRow(
                $"{TypeName(refLoc.ObjType)}: {refLoc.Label()} - {(string.IsNullOrEmpty(localName) ? "(unnamed)" : localName)} " +
                $"{AppMessages.Librarian.Shell.ResolvedFromLocalLibrary(whereMissing)}",
                parentInfo, () => DescribeLocalChildren(refLoc));
        }
        return new ObjectDependencyRow(
            DescribeDependency(refLoc, "", false, whereMissing), parentInfo,
            () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
            missingRef: refLoc);
    }

    public void ClearObjectDependencies()
    {
        _selectionDependencyRows.Clear();
        RebuildObjectDependencies();
    }

    // ── Per-object dependency detail (the Properties dialog's own lists, requirement 1) ──────
    // Same data the "Object Dependencies" panel shows, but for ONE object and in both directions:
    // what it REQUIRES (its own outgoing references, transitively - a Set List's Combis and their
    // Programs) and what USES it (incoming referrers). The panel is selection-driven and
    // outgoing-only; this is the "tell me everything about this one object" view.

    // One reference site with nothing local behind it. Site is carried (not just RefKind) because
    // resolving it later means patching THAT byte site inside RequiredBy - see
    // LocalEditOps.RepatchReference.
    public readonly record struct MissingDependency(ObjLoc Missing, RefKind RefKind, int Site, ObjLoc RequiredBy);

    // Both dependency views in ONE transitive walk. Each level reads the owner's full body off the
    // CAS store, so a Set List with 128 populated slots is ~129 blob reads - fine once per user
    // action, not fine repeated per caller. Callers that need both the display rows and the gaps
    // (the Properties dialog needs exactly that) must use this rather than calling the two
    // convenience wrappers below in sequence.
    public (IReadOnlyList<string> Rows, IReadOnlyList<MissingDependency> Missing) InspectDependencies(ObjLoc loc)
    {
        var rows = new List<ObjectDependencyRow>();
        var missing = new List<MissingDependency>();
        CollectLocalDeps(loc, new HashSet<ObjLoc>(), new HashSet<string>(), rows, missing);
        return (rows.Select(r => r.Description).ToList(), missing);
    }

    public IReadOnlyList<string> DescribeRequirements(ObjLoc loc) => InspectDependencies(loc).Rows;

    // What currently points AT `loc` (Combi timbres / Set List slots) - the delete-warning's own
    // referrer lookup, surfaced read-only. Empty for a Set List (nothing ever references one).
    public IReadOnlyList<string> DescribeReferrers(ObjLoc loc) => LocalPane.DescribeReferrers(loc);

    // Every reference of `loc` (transitively) with nothing local behind it - what a "find this
    // dependency" action has to go looking for. ROM (GM/g) references are excluded by
    // construction: they resolve on the instrument and can't be searched for.
    public IReadOnlyList<MissingDependency> MissingDependenciesOf(ObjLoc loc) => InspectDependencies(loc).Missing;

    // ── "Scan PCG for dependency" (requirement 2) ────────────────────────────────────────────
    // The manual counterpart to the automatic auto-heal pipeline: when an object shows unmet
    // dependencies, point this at a .pcg file and whatever it contains is staged into the Merge
    // Window, ready to place. Deliberately staged rather than placed automatically - only the user
    // knows which bank/slot a recovered dependency belongs in (the same reasoning the Merge
    // Window's manual placement rests on), and staging is undoable in one step.
    //
    // Reads the file into a PcgLibraryView of its own instead of loading it into the PCG pane: the
    // user is mid-task on whatever is already loaded there, and a scan for a missing Program
    // shouldn't replace it. Each found dependency comes in transitively (MergeCache.PullFromPcg),
    // so a recovered Combi brings its own Programs too.
    // Searches one .pcg for ONE specific missing address - the Unresolved Dependencies dialog's
    // right-click action, where the user is looking at a single reported gap rather than an
    // object's whole dependency set. Anything found is staged transitively, exactly like the
    // object-level scan; no new session-clipboard tracking is needed here because these entries
    // are ALREADY tracked (that's why they're in the dialog), so ResolvePendingDependencies will
    // repoint them by content wherever the user places them.
    // Scans ONE .pcg for EVERY address in `missing`, not just the one the user right-clicked.
    // A .pcg that holds one of a Combi's missing Programs very often holds the rest of them too
    // (they were saved together), so checking only the clicked row made the user re-pick the same
    // file once per gap. Everything present is staged in a single undo scope; the caller drops the
    // rows for what came back and leaves the rest of the list for another file.
    public (List<ObjLoc> Found, string? Error) ScanPcgForMissingObjects(
        IReadOnlyList<ObjLoc> missing, byte[] pcgBytes, string fileName)
    {
        var found = new List<ObjLoc>();
        if (missing.Count == 0) return (found, null);

        var file = PcgFile.Open(pcgBytes);
        if (file == null) return (found, AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));

        var view = new PcgLibraryView(file);
        foreach (var loc in missing)
            if (view.Get(loc) != null) found.Add(loc);
        if (found.Count == 0) return (found, null);

        // One undo step for the whole sweep - the user made one decision (this file), so one
        // Ctrl+Z should take all of it back, not unwind object by object. The nested Begin inside
        // the pane's list pull joins this step (see LibrarianUndoRecorder.Begin).
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoScannedPcgForDependencies(fileName));
        MergePane.PullFromPcg(view, fileName, found);
        return (found, null);
    }

    // A display name for an address, wherever it can be found - Keyboard Library first (the cached
    // DisplayName, no blob read), then the loaded PCG. Empty when neither knows it, which is itself
    // informative in the unresolved list: nothing loaded has this object at all.
    public string DescribeMissingName(ObjLoc loc)
    {
        if (_cache.Exists(loc.ObjType, loc.Bank, loc.Number))
            return _cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number);
        return PcgPane.Get(loc)?.Name ?? "";
    }

    // `missing` comes from the caller's own InspectDependencies/MissingDependenciesOf call, so the
    // transitive walk (one blob read per owner object) happens ONCE per user action rather than
    // again in here.
    public (int Found, int Missing, string? Error) ScanPcgForDependencies(
        ObjLoc loc, IReadOnlyList<MissingDependency> missing, byte[] pcgBytes, string fileName)
    {
        if (missing.Count == 0) return (0, 0, null);

        var file = PcgFile.Open(pcgBytes);
        if (file == null) return (0, missing.Count, AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));
        var view = new PcgLibraryView(file);

        // One undo step for the whole scan, however many dependencies it recovers - same
        // one-gesture-one-step rule as PullIntoMerge's list overload.
        using var undo = _undo.Begin(AppMessages.Librarian.Shell.UndoScannedPcgForDependencies(fileName));
        int found = 0;
        bool tracked = false;
        var toStage = new List<ObjLoc>();
        foreach (var gap in missing)
        {
            var entry = view.Get(gap.Missing);
            if (entry == null) continue;
            toStage.Add(gap.Missing);
            found++;

            // Staging alone doesn't repair anything: the referrer still encodes the OLD address,
            // and the user is free to place the recovered object anywhere. Tracking the gap in the
            // session clipboard - keyed by the CONTENT hash of what the PCG holds, exactly like
            // the direct-PCG placement path (StageAndTrackPcgDependencies) - is what lets
            // ResolvePendingDependencies find it by content at the next Sync/Commit and repatch
            // the reference to wherever it actually landed. Without this the feature would find
            // the dependency and still leave the referrer pointing at nothing unless the user
            // happened to place it at the exact original address.
            if (ProgramFormatConverter.WireBodyFromPcgEntry(gap.Missing.ObjType, entry) is { } wireBody)
            {
                _sessionClipboard.Add(new SessionDependencyEntry(
                    gap.Missing, gap.RefKind, gap.Site, gap.RequiredBy, LocalObjectStore.ComputeHash(wireBody)));
                tracked = true;
            }
        }
        MergePane.PullFromPcg(view, fileName, toStage);
        if (tracked) RefreshSessionClipboard();
        return (found, missing.Count, null);
    }

    // ── Missing staged dependencies (the panel's red section) ────────────────────────────────
    // The panel shows two things stacked: everything the Merge Window still MISSES (red, always
    // first, independent of any selection), then what the current selection references. Keeping
    // the selection half cached rather than re-derived is what lets a merge mutation refresh the
    // red rows for free: ScanPcgForMissingObjects pulls once per object it finds, so re-running
    // the selection's own transitive walk on each of those would be dozens of walks over a
    // DataDir that is routinely an SMB share (see _walkCache's comment).
    readonly List<ObjectDependencyRow> _selectionDependencyRows = new();

    void ReplaceObjectDependencies(List<ObjectDependencyRow> rows)
    {
        _selectionDependencyRows.Clear();
        _selectionDependencyRows.AddRange(rows);
        RebuildObjectDependencies();
    }

    void RebuildObjectDependencies()
    {
        ObjectDependencyRows.Clear();
        foreach (var r in BuildMergeGapRows()) ObjectDependencyRows.Add(r);
        foreach (var r in _selectionDependencyRows) ObjectDependencyRows.Add(r);
    }

    // One row per missing ADDRESS, not per reference site: a Set List whose Combis all want the
    // same absent Program would otherwise repeat that Program once per timbre and bury the
    // selection's own rows underneath. Reuses the Unresolved Dependencies dialog's own row format
    // so the same gap reads identically wherever the user meets it.
    List<ObjectDependencyRow> BuildMergeGapRows()
    {
        var rows = new List<ObjectDependencyRow>();
        foreach (var group in MergePane.UnresolvedDependencies
                     .GroupBy(s => s.TargetLoc)
                     // Keyboard Library already covers this address, so the reference resolves on the
                     // instrument and there is nothing to go find - the gap is only in the Merge
                     // Window's own pull source, which is not the user's problem. Filtering here
                     // rather than styling it differently keeps the red section to exactly the
                     // things that still need an action.
                     .Where(g => !AvailableLocally(g.Key))
                     .OrderBy(g => g.Key.ObjType).ThenBy(g => g.Key.Bank).ThenBy(g => g.Key.Number))
        {
            var referrers = group.Select(DescribeGapReferrer).ToList();
            rows.Add(new ObjectDependencyRow(
                // Safe to name now: the local-library case is filtered out above, so
                // DescribeMissingName can only be answering from a loaded PCG - i.e. "this is
                // sitting in the PCG you have open, pull it in", which is exactly the useful hint.
                AppMessages.UnresolvedDependencies.Row(
                    TypeName(group.Key.ObjType), group.Key.Label(), DescribeMissingName(group.Key), group.Count()),
                string.Join("; ", referrers),
                // Nothing staged HAS this object, so its own outgoing references are genuinely
                // unknowable until it's found - say that rather than letting the "More Info"
                // popup's default read as "it references nothing".
                () => new[] { AppMessages.UnresolvedDependencies.NotStagedChildren },
                missingRef: group.Key));
        }
        return rows;
    }

    // Which staged object needs this gap, and through which site - the merge cache is keyed by
    // content, so the referrer is named, never addressed.
    string DescribeGapReferrer(MergeRefSite site)
    {
        var owner = MergePane.TryGet(site.OwnerHash);
        string name = owner == null || string.IsNullOrEmpty(owner.DisplayName) ? "(unnamed)" : owner.DisplayName;
        return $"{TypeName(owner?.ObjType ?? site.TargetLoc.ObjType)}: {name} (via {RefKinds.Describe(site.RefKind, site.Site)})";
    }

    static string TypeName(int objType) => ObjectTypeRegistry.Get(objType).DisplayName;

    // Shared row format for the Local/PCG collectors above (Merge has no real address, so it
    // formats its own rows separately) - slot address alone isn't useful on its own, hence
    // type + name alongside it.
    // A read-only ROM (GM/g) Program reference - present on every Kronos, so it's listed for
    // completeness but never as a gap. See ObjectReferenceWalker.IsAlwaysAvailable.
    static string DescribeRomDependency(ObjLoc loc) =>
        $"{TypeName(loc.ObjType)}: {loc.Label()} - {AppMessages.Librarian.Shell.RomBankAlwaysAvailable}";

    static string DescribeDependency(ObjLoc loc, string name, bool found, string whereMissing) =>
        found
            ? $"{TypeName(loc.ObjType)}: {loc.Label()} - {(string.IsNullOrEmpty(name) ? "(unnamed)" : name)}"
            : $"{TypeName(loc.ObjType)}: {loc.Label()} - not found {whereMissing}";

    // "Referenced by ..." line for the "More Info" popup - the object that pulled this row's
    // own object into the walk, and which of its reference sites (timbre 4, drum track, osc1
    // zone2, slot 9, ...) did it.
    static string DescribeParent(ObjLoc parentLoc, string parentName, string via) =>
        $"{TypeName(parentLoc.ObjType)}: {parentLoc.Label()} - {(string.IsNullOrEmpty(parentName) ? "(unnamed)" : parentName)} (via {via})";

    // One level of a LOCAL object's own outgoing references, each annotated with which
    // reference site it came from - the "More Info" popup's "References:" section. Deliberately
    // NOT the transitive walk CollectLocalDeps does for the panel itself (that's for a whole
    // selection's dependency list; this is "what does just THIS one object point at").
    IReadOnlyList<string> DescribeLocalChildren(ObjLoc loc)
    {
        if (_cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) is not { } body) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(loc.ObjType, body)) lines.Add(ResolveSampleDescription(row));
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            bool found = AvailableLocally(refLoc);   // see CollectLocalDeps on why not bare Exists
            string desc = ObjectReferenceWalker.IsAlwaysAvailable(refLoc)
                ? DescribeRomDependency(refLoc)
                : DescribeDependency(refLoc,
                    found ? _cache.GetDisplayName(refLoc.ObjType, refLoc.Bank, refLoc.Number) : "", found, "locally");
            lines.Add($"{desc} (via {RefKinds.Describe(refKind, site)})");
        }
        return lines;
    }

    // Same as DescribeLocalChildren, sourced from a loaded PCG instead of Keyboard Library.
    IReadOnlyList<string> DescribePcgChildren(PcgLibraryView view, ObjLoc loc)
    {
        var entry = view.Get(loc);
        var body = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
        if (body == null) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(loc.ObjType, body)) lines.Add(ResolveSampleDescription(row));
        foreach (var (refKind, site, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            var depEntry = view.Get(refLoc);
            string desc = ObjectReferenceWalker.IsAlwaysAvailable(refLoc)
                ? DescribeRomDependency(refLoc)
                : DescribeDependency(refLoc, depEntry?.Name ?? "", depEntry != null, "in this PCG");
            lines.Add($"{desc} (via {RefKinds.Describe(refKind, site)})");
        }
        return lines;
    }

    // Same idea for a staged Merge Window entry - sourced from its own precomputed RefSites
    // (see CollectMergeDeps's own comment for why that needs none of ObjectReferenceWalker's
    // byte-decoding, unlike the Local/PCG variants above) plus SampleReferenceWalker straight
    // off entry.Body for the same reason CollectMergeDeps does.
    IReadOnlyList<string> DescribeMergeChildren(MergeEntry entry)
    {
        var lines = new List<string>();
        foreach (var row in SampleReferenceWalker.Walk(entry.ObjType, entry.Body)) lines.Add(ResolveSampleDescription(row));
        foreach (var site in entry.RefSites)
        {
            var dep = site.ResolvedContentHash is { } hash ? MergePane.TryGet(hash) : null;
            string desc = dep != null
                ? $"{TypeName(dep.ObjType)}: {(string.IsNullOrEmpty(dep.DisplayName) ? "(unnamed)" : dep.DisplayName)}"
                : $"{TypeName(site.TargetLoc.ObjType)}: {site.TargetLoc.Label()} - not found in any loaded PCG";
            lines.Add($"{desc} (via {RefKinds.Describe(site.RefKind, site.Site)})");
        }
        return lines;
    }
}
