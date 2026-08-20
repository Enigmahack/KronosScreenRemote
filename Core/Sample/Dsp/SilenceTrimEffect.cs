namespace KronosScreenRemote;

// Trims leading/trailing frames whose absolute amplitude is at or below
// thresholdAmplitude. A buffer that's silent throughout (or empty) returns empty
// rather than leaving one boundary frame behind.
sealed class SilenceTrimEffect(short thresholdAmplitude = 32) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = 0;
        while (start < pcm.Length && Math.Abs((int)pcm[start]) <= thresholdAmplitude) start++;
        int end = pcm.Length;
        while (end > start && Math.Abs((int)pcm[end - 1]) <= thresholdAmplitude) end--;

        var result = new short[end - start];
        Array.Copy(pcm, start, result, 0, result.Length);
        return result;
    }
}
