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

    // When true, entering a program/combi whose name isn't cached triggers a single
    // debounced func-0x72 name fetch so the footer fills in without a full Sync.
    bool PullNamesOnChange { get; set; }

    // Start (or switch) the MIDI backend. The transport is chosen by the caller
    // (TCP daemon or direct USB); every SysEx feature works over either. Disposes
    // any previously-running transport.
    void Start(IKronosMidiTransport transport);

    void Reset();

    void RefreshNow();

    void NotifyUserActivity();

    // True when bulk SysEx dumps are possible (MIDI monitor enabled).
    bool CanDump { get; }

    // Dump one Set List (obj 0x0D) by number; null if unavailable. Collected off
    // the live stream, so it bypasses the daemon's single-message SYSEX limit.
    Task<SetListData?> DumpSetListAsync(int number);

    // Sweep every set list (obj 0x0D, 0..127) in one pass for "Sync All". Reports
    // (done, total, found-with-content); cancellable between set lists (progress is
    // preserved in the returned result). See SetListSyncResult for the Found /
    // ConfirmedEmpty / no-response split.
    Task<SetListSyncResult> DumpAllSetListsAsync(
        IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct);

    // Request every program/combi bank's names and capture them into the cache,
    // so program-change follow shows names with no per-change SysEx query. Reports
    // (banks done, banks total, names captured). Returns the final name count.
    Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct);

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
