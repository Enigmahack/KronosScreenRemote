namespace KronosScreenRemote;

// Scales the whole buffer so its peak absolute sample hits targetPeakDb (default
// -0.1 dBFS, leaving a hair of headroom rather than exactly 0 dBFS). A silent buffer
// (peak 0) is returned unchanged rather than divide-by-zero amplified.
sealed class GainNormalizeEffect(double targetPeakDb = -0.1) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        if (pcm.Length == 0) return pcm;

        int peak = 0;
        foreach (var s in pcm)
        {
            int abs = Math.Abs((int)s);
            if (abs > peak) peak = abs;
        }
        if (peak == 0) return (short[])pcm.Clone();

        double targetPeakLinear = Math.Pow(10, targetPeakDb / 20.0) * short.MaxValue;
        double scale = targetPeakLinear / peak;

        var result = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = (short)Math.Clamp(pcm[i] * scale, short.MinValue, short.MaxValue);
        return result;
    }
}
