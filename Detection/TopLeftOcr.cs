namespace KronosScreenRemote;

// Detects changes in the top-left 140×55 pixel region of the raw 8bpp frame.
// Rows 0–26: mode banner.  Rows 27–55: help banner.
// Compares raw palette index bytes so palette overrides don't affect detection.
static class TopLeftOcr
{
    public const int RoiW = 140;
    public const int RoiH = 55;
    static readonly int RoiBytes = RoiW * RoiH;

    static byte[]? _last;

    // Returns true when the top-left region differs from the previous call.
    // Always updates the internal snapshot, so stable frames return false after the first.
    public static bool HasChanged(byte[] frame, int frameW)
    {
        if (frame.Length < frameW * RoiH) return false;
        var roi = Extract(frame, frameW);
        bool changed = _last == null || !roi.AsSpan().SequenceEqual(_last);
        _last = roi;
        return changed;
    }

    // Force-treat the next frame as a change (call on connect/disconnect).
    public static void Reset() => _last = null;

    static byte[] Extract(byte[] frame, int frameW)
    {
        var roi = new byte[RoiBytes];
        for (int y = 0; y < RoiH; y++)
            Array.Copy(frame, y * frameW, roi, y * RoiW, RoiW);
        return roi;
    }
}
