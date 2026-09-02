namespace KronosScreenRemote;

using System.IO;

// One tracked object's pointers into the CAS blob store, keyed "type:bank:number".
// CurrentHash == BaselineHash means clean/not-dirty. Conflicted is set by a Pull that
// found the object locally dirty AND its bank changed on hardware since baseline - the
// edit and the old baseline are both left untouched until the user resolves it.
//
// DisplayName is decoded ONCE, whenever CurrentHash is set (the body is already in memory
// at that moment - see LocalLibraryCache's write paths), and cached here specifically so
// the UI's tree-building never needs to re-read a blob from disk just to show a label.
// Reading every populated object's full body from the CAS store on every tree refresh was
// a real bug (see the fix's commit context): over an SMB-mounted DataDir, thousands of
// small synchronous file reads to extract a 24-byte name each turned "open the window"
// into a ~30-second freeze.
//
// HasResolvedDependencies follows the exact same discipline, for the same reason: computed
// ONCE at write time (via DependencyScanner.HasAllDependencies, using the body already in
// hand) rather than re-scanned per tree refresh - Combi/Set List's red/green dependency dot
// (Views/LibrarianShellWindow.xaml) reads this cached bit instead of re-walking a body's
// references and re-checking each one against the index on every RefreshTree. Defaults to
// true (no red dot) so existing/deserialized entries and Program rows (which are never
// referrers, so vacuously "have no missing dependencies") never need a value supplied.
//
// IsExi (Program only) is the same story again: which wire format a Program's body is in -
// EXi (4960 bytes) vs HD-1 (3706 bytes, ProgramFormatConverter.WireSizeExi/WireSizeHd1) - is
// derivable from the body's own length alone (verified against ~1000 real hardware-pulled
// bodies, see PcgObjectExtractor's class comment), so it's captured once at write time
// instead of the Keyboard Library tree reading a body just to label a Program bank "(EXi)"/
// "(HD-1)" the way the PCG pane's own BankNodeLabel already does for a loaded .pcg file.
// Meaningless for Combi/Set List - defaults to true (EXi) there, never displayed.
// PendingDelete (local-only "marked for removal" flag - see LocalLibraryCache.SetPendingDelete)
// defaults to false so every existing/deserialized entry is unaffected. A fresh Pull always
// constructs a brand-new LocalIndexEntry from scratch (RecordPullBaselines/push-baseline
// advance), so PendingDelete resets to false there with no extra code - matching Delete's own
// tooltip claim that a Pull restores a locally-deleted object.
// IsInit (see InitObjects) is cached at write time for the same reason IsExi is - the free-slot
// search scans whole banks (128 slots x 21 banks) and must not read a blob per slot. It is
// NULLABLE on purpose: null means "written by a build before this field existed", not "not init".
// LocalLibraryCache.IsInitSlot degrades those to the name-only check against the cached
// DisplayName, which is EXACT for Programs and catches the named case for Combis - so an already-
// synced library gets init-aware free slots immediately, with no re-Pull and no migration sweep.
//
// HasSampleDependency (Program/Drum Kit/Wave Sequence only) is the same "compute once at write
// time" story again, backing the tree's yellow sample-dependency dot (Views/
// LibrarianShellWindow.xaml, ObjectTreeNode.HasSampleDependency) via SampleReferenceWalker.Walk.
// Defaults to false (no dot) so every existing/deserialized entry needs no value supplied - a
// library synced before this field existed simply shows no dot until the object is next pulled/
// pushed/edited (or a Force Full Sync re-pulls everything), same "no migration sweep" tradeoff
// IsInit above already accepts, deliberately NOT given the same eager RecomputeXxx sweep
// HasResolvedDependencies has: that sweep is scoped to the (small) dirty Combi/Set List set and
// reuses bodies the catalog build already read for an unrelated reason: this bit instead applies
// to potentially every Program/Drum Kit/Wave Sequence in the whole library, which no existing
// pass already reads the body of - adding one would risk the exact whole-library blob-read stall
// this file's own header comment describes.
sealed record LocalIndexEntry(
    byte Version, string BaselineHash, string CurrentHash, string DisplayName,
    DateTime? LastPulledUtc, DateTime? LastPushedUtc, bool Conflicted,
    bool HasResolvedDependencies = true, bool IsExi = true, bool PendingDelete = false,
    bool? IsInit = null, bool HasSampleDependency = false);

// Persisted at {root}/index.json. This is a CACHE, not a second source of truth:
// CurrentHash is exactly "fold the op-log forward from Baseline" (RebuildCurrentFromOpLog
// reproduces it), so a lost/corrupted index.json is recoverable from oplog.jsonl alone.
// Deliberately NOT host-keyed (unlike every Storage.cs cache) - the keyboard library is a
// single global store; the Kronos's IP can change but the objects don't.
sealed class LocalLibraryIndex
{
    static readonly object FileCachesLock = new();
    static readonly Dictionary<string, JsonFileCache<Dto>> FileCaches = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LocalIndexEntry> Entries { get; } = new();       // key: "type:bank:number"
    public Dictionary<string, string> BankDigestBaseline { get; } = new();     // key: "type:bank", value: hex SHA-1

    // Program banks the user has staged a whole-bank HD-1/EXi type change for (requirement 4) -
    // key: program bank, value: target IsExi. Set when a whole bank of the opposite format is
    // placed into a destination bank; consumed by ChangesetBuilder (which emits a func 0x7C) on
    // the next Commit and cleared on success. Persisted so the intent survives closing/reopening
    // the Librarian before committing.
    public Dictionary<int, bool> PendingProgramBankTypeChanges { get; } = new();

    public static string Key(int objType, int bank, int number) => $"{objType}:{bank}:{number}";
    public static string BankKey(int objType, int bank) => $"{objType}:{bank}";

    // Marks "no hardware baseline exists" for a brand-new local-only object - distinct
    // from any real SHA-1 hex hash (always 40 chars), so IsDirty is unconditionally true
    // until an actual Push establishes a real baseline.
    public const string NoBaselineSentinel = "";

    // An OpLogTarget.ResultHash of this value means "this object was DELETED" (requirement 2's
    // committed deletion - see LocalLibraryCache.RemoveObject). Distinct from any real 40-char
    // SHA-1 hex hash, so the op-log fold (RebuildCurrentFromOpLog) can REMOVE the slot instead of
    // resurrecting it from its last real hash - without this, recovering the index from the log
    // alone would bring every deleted object back.
    public const string DeletedTombstone = "<deleted>";

    sealed record Dto(Dictionary<string, LocalIndexEntry> Entries, Dictionary<string, string> BankDigestBaseline,
        Dictionary<int, bool>? PendingProgramBankTypeChanges = null);

    static string PathFor(string root) => Path.Combine(root, "index.json");

    static JsonFileCache<Dto> FileFor(string root)
    {
        string path = Path.GetFullPath(PathFor(root));
        lock (FileCachesLock)
        {
            if (!FileCaches.TryGetValue(path, out var file))
            {
                file = new JsonFileCache<Dto>(() => path, "local-library-index");
                FileCaches[path] = file;
            }
            return file;
        }
    }

    public static LocalLibraryIndex Load(string root)
    {
        var idx = new LocalLibraryIndex();
        var (state, dto) = FileFor(root).ReadState();
        // An index.json that exists but will not parse must NOT degrade to an empty library.
        // It used to: Read() returned null for "absent" and "corrupt" alike, the empty index
        // loaded silently, and the session's first Save() wrote that emptiness back over the
        // one file that still held the user's slot pointers. Refusing to save is what makes
        // the corruption recoverable by hand (index.json.bak is right beside it) instead of
        // final.
        if (state == JsonCacheRead.Unreadable)
        {
            AppLog.Error($"[local-library-index] index at '{PathFor(root)}' is unreadable - " +
                         "starting empty and REFUSING to save over it. Restore index.json.bak to recover.");
            idx.LoadFailed = true;
            return idx;
        }
        if (dto == null) return idx;
        foreach (var kv in dto.Entries) idx.Entries[kv.Key] = kv.Value;
        foreach (var kv in dto.BankDigestBaseline) idx.BankDigestBaseline[kv.Key] = kv.Value;
        if (dto.PendingProgramBankTypeChanges != null)
            foreach (var kv in dto.PendingProgramBankTypeChanges) idx.PendingProgramBankTypeChanges[kv.Key] = kv.Value;
        return idx;
    }

    // Set when Load found an unreadable index. Latches for the lifetime of this instance:
    // nothing in a session that started blind can be trusted to describe the full library, so
    // no write from it may reach disk.
    public bool LoadFailed;

    public void Save(string root)
    {
        if (LoadFailed)
        {
            AppLog.Warn("[local-library-index] save suppressed - this index never loaded cleanly.");
            return;
        }
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            AppLog.Warn($"[local-library-index] save failed: {ex.Message}");
            return;
        }
        FileFor(root).Write(new Dto(Entries, BankDigestBaseline, PendingProgramBankTypeChanges));
    }

    // Replays the op-log, last-writer-wins per (type,bank,number) by timestamp order (the
    // log is append-only, so file order IS chronological order) - the literal "fold" design
    // point (b) describes. Only reconstructs CurrentHash; baselines only ever move via a
    // Pull/Push recording call, never via a fold.
    public static Dictionary<string, string> RebuildCurrentFromOpLog(IEnumerable<OpLogEntry> entries)
    {
        var current = new Dictionary<string, string>();
        foreach (var entry in entries)
            foreach (var t in entry.Targets)
            {
                var key = Key(t.ObjType, t.Bank, t.Number);
                if (t.ResultHash == DeletedTombstone) current.Remove(key);   // a committed deletion removes the slot
                else current[key] = t.ResultHash;
            }
        return current;
    }
}
