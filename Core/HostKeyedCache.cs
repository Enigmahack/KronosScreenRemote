using System.IO;
using System.Text.Json;

namespace KronosScreenRemote;

// One JSON file, one lock, atomic whole-file read-modify-write. The single owner of the
// "path + lock + (de)serialize + swallow-and-log" idiom that used to be hand-rolled once
// per cache in Storage.cs (and, unlocked, in RawKeyMap) — so locking/atomicity is correct
// in exactly one place instead of five subtly-different copies.
//
// Serialization defaults to System.Text.Json but is injectable: RawKeyMap passes its own
// JsonNode (de)serializers to keep its legacy on-disk shape and per-row skip-bad-entry
// resilience byte-for-byte unchanged, while still borrowing the lock + I/O plumbing.
sealed class JsonFileCache<T> where T : class
{
    readonly Func<string>     _pathFn;      // lazy: DataDir resolves from ProcessPath at call time
    readonly string           _tag;         // log prefix, e.g. "setlist-cache"
    readonly Func<T, string>  _serialize;
    readonly Func<string, T?> _deserialize;
    readonly object           _lock = new();

    public JsonFileCache(Func<string> pathFn, string tag,
        Func<T, string>? serialize = null, Func<string, T?>? deserialize = null)
    {
        _pathFn      = pathFn;
        _tag         = tag;
        _serialize   = serialize   ?? (v => JsonSerializer.Serialize(v));
        _deserialize = deserialize ?? (s => JsonSerializer.Deserialize<T>(s));
    }

    // Whole-file read. Returns null when the file is absent, empty-valued, or unreadable —
    // callers supply their own empty (?? new()). An unlocked read racing a Write hits a
    // sharing violation, so the lock here is what keeps a load from silently reporting empty.
    public T? Read()
    {
        lock (_lock)
        {
            try
            {
                var path = _pathFn();
                if (!File.Exists(path)) return null;
                return _deserialize(File.ReadAllText(path));
            }
            catch (Exception ex) { AppLog.Warn($"[{_tag}] load failed: {ex.Message}"); }
            return null;
        }
    }

    // Whole-file overwrite.
    public void Write(T value)
    {
        lock (_lock)
        {
            try { File.WriteAllText(_pathFn(), _serialize(value)); }
            catch (Exception ex) { AppLog.Warn($"[{_tag}] save failed: {ex.Message}"); }
        }
    }

    // Atomic read-modify-write under the lock: read the current whole-file value (or a fresh
    // fallback() when the file is absent/corrupt — same "?? new()" recovery the old caches
    // had, so a garbled file gets replaced by current data rather than throwing on save),
    // hand it to mutate, write the result back. Used by HostKeyedCache.Save.
    public void Mutate(Func<T> fallback, Func<T, T> mutate)
    {
        lock (_lock)
        {
            try
            {
                var path = _pathFn();
                T current = (File.Exists(path) ? _deserialize(File.ReadAllText(path)) : null) ?? fallback();
                File.WriteAllText(path, _serialize(mutate(current)));
            }
            catch (Exception ex) { AppLog.Warn($"[{_tag}] save failed: {ex.Message}"); }
        }
    }
}

// A per-host cache: the file holds Dictionary<host, TValue>, and each host's value is loaded
// and stored independently. Collapses the four copy-pasted "deserialize Dictionary<host,…> →
// mutate → serialize" caches in Storage into a one-line declaration each; the atomic RMW on
// Save is inherited from JsonFileCache.Mutate.
sealed class HostKeyedCache<TValue> where TValue : class
{
    readonly JsonFileCache<Dictionary<string, TValue>> _file;

    public HostKeyedCache(Func<string> pathFn, string tag)
        => _file = new JsonFileCache<Dictionary<string, TValue>>(pathFn, tag);

    // Null when this host has no stored entry — caller supplies its own empty (?? new()).
    public TValue? Load(string host)
    {
        var all = _file.Read();
        return all != null && all.TryGetValue(host, out var v) && v != null ? v : null;
    }

    public void Save(string host, TValue value)
        => _file.Mutate(() => new(), all => { all[host] = value; return all; });
}
