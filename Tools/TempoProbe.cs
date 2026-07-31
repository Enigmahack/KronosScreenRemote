namespace KronosScreenRemote;

using System.Diagnostics;

// THROWAWAY diagnostic - DELETE once the live BPM readout is implemented.
//
// Confirms the premise behind the clock-derived tempo readout before any transport-layer
// plumbing is committed: that the Kronos's MIDI clock (0xF8, 24 pulses/quarter-note) really
// does reach the client on each backend, and that the pulse timing computes to a stable,
// accurate BPM. Fed from the two points where F8 would otherwise be dropped/suppressed:
//   • UsbMidiTransport.OnShortMessage  (tag "usb")    - direct USB-MIDI
//   • MidiStreamParser.Process         (tag "stream") - the daemon's 9875 firehose
//
// Per-pulse work is deliberately bare (one timestamp + one counter write) so it's safe on
// the winmm callback thread the USB path protects. The BPM math runs only in the throttled
// log path, not per pulse. N ring timestamps span N-1 intervals - the off-by-one that would
// otherwise read ~2% high is handled explicitly below.
static class TempoProbe
{
    const int Window = 96;                       // ring of the last 96 pulses (4 quarter-notes @24PPQN)
    static readonly long[] _ring = new long[Window];
    static int  _idx;
    static long _count;
    static long _lastLogTicks;

    public static void Pulse(string tag)
    {
        long now = Stopwatch.GetTimestamp();
        _ring[_idx] = now;
        _idx = (_idx + 1) % Window;               // now points at the OLDEST retained sample
        _count++;

        if (_count < Window) return;              // wait for a full window
        if (now - _lastLogTicks < Stopwatch.Frequency) return;   // ~1x/sec
        _lastLogTicks = now;

        long oldest      = _ring[_idx];           // Window samples span oldest..now
        double spanSec   = (now - oldest) / (double)Stopwatch.Frequency;
        if (spanSec <= 0) return;
        const int intervals = Window - 1;         // N timestamps -> N-1 intervals
        double quarterSec = spanSec / (intervals / 24.0);
        double bpm        = 60.0 / quarterSec;
        AppLog.Info($"[tempo-probe:{tag}] {bpm:F1} BPM  ({intervals} intervals / {spanSec:F3}s)");
    }
}
