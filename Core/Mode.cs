namespace KronosScreenRemote;

// The seven Kronos operating modes, in the app/daemon "STATE mode" numbering (1-7,
// with 0 = not yet detected). This is the numbering the daemon's "STATE -> MODE=<n>
// EDITCTX=<e>" reply uses (see ScreenSession's STATE polling),
// and that the mode-tracking fields (_currentMode / _pendingMode / ...), SendMode(), and
// SetModeButton() all use.
//
// IMPORTANT: this is NOT the Korg SysEx wire-protocol mode numbering (0=Combi, 2=Program,
// 4=Sequencer, 6=Sampling, 7=Global, 8=Disk, 9=Setlist). Those values live in
// KronosSysEx.SysExModeData and are converted into this set via ToStateMode() before they
// reach any Mode-typed field.
public enum Mode
{
    Unknown  = 0,
    Setlist  = 1,
    Combi    = 2,
    Program  = 3,
    Sequence = 4,
    Sampling = 5,
    Global   = 6,
    Disk     = 7,
}

public static class ModeExtensions
{
    // Uppercase name for the daemon "BUTTON <name>" ctrl command. Unknown -> "".
    public static string ButtonName(this Mode mode) => mode switch
    {
        Mode.Setlist  => "SETLIST",
        Mode.Combi    => "COMBI",
        Mode.Program  => "PROGRAM",
        Mode.Sequence => "SEQUENCE",
        Mode.Sampling => "SAMPLING",
        Mode.Global   => "GLOBAL",
        Mode.Disk     => "DISK",
        _             => "",
    };

    // Human-readable name for the status-bar label. Unknown -> "".
    public static string DisplayName(this Mode mode) => mode switch
    {
        Mode.Setlist  => "Setlist",
        Mode.Combi    => "Combi",
        Mode.Program  => "Program",
        Mode.Sequence => "Sequence",
        Mode.Sampling => "Sampling",
        Mode.Global   => "Global",
        Mode.Disk     => "Disk",
        _             => "",
    };
}
