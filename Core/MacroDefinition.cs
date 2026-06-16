namespace KronosScreenRemote;

public class MacroDefinition
{
    public string          Description { get; set; } = "";
    public Keybind         Trigger     { get; set; } = Keybind.None;
    public int             StepDelayMs { get; set; } = 50;
    public List<MacroStep> Steps       { get; set; } = new();
}

public class MacroStep
{
    public int  Code { get; set; }
    public bool Down { get; set; }

    static readonly Dictionary<int, string> _names = new()
    {
        [1]  = "Esc",   [14] = "Back",  [15] = "Tab",   [28] = "Enter", [57] = "Space",
        [29] = "LCtrl", [97] = "RCtrl", [42] = "LSft",  [54] = "RSft",
        [56] = "LAlt",  [100]= "RAlt",  [58] = "Caps",
        [59] = "F1",    [60] = "F2",    [61] = "F3",    [62] = "F4",
        [63] = "F5",    [64] = "F6",    [65] = "F7",    [66] = "F8",
        [67] = "F9",    [68] = "F10",   [87] = "F11",   [88] = "F12",
        // numpad-style nav codes (raw map)
        [71] = "Home",  [72] = "Up",    [74] = "Minus", [75] = "Left",
        [77] = "Right", [79] = "End",   [80] = "Down",  [81] = "PgDn",
        // standard nav codes (KeyMap)
        [102]= "Home",  [103]= "Up",    [104]= "PgUp",  [105]= "Left",
        [106]= "Right", [107]= "End",   [108]= "Down",  [109]= "PgDn",
        [110]= "Ins",   [111]= "Del",
    };

    public string Name    => _names.TryGetValue(Code, out var n) ? n : $"k{Code}";
    public string Display => $"{Name}{(Down ? "↓" : "↑")}";  // ↓ / ↑
}
