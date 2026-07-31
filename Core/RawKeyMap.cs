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

    // File I/O + lock are owned by the shared helper; RawKeyMap keeps its OWN JsonNode
    // (de)serializers (Serialize/Deserialize below) so the on-disk shape - a snake_cased,
    // WriteIndented JSON array with the Key as a string - and its per-row skip-bad-entry
    // resilience stay byte-for-byte identical to the old hand-rolled version. Previously
    // this file did its I/O completely unlocked; folding it in fixes that for free.
    //
    // MUST be declared before Entries: static field initializers run in textual order, and
    // `Entries = Load()` reads through this field at type load - declared after, it'd NRE.
    static readonly JsonFileCache<List<RawMapping>> _file =
        new(() => FilePath, "raw-key-map", Serialize, Deserialize);

    // Eager + stable on purpose: two windows bind this instance as an ItemsSource
    // (InputTesterWindow, SettingsWindow), so its identity and change-notifications must
    // outlive them. Load() is the one bit of disk I/O at type load.
    public static readonly ObservableCollection<RawMapping> Entries = Load();

    public static RawMapping? Get(Key k, bool shift) =>
        Entries.FirstOrDefault(e => e.HostKey == k && e.HostShift == shift);

    public static void Upsert(RawMapping m) { UpsertInto(Entries, m); Save(); }

    // Pure upsert, no disk and no ObservableCollection required - so RawKeyMapSelfTests can
    // exercise it directly. Replaces any existing mapping for the same (HostKey, HostShift)
    // in place, then appends, matching the old FirstOrDefault-remove-then-Add behavior.
    internal static void UpsertInto(IList<RawMapping> list, RawMapping m)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].HostKey == m.HostKey && list[i].HostShift == m.HostShift)
            {
                list.RemoveAt(i);
                break;
            }
        list.Add(m);
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

    static ObservableCollection<RawMapping> Load() => new(_file.Read() ?? new());

    static void Save() => _file.Write(Entries.ToList());

    // ── Legacy JSON shape, kept verbatim so existing raw_key_mappings.json files load ──

    internal static string Serialize(List<RawMapping> list)
    {
        var arr = new JsonArray();
        foreach (var e in list)
            arr.Add(new JsonObject
            {
                ["host_key"]   = e.HostKey.ToString(),
                ["host_shift"] = e.HostShift,
                ["raw_code"]   = e.RawCode,
                ["raw_shift"]  = e.RawShift,
                ["label"]      = e.Label,
            });
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Skips a bad row (unparseable Key, non-positive raw_code) and keeps the rest, rather
    // than failing the whole file - the resilience a typed STJ deserialize would have lost.
    internal static List<RawMapping> Deserialize(string json)
    {
        var list = new List<RawMapping>();
        var arr = JsonNode.Parse(json)?.AsArray();
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
        return list;
    }
}
