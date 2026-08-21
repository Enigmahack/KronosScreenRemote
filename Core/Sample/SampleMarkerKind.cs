namespace KronosScreenRemote;

// Which draggable/editable point on a sample's waveform an edit targets - shared
// between Views/SampleWaveformControl.cs (mouse-drag on the waveform) and
// SampleEditorViewModel.SetMarker, the single choke point every entry point (drag,
// typed field, Loop Lock, Use Zero snapping) routes through so the "Loop Start can
// never precede Sample Start" invariant and Loop Lock's length preservation can't be
// bypassed by editing one path and not another.
public enum SampleMarkerKind { SampleStart, LoopStart, LoopEnd }
