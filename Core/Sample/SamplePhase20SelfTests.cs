namespace KronosScreenRemote;

// Off-hardware check for one real bug: LoopingSampleProvider's loop bounds were
// `readonly`, so a live loop-marker drag while a loop was already playing (Sample
// Editor's OnWaveformLoopRegionChanged/SetMarker -> ApplySampleFieldsTo ->
// SamplePlayback.UpdateLoopBounds) had nowhere to land - the provider kept looping on
// whatever bounds Read() was constructed with until Stop()/Play restarted it.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase20SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // 100-frame mono ramp (pcm[i] == i) so each frame's own played value identifies it.
        var pcm = new short[100];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)i;

        var provider = new LoopingSampleProvider(pcm, null, 44100,
            sampleStartFrame: 0, loopStartFrame: 0, loopEndFrame: 20, reverse: false);
        var buf = new byte[4]; // one mono 16-bit frame

        int ReadFrame()
        {
            provider.Read(buf, 0, buf.Length);
            return (short)(buf[0] | (buf[1] << 8));
        }

        // Drain the intro plus most of a first loop pass so the provider is mid-repeat,
        // then retarget the loop live - exactly what a marker drag mid-playback does.
        for (int i = 0; i < 25; i++) ReadFrame();
        provider.UpdateLoopBounds(50, 70);

        // The very next wrap must land on the NEW start (50), not the old one (0) -
        // confirms the retarget takes effect on the next loop repeat, not mid-repeat.
        int prev = -1, wrapTarget = -1;
        for (int i = 0; i < 200 && wrapTarget < 0; i++)
        {
            int frame = ReadFrame();
            if (prev >= 0 && frame < prev) wrapTarget = frame;
            prev = frame;
        }
        Check("loop-bounds-update-lands-on-new-start", wrapTarget == 50);

        bool staysInNewRange = true;
        for (int i = 0; i < 40; i++)
        {
            int frame = ReadFrame();
            if (frame < 50 || frame >= 70) staysInNewRange = false;
        }
        Check("loop-bounds-update-stays-in-new-range", staysInNewRange);

        return fails;
    }
}
