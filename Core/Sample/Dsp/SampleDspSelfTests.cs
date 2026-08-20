namespace KronosScreenRemote;

// Off-hardware checks for Core/Sample/Dsp/* and SampleEditUndo.cs. No SoundTouch
// output is asserted bit-exact against a reference (that would just be testing
// SoundTouch's own internals) - tempo/pitch checks assert the properties a caller can
// actually rely on: frame-count direction and no wraparound clipping on a synthetic
// sine buffer. Wired into App.xaml.cs's --librarian-selftest.
static class SampleDspSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        static short[] SineWave(int frames, int sampleRate, double freqHz, double amplitude = 0.8)
        {
            var pcm = new short[frames];
            for (int i = 0; i < frames; i++)
                pcm[i] = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * freqHz * i / sampleRate));
            return pcm;
        }

        // ── CropEffect: deterministic, bit-exact ──
        {
            short[] pcm = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            var cropped = new CropEffect(2, 6).Apply(pcm, 44100);
            Check("crop-exact-slice", cropped.SequenceEqual(new short[] { 3, 4, 5, 6 }));

            var clampedStart = new CropEffect(-5, 3).Apply(pcm, 44100);
            Check("crop-clamps-negative-start", clampedStart.SequenceEqual(new short[] { 1, 2, 3 }));

            var clampedEnd = new CropEffect(7, 1000).Apply(pcm, 44100);
            Check("crop-clamps-past-end", clampedEnd.SequenceEqual(new short[] { 8, 9, 10 }));

            var empty = new CropEffect(5, 5).Apply(pcm, 44100);
            Check("crop-zero-length", empty.Length == 0);
        }

        // ── GainNormalizeEffect ──
        {
            short[] pcm = [1000, -2000, 3000, -1500];
            var normalized = new GainNormalizeEffect(0.0).Apply(pcm, 44100);
            int peak = normalized.Max(s => Math.Abs((int)s));
            Check("gain-normalize-peak-near-fullscale", peak >= short.MaxValue - 5 && peak <= short.MaxValue);
            // Sign/relative-magnitude pattern must be preserved (pure scale, no shape change).
            Check("gain-normalize-preserves-shape", Math.Sign(normalized[1]) == Math.Sign(pcm[1]) &&
                Math.Abs((int)normalized[2]) > Math.Abs((int)normalized[0]));

            var silent = new short[100];
            var stillSilent = new GainNormalizeEffect().Apply(silent, 44100);
            Check("gain-normalize-silence-unchanged", stillSilent.All(s => s == 0));
        }

        // ── FadeEffect ──
        {
            var pcm = Enumerable.Repeat((short)10000, 100).ToArray();
            var faded = new FadeEffect(10, 10).Apply(pcm, 44100);
            Check("fade-in-starts-near-zero", Math.Abs((int)faded[0]) < 1000);
            Check("fade-in-reaches-full-after-ramp", Math.Abs((int)faded[50]) == 10000);
            Check("fade-out-ends-near-zero", Math.Abs((int)faded[^1]) < 1000);
        }

        // ── SilenceTrimEffect ──
        {
            short[] pcm = [0, 0, 1, 5000, -5000, 3, 0, 0, 0];
            var trimmed = new SilenceTrimEffect(10).Apply(pcm, 44100);
            Check("silence-trim-both-ends", trimmed.SequenceEqual(new short[] { 5000, -5000 }));

            var allSilent = new SilenceTrimEffect(10).Apply(new short[50], 44100);
            Check("silence-trim-all-silent-to-empty", allSilent.Length == 0);
        }

        // ── TempoPitchProcessor: frame-count direction + no clipping-wraparound ──
        {
            var sine = SineWave(44100, 44100, 440);

            var slower = TempoPitchProcessor.ChangeTempo(sine, 44100, 0.5); // half speed -> ~2x longer
            Check("tempo-slower-is-longer", slower.Length > sine.Length * 1.7);

            var faster = TempoPitchProcessor.ChangeTempo(sine, 44100, 2.0); // double speed -> ~half length
            Check("tempo-faster-is-shorter", faster.Length < sine.Length * 0.6);

            var pitchedUp = TempoPitchProcessor.ChangePitchSemitones(sine, 44100, 12); // +1 octave
            // Pitch-only shift keeps duration roughly constant (SoundTouch's whole point).
            Check("pitch-shift-preserves-length-roughly",
                Math.Abs(pitchedUp.Length - sine.Length) < sine.Length * 0.1);

            bool NoWraparoundClipping(short[] p)
            {
                // A genuine hard-clip run (many consecutive samples pinned at the exact
                // int16 boundary) is the wraparound-overflow symptom this guards against -
                // a real, valid loud sample can legitimately touch max briefly, so check
                // for a SUSTAINED run, not any single sample at the boundary.
                int run = 0;
                foreach (var s in p)
                {
                    run = (s == short.MaxValue || s == short.MinValue) ? run + 1 : 0;
                    if (run > 20) return false;
                }
                return true;
            }
            Check("tempo-change-no-wraparound-clipping", NoWraparoundClipping(slower) && NoWraparoundClipping(faster));
            Check("pitch-change-no-wraparound-clipping", NoWraparoundClipping(pitchedUp));

            var empty = TempoPitchProcessor.ChangeTempo([], 44100, 1.5);
            Check("tempo-change-empty-input-empty-output", empty.Length == 0);
        }

        // ── SampleEditUndo: byte-cap eviction ──
        {
            var undo = new SampleEditUndo(byteCap: 100);
            var a = new byte[40];
            var b = new byte[40];
            var c = new byte[40];
            undo.RecordBeforeEdit(a);
            undo.RecordBeforeEdit(b);
            Check("undo-two-under-cap-no-eviction", undo.UndoCount == 2 && undo.TakeEvictedCount() == 0);

            undo.RecordBeforeEdit(c); // 120 bytes total > 100 cap -> evicts 'a'
            Check("undo-eviction-past-cap", undo.UndoCount == 2);
            Check("undo-eviction-counted", undo.TakeEvictedCount() == 1);
            Check("undo-eviction-count-resets-after-take", undo.TakeEvictedCount() == 0);

            var current = new byte[40];
            var restored = undo.Undo(current);
            Check("undo-restores-most-recent-first", restored != null && restored.SequenceEqual(c));
            Check("undo-can-redo-after-undo", undo.CanRedo);

            var redone = undo.Redo(new byte[40]);
            Check("redo-restores-undone-value", redone != null && redone.SequenceEqual(c));

            var fresh = new SampleEditUndo(byteCap: 1000);
            Check("undo-empty-stack-returns-null", fresh.Undo(new byte[10]) == null);
            Check("redo-empty-stack-returns-null", fresh.Redo(new byte[10]) == null);

            fresh.RecordBeforeEdit(new byte[10]);
            fresh.Undo(new byte[10]);
            fresh.RecordBeforeEdit(new byte[10]); // a fresh edit must clear the redo stack
            Check("new-edit-clears-redo-stack", !fresh.CanRedo);
        }

        return fails;
    }
}
