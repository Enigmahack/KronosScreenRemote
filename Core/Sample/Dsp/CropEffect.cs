namespace KronosScreenRemote;

// Deterministic, bit-exact-verifiable: keeps [startFrame, endFrame) and nothing else.
// Callers that also care about LoopStart/LoopEnd/SampleStart staying in-range after a
// crop own re-deriving those themselves (KsfSample.ToBytes deliberately never
// auto-adjusts them - see its own doc comment).
sealed class CropEffect(int startFrame, int endFrameExclusive) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = Math.Clamp(startFrame, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive, start, pcm.Length);
        var result = new short[end - start];
        Array.Copy(pcm, start, result, 0, result.Length);
        return result;
    }
}
