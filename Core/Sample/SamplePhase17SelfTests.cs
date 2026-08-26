namespace KronosScreenRemote;

using NAudio.Wave;

// Off-hardware checks for pan-control: PanningSampleProvider's
// gain math and mono-upmix/stereo-balance Read() behavior. Deliberately does NOT drive
// SamplePlayback.Pan or SamplePanControl's own mouse handling end-to-end - Play* still
// opens a real WasapiOut device (see SamplePhase16SelfTests's own comment for why that's
// avoided here too), and SamplePanControl's click/drag hit-testing is exercised the same
// way SampleVolumeControl's already is - not at all, by established precedent - it's a
// pure UI gesture with no state a self-test can usefully assert on beyond what direct
// property assignment already covers.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase17SelfTests
{
    // Fixed-value stereo/mono stub - Read() fills every requested sample with `left`
    // (and `right`, ignored for a 1-channel format) so PanningSampleProvider's output
    // is fully predictable regardless of frame count.
    sealed class ConstSampleProvider(float left, float right, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] = channels == 1 ? left : (i % 2 == 0 ? left : right);
            return count;
        }
    }

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }
        void CheckClose(string name, float actual, float expected, float tol = 0.01f) =>
            Check(name, Math.Abs(actual - expected) <= tol);

        // ── Mono source, hard Left (pan=0) - upmixed to stereo, fully in the Left
        //    channel, Right silent ──
        {
            var provider = new PanningSampleProvider(new ConstSampleProvider(1f, 0f, 1));
            provider.SetPan(0);
            Check("pan-output-format-is-always-stereo", provider.WaveFormat.Channels == 2);
            var buf = new float[8]; // 4 output frames
            int read = provider.Read(buf, 0, buf.Length);
            Check("mono-hard-left-reads-full-buffer", read == 8);
            CheckClose("mono-hard-left-fills-left-channel", buf[0], 1f);
            CheckClose("mono-hard-left-silences-right-channel", buf[1], 0f);
        }

        // ── Mono source, hard Right (pan=127) ──
        {
            var provider = new PanningSampleProvider(new ConstSampleProvider(1f, 0f, 1));
            provider.SetPan(127);
            var buf = new float[8];
            provider.Read(buf, 0, buf.Length);
            CheckClose("mono-hard-right-silences-left-channel", buf[0], 0f);
            CheckClose("mono-hard-right-fills-right-channel", buf[1], 1f);
        }

        // ── Mono source, Center (pan=64) - equal-power law, both channels near unity.
        //    NOT exactly equal (127 is odd, so 64 isn't the exact midpoint of 0..127 in
        //    continuous terms - a ~0.7036/0.7106 split, not 0.7071/0.7071) - a wider
        //    tolerance here is intentional, documenting that asymmetry rather than
        //    masking a real bug. ──
        {
            var provider = new PanningSampleProvider(new ConstSampleProvider(1f, 0f, 1));
            provider.SetPan(64);
            var buf = new float[2]; // 1 output frame
            provider.Read(buf, 0, buf.Length);
            CheckClose("mono-center-left-near-equal-power", buf[0], 0.707f, 0.02f);
            CheckClose("mono-center-right-near-equal-power", buf[1], 0.707f, 0.02f);
        }

        // ── Stereo source (already 2 channels) - pan BALANCES the existing channels
        //    rather than summing/re-deriving them, so a hard pan silences the OPPOSITE
        //    channel while leaving the same-side channel's own distinct value intact. ──
        {
            var provider = new PanningSampleProvider(new ConstSampleProvider(100f, -100f, 2));
            provider.SetPan(0); // hard Left
            var buf = new float[2];
            provider.Read(buf, 0, buf.Length);
            CheckClose("stereo-hard-left-keeps-left-channel-value", buf[0], 100f, 1f);
            CheckClose("stereo-hard-left-silences-right-channel", buf[1], 0f, 1f);
        }
        {
            var provider = new PanningSampleProvider(new ConstSampleProvider(100f, -100f, 2));
            provider.SetPan(127); // hard Right
            var buf = new float[2];
            provider.Read(buf, 0, buf.Length);
            CheckClose("stereo-hard-right-silences-left-channel", buf[0], 0f, 1f);
            CheckClose("stereo-hard-right-keeps-right-channel-value", buf[1], -100f, 1f);
        }

        return fails;
    }
}
