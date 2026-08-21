namespace KronosScreenRemote;

// In-app waveform clipboard for the Sample Editor's Cut/Copy/Paste - deliberately NOT
// the Windows clipboard (raw PCM has no standard clipboard format worth interoperating
// with outside this app, and OS clipboard round-tripping would need its own encode/
// decode layer for zero real benefit here). One slot, process-lifetime, mirrors how a
// typical single-track waveform editor's clipboard works.
static class SampleClipboard
{
    public static short[]? Pcm { get; private set; }
    public static int SampleRate { get; private set; }
    public static bool HasContent => Pcm is { Length: > 0 };

    public static void Set(short[] pcm, int sampleRate)
    {
        Pcm = (short[])pcm.Clone();
        SampleRate = sampleRate;
    }
}
