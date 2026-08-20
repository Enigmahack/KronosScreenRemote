namespace KronosScreenRemote;

// Uniform shape every waveform edit implements - host-order (not KSF's on-disk
// big-endian) 16-bit signed PCM in, same out. Every consumer (SampleEditUndo,
// SampleEditorViewModel) routes through KsfPcm at the KsfSample boundary; effects
// themselves never touch big-endian bytes.
interface ISampleEffect
{
    short[] Apply(short[] pcm, int sampleRate);
}
