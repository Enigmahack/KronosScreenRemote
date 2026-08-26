namespace KronosScreenRemote;

// Zeroes a range without removing it - the "mute this bit"
// counterpart to Cut, which closes the gap. Length is unchanged, so every marker and
// the stereo partner's own alignment stay exactly where they were; that's the whole
// reason to have this alongside Cut rather than only Cut.
sealed class SilenceEffect(int startFrame, int endFrameExclusive) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = Math.Clamp(startFrame, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive, start, pcm.Length);
        var result = (short[])pcm.Clone();
        Array.Clear(result, start, end - start);
        return result;
    }
}
