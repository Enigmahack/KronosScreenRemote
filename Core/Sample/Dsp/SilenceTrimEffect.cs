namespace KronosScreenRemote;

// Trims leading/trailing frames whose absolute amplitude is at or below
// thresholdAmplitude. A buffer that's silent throughout (or empty) returns empty
// rather than leaving one boundary frame behind.
//
// sharedBounds (optional): when trimming a stereo pair, each channel's own leading/
// trailing silence run can differ in length - trimming each independently would cut a
// DIFFERENT number of frames off each channel, offsetting the two selections relative
// to each other (the exact bug this parameter exists to prevent: only delete silence
// that's present in BOTH channels). The caller computes each channel's own (start, end)
// via ComputeBounds, takes the UNION of the two non-silent ranges (min start, max end),
// and passes that union in here so both channels are cropped to the identical [start,
// end) - the same "compute once, bake into one shared instance, replay against both
// channels" shape ApplyNormalize already uses for its shared peak.
sealed class SilenceTrimEffect(short thresholdAmplitude = 32, (int Start, int End)? sharedBounds = null) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        var (start, end) = sharedBounds is { } b
            ? (Math.Clamp(b.Start, 0, pcm.Length), Math.Clamp(b.End, 0, pcm.Length))
            : ComputeBounds(pcm, thresholdAmplitude);
        if (end < start) end = start;

        var result = new short[end - start];
        Array.Copy(pcm, start, result, 0, result.Length);
        return result;
    }

    public static (int Start, int End) ComputeBounds(short[] pcm, short thresholdAmplitude)
    {
        int start = 0;
        while (start < pcm.Length && Math.Abs((int)pcm[start]) <= thresholdAmplitude) start++;
        int end = pcm.Length;
        while (end > start && Math.Abs((int)pcm[end - 1]) <= thresholdAmplitude) end--;
        return (start, end);
    }
}
