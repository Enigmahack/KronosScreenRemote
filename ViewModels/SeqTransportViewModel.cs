using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KronosScreenRemote.ViewModels;

// Sequencer Record / Start-Stop state for the footer transport row. Record and Start
// are front-panel toggle keys - one physical press flips the hardware state, so every
// press sends the same command regardless of which way IsRecording/IsPlaying lands;
// the properties only drive the view's depressed/icon visuals.
//
// The same physical REC/WRITE key is also how Setlist/Combi/Program/Global save the
// current edit ("Write") - CurrentMode (kept in sync by MainWindow.SetModeButton) drives
// which of the transport row / the separate Save button is enabled for a given mode.
partial class SeqTransportViewModel : ObservableObject
{
    readonly ICtrlSender _ctrl;

    public SeqTransportViewModel(ICtrlSender ctrl) => _ctrl = ctrl;

    // Kept in sync by MainWindow.ApplyDeviceFamilyUi. Record (Sequence-mode transport row) and
    // Save (Setlist/Combi/Program/Global) share ONE physical key on Kronos (SEQ_REC) - hence
    // sharing a single command there historically - but are genuinely DIFFERENT physical buttons
    // on Nautilus: Record is MS2 ("SEQ REC"), Save is front-panel "F" (wire EXIT - your QA slot
    // F is programmed as Write/Save), and Start/Stop is MS3 (swapped with Pause's MP6 per your
    // 2026-10-08 correction, reversing the 2026-09-22 one - see StartStop's own comment). Hence Save is its own RelayCommand
    // below rather than reusing Record's.
    public DeviceFamily DeviceFamily { get; set; } = DeviceFamily.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransportEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    Mode _currentMode = Mode.Unknown;

    // Locate/Rewind/Fast-Forward/Pause/Record/Start only mean anything in Sequence mode.
    public bool IsTransportEnabled => CurrentMode == Mode.Sequence;

    // REC/WRITE doubles as Save in these four modes; Sampling/Disk get neither role.
    public bool IsSaveEnabled => CurrentMode is Mode.Setlist or Mode.Combi or Mode.Program or Mode.Global;

    [ObservableProperty] bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    bool _isPlaying;

    public bool IsStopped => !IsPlaying;

    // Stopping the transport also stops any in-progress recording (matches the hardware -
    // there's no "recording while stopped" state). Starting playback does NOT touch Record:
    // arming Record before pressing Start is a normal record-then-play workflow.
    partial void OnIsPlayingChanged(bool value)
    {
        if (!value) IsRecording = false;
    }

    [RelayCommand]
    void Record() => _ctrl.Send(DaemonCommand.Button(
        DeviceFamily == DeviceFamily.Nautilus ? "MS2" : PanelButton.SeqRecord.Token()));

    // Nautilus token swapped with Pause's (MainWindow.xaml.cs's "Seq Pause" registry entry) per
    // your 2026-09-22 correction - the Start/Stop BUTTON is unchanged, only which scan code it
    // sends changed (was MP6 "SEQ PAUSE", now MS3 "SEQ PLAY/PAUSE").
    [RelayCommand]
    void StartStop() => _ctrl.Send(DaemonCommand.Button(
        DeviceFamily == DeviceFamily.Nautilus ? "MS3" : PanelButton.SeqStart.Token()));

    // Front-panel "F" on Nautilus (wire EXIT - see this class's own header comment); Kronos
    // keeps sending Record's own token, since Write/Record really is the same physical key there.
    [RelayCommand]
    void Save() => _ctrl.Send(DaemonCommand.Button(
        DeviceFamily == DeviceFamily.Nautilus ? PanelButton.Exit.Token() : PanelButton.SeqRecord.Token()));

    // The Kronos refuses a mode change while SEQ RECORD/START is armed, so any confirmed
    // mode change - or a disconnect - means hardware already has them off. Call this as a
    // guard against a desync (e.g. the front panel was used directly and this state is stale).
    public void Reset() => IsRecording = IsPlaying = false;
}
