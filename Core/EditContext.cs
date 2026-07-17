namespace KronosScreenRemote;

// Program-edit sub-state reported by the daemon's "STATE -> MODE=<n> EDITCTX=<e>" reply.
// Only meaningful when Mode == Mode.Program; None otherwise.
public enum EditContext
{
    None                = 0,
    ProgramFromCombi    = 1,
    ProgramFromSequence = 2,
}

public static class EditContextExtensions
{
    // Which mode button should stay lit solid while BTN_Program flashes.
    public static Mode OriginMode(this EditContext ctx) => ctx switch
    {
        EditContext.ProgramFromCombi    => Mode.Combi,
        EditContext.ProgramFromSequence => Mode.Sequence,
        _                                => Mode.Unknown,
    };

    // Human-readable origin name for the status-bar label ("Mode: Program (from <name>)").
    public static string DisplayName(this EditContext ctx) => ctx switch
    {
        EditContext.ProgramFromCombi    => "Combi",
        EditContext.ProgramFromSequence => "Sequence",
        _                                => "",
    };
}
