namespace KronosScreenRemote;

// The complete outbound command vocabulary of the KronosScreenRemote daemon's ctrl
// port (CtrlClient.CtrlPort). Every string the client sends to the daemon is built
// here, so the wire protocol is defined in exactly one place instead of being spelled
// out as ad-hoc string literals at ~110 call sites.
//
// Builders return the command WITHOUT a trailing newline — CtrlClient.Send / QueryAsync
// append "\n" themselves. (CTRL_PERSIST is the one command CtrlClient writes to the
// socket directly, and it adds the newline there.)
static class DaemonCommand
{
    // ── Front-panel buttons: "BUTTON <token>" ──────────────────────────────────
    // A single press of one physical Kronos front-panel key.

    public static string Button(PanelButton button) => $"BUTTON {button.Token()}";

    // Mode keys reuse the existing Mode → daemon-token map (Mode.ButtonName()).
    public static string Button(Mode mode) => $"BUTTON {mode.ButtonName()}";

    // Numeric keypad digit (0–9): "BUTTON NUM0" … "BUTTON NUM9".
    public static string NumberButton(int digit) => $"BUTTON NUM{digit}";

    // Bank-select key: "BUTTON BANK_IA" (Internal A) … "BUTTON BANK_UG" (User G).
    public static string BankButton(BankGroup group, char letter) => $"BUTTON {BankToken(group, letter)}";

    // ── Chords: "CHORD <token> <token> …" — buttons pressed simultaneously ───────

    // The doubled User banks (U-AA … U-GG) are selected by chording the User and
    // Internal keys of the same letter.
    public static string DoubleUserBank(char letter) =>
        $"CHORD {BankToken(BankGroup.User, letter)} {BankToken(BankGroup.Internal, letter)}";

    // Enter the Kronos hardware test mode: hold the chord 500 ms, then MIX_KNOBS/RESET/ENTER/NUM5.
    public const string EnterTestMode = "CHORD 500 MIX_KNOBS RESET ENTER NUM5";

    // ── Value wheel and VALUE slider ────────────────────────────────────────────

    public static string Wheel(bool clockwise) => clockwise ? "WHEEL CW" : "WHEEL CCW";
    public static string ValueSlider(int value) => $"VSLIDER {value}";

    // ── Raw keyboard: "KEY <linuxKeyCode> <1|0>" (1 = press, 0 = release) ────────

    // Linux KEY_LEFTSHIFT. Eva only treats Left-Shift (42) as a case modifier — see KeyMap
    // (Key.LeftShift → 42). Kept as a named constant because it is injected around other keys.
    public const int ShiftKeyCode = 42;

    public static string Key(int linuxKeyCode, bool pressed) => $"KEY {linuxKeyCode} {(pressed ? 1 : 0)}";
    public static string Shift(bool pressed) => Key(ShiftKeyCode, pressed);

    // ── Touch: "TOUCH_DOWN|TOUCH_MOVE|TOUCH_UP <x> <y>" ─────────────────────────

    // CtrlClient coalesces on this literal prefix, so TouchMove() must keep it verbatim.
    public const string TouchMovePrefix = "TOUCH_MOVE ";

    public static string TouchDown(int x, int y) => $"TOUCH_DOWN {x} {y}";
    public static string TouchMove(int x, int y) => $"{TouchMovePrefix}{x} {y}";
    public static string TouchUp(int x, int y)   => $"TOUCH_UP {x} {y}";

    // ── Display / session ───────────────────────────────────────────────────────

    public const string RefreshDisplay = "REFRESH";
    public static string VgaMirror(bool on) => on ? "MIRROR_ON" : "MIRROR_OFF";
    public static string ScreensaverTimeout(int seconds) => $"SS_TIMEOUT {seconds}";

    // Handshake that marks the connection persistent so the daemon keeps it open.
    public const string PersistentSession = "CTRL_PERSIST";

    // ── Queries (expect a response) ─────────────────────────────────────────────

    public const string QueryState      = "STATE";       // reply: "MODE=<n> EDITCTX=<e>"
    public const string QueryMidiStatus = "MIDI_STATUS";
    public const string QuerySysInfo    = "SYSINFO";

    // ── MIDI bridge ─────────────────────────────────────────────────────────────

    public static string MidiSend(string hexBytes) => $"MIDI_SEND {hexBytes}";

    // ── Shared token builder ────────────────────────────────────────────────────

    static string BankToken(BankGroup group, char letter) =>
        $"BANK_{(group == BankGroup.User ? 'U' : 'I')}{letter}";
}

// The Kronos performance-bank groups reachable from the front panel.
enum BankGroup { Internal, User }

// The fixed (non-parameterized) Kronos front-panel keys the daemon accepts as
// "BUTTON <token>". Parameterized families (numeric keypad, bank select) and the
// mode keys are built by dedicated DaemonCommand methods instead.
enum PanelButton
{
    Help, Compare,
    Exit, Enter,
    Inc, Dec,
    NumDash, NumDot,
    SeqLocate, SeqRewind, SeqForward, SeqPause, SeqRecord, SeqStart,
}

static class PanelButtonExtensions
{
    // Human-readable enum name → exact daemon wire token.
    public static string Token(this PanelButton button) => button switch
    {
        PanelButton.Help       => "HELP",
        PanelButton.Compare    => "COMPARE",
        PanelButton.Exit       => "EXIT",
        PanelButton.Enter      => "ENTER",
        PanelButton.Inc        => "INC",
        PanelButton.Dec        => "DEC",
        PanelButton.NumDash    => "NUM_DASH",
        PanelButton.NumDot     => "NUM_DOT",
        PanelButton.SeqLocate  => "SEQ_LOCATE",
        PanelButton.SeqRewind  => "SEQ_REW",
        PanelButton.SeqForward => "SEQ_FF",
        PanelButton.SeqPause   => "SEQ_PAUSE",
        PanelButton.SeqRecord  => "SEQ_REC",
        PanelButton.SeqStart   => "SEQ_START",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown panel button"),
    };
}
