namespace KronosScreenRemote;

static class DetectionSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = HelpDetector.SelfTest();
        var detector = new TopLeftOcr();
        var frame = new byte[(TopLeftOcr.RoiW + 1) * TopLeftOcr.RoiH];

        void Check(string name, bool condition)
        {
            if (!condition) fails.Add(name);
        }

        Check("top-left-first-frame-changed", detector.HasChanged(frame, TopLeftOcr.RoiW + 1));
        Check("top-left-stable-frame-unchanged", !detector.HasChanged(frame, TopLeftOcr.RoiW + 1));
        frame[TopLeftOcr.RoiW + 1] = 1;
        Check("top-left-changed-pixel-detected", detector.HasChanged(frame, TopLeftOcr.RoiW + 1));
        detector.Reset();
        Check("top-left-reset-forces-change", detector.HasChanged(frame, TopLeftOcr.RoiW + 1));
        Check("top-left-short-frame-ignored", !detector.HasChanged(Array.Empty<byte>(), TopLeftOcr.RoiW + 1));
        return fails;
    }
}
