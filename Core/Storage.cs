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
    static string LockPath      => Path.Combine(DataDir, "palette_lock.json");
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
                    prop.Name == nameof(AppSettings.RecentHosts)   || !prop.CanWrite) continue;
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
                    prop.Name == nameof(AppSettings.RecentHosts)   || !prop.CanRead) continue;
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
        string? json = null;
        if (File.Exists(OverridePath))
            json = File.ReadAllText(OverridePath);
        else
            json = ReadEmbedded("palette_override.json");

        if (json == null) return new();
        try
        {
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

    public static void SaveOverrides(Dictionary<int, PaletteEntry> ov)
    {
        var obj = new JsonObject();
        foreach (var kv in ov.OrderBy(x => x.Key))
            obj[kv.Key.ToString()] = new JsonArray(kv.Value.R, kv.Value.G, kv.Value.B);
        File.WriteAllText(OverridePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[palette] {ov.Count} override(s) saved → {OverridePath}");
    }

    // ── Palette locks ─────────────────────────────────────────────────────────

    public static HashSet<int> LoadLocks()
    {
        if (!File.Exists(LockPath)) return new();
        try
        {
            var arr = JsonNode.Parse(File.ReadAllText(LockPath))?.AsArray();
            if (arr == null) return new();
            return arr.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0).ToHashSet();
        }
        catch { return new(); }
    }

    public static void SaveLocks(HashSet<int> locked)
    {
        var arr = new JsonArray();
        foreach (var i in locked.OrderBy(x => x)) arr.Add(i);
        File.WriteAllText(LockPath, arr.ToJsonString());
        Console.WriteLine($"[lock] {locked.Count} locked entry/entries saved → {LockPath}");
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    public static (CalMesh mesh, List<CalBiasDot> dots) LoadCal()
    {
        var dots = new List<CalBiasDot>();

        string? json = null;
        if (File.Exists(CalPath))
            json = File.ReadAllText(CalPath);
        else
            json = ReadEmbedded("cal_data.json");

        if (json == null) return (new CalMesh(), dots);
        try
        {
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
