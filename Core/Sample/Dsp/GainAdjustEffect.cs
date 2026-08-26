namespace KronosScreenRemote;

// Fixed-dB gain change - the Amplify/Soften toolbar presets (+1/+3/+6 dB, -1/-3/-6 dB).
// Deliberately separate from GainNormalizeEffect: that one targets a specific peak
// level regardless of the source, this one applies exactly the dB amount asked for,
// clipping if it overshoots full scale (the honest outcome of "amplify by a fixed
// amount" - silently re-normalizing instead would make Amplify indistinguishable from
// Normalize).
//
// Acts on [startFrame, endFrameExclusive) only, same convention as
// SilenceEffect/ReverseEffect - the caller (SampleEditorViewModel.ApplyGainAdjust, via
// SelectionOrWholeBuffer) passes the current selection when there is one, or the whole
// buffer otherwise, so the button applies to "the highlighted range if you've made one,
// the whole sample if you haven't" without this class needing to know about selection
// at all.
sealed class GainAdjustEffect(double decibels, int? startFrame = null, int? endFrameExclusive = null) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        double factor = Math.Pow(10.0, decibels / 20.0);
        int start = Math.Clamp(startFrame ?? 0, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive ?? pcm.Length, start, pcm.Length);
        var result = (short[])pcm.Clone();
        for (int i = start; i < end; i++)
            result[i] = (short)Math.Clamp(pcm[i] * factor, short.MinValue, short.MaxValue);
        return result;
    }
}
