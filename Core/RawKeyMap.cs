using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace KronosScreenRemote;

class RawMapping
{
    public Key    HostKey   { get; init; }
    public bool   HostShift { get; init; }
    public int    RawCode   { get; init; }
    public bool   RawShift  { get; init; }
    public string Label     { get; init; } = "";

    public string HostKeyDisplay => HostShift ? $"Shift+{HostKey}" : HostKey.ToString();
    public string RawDisplay     => RawShift  ? $"KEY {RawCode} (Shift)" : $"KEY {RawCode}";
}

static class RawKeyMap
{
    static string FilePath => Path.Combine(Storage.DataDir, "raw_key_mappings.json");

    public static readonly ObservableCollection<RawMapping> Entries = Load();

    public static RawMapping? Get(Key k, bool shift) =>
        Entries.FirstOrDefault(e => e.HostKey == k && e.HostShift == shift);

    public static void Upsert(RawMapping m)
    {
        var existing = Entries.FirstOrDefault(e => e.HostKey == m.HostKey && e.HostShift == m.HostShift);
        if (existing != null) Entries.Remove(existing);
        Entries.Add(m);
        Save();
    }

    public static void Remove(RawMapping m) { Entries.Remove(m); Save(); }

    // Snapshot / Restore let a dialog offer an undo of raw-map edits made during its session
    // (Entries is a live global, also edited by the Input Tester, so restoring is the caller's
    // responsibility to gate on "no external editor touched it").
    public static List<RawMapping> Snapshot() =>
        Entries.Select(e => new RawMapping
        {
            HostKey = e.HostKey, HostShift = e.HostShift,
            RawCode = e.RawCode, RawShift = e.RawShift, Label = e.Label,
        }).ToList();

    public static void Restore(IReadOnlyList<RawMapping> snapshot)
    {
        Entries.Clear();
        foreach (var m in snapshot) Entries.Add(m);
        Save();
    }

    static ObservableCollection<RawMapping> Load()
    {
        var list = new ObservableCollection<RawMapping>();
        if (!File.Exists(FilePath)) return list;
        try
        {
            var arr = JsonNode.Parse(File.ReadAllText(FilePath))?.AsArray();
            if (arr == null) return list;
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                if (!Enum.TryParse<Key>(o["host_key"]?.GetValue<string>(), out var k)) continue;
                int code = o["raw_code"]?.GetValue<int>() ?? 0;
                if (code <= 0) continue;
                list.Add(new RawMapping
                {
                    HostKey   = k,
                    HostShift = o["host_shift"]?.GetValue<bool>() ?? false,
                    RawCode   = code,
                    RawShift  = o["raw_shift"]?.GetValue<bool>()  ?? false,
                    Label     = o["label"]?.GetValue<string>()    ?? "",
                });
            }
        }
        catch { }
        return list;
    }

    static void Save()
    {
        try
        {
            var arr = new JsonArray();
            foreach (var e in Entries)
                arr.Add(new JsonObject
                {
                    ["host_key"]   = e.HostKey.ToString(),
                    ["host_shift"] = e.HostShift,
                    ["raw_code"]   = e.RawCode,
                    ["raw_shift"]  = e.RawShift,
                    ["label"]      = e.Label,
                });
            File.WriteAllText(FilePath, arr.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
