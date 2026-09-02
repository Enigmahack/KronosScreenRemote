namespace KronosScreenRemote;

using System.IO;
using System.Text.Json;

// One entry as persisted to disk - a flat, JSON-friendly mirror of MergeEntry (which stays a
// plain mutable class for the algorithm's own convenience; this record is what actually
// round-trips through System.Text.Json without fighting required/init members mid-mutation).
sealed record MergeEntrySnapshot(
    string ContentHash, int ObjType, byte[] Body, byte Version, string DisplayName, bool IsTopLevelPull,
    List<MergeOrigin> Origins, List<string> ReferencedBy, List<MergeRefSite> RefSites);

// Full on-disk state: the staged entries themselves, plus which content hashes have already
// been placed into Keyboard Library THIS batch and where (PlacedAddresses) - needed too, not
// just the still-pending entries, so that if the app crashes mid-batch (some items placed,
// others not yet), resuming after restart still correctly resolves a not-yet-placed Combi's
// reference to a dependency that WAS placed before the crash (see MergeCache.
// ResolveReferencesForPlacement).
sealed record MergeCacheSnapshot(List<MergeEntrySnapshot> Entries, Dictionary<string, ObjLoc> PlacedAddresses);

// Strategy for how (or whether) the Merge Window's staging cache survives past this process -
// selected once, at MergeCache construction, from AppSettings.MergeBehavior. New behaviors
// (e.g. a size-capped rolling log) plug in here without MergeCache itself changing (OCP).
interface IMergeCachePersistence
{
    MergeCacheSnapshot? Load();
    void Save(MergeCacheSnapshot snapshot);
    void Clear();
}

// MergeCacheBehavior.TemporaryMemory - never touches disk; every call is a no-op, so a fresh
// MergeCache always starts (and stays) empty across restarts.
sealed class InMemoryMergeCachePersistence : IMergeCachePersistence
{
    public MergeCacheSnapshot? Load() => null;
    public void Save(MergeCacheSnapshot snapshot) { }
    public void Clear() { }
}

// MergeCacheBehavior.LocalStorage - one JSON snapshot file, fully rewritten on every
// mutation. Crash recovery means saves can't wait for a clean shutdown (a crash skips that
// code entirely), so this deliberately isn't an incremental/CAS-style store like Local
// Library's own - a rewrite-on-change of what's realistically a small, actively-curated
// working set (tens of objects, not thousands) is proportionate to what the Merge Window
// actually is: temporary staging, not a second permanent library.
sealed class FileMergeCachePersistence : IMergeCachePersistence
{
    readonly string _path;

    public FileMergeCachePersistence(string path) => _path = path;

    public MergeCacheSnapshot? Load()
    {
        try
        {
            // Walks AtomicFile's candidate list rather than just _path: a crash between
            // Save's two renames leaves the complete new snapshot in .tmp and the previous
            // one in .bak, and losing a whole batch of staged merge work to that window is
            // exactly what this persistence mode exists to prevent.
            foreach (var candidate in AtomicFile.CandidatesForRead(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<MergeCacheSnapshot>(File.ReadAllText(candidate)) is { } snap)
                        return snap;
                }
                catch (Exception ex) { AppLog.Warn($"[merge-cache] snapshot load failed for '{candidate}': {ex.Message}"); }
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[merge-cache] snapshot load failed: {ex.Message}");
            return null;
        }
    }

    public void Save(MergeCacheSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) { AppLog.Warn($"[merge-cache] snapshot save failed: {ex.Message}"); }
    }

    public void Clear()
    {
        // Deletes the swap siblings too - a stale .tmp/.bak left behind would be resurrected
        // by Load's candidate walk as if it were live staging state.
        try { foreach (var f in new[] { _path, _path + ".tmp", _path + ".bak" }) File.Delete(f); }
        catch (Exception ex) { AppLog.Warn($"[merge-cache] snapshot clear failed: {ex.Message}"); }
    }
}
