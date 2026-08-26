namespace KronosScreenRemote;

// Subtracts the buffer's mean sample value, recentering the waveform on zero. A DC
// offset costs headroom (the trace sits off-center, so one side clips before the other
// on Normalize) and it's the reason "Use Zero" can fail to find any zero-crossing at
// all - NearestZeroCrossing legitimately returns null on a signal that never returns to
// the centre line, which is exactly what a DC-offset recording does.
//
// Whole-buffer only, deliberately: the offset is a property of the recording chain, and
// removing it from just a selection would create a step discontinuity (an audible click)
// at each edge of that selection.
//
// The mean is computed in `long` - a full-scale 32-bit-frame-count buffer would overflow
// an int sum long before it overflowed the sample range.
sealed class DcOffsetEffect : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        if (pcm.Length == 0) return pcm;

        long sum = 0;
        foreach (var s in pcm) sum += s;
        int offset = (int)Math.Round((double)sum / pcm.Length);
        if (offset == 0) return pcm;

        var result = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            result[i] = (short)Math.Clamp(pcm[i] - offset, short.MinValue, short.MaxValue);
        return result;
    }

    // Exposed for the status message ("removed N of DC offset") and for self-tests -
    // the same measurement Apply makes, without applying anything.
    public static int MeasureOffset(short[] pcm)
    {
        if (pcm.Length == 0) return 0;
        long sum = 0;
        foreach (var s in pcm) sum += s;
        return (int)Math.Round((double)sum / pcm.Length);
    }
}
