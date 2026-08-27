namespace KronosScreenRemote;

// Facade over the local full-body cache: LocalObjectStore (CAS blobs) + LocalLibraryIndex
// (current/baseline hash pointers, loaded once and mutated in memory - same convention
// LibraryRepository.ScanAsync already uses for RefIndex: mutate incrementally, persist
// once via Save() at the end of a batch of changes) + OpLog (permanent history, persisted
// immediately on every mutating call since it's append-only and cheap).
//
// Root: {Storage.DataDir}/local_library/ - deliberately NOT host-keyed, unlike every other
// Storage.cs cache (the Kronos's IP can change; the objects don't).
sealed class LocalLibraryCache
{
    public string Root { get; }
    readonly LocalLibraryIndex _index;

    // Guards _index.Entries writes (below) and _catalog/_catalogBuildTask/_pendingCatalogPatches
    // against BuildCatalogInBackground's snapshot phase, which reads _index.Entries from a
    // thread-pool thread while the UI thread may be mutating it - see BuildCatalogAsync's own
    // comment. Plain reads (GetCurrentBody, Exists, etc.) stay lock-free: a Dictionary supports
    // any number of concurrent readers, it's only a read/write or write/write pair racing that
    // corrupts it, so only writers (and this one background reader) need to coordinate.
    readonly object _lock = new();

    // Lazily built, then kept in sync in-place by PatchCatalog below - NOT rebuilt from disk
    // on every BuildCatalog() call. See BuildCatalogAsync()'s comment for why this matters.
    LibraryCatalog? _catalog;
    Task<LibraryCatalog>? _catalogBuildTask;

    // Edits that land while a background build is mid-flight (its disk-read loop already
    // holds a snapshot of _index.Entries taken before the edit happened, so it won't see it)
    // - replayed onto the freshly-built catalog once that loop finishes. See PatchCatalog.
    readonly List<(int ObjType, int Bank, int Number, byte Version, byte[] Body)> _pendingCatalogPatches = new();

    // Raised immediately BEFORE one slot's index entry is written or removed, carrying that
    // slot's PRIOR entry (null = it didn't exist). The Librarian's linear undo
    // (Core/LocalLibrary/LibrarianUndo.cs) is the only subscriber: observing this is what lets
    // one capture scope record every slot an action touched - however deep inside LocalEditOps/
    // BatchLibrarian the write happens - without every edit path having to opt in by hand.
    // Raised from the four LOCAL-EDIT mutators only (RecordEdits, Discard, RemoveObject,
    // SetPendingDelete): a Pull/Push baseline advance isn't a local edit and isn't undoable.
    public event Action<int, int, int, LocalIndexEntry?>? SlotMutating;

    public LocalLibraryCache(string root)
    {
        Root = root;
        _index = LocalLibraryIndex.Load(root);
    }

    public static LocalLibraryCache Open() => new(System.IO.Path.Combine(Storage.DataDir, "local_library"));

    public void Save()
    {
        lock (_lock) _index.Save(Root);
    }

    // ── Reads ─────────────────────────────────────────────────────────────────

    public byte[]? GetCurrentBody(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e)
            ? LocalObjectStore.TryGet(Root, e.CurrentHash) : null;

    public byte[]? GetBaselineBody(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e)
            ? LocalObjectStore.TryGet(Root, e.BaselineHash) : null;

    public byte? GetVersion(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) ? e.Version : null;

    public bool IsDirty(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) &&
        e.CurrentHash != e.BaselineHash;

    public bool IsConflicted(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) && e.Conflicted;

    // Cached at write time (see LocalIndexEntry's own doc comment) - index-only, no blob
    // read, safe to call once per node on every tree refresh. Defaults to true (no red dot)
    // for an object that somehow isn't tracked at all.
    public bool HasResolvedDependencies(int objType, int bank, int number) =>
        !_index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) || e.HasResolvedDependencies;

    // Which wire format a Program's body is in (EXi vs HD-1) - cached at write time (see
    // LocalIndexEntry's own doc comment), index-only, no blob read. Meaningless for Combi/
    // Set List; defaults to true there (never displayed).
    public bool IsExi(int objType, int bank, int number) =>
        !_index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) || e.IsExi;

    // Existence check with NO disk I/O (dictionary lookup only) - use this instead of
    // `GetCurrentBody(...) != null` for a plain "is anything here" test over many slots
    // (e.g. building a tree), since GetCurrentBody reads the full blob from the CAS store.
    public bool Exists(int objType, int bank, int number) =>
        _index.Entries.ContainsKey(LocalLibraryIndex.Key(objType, bank, number));

    // Is this slot's occupant merely an INIT/blank placeholder? Index-only, no blob read - the
    // cached flag when we have one, else the name-only fallback for entries written before that
    // field existed (see LocalIndexEntry's own comment on why null is not "false").
    public bool IsInitSlot(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e)
        && (e.IsInit ?? InitObjects.IsInitName(objType, e.DisplayName));

    // "Is there real content here?" - the test the free-slot search wants, as opposed to Exists,
    // which only asks whether the slot is INDEXED. On a Kronos those differ for most of a library:
    // the protocol has no empty slot and no delete (see EraseBody), so a synced library indexes all
    // 128 slots of every bank and Exists is true everywhere - which is why "every Combi bank is
    // full" was reported against a library whose USER banks are almost entirely init placeholders.
    //
    // NOT a drop-in replacement for Exists. Deliberately still Exists-based:
    //   • LocalEditOps.LocalProgramBankFormat - an init HD-1 Program still proves its bank is
    //     HD-1, and reading it as "empty" would put the bank's type back to unknown, re-opening
    //     the wrong-format-bank hole FindBankWithFreeSlot exists to close;
    //   • tree building and HasAnyObjects/IsLibraryEmpty - init objects are real rows the user
    //     can select, rename and overwrite, and an all-init library is synced, not empty.
    public bool HasContent(int objType, int bank, int number) =>
        Exists(objType, bank, number) && !IsInitSlot(objType, bank, number);

    // One-time upgrade of entries written before IsInit existed, for the ONE object type whose
    // emptiness a cached display name can't answer: a Set List's is the aggregate of its 128 slots
    // (SetListData.IsEmpty), and an untouched one is named "Set List 042", not "Init ...". Without
    // this, a library synced by an earlier build reports its Set List root as full forever - the
    // IsInit==null entries fall back to IsInitName, which is false for Set Lists by construction.
    //
    // Programs need no equivalent pass (ProgramBody.IsInit IS the name check, so the fallback is
    // already exact) and Combis are covered well enough by their name signal, so this stays scoped
    // to one pseudo-bank of 128 objects rather than sweeping the whole library. Re-entrant and
    // idempotent: it only fills nulls, re-checks under the lock before writing, and saves once.
    public int BackfillInitFlags(int objType)
    {
        var descriptor = ObjectTypeRegistry.Get(objType);
        int changed = 0;
        foreach (var bank in descriptor.EditableBanks())
            for (int n = 0; n < descriptor.SlotCount(bank); n++)
            {
                string key = LocalLibraryIndex.Key(objType, bank, n);
                string hash;
                lock (_lock)
                {
                    if (!_index.Entries.TryGetValue(key, out var e) || e.IsInit != null) continue;
                    hash = e.CurrentHash;
                }
                // The blob read itself - deliberately outside the lock (this runs on a background
                // thread; see LibrarianShellViewModel's fire-and-forget caller). Reads
                // LocalObjectStore directly rather than through GetCurrentBody, which re-enters
                // _index.Entries WITHOUT the lock - safe for the UI thread's own synchronous reads
                // (never races itself), but this method's calling thread races the UI thread's
                // _index.Entries WRITES (RecordEdits et al.), which a plain unlocked
                // Dictionary read/write pair does not tolerate.
                var body = LocalObjectStore.TryGet(Root, hash);
                if (body == null) continue;
                bool isInit = ComputeIsInit(objType, body);
                lock (_lock)
                {
                    if (!_index.Entries.TryGetValue(key, out var cur) || cur.IsInit != null) continue;
                    _index.Entries[key] = cur with { IsInit = isInit };
                    changed++;
                }
            }
        if (changed > 0) Save();
        return changed;
    }

    // "Does the library hold anything at all?" - index-only, no disk I/O. Drives the pane's
    // empty-state hint (LocalLibraryPaneViewModel.ShowEmptyHint): a fresh install, or the exe
    // run from a folder with no library beside it, starts with zero entries.
    public bool HasAnyObjects => _index.Entries.Count > 0;

    // The cached name, decoded once at write time - NEVER touches the CAS blob store. Use
    // this for any display purpose (tree labels, dialog pre-fill); reserve GetCurrentBody
    // for callers that actually need the full body (an edit/move/push operation).
    public string GetDisplayName(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) ? e.DisplayName : "";

    // Searches the WHOLE library for an object matching `contentHash`, regardless of where it
    // lives - the primitive the placement pipeline needs to repoint a reference at whatever
    // address its dependency actually occupies, instead of only ever checking the one literal
    // address the reference's raw bytes happen to encode. Index-only (CurrentHash is cached at
    // write time - see LocalIndexEntry's own comment), so this is a cheap in-memory scan, no
    // blob reads. Excludes PendingDelete entries - never repoint a fresh reference at something
    // about to be removed. FirstOrDefault if more than one identical-content copy exists
    // anywhere (which one doesn't matter - they're byte-identical).
    public ObjLoc? FindByContentHash(int objType, string contentHash)
    {
        foreach (var kv in _index.Entries)
        {
            if (kv.Value.PendingDelete || kv.Value.CurrentHash != contentHash) continue;
            var loc = ParseKey(kv.Key);
            if (loc.ObjType == objType) return loc;
        }
        return null;
    }

    // The ObjType >= 0 filter drops ParseKey's malformed-key sentinel (-1,-1,-1) so it
    // can never leak into a caller's hardware request or tree node (see ParseKey).
    public IEnumerable<ObjLoc> AllObjects() =>
        _index.Entries.Keys.Select(ParseKey).Where(loc => loc.ObjType >= 0);

    public IEnumerable<ObjLoc> PendingDeleteObjects() =>
        _index.Entries.Where(kv => kv.Value.PendingDelete).Select(kv => ParseKey(kv.Key))
              .Where(loc => loc.ObjType >= 0);

    public IEnumerable<ObjLoc> DirtyObjects() =>
        _index.Entries.Where(kv => kv.Value.CurrentHash != kv.Value.BaselineHash).Select(kv => ParseKey(kv.Key))
              .Where(loc => loc.ObjType >= 0);

    public Dictionary<(int ObjType, int Bank), string> BankDigestBaselineHex()
    {
        var result = new Dictionary<(int, int), string>();
        foreach (var (key, hex) in _index.BankDigestBaseline)
        {
            var parts = key.Split(':');
            // TryParse, not Parse: these keys round-trip through a user-visible JSON
            // file - a hand-edited or corrupted cache must skip a bad key, not throw
            // FormatException into a tree-refresh path.
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int t) &&
                int.TryParse(parts[1], out int b))
                result[(t, b)] = hex;
        }
        return result;
    }

    public void SetBankDigestBaseline(int objType, int bank, string hex) =>
        _index.BankDigestBaseline[LocalLibraryIndex.BankKey(objType, bank)] = hex;

    // ── Whole-bank HD-1/EXi type change intent (requirement 4) ──────────────────────
    // Records that a Program bank should be reformatted to `isExi` on the next Commit (because
    // a whole bank of the opposite format was placed into it). ChangesetBuilder turns this into
    // a func 0x7C, and SyncPipeline clears it on success. Persisted, so the intent survives
    // closing/reopening the Librarian before committing.
    public void SetPendingBankTypeChange(int bank, bool isExi)
    {
        lock (_lock) _index.PendingProgramBankTypeChanges[bank] = isExi;
    }

    public bool? PendingBankTypeChange(int bank) =>
        _index.PendingProgramBankTypeChanges.TryGetValue(bank, out var isExi) ? isExi : null;

    public void ClearPendingBankTypeChange(int bank)
    {
        lock (_lock) _index.PendingProgramBankTypeChanges.Remove(bank);
    }

    // Keys are written by LocalLibraryIndex.Key ("type:bank:number") but live in a
    // user-visible JSON file - tolerate a malformed key rather than crashing the
    // caller. The (-1,-1,-1) sentinel never matches a real LibObj type in direct
    // comparisons, and the enumeration properties above filter it out entirely.
    static ObjLoc ParseKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int t) &&
            int.TryParse(parts[1], out int b) &&
            int.TryParse(parts[2], out int n))
            return new ObjLoc(t, b, n);
        return new ObjLoc(-1, -1, -1);
    }

    // Decodes just the name field from a raw body - cheap, in-memory, no disk I/O. Called
    // exactly once per write (the body is already in hand at that moment), never re-derived
    // later, which is what makes GetDisplayName above free of blob reads.
    static string ExtractDisplayName(int objType, byte[] body) => objType switch
    {
        LibObj.Program => ProgramBody.ReadName(body),
        LibObj.Combi   => CombiBody.ReadName(body),
        LibObj.SetList => SetListBody.FromRawBody(0, body)?.Name ?? "",
        _ => "",
    };

    // Same "compute once, using the body already in hand" discipline as ExtractDisplayName
    // above - checks THIS cache's current state (Exists, index-only), which by the time a
    // given write in a batch runs already reflects every earlier write in the same batch.
    bool ComputeHasResolvedDependencies(int objType, byte[] body) => DependencyScanner.HasAllDependencies(this, objType, body);

    // Wire body length alone distinguishes the two Program formats (verified against ~1000
    // real hardware-pulled bodies - see PcgObjectExtractor's class comment); irrelevant for
    // Combi/Set List, which have no such split.
    static bool ComputeIsExi(int objType, byte[] body) =>
        objType != LibObj.Program || body.Length == ProgramFormatConverter.WireSizeExi;

    // Whether this object is merely an INIT/blank placeholder (see InitObjects) - cached at write
    // time exactly like IsExi above, because the free-slot search scans whole banks and a blob read
    // per slot would make every drop pay for 128 of them.
    static bool ComputeIsInit(int objType, byte[] body) => InitObjects.IsInit(objType, body);

    // ── Mutations ─────────────────────────────────────────────────────────────

    // Advances Baseline=Current=hash(freshBody) for every successfully re-dumped object from
    // ONE Pull, and appends exactly ONE "PullBaseline" op-log entry covering all of them.
    //
    // Deliberately batched, not one call per object: a full pull can mean thousands of
    // objects, and OpLog.Append does a full file-open/write/close each time it's called -
    // calling it once per object (this method's earlier shape) meant thousands of separate
    // appends to the SAME file, redundantly reopening it every time. Over an SMB-mounted
    // DataDir (this app's typical dev/test setup), that turned a routine Sync Library into
    // a multi-minute stall - a real, reported regression, not a hypothetical one. The
    // per-object CAS blob write (LocalObjectStore.Put) still happens once per NEW/changed
    // object, which is inherent to a content-addressed store and already cheap for anything
    // unchanged since a prior pull (Put no-ops if the blob already exists).
    public void RecordPullBaselines(
        IEnumerable<(int ObjType, int Bank, int Number, byte Version, byte[] Body)> pulled, DateTime utcNow)
    {
        var targets = new List<OpLogTarget>();
        foreach (var p in pulled)
        {
            string hash = LocalObjectStore.Put(Root, p.Body);
            string key = LocalLibraryIndex.Key(p.ObjType, p.Bank, p.Number);
            lock (_lock)
            {
                _index.Entries.TryGetValue(key, out var prev);
                _index.Entries[key] = new LocalIndexEntry(p.Version, hash, hash, ExtractDisplayName(p.ObjType, p.Body),
                    utcNow, prev?.LastPushedUtc, Conflicted: false,
                    ComputeHasResolvedDependencies(p.ObjType, p.Body), ComputeIsExi(p.ObjType, p.Body),
                    IsInit: ComputeIsInit(p.ObjType, p.Body));
            }
            targets.Add(new OpLogTarget(p.ObjType, p.Bank, p.Number, hash));
            PatchCatalog(p.ObjType, p.Bank, p.Number, p.Version, p.Body);
        }
        if (targets.Count == 0) return;
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, "PullBaseline", targets,
            $"Pulled {targets.Count} object(s)", null, null));
    }

    // A successful push advances baseline forward too, symmetric with RecordPullBaselines -
    // "local now agrees with hardware," just via push instead of pull. Appends exactly ONE
    // PERMANENT "PushCommit" op-log entry (carrying SyncBatchId/SyncedAtUtc - the audit
    // marker requirement 10 asks for, persisting indefinitely) covering every object this
    // push wrote - same batching rationale as RecordPullBaselines: one file-append per
    // object in a large commit would hit the identical SMB-share stall.
    public void RecordPushSuccesses(
        IEnumerable<(int ObjType, int Bank, int Number, byte Version, byte[] Body)> pushed,
        DateTime utcNow, Guid syncBatchId)
    {
        var targets = new List<OpLogTarget>();
        foreach (var p in pushed)
        {
            string hash = LocalObjectStore.Put(Root, p.Body);
            string key = LocalLibraryIndex.Key(p.ObjType, p.Bank, p.Number);
            lock (_lock)
            {
                _index.Entries.TryGetValue(key, out var prev);
                _index.Entries[key] = new LocalIndexEntry(p.Version, hash, hash, ExtractDisplayName(p.ObjType, p.Body),
                    prev?.LastPulledUtc, utcNow, Conflicted: false,
                    ComputeHasResolvedDependencies(p.ObjType, p.Body), ComputeIsExi(p.ObjType, p.Body),
                    IsInit: ComputeIsInit(p.ObjType, p.Body));
            }
            targets.Add(new OpLogTarget(p.ObjType, p.Bank, p.Number, hash));
            PatchCatalog(p.ObjType, p.Bank, p.Number, p.Version, p.Body);
        }
        if (targets.Count == 0) return;
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, "PushCommit", targets,
            $"Pushed {targets.Count} object(s)", syncBatchId, utcNow));
    }

    // A Pull found this object locally dirty AND its bank changed on hardware since
    // baseline - flag it, touch nothing else. The edit and the old baseline are both left
    // exactly as they were until the user resolves the conflict.
    public void MarkConflicted(int objType, int bank, int number)
    {
        string key = LocalLibraryIndex.Key(objType, bank, number);
        lock (_lock)
        {
            if (_index.Entries.TryGetValue(key, out var e))
                _index.Entries[key] = e with { Conflicted = true };
        }
    }

    // Records a local edit touching potentially MULTIPLE objects as one logical action
    // (a Move touches src+dst+every referrer; BatchPlace touches every placement) - one
    // OpLogEntry with multiple Targets, not N separate entries, so the history reads as
    // one line per user action ("Moved Program U-B12 -> U-C01"), not a flood of per-object
    // rows. Baseline is untouched for every target; only Push/Pull ever move it.
    public void RecordEdits(
        IEnumerable<(int ObjType, int Bank, int Number, byte Version, byte[] Body)> writes,
        string opKind, string description, DateTime utcNow)
    {
        var targets = new List<OpLogTarget>();
        foreach (var w in writes)
        {
            string hash = LocalObjectStore.Put(Root, w.Body);
            string key = LocalLibraryIndex.Key(w.ObjType, w.Bank, w.Number);
            lock (_lock)
            {
                _index.Entries.TryGetValue(key, out var existing);
                SlotMutating?.Invoke(w.ObjType, w.Bank, w.Number, existing);
                // No prior entry = a brand-new local-only object (Phase 6: PCG-sourced or a
                // fresh clipboard placement) - it doesn't exist on hardware yet, so it must be
                // dirty until pushed. NoBaselineSentinel ("", never a real 40-char SHA-1 hex
                // hash) guarantees CurrentHash != BaselineHash regardless of body content.
                string baseline = existing?.BaselineHash ?? LocalLibraryIndex.NoBaselineSentinel;
                _index.Entries[key] = new LocalIndexEntry(w.Version, baseline, hash, ExtractDisplayName(w.ObjType, w.Body),
                    existing?.LastPulledUtc, existing?.LastPushedUtc, Conflicted: false,
                    ComputeHasResolvedDependencies(w.ObjType, w.Body), ComputeIsExi(w.ObjType, w.Body),
                    IsInit: ComputeIsInit(w.ObjType, w.Body));
            }
            targets.Add(new OpLogTarget(w.ObjType, w.Bank, w.Number, hash));
            PatchCatalog(w.ObjType, w.Bank, w.Number, w.Version, w.Body);
        }
        if (targets.Count == 0) return;
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, opKind, targets, description, null, null));
    }

    // Single-target convenience wrapper (Rename, a single property edit).
    public void RecordEdit(int objType, int bank, int number, byte version, byte[] newBody,
                            string opKind, string description, DateTime utcNow) =>
        RecordEdits(new[] { (objType, bank, number, version, newBody) }, opKind, description, utcNow);

    // Reverts CurrentHash to BaselineHash and clears any Conflicted flag - the only local
    // undo v1 supports (per-object revert-to-baseline, no linear undo stack). Itself an
    // op-log entry (OpKind "Discard"), so a revert is auditable history, not an erasure.
    // Returns false if there was nothing pending to discard.
    public bool Discard(int objType, int bank, int number, DateTime utcNow)
    {
        string key = LocalLibraryIndex.Key(objType, bank, number);
        LocalIndexEntry e;
        lock (_lock)
        {
            if (!_index.Entries.TryGetValue(key, out e!) || e.CurrentHash == e.BaselineHash) return false;
        }
        SlotMutating?.Invoke(objType, bank, number, e);
        // A single blob read here is fine - Discard is a one-object, user-initiated action,
        // not a per-slot bulk operation (unlike the tree-building path this whole cache of
        // DisplayName exists to avoid).
        var baselineBody = LocalObjectStore.TryGet(Root, e.BaselineHash);
        string baselineName = baselineBody != null ? ExtractDisplayName(objType, baselineBody) : e.DisplayName;
        bool baselineHasResolvedDeps = baselineBody != null ? ComputeHasResolvedDependencies(objType, baselineBody) : e.HasResolvedDependencies;
        bool baselineIsExi = baselineBody != null ? ComputeIsExi(objType, baselineBody) : e.IsExi;
        bool? baselineIsInit = baselineBody != null ? ComputeIsInit(objType, baselineBody) : e.IsInit;
        lock (_lock)
        {
            _index.Entries[key] = e with
            {
                CurrentHash = e.BaselineHash, DisplayName = baselineName, Conflicted = false,
                HasResolvedDependencies = baselineHasResolvedDeps, IsExi = baselineIsExi,
                IsInit = baselineIsInit,
            };
        }
        if (baselineBody != null) PatchCatalog(objType, bank, number, e.Version, baselineBody);
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, "Discard",
            new[] { new OpLogTarget(objType, bank, number, e.BaselineHash) },
            $"Discarded {new ObjLoc(objType, bank, number).Label()}", null, null));
        return true;
    }

    // Removes an object's index entry entirely - the final step of a committed deletion
    // (requirement 2): once the slot has been erased/INIT'd + Stored on hardware (or, for a
    // local-only object never on hardware, simply abandoned), there's nothing left to track
    // locally, so the row disappears from the tree instead of lingering faded. Also drops it
    // from the memoized referrer catalog so a later Move this session can't find a deleted
    // Combi/Set List as a referrer. The CAS blob is left in the store (harmless debris, same
    // "an unreferenced blob is accepted" spirit as everywhere else). Itself an op-log entry,
    // same auditable-history convention as Discard/SetPendingDelete. False if there was no entry.
    public bool RemoveObject(int objType, int bank, int number, DateTime utcNow)
    {
        string key = LocalLibraryIndex.Key(objType, bank, number);
        LocalIndexEntry e;
        lock (_lock)
        {
            if (!_index.Entries.TryGetValue(key, out e!)) return false;
            SlotMutating?.Invoke(objType, bank, number, e);
            _index.Entries.Remove(key);
            if (_catalog != null)
            {
                if (objType == LibObj.Combi) _catalog.Combis.Remove((bank, number));
                else if (objType == LibObj.SetList) _catalog.Setlists.Remove(number);
            }
        }
        // Tombstone the target (not e.CurrentHash) so the op-log fold REMOVES the slot on
        // recovery instead of resurrecting it - see LocalLibraryIndex.DeletedTombstone.
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, "Delete",
            new[] { new OpLogTarget(objType, bank, number, LocalLibraryIndex.DeletedTombstone) },
            $"Deleted {new ObjLoc(objType, bank, number).Label()}", null, null));
        return true;
    }

    // Puts a set of slots back exactly as LibrarianUndo captured them before an action ran - the
    // rollback half of the Librarian's Ctrl+Z (Core/LocalLibrary/LibrarianUndo.cs). A snapshot
    // whose Entry is null means the slot didn't exist then, so it's removed again.
    //
    // Restores the captured LocalIndexEntry record wholesale (it's immutable, so BaselineHash/
    // PendingDelete/Conflicted/Version/DisplayName all come back exactly), keeps the memoized
    // referrer catalog correct in BOTH directions (re-add a restored Combi/Set List, drop one
    // that's going away again), and appends exactly ONE op-log entry for the whole rollback -
    // a DeletedTombstone target for each slot that goes back to nonexistent, so index.json stays
    // a valid fold of oplog.jsonl and the rollback is auditable history rather than an erasure,
    // same convention Discard/Delete already follow. The catalog's body re-read is confined to
    // Combi/Set List (the only referrer types PatchCatalog stores) so undoing a whole Program
    // bank never turns into 128 blob reads over an SMB-mounted DataDir.
    //
    // Does NOT raise SlotMutating: an undo is never itself an undoable step (the recorder also
    // suppresses capture around this call).
    public void RestoreSlots(IReadOnlyList<LocalSlotSnapshot> slots, string description, DateTime utcNow)
    {
        var targets = new List<OpLogTarget>();
        foreach (var s in slots)
        {
            string key = LocalLibraryIndex.Key(s.ObjType, s.Bank, s.Number);
            if (s.Entry is { } entry)
            {
                lock (_lock) _index.Entries[key] = entry;
                if (s.ObjType is LibObj.Combi or LibObj.SetList &&
                    LocalObjectStore.TryGet(Root, entry.CurrentHash) is { } body)
                    PatchCatalog(s.ObjType, s.Bank, s.Number, entry.Version, body);
                targets.Add(new OpLogTarget(s.ObjType, s.Bank, s.Number, entry.CurrentHash));
            }
            else
            {
                lock (_lock)
                {
                    _index.Entries.Remove(key);
                    if (_catalog != null)
                    {
                        if (s.ObjType == LibObj.Combi) _catalog.Combis.Remove((s.Bank, s.Number));
                        else if (s.ObjType == LibObj.SetList) _catalog.Setlists.Remove(s.Number);
                    }
                }
                targets.Add(new OpLogTarget(s.ObjType, s.Bank, s.Number, LocalLibraryIndex.DeletedTombstone));
            }
        }
        if (targets.Count == 0) return;
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, "Undo", targets, description, null, null));
        Save();
    }

    // Re-derives the cached dependency-completeness bit (HasResolvedDependencies) for slots whose
    // bodies the caller ALREADY holds - no blob reads here at all.
    //
    // That bit is computed once, at write time, and never revisited; it drives the Local tree's
    // red/green dependency dot. So when the RULES change - as they did when references into the
    // read-only ROM Program banks stopped counting as dependencies (see
    // ObjectReferenceWalker.IsAlwaysAvailable) - every object already in the library keeps
    // displaying a verdict reached under the old rules. Everything computed fresh (the push's
    // referential gate, the dependency panels) corrects itself immediately; only this cached bit
    // needs sweeping, or a Combi referencing GM keeps its red dot forever and the fix looks like
    // it didn't work.
    //
    // Returns how many entries actually changed, and persists only if something did.
    public int RecomputeResolvedDependencies(IEnumerable<(int ObjType, int Bank, int Number, byte[] Body)> objects)
    {
        int changed = 0;
        foreach (var (objType, bank, number, body) in objects)
        {
            string key = LocalLibraryIndex.Key(objType, bank, number);
            lock (_lock)
            {
                if (!_index.Entries.TryGetValue(key, out var e)) continue;
                bool resolved = ComputeHasResolvedDependencies(objType, body);
                if (resolved == e.HasResolvedDependencies) continue;
                _index.Entries[key] = e with { HasResolvedDependencies = resolved };
                changed++;
            }
        }
        if (changed > 0) Save();
        return changed;
    }

    // Cached at write time, same discipline as HasResolvedDependencies/IsExi above - index-only,
    // no blob read, safe to call once per node on every tree refresh.
    public bool IsPendingDelete(int objType, int bank, int number) =>
        _index.Entries.TryGetValue(LocalLibraryIndex.Key(objType, bank, number), out var e) && e.PendingDelete;

    // Local-only "marked for removal" flag (see LocalIndexEntry's own comment on why a fresh
    // Pull clears it with no extra code here). Does NOT touch CurrentHash/BaselineHash - it's
    // orthogonal to whatever edit state the object is in; LocalEditOps.SetPendingDelete is what
    // pairs this with Discard for the UI's "Delete" action. Itself an op-log entry, same
    // auditable-history convention as Discard. Returns false if the flag is already at `value`.
    public bool SetPendingDelete(int objType, int bank, int number, bool value, DateTime utcNow)
    {
        string key = LocalLibraryIndex.Key(objType, bank, number);
        LocalIndexEntry e;
        lock (_lock)
        {
            if (!_index.Entries.TryGetValue(key, out e!) || e.PendingDelete == value) return false;
            SlotMutating?.Invoke(objType, bank, number, e);
            _index.Entries[key] = e with { PendingDelete = value };
        }
        OpLog.Append(Root, new OpLogEntry(Guid.NewGuid(), utcNow, value ? "PendingDelete" : "RestoreFromPendingDelete",
            new[] { new OpLogTarget(objType, bank, number, e.CurrentHash) },
            $"{(value ? "Marked" : "Restored")} {new ObjLoc(objType, bank, number).Label()} {(value ? "for deletion (pending Commit)" : "from pending deletion")}",
            null, null));
        return true;
    }

    // Populates Core/LibrarianModel.cs's (UNMODIFIED) LibraryCatalog from this cache's
    // CURRENT bodies - the reuse point that lets Phase 2's LocalEditOps call
    // Librarian.PlanMove/BatchLibrarian.PlanBatchMove exactly as they are today, just fed
    // local state instead of a fresh hardware dump. Only Combi/SetList populate the
    // catalog (Programs are never referrers - same scope LibraryCatalog has always had).
    //
    // Memoized, not rebuilt from disk every call. Move/BatchPlace (so every PCG drag-drop
    // and every Local-pane move) used to call this fresh each time, which read the FULL
    // body of every Combi + Set List in the library from the CAS blob store just to run the
    // orphan-gate referrer check for ONE destination slot - over an SMB-mounted DataDir
    // (this app's typical dev/test setup) that's ~1000-1900 network round-trips per drop,
    // a real ~15s stall on every single placement. PatchCatalog below keeps this in sync
    // in-place from bodies already in hand at write time, so only the FIRST build after this
    // cache is constructed pays the full-disk-read cost; every edit after that is O(1).
    //
    // That first build runs on a thread-pool thread (Task.Run below), not the caller's -
    // opening the Librarian window used to pay this cost synchronously on the UI thread
    // (a real 10-20s freeze on a large library) just to have it ready before the first
    // drag-drop. Callers that can await do so via this method, kicked off as soon as the
    // window opens; LocalEditOps' Move/BatchPlace can't (they're synchronous), so they go
    // through the blocking BuildCatalog() wrapper below instead - a no-op if the background
    // build already finished, otherwise a wait for whatever's left of it, which is still
    // strictly better than redoing the full disk read on every call site.
    public Task<LibraryCatalog> BuildCatalogAsync()
    {
        lock (_lock)
        {
            if (_catalog != null) return Task.FromResult(_catalog);
            return _catalogBuildTask ??= Task.Run(BuildCatalogInBackground);
        }
    }

    public LibraryCatalog BuildCatalog() => BuildCatalogAsync().GetAwaiter().GetResult();

    LibraryCatalog BuildCatalogInBackground()
    {
        // Snapshot under the lock (fast, in-memory only) so this can't race a concurrent
        // _index.Entries write from RecordEdits/RecordPullBaselines/etc. on the UI thread -
        // a `foreach` over a Dictionary racing a write on another thread throws "Collection
        // was modified." The slow part (reading each blob's full body off the CAS store)
        // then runs unlocked against the snapshot, which is plain immutable data by this
        // point, so it doesn't block the UI thread's own cache access at all.
        var snapshot = new List<(ObjLoc Loc, byte Version, string Hash)>();
        lock (_lock)
        {
            foreach (var kv in _index.Entries)
            {
                var loc = ParseKey(kv.Key);
                if (loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) continue;
                snapshot.Add((loc, kv.Value.Version, kv.Value.CurrentHash));
            }
        }

        var cat = new LibraryCatalog();
        foreach (var (loc, version, hash) in snapshot)
        {
            var body = LocalObjectStore.TryGet(Root, hash);
            if (body == null) continue;
            var dump = new ObjectDump(loc.ObjType, loc.Bank, loc.Number, version, body);
            if (loc.ObjType == LibObj.Combi) cat.AddCombi(dump); else cat.AddSetlist(dump);
        }

        // Replay any edits that landed while the loop above was still reading blobs (its
        // snapshot predates them, so it never saw them) before publishing _catalog - see
        // PatchCatalog's own comment.
        lock (_lock)
        {
            foreach (var (objType, bank, number, version, body) in _pendingCatalogPatches)
            {
                var dump = new ObjectDump(objType, bank, number, version, body);
                if (objType == LibObj.Combi) cat.AddCombi(dump); else cat.AddSetlist(dump);
            }
            _pendingCatalogPatches.Clear();
            _catalog = cat;
        }
        return cat;
    }

    // Keeps a not-yet-requested-this-session catalog cheap (still null, so still skipped)
    // and an already-built one accurate - called from every mutation path below whenever a
    // Combi/SetList body changes, using the body already in hand (zero extra disk I/O).
    // AddCombi/AddSetlist already no-op for the wrong ObjType; the guard here just skips the
    // ObjectDump allocation for Program writes, which can never be referrers.
    void PatchCatalog(int objType, int bank, int number, byte version, byte[] body)
    {
        if (objType != LibObj.Combi && objType != LibObj.SetList) return;
        lock (_lock)
        {
            if (_catalog != null)
            {
                var dump = new ObjectDump(objType, bank, number, version, body);
                if (objType == LibObj.Combi) _catalog.AddCombi(dump); else _catalog.AddSetlist(dump);
            }
            else if (_catalogBuildTask != null)
            {
                // The background build's disk-read loop is still running off a snapshot
                // taken before this edit - queue it so BuildCatalogInBackground can replay it
                // onto the finished catalog instead of silently losing it.
                _pendingCatalogPatches.Add((objType, bank, number, version, body));
            }
        }
    }
}
