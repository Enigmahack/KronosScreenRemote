using System.IO;
using System.Text.Json;

namespace KronosScreenRemote;

// One JSON file, one lock, atomic whole-file read-modify-write.
// Serialization defaults to System.Text.Json but is injectable, for callers that need to
// preserve a specific on-disk shape while still getting the lock + I/O plumbing.
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

    // Whole-file read. Returns null when the file is absent, empty-valued, or unreadable -
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

    public void Write(T value)
    {
        lock (_lock)
        {
            try { File.WriteAllText(_pathFn(), _serialize(value)); }
            catch (Exception ex) { AppLog.Warn($"[{_tag}] save failed: {ex.Message}"); }
        }
    }

    // Atomic read-modify-write under the lock: reads the current value (or fallback() if the
    // file is absent/corrupt, so a garbled file gets replaced by current data instead of
    // throwing on save), hands it to mutate, writes the result back.
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
// and stored independently.
sealed class HostKeyedCache<TValue> where TValue : class
{
    readonly JsonFileCache<Dictionary<string, TValue>> _file;

    public HostKeyedCache(Func<string> pathFn, string tag)
        => _file = new JsonFileCache<Dictionary<string, TValue>>(pathFn, tag);

    // Null when this host has no stored entry - caller supplies its own empty (?? new()).
    public TValue? Load(string host)
    {
        var all = _file.Read();
        return all != null && all.TryGetValue(host, out var v) && v != null ? v : null;
    }

    public void Save(string host, TValue value)
        => _file.Mutate(() => new(), all => { all[host] = value; return all; });
}
