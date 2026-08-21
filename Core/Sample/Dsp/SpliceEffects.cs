namespace KronosScreenRemote;

// The three length-CHANGING edits, expressed as ISampleEffects rather than as ad-hoc
// array splices inside the ViewModel.
//
// That distinction is the whole point. SampleEditorViewModel.ApplyEffect is what
// replays an edit against the stereo partner in Combine mode, records both undo stacks,
// and re-clamps the markers afterwards. Cut and Paste used to bypass it and splice
// _selectedSample's array directly, so in a stereo pair only ONE channel changed
// length: SamplePlayback.Interleave then padded the shorter channel with silence and
// everything after the edit point played back time-offset between L and R. Routing them
// through the same ISampleEffect shape every other edit already uses makes that class
// of divergence unrepresentable rather than merely fixed once.

// Removes [startFrame, endFrameExclusive), closing the gap. The clipboard capture is
// the ViewModel's job (it reads the primary channel before applying this) - an effect
// is a pure function of (pcm, sampleRate) and has no business owning shared state.
sealed class DeleteRangeEffect(int startFrame, int endFrameExclusive) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = Math.Clamp(startFrame, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive, start, pcm.Length);
        if (end == start) return pcm;

        var result = new short[pcm.Length - (end - start)];
        Array.Copy(pcm, 0, result, 0, start);
        Array.Copy(pcm, end, result, start, pcm.Length - end);
        return result;
    }
}

// Replaces [startFrame, endFrameExclusive) with `clip`, or inserts at startFrame when
// the range is empty (a bare cursor position). Clip content at a different sample rate
// than the target is used as-is, no resample - visible in the resulting duration rather
// than silently "corrected", matching the behaviour the old inline paste documented.
sealed class PasteRangeEffect(int startFrame, int endFrameExclusive, short[] clip) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int start = Math.Clamp(startFrame, 0, pcm.Length);
        int end = Math.Clamp(endFrameExclusive, start, pcm.Length);

        var result = new short[pcm.Length - (end - start) + clip.Length];
        Array.Copy(pcm, 0, result, 0, start);
        Array.Copy(clip, 0, result, start, clip.Length);
        Array.Copy(pcm, end, result, start + clip.Length, pcm.Length - end);
        return result;
    }
}

// Inserts `frameCount` frames of silence at `atFrame`, pushing everything after it
// later.
sealed class InsertSilenceEffect(int atFrame, int frameCount) : ISampleEffect
{
    public short[] Apply(short[] pcm, int sampleRate)
    {
        int at = Math.Clamp(atFrame, 0, pcm.Length);
        int count = Math.Max(0, frameCount);
        if (count == 0) return pcm;

        var result = new short[pcm.Length + count];
        Array.Copy(pcm, 0, result, 0, at);
        Array.Copy(pcm, at, result, at + count, pcm.Length - at);
        return result;
    }
}
