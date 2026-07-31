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

    [RelayCommand] void Record()    => _ctrl.Send(DaemonCommand.Button(PanelButton.SeqRecord));
    [RelayCommand] void StartStop() => _ctrl.Send(DaemonCommand.Button(PanelButton.SeqStart));

    // The Kronos refuses a mode change while SEQ RECORD/START is armed, so any confirmed
    // mode change - or a disconnect - means hardware already has them off. Call this as a
    // guard against a desync (e.g. the front panel was used directly and this state is stale).
    public void Reset() => IsRecording = IsPlaying = false;
}
