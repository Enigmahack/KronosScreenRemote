namespace KronosScreenRemote;

// One entry in the daemon's "BUTTON <name>" vocabulary. Category/Token/Code/Description
// are copied verbatim from KronosScreenRemoteDaemon/docs/api.md §9 "Button name
// reference" - keep in sync if that table changes. Code is the button's flat NKS4
// hardware scan code (0-127); not sendable directly (the wire command takes a name, not
// a number) but useful for sorting/cross-referencing against the daemon's own docs.
public readonly record struct ButtonRef(string Category, string Token, int Code, string Description);

// Complete catalog of every button the daemon's BUTTON command accepts, used by
// ButtonInjectorWindow to enumerate every sendable button for live testing against a
// connected unit - see nautilus_button_mapping.md for why (the Nautilus's physical
// panel silkscreens different labels over the same underlying scan codes, and this
// tool is how that gets confirmed instead of guessed).
public static class ButtonReference
{
    public static readonly IReadOnlyList<ButtonRef> All = BuildAll();

    // Seed labels for the "Nautilus" layout, from user-reported hands-on testing
    // (2026-09-19, see nautilus_button_mapping.md). Everything not listed here starts
    // blank in that layout - deliberately, so a blank cell in the Nautilus column reads
    // as "not yet confirmed" rather than a false duplicate of the Kronos label.
    public static readonly IReadOnlyDictionary<string, string> NautilusSeedLabels =
        new Dictionary<string, string>
        {
            ["COMBI"]    = "MODE",
            ["SETLIST"]  = "QA E",
            ["PROGRAM"]  = "PAGE",
            ["SEQUENCE"] = "QA A",
            ["SAMPLING"] = "QA B",
            ["GLOBAL"]   = "QA C",
            ["DISK"]     = "QA D",
            ["HELP"]     = "Data Increment",
            ["COMPARE"]  = "Data Decrement",
            ["EXIT"]     = "QA F",
            ["ENTER"]    = "OCTAVE -",
            ["NUM2"]     = "DYNAMICS",
            ["NUM3"]     = "CONTROL SELECT",
            ["NUM4"]     = "LATCH",
            ["NUM5"]     = "ARP",
            ["NUM6"]     = "DRUM",
            ["NUM7"]     = "AUDIO IN",
            ["NUM9"]     = "TAP TEMPO",
            ["NUM_DASH"] = "MFX",
            ["NUM_DOT"]  = "TFX",
        };

    static List<ButtonRef> BuildAll()
    {
        var list = new List<ButtonRef>
        {
            new("Navigation", "EXIT",  8,  "Exit button"),
            new("Navigation", "ENTER", 23, "Enter / confirm button"),

            new("Value control", "INC", 51, "Increment the currently selected value"),
            new("Value control", "DEC", 52, "Decrement the currently selected value"),

            new("Mode select", "SETLIST",  7, "Setlist mode"),
            new("Mode select", "COMBI",    1, "Combi mode"),
            new("Mode select", "PROGRAM",  2, "Program mode"),
            new("Mode select", "SEQUENCE", 3, "Sequence mode"),
            new("Mode select", "SAMPLING", 4, "Sampling mode"),
            new("Mode select", "GLOBAL",   5, "Global mode"),
            new("Mode select", "DISK",     6, "Disk mode"),

            new("Utility", "HELP",    9,  "Help button"),
            new("Utility", "COMPARE", 10, "Compare button"),
            new("Utility", "RESET",   75, "Reset Controls button"),
        };

        for (int d = 0; d <= 9; d++)
            list.Add(new ButtonRef("Numeric pad", $"NUM{d}", 11 + d, $"Numeric key {d}"));
        list.Add(new ButtonRef("Numeric pad", "NUM_DASH", 21, "Numeric dash / minus"));
        list.Add(new ButtonRef("Numeric pad", "NUM_DOT",  22, "Numeric dot / decimal"));

        for (int i = 0; i < 8; i++)
            list.Add(new ButtonRef("Mix Play",   $"MP{i + 1}", 58 + i, $"Mix Play {i + 1}"));
        for (int i = 0; i < 8; i++)
            list.Add(new ButtonRef("Mix Select", $"MS{i + 1}", 66 + i, $"Mix Select {i + 1}"));

        for (int i = 0; i < 7; i++)
        {
            char letter = (char)('A' + i);
            list.Add(new ButtonRef("Bank", $"BANK_I{letter}", 24 + i, $"Internal bank {letter}"));
        }
        for (int i = 0; i < 7; i++)
        {
            char letter = (char)('A' + i);
            list.Add(new ButtonRef("Bank", $"BANK_U{letter}", 31 + i, $"User bank {letter}"));
        }

        list.AddRange(new[]
        {
            new ButtonRef("Sequencer", "SEQ_PAUSE",  38, "Pause"),
            new ButtonRef("Sequencer", "SEQ_REW",    39, "Rewind"),
            new ButtonRef("Sequencer", "SEQ_FF",     40, "Fast forward"),
            new ButtonRef("Sequencer", "SEQ_LOCATE", 41, "Locate / return to start"),
            new ButtonRef("Sequencer", "SEQ_REC",    42, "Sequencer record"),
            new ButtonRef("Sequencer", "SEQ_START",  43, "Sequencer start / stop"),
            new ButtonRef("Sequencer", "TAP_TEMPO",  44, "Tap tempo"),

            new ButtonRef("Sampling", "SMPL_REC",   45, "Sampling record"),
            new ButtonRef("Sampling", "SMPL_START", 46, "Sampling start"),

            new ButtonRef("Channel strip", "MIX_KNOBS",     74, "Mixer Knobs selector"),
            new ButtonRef("Channel strip", "SOLO",          76, "Solo (fires on release)"),
            new ButtonRef("Channel strip", "MODULE_CONTROL",47, "Module Control"),
            new ButtonRef("Channel strip", "KARMA_ONOFF",   48, "Karma On/Off"),
            new ButtonRef("Channel strip", "KARMA_LATCH",   49, "Karma Latch"),
            new ButtonRef("Channel strip", "DRUM_TRACK",    50, "Drum Track select"),
            new ButtonRef("Channel strip", "TIMBRE_TRACK",  53, "Timbre/Track select"),
            new ButtonRef("Channel strip", "AUDIO_TRACK",   54, "Audio select"),
            new ButtonRef("Channel strip", "EXT_TRACK",     55, "Ext select"),
            new ButtonRef("Channel strip", "RTKNOBS_KARMA", 56, "RT Knobs/Karma page select"),
            new ButtonRef("Channel strip", "TONE_ADJUST",   57, "Tone Adjust"),
            new ButtonRef("Channel strip", "SW1",           77, "Front-panel switch 1"),
            new ButtonRef("Channel strip", "SW2",           78, "Front-panel switch 2"),
        });

        return list.OrderBy(b => b.Code).ToList();
    }
}
