using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace KronosScreenRemote;

static class Storage
{
    public static string DataDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

    static string OverridePath  => Path.Combine(DataDir, "palette_override.json");
    static string CalPath       => Path.Combine(DataDir, "cal_data.json");
    static string SettingsPath  => Path.Combine(DataDir, "settings.json");

    // ── App settings ──────────────────────────────────────────────────────────

    // Converts PascalCase property names to snake_case JSON keys (e.g. MaxFps → max_fps).
    static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static AppSettings LoadSettings() => LoadSettingsFrom(SettingsPath);
    public static void SaveSettings(AppSettings s) => SaveSettingsTo(s, SettingsPath);

    // Guards a headless diagnostic that drives real production code (e.g. a
    // SampleEditorViewModel self-test/smoketest calling OpenCollection, which writes
    // Recent Files via Storage.SaveSettings) from ever leaving behind a mutated real
    // settings.json - a person running --librarian-selftest on their own machine must
    // get their real settings back untouched, not scratch-test paths or a settings.json
    // that didn't previously exist. Snapshots (or notes the absence of) settings.json,
    // runs `action`, then restores (or removes) it - even if `action` throws.
    public static void RunWithSettingsFileProtected(Action action)
    {
        byte[]? backup = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : null;
        try { action(); }
        finally
        {
            if (backup != null) File.WriteAllBytes(SettingsPath, backup);
            else if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }
    }

    public static AppSettings LoadSettingsFrom(string path)
    {
        var s = new AppSettings();
        if (!File.Exists(path)) return s;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (root == null) return s;

            foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == nameof(AppSettings.Keybinds)     ||
                    prop.Name == nameof(AppSettings.Macros)        ||
                    prop.Name == nameof(AppSettings.RecentHosts)   ||
                    prop.Name == nameof(AppSettings.SampleRecentFiles) || !prop.CanWrite) continue;
                if (root[ToSnakeCase(prop.Name)] is not JsonNode node) continue;
                try
                {
                    if      (prop.PropertyType == typeof(string)) prop.SetValue(s, node.GetValue<string>());
                    else if (prop.PropertyType == typeof(int))    prop.SetValue(s, node.GetValue<int>());
                    else if (prop.PropertyType == typeof(double))  prop.SetValue(s, node.GetValue<double>());
                    else if (prop.PropertyType == typeof(bool))   prop.SetValue(s, node.GetValue<bool>());
                    else if (prop.PropertyType.IsEnum)
                    {
                        var str = node.GetValue<string>();
                        if (Enum.TryParse(prop.PropertyType, str, out var ev)) prop.SetValue(s, ev);
                    }
                }
                catch { }
            }

            if (root["recent_hosts"] is JsonArray recentArr)
                foreach (var rn in recentArr)
                    if (rn?.GetValue<string>() is string h) s.RecentHosts.Add(h);

            if (root["sample_recent_files"] is JsonArray recentSampleArr)
                foreach (var rn in recentSampleArr)
                    if (rn?.GetValue<string>() is string p) s.SampleRecentFiles.Add(p);

            if (root["keybinds"] is JsonObject kb)
                foreach (var kv in kb)
                    if (kv.Value != null)
                        s.Keybinds[kv.Key] = Keybind.Parse(kv.Value.GetValue<string>());

            if (root["macros"] is JsonArray macrosArr)
                foreach (var mn in macrosArr)
                {
                    if (mn is not JsonObject mo) continue;
                    var m = new MacroDefinition
                    {
                        Description = mo["description"]?.GetValue<string>() ?? "",
                        Trigger     = Keybind.Parse(mo["trigger"]?.GetValue<string>() ?? "None"),
                        StepDelayMs = Math.Clamp(mo["step_delay_ms"]?.GetValue<int>() ?? 50, 10, 2000),
                    };
                    if (mo["steps"] is JsonArray stepsArr)
                        foreach (var sn in stepsArr)
                            if (sn is JsonObject so)
                            {
                                int code = so["code"]?.GetValue<int>() ?? 0;
                                if (code > 0) m.Steps.Add(new MacroStep
                                {
                                    Code = code,
                                    Down = so["down"]?.GetValue<bool>() ?? true,
                                });
                            }
                    s.Macros.Add(m);
                }
        }
        catch { }
        return s;
    }

    public static void SaveSettingsTo(AppSettings s, string path)
    {
        try
        {
            var root = new JsonObject();
            foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == nameof(AppSettings.Keybinds)     ||
                    prop.Name == nameof(AppSettings.Macros)        ||
                    prop.Name == nameof(AppSettings.RecentHosts)   ||
                    prop.Name == nameof(AppSettings.SampleRecentFiles) || !prop.CanRead) continue;
                root[ToSnakeCase(prop.Name)] = prop.GetValue(s) switch
                {
                    string str => JsonValue.Create(str),
                    int    i   => JsonValue.Create(i),
                    double d   => JsonValue.Create(d),
                    bool   b   => JsonValue.Create(b),
                    Enum   e   => JsonValue.Create(e.ToString()),
                    _          => null
                };
            }

            var recentOut = new JsonArray();
            foreach (var h in s.RecentHosts) recentOut.Add(h);
            root["recent_hosts"] = recentOut;

            var recentSampleOut = new JsonArray();
            foreach (var p in s.SampleRecentFiles) recentSampleOut.Add(p);
            root["sample_recent_files"] = recentSampleOut;
            var kb = new JsonObject();
            foreach (var kv in s.Keybinds)
                kb[kv.Key] = kv.Value.Serialize();
            root["keybinds"] = kb;

            var macrosOut = new JsonArray();
            foreach (var m in s.Macros)
            {
                var stepsOut = new JsonArray();
                foreach (var step in m.Steps)
                    stepsOut.Add(new JsonObject { ["code"] = step.Code, ["down"] = step.Down });
                macrosOut.Add(new JsonObject
                {
                    ["description"]   = m.Description,
                    ["trigger"]       = m.Trigger.Serialize(),
                    ["step_delay_ms"] = m.StepDelayMs,
                    ["steps"]         = stepsOut,
                });
            }
            root["macros"] = macrosOut;

            File.WriteAllText(path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.WriteLine($"[settings] save failed: {ex.Message}"); }
    }

    // ── Embedded resource fallback ────────────────────────────────────────────

    static string? ReadEmbedded(string name)
    {
        var asm  = Assembly.GetExecutingAssembly();
        var full = $"KronosScreenRemote.Resources.{name}";
        using var s = asm.GetManifestResourceStream(full);
        if (s == null) return null;
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    // ── Palette overrides ─────────────────────────────────────────────────────

    public static Dictionary<int, PaletteEntry> LoadOverrides()
    {
        try
        {
            string? json = File.Exists(OverridePath)
                ? File.ReadAllText(OverridePath)
                : ReadEmbedded("palette_override.json");
            if (json == null) return new();

            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return new();
            var d = new Dictionary<int, PaletteEntry>();
            foreach (var kv in node)
            {
                if (!int.TryParse(kv.Key, out int idx)) continue;
                var arr = kv.Value?.AsArray();
                if (arr == null || arr.Count < 3) continue;
                d[idx] = new PaletteEntry(
                    (byte)(arr[0]?.GetValue<int>() ?? 0),
                    (byte)(arr[1]?.GetValue<int>() ?? 0),
                    (byte)(arr[2]?.GetValue<int>() ?? 0));
            }
            return d;
        }
        catch { return new(); }
    }

    // ── Set List cache ─────────────────────────────────────────────────────────
    // Decoded Set List dumps cached per host so re-viewing doesn't re-interrupt
    // the Kronos. Stored as host → (set list number → data).

    static string SetListCachePath => Path.Combine(DataDir, "setlist_cache.json");
    static readonly HostKeyedCache<Dictionary<int, SetListData>> _setLists = new(() => SetListCachePath, "setlist-cache");

    // NOTE: Load/Save read + JSON-(de)serialize the whole cache file. Callers on the UI
    // thread (viewer open, load/refresh, sync) MUST wrap them in Task.Run - a full Set
    // List is ~79 KB of decoded data, so a populated cache is heavy to (de)serialize and
    // would freeze the window. HostKeyedCache serializes both against each other under one
    // lock (Save is an atomic read-modify-write), so a UI-thread save racing a background
    // one can no longer interleave and corrupt the file.
    public static Dictionary<int, SetListData> LoadSetLists(string host) => _setLists.Load(host) ?? new();

    public static void SaveSetLists(string host, Dictionary<int, SetListData> lists) => _setLists.Save(host, lists);

    // ── Program/Combi name cache ───────────────────────────────────────────────
    // Bulk-dumped names persisted per host so program-change follow is flash-free
    // after the first session. Invalidated per bank on a Bank Digest (func 0x38).

    static string NameCachePath => Path.Combine(DataDir, "name_cache.json");
    static readonly HostKeyedCache<List<CachedName>> _names = new(() => NameCachePath, "name-cache");

    public static List<CachedName> LoadNames(string host) => _names.Load(host) ?? new();

    public static void SaveNames(string host, List<CachedName> entries) => _names.Save(host, entries);

    // ── Dumped-bank ledger ─────────────────────────────────────────────────────
    // Which (type, objBank) name-dumps have already been collected, persisted per
    // host. SEPARATE from the name cache on purpose: an EMPTY bank dumps 128 blank
    // names (all filtered out, nothing cached), so "has cached names" cannot tell a
    // never-dumped bank from a dumped-but-empty one. The ledger lets a Sync skip
    // banks already done (across sessions), and a bank that didn't complete is left
    // un-dumped so it retries next time. NOTE: the "~13 banks/session then the Kronos
    // rejects everything" rationale this ledger was built under turned out to be a
    // misdiagnosis (the func-0x77 whole-bank name enum is preset-only - it was
    // rejecting USER banks, not imposing a session cap; see SysExService.
    // SyncNamesAsync). The ledger is still useful, just not a throttle budget.
    // Invalidated per bank on a Bank Digest (func 0x38), same as the name cache.

    static string DumpedBanksPath => Path.Combine(DataDir, "dumped_banks.json");
    // Stored on disk as "type:objBank" hex strings, e.g. "1:47"; decoded to/from
    // (Type, Bank) tuples at the boundary here so the ledger stays a plain List<string>
    // the shared cache can (de)serialize without knowing the encoding.
    static readonly HostKeyedCache<List<string>> _dumpedBanks = new(() => DumpedBanksPath, "dumped-banks");

    public static HashSet<(int Type, int Bank)> LoadDumpedBanks(string host)
    {
        var set = new HashSet<(int, int)>();
        foreach (var s in _dumpedBanks.Load(host) ?? new())
        {
            var parts = s.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var t) &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var b))
                set.Add((t, b));
        }
        return set;
    }

    public static void SaveDumpedBanks(string host, HashSet<(int Type, int Bank)> set)
        => _dumpedBanks.Save(host, set.Select(k => $"{k.Type}:{k.Bank:X2}").ToList());

    // ── Librarian reference-graph cache ───────────────────────────────────────
    // ── Librarian clipboard (Core/BatchMoveModel.cs's BatchClipboard) ───────────
    // A flat list, not a Dictionary<host,...> like the caches above - deliberately not
    // host-keyed, because the local library it belongs to (Core/LocalLibrary) is a single
    // global store: the Kronos's IP can change but the objects don't. (The old per-host
    // reference-graph cache and host-keyed clipboard this file used to also carry - for
    // the classic, now-retired LibrarianWindow - were removed in the Phase 7 cutover, along
    // with that window itself.)

    public sealed record ClipboardEntryDto(
        int ObjType, int OriginBank, int OriginNumber, byte Version, byte[] Body,
        string Provenance, string Reason, DateTime CutAt,
        int? PastedBank, int? PastedNumber, DateTime? PastedAt, Guid? BankCopyGroup = null);

    static string ClipboardGlobalPath => Path.Combine(DataDir, "local_library_clipboard.json");
    // Flat, not host-keyed (see the note above), so it rides the JsonFileCache base directly
    // rather than HostKeyedCache - same lock/I/O plumbing, whole-file value.
    static readonly JsonFileCache<List<ClipboardEntryDto>> _clipboard = new(() => ClipboardGlobalPath, "local-library-clipboard");

    public static List<ClipboardEntryDto> LoadClipboardGlobal() => _clipboard.Read() ?? new();

    public static void SaveClipboardGlobal(List<ClipboardEntryDto> entries) => _clipboard.Write(entries);

    // ── Program bank type cache ───────────────────────────────────────────────
    // Persists the func-0x61 Program Bank Types bitmap (HD-1 vs EXi) per host. Currently
    // unused by the new Librarian (Views/LibrarianShellWindow.xaml) - its batch-place path
    // (ViewModels/LibrarianShellViewModel.cs's BatchPlaceFromPcg) doesn't yet gate on
    // bank-type compatibility the way the old, now-retired LibrarianWindow's batch-move did
    // (a known, flagged gap, not a silent regression). Left in place rather than deleted:
    // ISysExService.RequestProgramBankTypesAsync and this cache are exactly what a future
    // fix would need.

    static string ProgramBankTypesPath => Path.Combine(DataDir, "program_bank_types_cache.json");
    static readonly HostKeyedCache<bool[]> _programBankTypes = new(() => ProgramBankTypesPath, "program-bank-types");

    // Null (not empty) when the host was never dumped - callers distinguish the two.
    public static bool[]? LoadProgramBankTypes(string host) => _programBankTypes.Load(host);

    public static void SaveProgramBankTypes(string host, bool[] flags) => _programBankTypes.Save(host, flags);

    // ── Category name cache (requirement 4) ───────────────────────────────────
    // The Program/Combi Category + Sub-Category NAMES decoded from a Global object dump
    // (GlobalBody.ReadCategoryNames), persisted per host exactly like the bank types above and for
    // the same reason: they're user-editable ON the instrument, so they belong to that instrument,
    // and re-dumping ~24 KB of Global just to label a dropdown on every window open would be
    // wasteful. Seeded from here at Librarian open, refreshed live in the background.

    // A flat DTO rather than persisting CategoryNames directly: that type uses `required init`
    // members, which System.Text.Json can populate but only with a matching constructor shape -
    // a plain mutable record keeps the on-disk format independent of the model's own API.
    public sealed record CategoryNamesDto(string[] Program, string[][] ProgramSub, string[] Combi, string[][] CombiSub);

    static string CategoryNamesPath => Path.Combine(DataDir, "category_names_cache.json");
    static readonly HostKeyedCache<CategoryNamesDto> _categoryNames = new(() => CategoryNamesPath, "category-names");

    // Null when this host's categories were never synced - the caller falls back to
    // CategoryNames.Numeric() (plain "Category 05" labels), never to an error.
    public static CategoryNamesDto? LoadCategoryNames(string host) => _categoryNames.Load(host);

    public static void SaveCategoryNames(string host, CategoryNamesDto names) => _categoryNames.Save(host, names);

    // ── Librarian backups ──────────────────────────────────────────────────────
    // Shared by the move feature (Librarian.ApplyMoveAsync) and the Store-Bank
    // verification tool - both back up pre-images to timestamped .syx files here
    // before writing anything.

    public static string BackupDir()
    {
        var d = Path.Combine(DataDir, "librarian_backups");
        Directory.CreateDirectory(d);
        return d;
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    public static (CalMesh mesh, List<CalBiasDot> dots) LoadCal()
    {
        var dots = new List<CalBiasDot>();

        try
        {
            // No user calibration file -> default (identity mesh, no bias dots): the same
            // "cleared" state the in-app Reset (R) / Clear-dots (X) keys produce. A fresh
            // install and a post-"Reset settings" run (which deletes cal_data.json) therefore
            // both start uncalibrated, rather than inheriting a baked-in factory mesh.
            if (!File.Exists(CalPath)) return (new CalMesh(), dots);
            string json = File.ReadAllText(CalPath);

            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null) return (new CalMesh(), dots);

            int size = root["grid_size"]?.GetValue<int>() ?? 5;
            size = size is 3 or 4 or 5 ? size : 5;
            var mesh = new CalMesh(size, size);

            if (root["mesh"] is JsonArray meshArr)
                foreach (var n in meshArr)
                    if (n is JsonArray row && row.Count >= 4)
                        mesh.SetOffset(row[0]!.GetValue<int>(), row[1]!.GetValue<int>(),
                                       row[2]!.GetValue<int>(), row[3]!.GetValue<int>());

            if (root["bias_dots"] is JsonArray dotsArr)
                foreach (var d in dotsArr)
                    if (d is JsonArray row && row.Count >= 2)
                        dots.Add(new CalBiasDot(row[0]!.GetValue<int>(), row[1]!.GetValue<int>()));

            return (mesh, dots);
        }
        catch { return (new CalMesh(), dots); }
    }

    public static void SaveCal(CalMesh mesh, List<CalBiasDot> dots)
    {
        try
        {
            var root    = new JsonObject();
            root["grid_size"] = mesh.Cols;
            var meshArr = new JsonArray();
            for (int c = 0; c < mesh.Cols; c++)
                for (int r = 0; r < mesh.Rows; r++)
                {
                    var (ox, oy) = mesh.GetOffset(c, r);
                    if (ox != 0 || oy != 0)
                        meshArr.Add(new JsonArray(c, r, ox, oy));
                }
            root["mesh"] = meshArr;

            var dotsArr = new JsonArray();
            foreach (var d in dots) dotsArr.Add(new JsonArray(d.Nx, d.Ny));
            root["bias_dots"] = dotsArr;

            File.WriteAllText(CalPath, root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Console.WriteLine($"[cal] save failed: {e.Message}"); }
    }
}
