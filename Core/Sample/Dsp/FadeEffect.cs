namespace KronosScreenRemote;

// Linear fade-in over the first fadeInFrames and/or fade-out over the last
// fadeOutFrames. Either can be 0 to skip that side. Frame counts are clamped to the
// buffer length rather than throwing on a too-large request.
sealed class FadeEffect(int fadeInFrames, int fadeOutFrames) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        var result = (short[])pcm.Clone();
        if (result.Length == 0) return result;

        int fadeIn = Math.Clamp(fadeInFrames, 0, result.Length);
        for (int i = 0; i < fadeIn; i++)
        {
            double gain = (double)i / fadeIn;
            result[i] = (short)Math.Clamp(result[i] * gain, short.MinValue, short.MaxValue);
        }

        int fadeOut = Math.Clamp(fadeOutFrames, 0, result.Length);
        for (int i = 0; i < fadeOut; i++)
        {
            double gain = (double)i / fadeOut;
            int idx = result.Length - 1 - i;
            result[idx] = (short)Math.Clamp(result[idx] * gain, short.MinValue, short.MaxValue);
        }
        return result;
    }
}
