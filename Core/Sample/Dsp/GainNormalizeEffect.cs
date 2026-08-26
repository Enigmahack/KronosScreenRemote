namespace KronosScreenRemote;

// Scales the whole buffer so its peak absolute sample hits targetPeakDb (default
// -0.1 dBFS, leaving a hair of headroom rather than exactly 0 dBFS). A silent buffer
// (peak 0) is returned unchanged rather than divide-by-zero amplified.
//
// `sharedPeak`, when supplied, is used INSTEAD of measuring this call's own pcm - the
// stereo mirroring case (SampleEditorViewModel.ApplyNormalize): a stereo pair must be
// normalized as a SINGLE track, scaled by whichever channel has the higher peak, not
// each channel independently to its own peak (which would silently shift the L/R
// balance - a quieter channel gets boosted more than its louder partner). The caller
// computes the shared peak across both channels first and bakes it into one effect
// instance, reused for both Apply() calls via ApplyEffect's own instance-reuse pattern.
// Acts on [startFrame, endFrameExclusive) only, same convention as
// SilenceEffect/ReverseEffect - the caller (SampleEditorViewModel.ApplyNormalize, via
// SelectionOrWholeBuffer) passes the current selection when there is one (both the peak
// measurement AND the scaling stay confined to it, so normalizing a highlighted range
// doesn't also boost/cut everything outside it), or the whole buffer otherwise.
sealed class GainNormalizeEffect(double targetPeakDb = -0.1, int? sharedPeak = null, int? startFrame = null, int? endFrameExclusive = null) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        if (pcm.Length == 0) return pcm;
        int start = Math.Clamp(startFrame ?? 0, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive ?? pcm.Length, start, pcm.Length);
        if (end == start) return (short[])pcm.Clone();

        int peak = sharedPeak ?? ComputePeak(pcm, start, end);
        if (peak == 0) return (short[])pcm.Clone();

        double targetPeakLinear = Math.Pow(10, targetPeakDb / 20.0) * short.MaxValue;
        double scale = targetPeakLinear / peak;

        var result = (short[])pcm.Clone();
        for (int i = start; i < end; i++)
            result[i] = (short)Math.Clamp(pcm[i] * scale, short.MinValue, short.MaxValue);
        return result;
    }

    public static int ComputePeak(short[] pcm) => ComputePeak(pcm, 0, pcm.Length);

    public static int ComputePeak(short[] pcm, int start, int end)
    {
        int peak = 0;
        for (int i = start; i < end; i++)
        {
            int abs = Math.Abs((int)pcm[i]);
            if (abs > peak) peak = abs;
        }
        return peak;
    }
}
