namespace KronosScreenRemote;

// Off-hardware checks for keymap-piano playback:
//
//  1. The Reverse flag (SMD1 flags bit 0x40, hardware-confirmed via the Sample Editor's
//     own tooltip - "reverses playback direction of the whole sample," unconditional on
//     loop state) used to be silently dropped by every ONE-SHOT playback path
//     (PlaySelectedSample's non-loop branch, PlayZoneAtKey/PlayAtKey/PlayStereoAtKey) -
//     only the LOOPING path ever read it. OneShotSampleWaveProvider now has a real
//     backward-read mode; these checks pin its byte-for-byte output against a
//     hand-computed expectation, both mono and stereo.
//  2. SamplePlayback.Generation - the mechanism ReleasePianoKey (mouse-up/lost-capture on
//     the keymap piano) uses to decide "is the sound I started still the one playing, or
//     has something else (the transport Play button, another key) taken over the single
//     output slot since" - is exercised directly against Stop() (which changes it
//     unconditionally, without needing a real audio device), both the "nothing happened
//     since" and "something happened since" cases, so a regression that makes the token
//     always/never match is caught either direction.
//
// Deliberately does NOT drive SamplePlayback.Play*/PlayZoneAtKey end-to-end - every
// Play* method opens a real WasapiOut device (Start()), which this suite's other
// playback-adjacent tests (SamplePhase8SelfTests) already avoid for the same reason.
// The VM-level wiring (PlayZoneAtKey picking Boost/Loop/Reverse off the ZONE's own
// sample, and the keymap's mouse-hold actually silencing the output) is an owed
// click-through - see Commit Notes.md.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase16SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── OneShotSampleWaveProvider, reverse=true, mono ──────────────────────────
        // 5 frames: [10,11,12,13,14]. Forward from frame 1 would read 11,12,13,14.
        // Reverse from frame 1 reads the SAME bounds in the OPPOSITE direction: starts
        // at the last frame (14) and reads DOWN TO (inclusive) frame 1 - 14,13,12,11.
        {
            short[] pcm = [10, 11, 12, 13, 14];
            var provider = new OneShotSampleWaveProvider(pcm, null, 44100, startFrame: 1, reverse: true);
            var buf = new byte[20]; // room for 10 frames, only 4 exist in bounds
            int read = provider.Read(buf, 0, buf.Length);
            short[] got = new short[read / 2];
            for (int i = 0; i < got.Length; i++) got[i] = BitConverter.ToInt16(buf, i * 2);
            Check("reverse-mono-reads-expected-frame-count", read == 8);
            Check("reverse-mono-reads-backward-from-end-to-startframe", got.SequenceEqual<short>([14, 13, 12, 11]));
            // PositionFrame decrements once more past the last frame actually written
            // (11, at buffer index 1) - lands on 0, one below the lower bound - the same
            // "one past the last frame read" convention the forward path already has
            // (its own PositionFrame sits at _endFrame, not _endFrame - 1, once exhausted).
            Check("reverse-mono-position-decrements-one-past-bound", provider.PositionFrame == 0);
        }

        // ── OneShotSampleWaveProvider, reverse=true, stereo ────────────────────────
        {
            short[] left = [1, 2, 3];
            short[] right = [10, 20, 30];
            var provider = new OneShotSampleWaveProvider(left, right, 44100, startFrame: 0, reverse: true);
            var buf = new byte[20];
            int read = provider.Read(buf, 0, buf.Length);
            Check("reverse-stereo-reads-all-3-frames", read == 12);
            // Frame order should be 2,1,0 (reversed); each frame is (L,R) interleaved.
            Check("reverse-stereo-first-frame-is-last-source-frame",
                BitConverter.ToInt16(buf, 0) == 3 && BitConverter.ToInt16(buf, 2) == 30);
            Check("reverse-stereo-last-frame-is-first-source-frame",
                BitConverter.ToInt16(buf, 8) == 1 && BitConverter.ToInt16(buf, 10) == 10);
        }

        // ── OneShotSampleWaveProvider, reverse=true, forward playback unaffected ───
        // Regression guard: adding the reverse path must not perturb the existing
        // forward fast-path (BlockCopy) mono behavior.
        {
            short[] pcm = [10, 11, 12, 13, 14];
            var provider = new OneShotSampleWaveProvider(pcm, null, 44100, startFrame: 1, reverse: false);
            var buf = new byte[20];
            int read = provider.Read(buf, 0, buf.Length);
            short[] got = new short[read / 2];
            for (int i = 0; i < got.Length; i++) got[i] = BitConverter.ToInt16(buf, i * 2);
            Check("forward-mono-unaffected-by-reverse-path", got.SequenceEqual<short>([11, 12, 13, 14]));
        }

        // ── LoopingSampleProvider, reverse=true - intro must ALSO read backward ────
        // Bug: the intro (pre-loop attack) always read forward regardless of `reverse`,
        // so a Reverse+Loop sample played its attack forward and only started reversing
        // once it reached the loop - report was "plays forward, and then reverses at
        // the loop." 10 frames [100..109], loop = [6,9) (frames 6,7,8). Expected: intro
        // reads backward from the buffer's last frame (9) down to Loop Start (6)
        // inclusive - 9,8,7,6 - THEN hands off to the existing backward loop-repeat
        // (8,7,6 repeating).
        {
            short[] pcm = [100, 101, 102, 103, 104, 105, 106, 107, 108, 109];
            var provider = new LoopingSampleProvider(pcm, null, 44100, sampleStartFrame: 0, loopStartFrame: 6, loopEndFrame: 9, reverse: true);
            var buf = new byte[14]; // 7 frames: 4 intro + 3 of the first loop repeat
            int read = provider.Read(buf, 0, buf.Length);
            short[] got = new short[read / 2];
            for (int i = 0; i < got.Length; i++) got[i] = BitConverter.ToInt16(buf, i * 2);
            Check("reverse-loop-intro-reads-backward-from-end-to-loopstart",
                got.SequenceEqual<short>([109, 108, 107, 106, 108, 107, 106]));
        }

        // ── SamplePlayback.Generation - ReleasePianoKey's own building block ──────
        {
            var playback = new SamplePlayback();
            int gen0 = playback.Generation;
            Check("generation-stable-with-no-activity", playback.Generation == gen0);

            playback.Stop(); // simulates "some playback activity happened" without needing a real device
            int gen1 = playback.Generation;
            Check("stop-changes-generation", gen1 != gen0);

            // ReleasePianoKey's exact check: token still equal to current -> "nothing
            // else happened since, safe to stop." Token snapshotted BEFORE another
            // Stop() -> stale -> must NOT match (this is the negative control for the
            // stuck-note bug class: a release firing after the transport Play button
            // took over must not stop the button's own playback).
            int tokenBeforeIntervening = playback.Generation;
            Check("token-matches-when-nothing-intervenes", tokenBeforeIntervening == playback.Generation);
            playback.Stop(); // an intervening "something else played/stopped" event
            Check("token-goes-stale-after-intervening-activity", tokenBeforeIntervening != playback.Generation);
        }

        return fails;
    }
}
