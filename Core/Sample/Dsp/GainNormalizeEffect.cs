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
sealed class GainNormalizeEffect(double targetPeakDb = -0.1, int? sharedPeak = null) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        if (pcm.Length == 0) return pcm;

        int peak = sharedPeak ?? ComputePeak(pcm);
        if (peak == 0) return (short[])pcm.Clone();

        double targetPeakLinear = Math.Pow(10, targetPeakDb / 20.0) * short.MaxValue;
        double scale = targetPeakLinear / peak;

        var result = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = (short)Math.Clamp(pcm[i] * scale, short.MinValue, short.MaxValue);
        return result;
    }

    public static int ComputePeak(short[] pcm)
    {
        int peak = 0;
        foreach (var s in pcm)
        {
            int abs = Math.Abs((int)s);
            if (abs > peak) peak = abs;
        }
        return peak;
    }
}
