using System.ComponentModel;

namespace KronosScreenRemote;

interface ISysExService : INotifyPropertyChanged
{
    string PerformanceDisplay { get; }

    bool IsAvailable { get; }

    void Start(string host, int ctrlPort);

    void Reset();

    void RefreshNow();

    void NotifyUserActivity();

    event Action<int>? InitialModeDetected;

    event Action<SysExTrafficEntry>? SysExTraffic;

    // Apply MIDI/SysEx settings. Safe to call before or after Start().
    // midiMonitorEnabled — when false, the MIDI stream monitor is stopped.
    // proactivePoll      — when true, polls on a fixed interval; otherwise only on-change triggers.
    void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges);

    // Send raw MIDI bytes via MIDI_SEND on the control port.
    // Fires SysExTraffic for both the TX bytes and the OK/ERR response.
    Task<bool> SendMidiAsync(string hexBytes);
}
