using System.Windows.Input;

namespace KronosScreenRemote;

public class AppSettings
{
    public string KronosHost { get; set; } = "";
    public int    StreamPort { get; set; } = 7373;
    public int    CtrlPort   { get; set; } = 7374;

    public bool PullMode { get; set; } = true;
    public int  MaxFps   { get; set; } = 15;

    public bool   PromptBeforeQuitting { get; set; } = true;
    public bool   HideControls         { get; set; } = false;
    public string ScreenshotDirectory  { get; set; } = "";

    public bool VgaMirrorEnabled   { get; set; } = false;
    public int  ScreensaverTimeout { get; set; } = 300;

    public LayoutPreset LayoutPreset { get; set; } = LayoutPreset.Full;

    public bool DebugLogging { get; set; } = false;

    // Window geometry — -1 means "not yet saved; use defaults"
    public double WindowLeft     { get; set; } = -1;
    public double WindowTop      { get; set; } = -1;
    public double WindowWidth    { get; set; } = -1;
    public double WindowHeight   { get; set; } = -1;
    public bool   WindowMaximized { get; set; } = false;

    public bool   AlwaysOnTop     { get; set; } = false;
    public double ZoomDefaultLevel { get; set; } = 2.5;
    public double ZoomWindowSize   { get; set; } = 1.0;

    public List<string> RecentHosts { get; set; } = new();

    public List<MacroDefinition> Macros { get; set; } = new();

    public string FtpUsername { get; set; } = "";
    public string FtpPassword { get; set; } = "";
    public int    FtpPort     { get; set; } = 21;

    public Dictionary<string, Keybind> Keybinds { get; set; } = new();

    public string? VuDeviceId { get; set; } = null;

    public static readonly (string Action, string Label, Key DefaultKey)[] Rebindable =
    [
        ("Quit",          "Quit",                   Key.Q),
        ("Fullscreen",    "Toggle Fullscreen",       Key.F),
        ("Zoom Window",   "Toggle Zoom Window",      Key.Z),
        ("Zoom In",       "Zoom In",                 Key.None),
        ("Zoom Out",      "Zoom Out",                Key.None),
        ("AspectLock",    "Toggle Aspect Lock",      Key.A),
        ("Mirror",        "Toggle VGA Mirror",       Key.M),
        ("Help",          "Toggle Help",             Key.F1),
        ("Calibrate",     "Toggle Calibration Mode", Key.C),
        ("HideControls",  "Hide/Show Controls",      Key.None),
        // Mode select
        ("Mode Setlist",  "Mode: Setlist",           Key.F2),
        ("Mode Combi",    "Mode: Combi",             Key.F3),
        ("Mode Program",  "Mode: Program",           Key.F4),
        ("Mode Sequence", "Mode: Sequence",          Key.F5),
        ("Mode Sampling", "Mode: Sampling",          Key.F6),
        ("Mode Global",   "Mode: Global",            Key.F7),
        ("Mode Disk",     "Mode: Disk",              Key.F8),
        // Bank select (unassigned by default)
        ("Bank I-A",  "Bank: I-A",   Key.None),
        ("Bank I-B",  "Bank: I-B",   Key.None),
        ("Bank I-C",  "Bank: I-C",   Key.None),
        ("Bank I-D",  "Bank: I-D",   Key.None),
        ("Bank I-E",  "Bank: I-E",   Key.None),
        ("Bank I-F",  "Bank: I-F",   Key.None),
        ("Bank I-G",  "Bank: I-G",   Key.None),
        ("Bank U-A",  "Bank: U-A",   Key.None),
        ("Bank U-B",  "Bank: U-B",   Key.None),
        ("Bank U-C",  "Bank: U-C",   Key.None),
        ("Bank U-D",  "Bank: U-D",   Key.None),
        ("Bank U-E",  "Bank: U-E",   Key.None),
        ("Bank U-F",  "Bank: U-F",   Key.None),
        ("Bank U-G",  "Bank: U-G",   Key.None),
        ("Bank U-AA", "Bank: U-AA",  Key.None),
        ("Bank U-BB", "Bank: U-BB",  Key.None),
        ("Bank U-CC", "Bank: U-CC",  Key.None),
        ("Bank U-DD", "Bank: U-DD",  Key.None),
        ("Bank U-EE", "Bank: U-EE",  Key.None),
        ("Bank U-FF", "Bank: U-FF",  Key.None),
        ("Bank U-GG", "Bank: U-GG",  Key.None),
    ];

    public Keybind GetKeybind(string action)
    {
        if (Keybinds.TryGetValue(action, out var kb)) return kb;
        foreach (var (a, _, dk) in Rebindable)
            if (a == action) return new Keybind(dk);
        return Keybind.None;
    }

    public string GetKeyName(string action) => GetKeybind(action).ToDisplayString();
}
