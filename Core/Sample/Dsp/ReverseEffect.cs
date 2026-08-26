namespace KronosScreenRemote;

// Plays the chosen range backwards, in place - length is unchanged, so nothing outside
// [startFrame, endFrameExclusive) moves and the sample's own markers stay meaningful.
// A whole-buffer reverse is just this with the full range, so there's no separate
// "reverse all" variant; SampleEditorViewModel picks the range (selection if there is
// one, otherwise everything) before constructing this.
//
// Unrelated to the Looping tab's "Reverse Loop" checkbox: that one is playback-preview
// only (no hardware-confirmed .KSF byte exists for it). This physically rewrites the
// PCM, so it survives the save and plays back reversed on the Kronos itself.
sealed class ReverseEffect(int startFrame, int endFrameExclusive) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = Math.Clamp(startFrame, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive, start, pcm.Length);
        var result = (short[])pcm.Clone();
        Array.Reverse(result, start, end - start);
        return result;
    }
}
