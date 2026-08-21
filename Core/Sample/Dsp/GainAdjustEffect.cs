namespace KronosScreenRemote;

// Fixed-dB gain change - the context menu's Amplify/Soften presets (+1/+3/+6 dB,
// -1/-3/-6 dB). Deliberately separate from GainNormalizeEffect: that one targets a
// specific peak level regardless of the source, this one applies exactly the dB amount
// asked for, clipping if it overshoots full scale (the honest outcome of "amplify by a
// fixed amount" - silently re-normalizing instead would make Amplify indistinguishable
// from Normalize).
sealed class GainAdjustEffect(double decibels) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        double factor = Math.Pow(10.0, decibels / 20.0);
        var result = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = (short)Math.Clamp(pcm[i] * factor, short.MinValue, short.MaxValue);
        return result;
    }
}
