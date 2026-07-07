using System.ComponentModel;

namespace KronosScreenRemote;

interface ISysExService : INotifyPropertyChanged
{
    string PerformanceDisplay { get; }

    bool IsAvailable { get; }

    // CC# the Kronos VALUE slider transmits (default 18). Incoming CCs with this
    // controller number drive ValueSliderChanged so the UI slider can follow the
    // hardware. Assignment-dependent on the Kronos; settable to match.
    int ValueSliderCc { get; set; }

    void Start(string host, int ctrlPort);

    void Reset();

    void RefreshNow();

    void NotifyUserActivity();

    event Action<int>? InitialModeDetected;

    // Fired (on the UI thread) when the Kronos transmits a Mode Change (SysEx
    // func 0x4E) over the live MIDI stream. Argument is the STATE-equivalent
    // mode (1-7). This is the authoritative, event-driven mode source; screen
    // detection is only a fallback.
    event Action<int>? ModeChanged;

    // Fired (on the UI thread) when an incoming CC matching ValueSliderCc is
    // seen on the live MIDI stream. Argument is the 0-127 controller value.
    event Action<int>? ValueSliderChanged;

    event Action<SysExTrafficEntry>? SysExTraffic;

    // Apply MIDI/SysEx settings. Safe to call before or after Start().
    // midiMonitorEnabled — when false, the MIDI stream monitor is stopped.
    // proactivePoll      — when true, polls on a fixed interval; otherwise only on-change triggers.
    void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges);

    // Send raw MIDI bytes via MIDI_SEND on the control port.
    // Fires SysExTraffic for both the TX bytes and the OK/ERR response.
    Task<bool> SendMidiAsync(string hexBytes);
}
