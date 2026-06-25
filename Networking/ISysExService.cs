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

    // Send raw MIDI bytes via MIDI_SEND on the control port.
    // Fires SysExTraffic for both the TX bytes and the OK/ERR response.
    Task<bool> SendMidiAsync(string hexBytes);
}
