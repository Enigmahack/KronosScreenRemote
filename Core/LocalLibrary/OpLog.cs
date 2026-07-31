namespace KronosScreenRemote;

using System.IO;
using System.Text.Json;

// One object touched by a logged action - TYPE/BANK/NUMBER plus the resulting body's hash
// (not just a description), so the log is machine-foldable (LocalLibraryIndex.RebuildCurrentFromOpLog),
// not merely a human-readable transcript.
sealed record OpLogTarget(int ObjType, int Bank, int Number, string ResultHash);

// One logged action. Description is the human-readable history line ("Renamed Combi
// I-A:003"); Targets is what a fold actually replays. SyncBatchId/SyncedAtUtc are set only
// on "PushCommit" entries - the permanent audit marker that a batch of edits was actually
// written to hardware (distinct from every other OpKind, which never touches hardware).
sealed record OpLogEntry(
    Guid Id, DateTime TimestampUtc, string OpKind, IReadOnlyList<OpLogTarget> Targets,
    string Description, Guid? SyncBatchId, DateTime? SyncedAtUtc);

// Append-only log at {root}/oplog.jsonl - one JSON object per line (File.AppendAllText,
// never a read-modify-write of the whole file). This is the AUTHORITATIVE history;
// LocalLibraryIndex's CurrentHash is a cached fold of it, always rebuildable if lost.
// A "Discard" entry (target hash = the object's baseline hash) is itself logged here -
// a revert is an auditable event, not an erasure.
static class OpLog
{
    static readonly object _lock = new();

    // In-memory mirror of the log, per root, for the UI history panel ONLY. The Librarian
    // refreshes its history after EVERY local edit (LibrarianShellViewModel.NotifyLocalEditMade);
    // re-reading a growing oplog.jsonl over a possibly SMB-mounted DataDir on each edit was a
    // UI-thread stall that grew with the log. Seeded lazily from disk on the first ReadForDisplay
    // and kept in step by Append/ClearAll thereafter (OpLog.Append is the sole writer). NOT used
    // by ReadAll: the fold/recovery path (LocalLibraryIndex.RebuildCurrentFromOpLog, the
    // DataSafety crash-recovery tests) must stay disk-authoritative - those prove oplog.jsonl
    // itself carries the state, so they must actually read the file, never this mirror.
    static readonly Dictionary<string, List<OpLogEntry>> _displayMirror = new();

    static string PathFor(string root) => Path.Combine(root, "oplog.jsonl");

    public static void Append(string root, OpLogEntry entry)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(PathFor(root), JsonSerializer.Serialize(entry) + Environment.NewLine);
                // Keep the display mirror in step, but only if it's already seeded - an unseeded
                // root loads the full file (this entry included) on its first ReadForDisplay.
                if (_displayMirror.TryGetValue(root, out var mirror)) mirror.Add(entry);
            }
            catch (Exception ex) { AppLog.Warn($"[local-library] oplog append failed: {ex.Message}"); }
        }
    }

    // Wipes the audit trail on user request ("Clear History" in the Librarian UI). Doesn't
    // touch index.json/the CAS blob store - only the human-readable log of how the current
    // state was reached, which after this can no longer be replayed if index.json is ever
    // lost or corrupted (RebuildCurrentFromOpLog's fallback stops working retroactively).
    public static void ClearAll(string root)
    {
        lock (_lock)
        {
            try { File.Delete(PathFor(root)); _displayMirror[root] = new(); }
            catch (Exception ex) { AppLog.Warn($"[local-library] oplog clear failed: {ex.Message}"); }
        }
    }

    // Disk-authoritative full read - the fold/recovery source of truth. ALWAYS reads the file.
    // Do NOT route the UI history refresh through this (use ReadForDisplay); the durability tests
    // rely on this actually hitting oplog.jsonl to prove the log carries the state.
    public static List<OpLogEntry> ReadAll(string root)
    {
        lock (_lock) return LoadFromDisk(root);
    }

    // Cheap read for the UI history panel: returns the in-memory mirror (seeded from disk once),
    // so a per-edit refresh never re-reads the growing file. A copy, so a caller can't mutate the
    // mirror out from under a later read.
    public static List<OpLogEntry> ReadForDisplay(string root)
    {
        lock (_lock)
        {
            if (!_displayMirror.TryGetValue(root, out var mirror))
                _displayMirror[root] = mirror = LoadFromDisk(root);
            return new List<OpLogEntry>(mirror);
        }
    }

    // Caller holds _lock.
    static List<OpLogEntry> LoadFromDisk(string root)
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
