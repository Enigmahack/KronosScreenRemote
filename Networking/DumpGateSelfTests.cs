namespace KronosScreenRemote;

// Deterministic, off-hardware self-test for DumpGate — the one part of the transport-lifecycle
// race fix that's testable without a live stream (the CTS/monitor races are timing-bound and
// are verified against real hardware). Locks in the two behaviors that were racy before:
// same-generation overlap keeps the loop paused until the last dump ends, and an orphaned
// old-generation End can't un-pause a fresh generation's dump. Wired into --librarian-selftest.
static class DumpGateSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── Baseline: Begin pauses, matched End resumes ──
        {
            var g = new DumpGate();
            Check("idle-not-active", !g.Active);
            int e = g.Begin();
            Check("begin-active", g.Active);
            g.End(e);
            Check("end-resumes", !g.Active);
        }

        // ── Overlap: two dumps in the same generation; the loop stays paused until BOTH end ──
        {
            var g = new DumpGate();
            int a = g.Begin();
            int b = g.Begin();
            Check("overlap-active", g.Active);
            g.End(a);
            Check("overlap-still-active-after-first", g.Active);   // the bool bug un-paused here
            g.End(b);
            Check("overlap-resumes-after-last", !g.Active);
        }

        // ── Transport switch: an orphaned old-generation dump's End must not touch the new one ──
        {
            var g = new DumpGate();
            int oldEpoch = g.Begin();     // old-generation dump in flight
            g.NewGeneration();            // transport swapped out from under it → depth reset to 0
            Check("newgen-clears-orphan", !g.Active);
            int newEpoch = g.Begin();     // a fresh dump on the new transport
            Check("newgen-dump-active", g.Active);
            g.End(oldEpoch);              // orphan finishes late — must be a no-op
            Check("orphan-end-is-noop", g.Active);
            g.End(newEpoch);
            Check("newgen-dump-resumes", !g.Active);
        }

        return fails;
    }
}
