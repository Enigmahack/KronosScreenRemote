using System.Reflection;
using System.Windows.Input;

namespace KronosScreenRemote;

public class AppSettings
{
    public string KronosHost { get; set; } = "";
    public int    StreamPort { get; set; } = 7373;
    public int    CtrlPort   { get; set; } = 7374;

    public bool   PullMode { get; set; } = true;
    public int    MaxFps   { get; set; } = 15;

    public bool   PromptBeforeQuitting { get; set; } = true;
    public bool   HideDataInput        { get; set; } = false;
    public bool   HideValueInput       { get; set; } = false;
    public bool   ReverseScrolling { get; set;  } = false;
    public string ScreenshotDirectory  { get; set; } = "";

    public bool   VgaMirrorEnabled   { get; set; } = false;
    public int    ScreensaverTimeout { get; set; } = 300;

    public LayoutPreset LayoutPreset { get; set; } = LayoutPreset.Full;
    public bool   FocusedDataExpanded  { get; set; } = false;
    public bool   FocusedValueExpanded { get; set; } = false;

    public bool   DebugLogging { get; set; } = false;

    // Librarian: Merge Window staging cache - see MergeCacheBehavior's own doc comment.
    public MergeCacheBehavior MergeBehavior { get; set; } = MergeCacheBehavior.TemporaryMemory;

    // Merge Window -> Local Library duplication policy (Settings > Librarian; also mirrored as
    // quick toggles in the Merge Window toolbar - see LibrarianShellViewModel's same-named
    // properties). When checked, placing a staged object whose content already exists somewhere
    // in Local Library still writes a FRESH copy ("preserve duplication"); when unchecked, the
    // existing copy is reused instead of writing a duplicate (LibrarianShellViewModel.
    // FindExistingLocalCopy). Defaults mirror the long-standing behavior: Programs dedup,
    // Combis copy as-is.
    public bool MergePreserveDuplicatePrograms { get; set; } = false;
    public bool MergePreserveDuplicateCombis   { get; set; } = true;

    // MIDI / SysEx
    // Which backend carries MIDI/SysEx to the Kronos (screen/video stays TCP).
    // Auto prefers a directly-connected Kronos USB-MIDI device, else the daemon.
    public MidiTransportMode MidiTransport { get; set; } = MidiTransportMode.Auto;
    // Device-name substring used to locate the Kronos among USB-MIDI ports
    // (case-insensitive; never a fixed slot). Default matches the Korg product name.
    public string UsbMidiDeviceName { get; set; } = "KRONOS";

    public bool MidiMonitorEnabled    { get; set; } = true;
    public bool ProactiveSysExPolling { get; set; } = false;
    public int  SysExPollIntervalSec  { get; set; } = 60;
    public bool SysExPollOnChanges    { get; set; } = true;
    public int  MidiOutputChannel     { get; set; } = 1;
    // CC# the Kronos VALUE slider transmits (default 18). Used to sync the UI
    // value slider to physical slider moves. The actual CC can vary with the
    // selected parameter/page; 18 is the Kronos default.
    public int  ValueSliderCc         { get; set; } = 18;
    // Pull a program/combi's name (func 0x72) as you select it, when not already
    // cached. Viable now that USB carries a name object in ~ms; off by default.
    public bool PullNamesOnChange     { get; set; } = false;

    // Window geometry - -1 means "not yet saved; use defaults"
    public double WindowLeft     { get; set; } = -1;
    public double WindowTop      { get; set; } = -1;
    public double WindowWidth    { get; set; } = -1;
    public double WindowHeight   { get; set; } = -1;
    public bool   WindowMaximized { get; set; } = false;

    // Geometry for every OTHER window, keyed by type name - see ThemedWindow's own placement
    // handling for which windows opt in. MainWindow keeps the dedicated fields above instead:
    // its restore is entangled with the tray/fullscreen paths (MainWindow.Input.cs), which this
    // generic one deliberately doesn't know about.
    public Dictionary<string, WindowPlacement> WindowPlacements { get; set; } = new();

    public bool   AlwaysOnTop     { get; set; } = false;
    public double ZoomDefaultLevel { get; set; } = 2.5;
    public double ZoomWindowSize   { get; set; } = 1.0;

    // Image quality / adjustments - applied to the streamed frame before display.
    // ScalingMode selects WPF's upscale filter; the rest fold into the palette LUT (brightness/
    // contrast/gamma/saturation) or a per-frame unsharp-mask pass (sharpen).  See ImageAdjust.
    public ScalingQuality ImageScalingMode { get; set; } = ScalingQuality.HighQuality;
    public int    ImageBrightness { get; set; } = 0;    // -100..100  (0 = none)
    public int    ImageContrast   { get; set; } = 0;    // -100..100  (0 = none)
    public double ImageGamma      { get; set; } = 1.0;  // 0.4..2.5   (1.0 = none)
    public int    ImageSaturation { get; set; } = 0;    // -100..100  (0 = none)
    public int    ImageSharpen    { get; set; } = 0;    // 0..100     (0 = off)

    public List<string> RecentHosts { get; set; } = new();

    public List<MacroDefinition> Macros { get; set; } = new();

    public string FtpUsername { get; set; } = "";
    public string FtpPassword { get; set; } = "";
    public int    FtpPort     { get; set; } = 21;

    public Dictionary<string, Keybind> Keybinds { get; set; } = new();

    public string? VuDeviceId { get; set; } = null;

    // Sample Editor waveform-edit undo (Core/Sample/SampleEditUndo.cs): a bounded
    // byte-size cap, not a step count - a single crop/tempo/pitch snapshot is a
    // multi-MB PCM buffer, so "keep the last 50 steps" could mean anywhere from a few
    // MB to gigabytes depending on sample size. 256 MB is a few dozen steps for a
    // typical few-hundred-KB sample, fewer for a multi-MB one.
    public int SampleUndoByteCapMb { get; set; } = 256;

    // Local root for content pulled from the Kronos by the Sample Editor's "Pull from
    // Kronos" flow - mirrors the remote directory structure underneath it (same
    // <ksc-basename>/<kmp-basename>/ convention on both sides, see KmpZone.KsfPath).
    // Empty means "use the default" (SampleWorkspace.ResolveRoot), same lazy-default
    // pattern as LocalLibraryCache.Open() uses for its own {DataDir}-relative root.
    public string SampleWorkspaceRoot { get; set; } = "";

    // Sample Editor "Recent Files" - most-recently-opened .KSC/.KMP paths, newest
    // first, capped at SampleRecentFilesMax entries. Local disk paths only (a Kronos
    // FTP pull already lands as a local path once PickAndPullAsync finishes, so this
    // needs no separate remote-path tracking).
    public List<string> SampleRecentFiles { get; set; } = new();
    public const int SampleRecentFilesMax = 8;

    // Settings > Sample Editor > "Create Zone Preferences" - see
    // Core/Sample/SampleZoneCreatePreferences.cs and AddPlaceholderZone's own comment
    // for what each one actually does to a newly created zone's key range.
    public SampleZoneCreatePosition SampleZoneCreatePosition { get; set; } = SampleZoneCreatePosition.Right;
    public int SampleZoneCreateRange { get; set; } = 12; // 1..127
    public SampleZoneOriginalKeyPosition SampleZoneOriginalKeyPosition { get; set; } = SampleZoneOriginalKeyPosition.Bottom;

    public static readonly (string Action, string Label, Key DefaultKey)[] Rebindable =
    [
        ("Quit",          "Quit",                   Key.Q),
        ("Fullscreen",    "Toggle Fullscreen",       Key.F),
        ("Zoom Window",   "Toggle Zoom Window",      Key.Z),
        ("Zoom In",       "Zoom In",                 Key.None),
        ("Zoom Out",      "Zoom Out",                Key.None),
        ("Mirror",        "Toggle VGA Mirror",       Key.M),
        ("Help",          "Toggle Help",             Key.F1),
        ("Calibrate",     "Toggle Calibration Mode", Key.C),
        ("HideDataInput",  "Hide/Show Data Input",    Key.None),
        ("HideValueInput", "Hide/Show Value Input",   Key.None),
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
        // Sequencer transport (unassigned by default) - mirrors the footer transport
        // row; "Seq Save" fires the same shared REC/WRITE press as "Seq Record" does.
        ("Seq Locate",  "Seq: Locate",       Key.None),
        ("Seq Rewind",  "Seq: Rewind",       Key.None),
        ("Seq Forward", "Seq: Fast-Forward", Key.None),
        ("Seq Pause",   "Seq: Pause",        Key.None),
        ("Seq Record",  "Seq: Record",       Key.None),
        ("Seq Start",   "Seq: Start/Stop",   Key.None),
        ("Seq Save",    "Write / Save",      Key.None),
        // Tap tempo (unassigned by default) - the bound key taps once per press; hold
        // is ignored (auto-repeat is filtered) so a held key can't spam phantom taps.
        ("Tap Tempo",   "Tap Tempo",         Key.None),
    ];

    public Keybind GetKeybind(string action)
    {
        if (Keybinds.TryGetValue(action, out var kb)) return kb;
        foreach (var (a, _, dk) in Rebindable)
            if (a == action) return new Keybind(dk);
        return Keybind.None;
    }

    public string GetKeyName(string action) => GetKeybind(action).ToDisplayString();

    // Deep copy of every setting. Reflection over all read/write properties means a newly
    // added setting is copied automatically - no more silently-dropped pass-through fields.
    // Mutable collections are copied by value so edits to the clone never mutate the original.
    public AppSettings Clone()
    {
        var copy = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite)
                p.SetValue(copy, p.GetValue(this));

        copy.RecentHosts = new List<string>(RecentHosts);
        copy.SampleRecentFiles = new List<string>(SampleRecentFiles);
        copy.Keybinds    = new Dictionary<string, Keybind>(Keybinds);
        copy.Macros      = Macros.Select(m => new MacroDefinition
        {
            Description = m.Description,
            Trigger     = m.Trigger,
            StepDelayMs = m.StepDelayMs,
            Steps       = m.Steps.Select(x => new MacroStep { Code = x.Code, Down = x.Down }).ToList(),
        }).ToList();
        return copy;
    }
}

// One window's remembered geometry (AppSettings.WindowPlacements). Always the NORMAL-state
// bounds even when the window was closed maximized (Window.RestoreBounds), so un-maximizing
// after a restart lands where the user last had it rather than at some default size.
public class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
