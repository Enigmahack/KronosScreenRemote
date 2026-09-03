namespace KronosScreenRemote;

// Off-hardware self-test for MidiTransportCoordinator's defer/apply logic (finding 1's fix):
// a transport swap that would tear down the active transport while a Librarian write is in
// flight (IMidiBackendControl.DumpGateActive) must be deferred rather than applied immediately,
// and applied later once the gate closes. Uses MidiTransportMode.Tcp throughout so Reevaluate
// never touches real USB hardware (its TCP branch just constructs a TcpMidiTransport and calls
// Start - no actual network probe), and drives TryApplyDeferredSwap directly rather than
// waiting on the real 3 s hot-plug timer. Wired into --librarian-selftest.
static class MidiTransportCoordinatorSelfTests
{
    sealed class FakeBackend : IMidiBackendControl
    {
        public bool IsAvailable => false;
        public bool DumpGateActive { get; set; }
        public int StartCount;
        public int ResetCount;
        public void Start(IKronosMidiTransport transport) => StartCount++;
        public void Reset() => ResetCount++;
        public Task<bool> RecheckAvailabilityAsync() => Task.FromResult(IsAvailable);
        public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges) { }
    }

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── Gate closed: a screen-connection change applies immediately, same as before ──
        {
            var backend = new FakeBackend();
            var coord = new MidiTransportCoordinator(backend);
            coord.ApplySettings(MidiTransportMode.Tcp, "");
            coord.SetScreenConnection(true, "10.0.0.5", 7374);
            Check("immediate-applies-now", backend.StartCount == 1);
            Check("immediate-sets-active", coord.ActiveDescription != null);
        }

        // ── Gate open: the same change is deferred, not applied ──
        int changedEvents;
        MidiTransportCoordinator deferredCoord;
        FakeBackend deferredBackend;
        {
            var backend = new FakeBackend();
            var coord = new MidiTransportCoordinator(backend);
            coord.ApplySettings(MidiTransportMode.Tcp, "");
            backend.DumpGateActive = true;

            changedEvents = 0;
            coord.ActiveTransportChanged += _ => changedEvents++;
            coord.SetScreenConnection(true, "10.0.0.5", 7374);

            Check("deferred-does-not-start", backend.StartCount == 0);
            Check("deferred-no-active-yet", coord.ActiveDescription == null);
            Check("deferred-fires-no-event", changedEvents == 0);

            deferredCoord = coord;
            deferredBackend = backend;
        }

        // ── Re-checking while STILL open stays deferred (the double-check in TryApplyDeferredSwap) ──
        {
            deferredCoord.TryApplyDeferredSwap();
            Check("still-open-stays-deferred", deferredBackend.StartCount == 0);
        }

        // ── Gate closes: the deferred swap is applied on the next check ──
        {
            deferredBackend.DumpGateActive = false;
            deferredCoord.TryApplyDeferredSwap();
            Check("closes-applies-deferred", deferredBackend.StartCount == 1);
            Check("closes-sets-active", deferredCoord.ActiveDescription != null);
            Check("closes-fires-event", changedEvents == 1);
        }

        // ── Applying twice is a no-op the second time (nothing left pending) ──
        {
            deferredCoord.TryApplyDeferredSwap();
            Check("no-double-apply", deferredBackend.StartCount == 1);
        }

        // ── A deferred DISCONNECT (tear down to nothing, not swap to another transport) also
        //    stays deferred while the gate is open - this is the actual erase-then-abandon shape
        //    finding 1 names: Reset() (Reevaluate's `desired == null` branch) is gated exactly
        //    the same way Start() is, since both live inside Reevaluate. ──
        {
            var backend = new FakeBackend();
            var coord = new MidiTransportCoordinator(backend);
            coord.ApplySettings(MidiTransportMode.Tcp, "");
            coord.SetScreenConnection(true, "10.0.0.5", 7374);   // connects (gate closed)
            Check("disconnect-setup-connected", backend.StartCount == 1);

            backend.DumpGateActive = true;
            coord.SetScreenConnection(false, "10.0.0.5", 7374);  // wants to tear down to nothing
            Check("disconnect-deferred-no-reset-yet", backend.ResetCount == 0);

            backend.DumpGateActive = false;
            coord.TryApplyDeferredSwap();
            Check("disconnect-applies-reset-after-gate-closes", backend.ResetCount == 1);
        }

        // ── TryApplyDeferredSwap on a coordinator with nothing pending is a safe no-op ──
        {
            var backend = new FakeBackend();
            var coord = new MidiTransportCoordinator(backend);
            coord.TryApplyDeferredSwap();
            Check("noop-when-nothing-pending", backend.StartCount == 0 && backend.ResetCount == 0);
        }

        return fails;
    }
}
