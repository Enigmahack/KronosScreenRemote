namespace KronosScreenRemote;

using System.IO;
using System.Text.Json;

// One object touched by a logged action — TYPE/BANK/NUMBER plus the resulting body's hash
// (not just a description), so the log is machine-foldable (LocalLibraryIndex.RebuildCurrentFromOpLog),
// not merely a human-readable transcript.
sealed record OpLogTarget(int ObjType, int Bank, int Number, string ResultHash);

// One logged action. Description is the human-readable history line ("Renamed Combi
// I-A:003"); Targets is what a fold actually replays. SyncBatchId/SyncedAtUtc are set only
// on "PushCommit" entries — the permanent audit marker that a batch of edits was actually
// written to hardware (distinct from every other OpKind, which never touches hardware).
sealed record OpLogEntry(
    Guid Id, DateTime TimestampUtc, string OpKind, IReadOnlyList<OpLogTarget> Targets,
    string Description, Guid? SyncBatchId, DateTime? SyncedAtUtc);

// Append-only log at {root}/oplog.jsonl — one JSON object per line (File.AppendAllText,
// never a read-modify-write of the whole file). This is the AUTHORITATIVE history;
// LocalLibraryIndex's CurrentHash is a cached fold of it, always rebuildable if lost.
// A "Discard" entry (target hash = the object's baseline hash) is itself logged here —
// a revert is an auditable event, not an erasure.
static class OpLog
{
    static readonly object _lock = new();

    static string PathFor(string root) => Path.Combine(root, "oplog.jsonl");

    public static void Append(string root, OpLogEntry entry)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(PathFor(root), JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch (Exception ex) { AppLog.Warn($"[local-library] oplog append failed: {ex.Message}"); }
        }
    }

    // Wipes the audit trail on user request ("Clear History" in the Librarian UI). Doesn't
    // touch index.json/the CAS blob store — only the human-readable log of how the current
    // state was reached, which after this can no longer be replayed if index.json is ever
    // lost or corrupted (RebuildCurrentFromOpLog's fallback stops working retroactively).
    public static void ClearAll(string root)
    {
        lock (_lock)
        {
            try { File.Delete(PathFor(root)); }
            catch (Exception ex) { AppLog.Warn($"[local-library] oplog clear failed: {ex.Message}"); }
        }
    }

    public static List<OpLogEntry> ReadAll(string root)
    {
        lock (_lock)
        {
            var list = new List<OpLogEntry>();
            string path = PathFor(root);
            if (!File.Exists(path)) return list;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<OpLogEntry>(line);
                    if (entry != null) list.Add(entry);
                }
            }
            catch (Exception ex) { AppLog.Warn($"[local-library] oplog read failed: {ex.Message}"); }
            return list;
        }
    }
}
