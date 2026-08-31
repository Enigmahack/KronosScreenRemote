namespace KronosScreenRemote;

// How badly a plan warning matters. Previously encoded as a "REFUSE:" / "CHECK:" prefix on a
// plain string, with three separate plan types each re-implementing IsRefusable as their own
// StartsWith("REFUSE:") scan - so the prefix was load-bearing production behavior that a
// reworded message could silently turn off, and AppMessages carried a "keep it" comment
// instead of a type.
public enum PlanSeverity
{
    Check,    // advisory: the plan still runs, the user is told what it will cost
    Refuse,   // blocking: nothing is written, the plan reports why
}

public readonly record struct PlanWarning(PlanSeverity Severity, string Text)
{
    public static PlanWarning Refuse(string text) => new(PlanSeverity.Refuse, text);
    public static PlanWarning Check(string text) => new(PlanSeverity.Check, text);

    // The prefix now EXISTS only for display - nothing branches on it any more.
    public override string ToString() => $"{(Severity == PlanSeverity.Refuse ? "REFUSE" : "CHECK")}: {Text}";
}

public static class PlanWarnings
{
    // The single refusal test, replacing the three hand-rolled copies (MovePlan, BatchMovePlan,
    // ChangesetPlan). Each plan's IsRefusable delegates here; IExecutablePlan can't just declare
    // a default member because callers reach it through the concrete types.
    public static bool AnyRefusal(this IEnumerable<PlanWarning> warnings) =>
        warnings.Any(w => w.Severity == PlanSeverity.Refuse);

    // The one place plan warnings become a single user-facing string (pane status, PushResult.Error).
    public static string Join(this IEnumerable<PlanWarning> warnings) => string.Join("; ", warnings);
}
