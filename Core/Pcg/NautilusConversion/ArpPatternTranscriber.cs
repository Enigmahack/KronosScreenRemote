namespace KronosScreenRemote;

// Turns a captured KARMA performance (the notes a Kronos GE generated while a chord was held)
// into a 556-byte Nautilus Arp Pattern (SysEx obj 0x19 / PCG ABK1 record - same bytes in both).
// This is the only way a generative GE can reach the Nautilus: the two engines share no data
// format (nautilus_arp_engine_architecture.md). Lossy by construction - randomness, CC, bend
// and envelope output are not representable.
//
// Two encodings, chosen by simulated playback against the capture:
//  - Normal: tones are the held chord's notes (sorted) plus one pitch offset per step, so the
//    pattern follows whatever chord is played. A step whose notes need two offsets can't be
//    represented and keeps only the tones matching the smallest one.
//  - Fixed / All Tones: tones are up to 12 absolute note numbers, played regardless of the keys
//    held (hardware-confirmed). Exact for drum GEs and wide voicings, but no longer follows the chord.
static class ArpPatternTranscriber
{
    // `Group` numbers note-ons that belong to one step (same chord) in a live capture; -1 = unknown,
    // fall back to timing alone.
    public sealed record Note(double OnSec, int Pitch, int Velocity, double DurSec, int Group = -1);
    public enum Encoding { Normal, Fixed }
    // Score = fraction of steady-state steps whose simulated pitch set equals the capture's.
    // IntroSteps > 0: the GE played a one-time intro of that many steps before its loop; only the
    // loop is in the pattern (the Nautilus starts straight into it).
    public sealed record Result(byte[] Pattern, int Length, int StepsPerQuarter, List<string> Lossy,
        Encoding Mode, double Score, double NormalScore, double FixedScore, int IntroSteps = 0, bool Periodic = true);

    public const int PatternSize = 556, NameLen = 24, StepBase = 40, StepSize = 8, MaxSteps = 64;
    const int VelocityBucket = 4;
    const double PeriodMatch = 0.97;

    // Coarsest grid first; the Nautilus Scene Resolution has to be set to match.
    static readonly int[] Grids = { 2, 3, 4, 6, 8, 12 };

    // Nautilus Scene "ARP Resolution" index (bits 4-2 of the Scene flags byte). All six grids used here
    // are hardware-confirmed (--karma-ge-verify: Nautilus output matched SimulateStep on every step).
    public static int ResolutionIndex(int stepsPerQuarter) => stepsPerQuarter switch
    {
        12 => 0, 8 => 1, 6 => 2, 4 => 3, 3 => 4, 2 => 5, _ => 3,
    };

    // One step's steady-state content: pitch -> (median velocity, median duration).
    // Skip = first capture step that is scored; Phase = capture step that pattern step 0 stands for
    // (non-zero only when a one-time intro was dropped).
    sealed record Analysis(int Div, double StepSec, int Length, int Total, int Skip, int Phase, bool Periodic,
        Dictionary<int, HashSet<int>> Captured, Dictionary<int, Dictionary<int, (int Vel, double Dur)>> Loop);

    static Analysis Analyze(List<Note> ordered, double tempoBpm)
    {
        double Step(int div) => 60.0 / tempoBpm / div;
        double Phase(int div)
        {
            double st = Step(div), re = 0, im = 0;
            foreach (var n in ordered) { double a = 2 * Math.PI * n.OnSec / st; re += Math.Cos(a); im += Math.Sin(a); }
            return Math.Atan2(im, re) / (2 * Math.PI) * st;
        }
        double GridErr(int div)
        {
            double st = Step(div), ph = Phase(div);
            return ordered.Average(n => { double x = (n.OnSec - ph) / st; return Math.Abs(x - Math.Round(x)); });
        }

        int div = Grids.FirstOrDefault(d => GridErr(d) < 0.1, 4);
        double step = Step(div), phase = Phase(div);
        double t0 = phase + Math.Floor((ordered[0].OnSec - phase) / step + 0.5) * step;

        var steps = new Dictionary<int, List<Note>>();
        foreach (var (n, s) in AssignSteps(ordered, t0, step))
        {
            if (!steps.TryGetValue(s, out var list)) steps[s] = list = new();
            list.Add(n);
        }
        int total = steps.Keys.Max() + 1;

        // Velocity is part of the signature: accent cycles can be longer than the pitch cycle.
        string Sig(int i) => steps.TryGetValue(i, out var l)
            ? string.Join(",", l.GroupBy(n => n.Pitch).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Last().Velocity / VelocityBucket}"))
            : "";
        // KARMA's first steps can carry extra start-up notes, so they don't vote on the period, and
        // some GEs play a one-time intro (e.g. a multi-octave run) before settling into a loop. The
        // Nautilus pattern just loops, so the loop is what gets transcribed: search intro lengths up to
        // MaxIntro for the shortest period that explains PeriodMatch of the rest (one dropped note or
        // jittered onset must not force a longer loop) and repeats at least twice.
        int minSkip = Math.Min(2, total / 4);
        bool Explains(int L, int skip)
        {
            int n = Math.Max(0, total - L - skip);
            return n == 0 || Enumerable.Range(skip, n).Count(i => Sig(i) == Sig(i + L)) >= PeriodMatch * n;
        }
        (int L, int Skip)? found = null;
        foreach (int skip in new[] { minSkip, 4, 8, 12, 16, 24, 32 }.Where(k => k >= minSkip).Distinct())
        {
            int maxL = Math.Min(MaxSteps, (total - skip) / 2);
            if (maxL < 1 || total - skip < 16) break;
            int L = Enumerable.Range(1, maxL).FirstOrDefault(L => Explains(L, skip));
            if (L > 0 && (found == null || L < found.Value.L)) found = (L, skip);
        }
        // Phase: pattern step 0 = capture step `phase`. A real intro is dropped; the start-up skip is not.
        int length = found?.L ?? Math.Min(MaxSteps, total);
        int scoreFrom = found?.Skip ?? minSkip;
        int loopStart = found is { } f && f.Skip > minSkip ? f.Skip : 0;
        if (Environment.GetEnvironmentVariable("KSR_TRANSCRIBE_DEBUG") == "1")
        {
            foreach (var kv in steps.OrderBy(k => k.Key).Take(40)) Console.WriteLine($"  step {kv.Key}: {Sig(kv.Key)}");
            Console.WriteLine($"  grid div {div}, total {total}, chosen L {length}, intro {loopStart}, periodic {found != null}");
        }

        static T Median<T>(List<T> xs) { xs.Sort(); return xs[xs.Count / 2]; }
        var loop = new Dictionary<int, Dictionary<int, (int, double)>>();
        for (int s = 0; s < length; s++)
        {
            // Median over every full repetition: the first pass can carry a retrigger and the
            // last is cut short by the release. Without a period there is only the one pass.
            var reps = new List<List<Note>>();
            if (found != null)
            {
                for (int r = loopStart + s; r < total - 1; r += length)
                    if (steps.TryGetValue(r, out var rep)) reps.Add(rep);
            }
            if (reps.Count == 0 && steps.TryGetValue(loopStart + s, out var only)) reps.Add(only);
            var perPitch = new Dictionary<int, (List<int> Vel, List<double> Dur)>();
            foreach (var n in reps.SelectMany(rep => rep.GroupBy(n => n.Pitch).Select(g => g.Last())))
            {
                if (!perPitch.TryGetValue(n.Pitch, out var acc)) perPitch[n.Pitch] = acc = (new(), new());
                acc.Vel.Add(n.Velocity); acc.Dur.Add(n.DurSec);
            }
            // A pitch heard in fewer than half the repetitions is a start-up/release artifact.
            var kept = perPitch.Where(kv => kv.Value.Vel.Count * 2 >= reps.Count)
                .ToDictionary(kv => kv.Key, kv => (Median(kv.Value.Vel), Median(kv.Value.Dur)));
            if (kept.Count > 0) loop[s] = kept;
        }
        var captured = steps.ToDictionary(kv => kv.Key, kv => kv.Value.Select(n => n.Pitch).ToHashSet());
        return new Analysis(div, step, length, total, scoreFrom, loopStart, found != null, captured, loop);
    }

    // The capture's timestamps are PC arrival times, and the daemon's MIDI tap occasionally delivers
    // several steps in one burst (seen: ~60 ms late, then two steps at once). Arrival delay only ever
    // makes a note late, so each group is placed by its earliest arrival; two groups that land on one
    // step are separated by moving whichever costs less, preferring to put a late group back into a
    // free step before it.
    static List<(Note, int)> AssignSteps(List<Note> ordered, double t0, double step)
    {
        var groups = ordered.GroupBy(n => n.Group < 0 ? -1 - ordered.IndexOf(n) : n.Group)
            .Select(g => (Notes: g.ToList(), X: (g.Min(n => n.OnSec) - t0) / step)).OrderBy(g => g.X).ToList();
        if (ordered.All(n => n.Group < 0))   // no grouping information: timing only
            return ordered.Select(n => (n, (int)Math.Round((n.OnSec - t0) / step))).ToList();
        var at = groups.Select(g => (int)Math.Round(g.X)).ToArray();
        // Moves group j (and, if needed, up to MaxChain groups before it) one step earlier, keeping
        // every moved group within 1.5 steps of where it arrived.
        const int MaxChain = 4;
        bool ShiftBack(int j, int depth)
        {
            if (depth > MaxChain || groups[j].X - (at[j] - 1) > 1.5) return false;
            if (j > 0 && at[j] - 1 <= at[j - 1] && !ShiftBack(j - 1, depth + 1)) return false;
            at[j]--;
            return true;
        }
        for (int i = 1; i < at.Length; i++)
        {
            if (at[i] > at[i - 1]) continue;
            // Arrival delay only ever makes a note late, so a collision means the earlier group
            // arrived late: move it back if that fits, otherwise push this one forward.
            if (!ShiftBack(i - 1, 0)) at[i] = at[i - 1] + 1;
        }
        if (Environment.GetEnvironmentVariable("KSR_TRANSCRIBE_DEBUG") == "1")
            for (int i = 0; i < Math.Min(groups.Count, 45); i++)
                Console.WriteLine($"  group {i}: x {groups[i].X:0.00} -> {at[i]} [{string.Join(",", groups[i].Notes.Select(n => n.Pitch))}] g{groups[i].Notes[0].Group}");
        return groups.SelectMany((g, i) => g.Notes.Select(n => (n, at[i]))).ToList();
    }

    public static Result? Transcribe(IReadOnlyList<Note> notes, IReadOnlyList<int> heldNotes, double tempoBpm, string name,
        bool preferFixed = false)
    {
        if (notes.Count == 0 || heldNotes.Count == 0 || tempoBpm <= 0) return null;
        var held = heldNotes.OrderBy(n => n).ToArray();
        var a = Analyze(notes.OrderBy(n => n.OnSec).ToList(), tempoBpm);

        var (normal, normalLossy) = BuildNormal(a, held, name);
        var (fixedPat, fixedLossy) = BuildFixed(a, name);
        double ns = Score(normal, held, a), fs = Score(fixedPat, held, a);
        bool useFixed = preferFixed ? fs >= ns - 0.02 : fs > ns + 1e-9;
        return useFixed
            ? new Result(fixedPat, a.Length, a.Div, fixedLossy, Encoding.Fixed, fs, ns, fs, a.Phase, a.Periodic)
            : new Result(normal, a.Length, a.Div, normalLossy, Encoding.Normal, ns, ns, fs, a.Phase, a.Periodic);
    }

    static byte[] Header(Analysis a, string name, byte mode)
    {
        var d = new byte[PatternSize];
        Array.Copy(System.Text.Encoding.ASCII.GetBytes(name.PadRight(NameLen)), d, NameLen);
        d[24] = mode;                   // bits 3-2 Type = As Played, 1-0 Octave Up, bit 4/5 tone modes
        d[25] = (byte)a.Length;
        d[26] = 0;                      // x1 - resolution lives in the Scene
        for (int i = 28; i < 40; i++) d[i] = 60;
        for (int s = 0; s < MaxSteps; s++) { d[StepBase + s * StepSize + 1] = 50; d[StepBase + s * StepSize + 2] = 64; }   // rest defaults
        d[552] = 16; d[553] = 0; d[554] = 0x13;   // Category Off, 4/4 - as the factory Init pattern
        return d;
    }

    static void WriteStep(byte[] d, Analysis a, int s, int pitch, int mask, IEnumerable<(int Vel, double Dur)> notes)
    {
        var list = notes.ToList();
        int o = StepBase + s * StepSize;
        double gate = list.Max(n => n.Dur) / a.StepSec * 100;
        d[o] = (byte)(sbyte)pitch;
        d[o + 1] = (byte)(gate >= 99 ? 101 : Math.Clamp((int)Math.Round(gate), 1, 100));
        d[o + 2] = (byte)Math.Clamp((int)Math.Round(list.Average(n => n.Vel)), 1, 127);
        d[o + 4] = (byte)(mask & 0xFF);
        d[o + 5] = (byte)(mask >> 8);
    }

    static (byte[], List<string>) BuildNormal(Analysis a, int[] held, string name)
    {
        var d = Header(a, name, 0);
        var lossy = new List<string>();
        (int Tone, int Offset) ToneFor(int pitch)
        {
            var samePc = Enumerable.Range(0, held.Length).Where(k => ((pitch - held[k]) % 12 + 12) % 12 == 0).ToList();
            var pool = samePc.Count > 0 ? samePc : Enumerable.Range(0, held.Length).ToList();
            int k = pool.OrderBy(k => Math.Abs(pitch - held[k])).First();
            return (k, pitch - held[k]);
        }
        foreach (var (s, content) in a.Loop)
        {
            var mapped = content.Select(kv => (Map: ToneFor(kv.Key), kv.Value)).ToList();
            var offsets = mapped.Select(m => m.Map.Offset).Distinct().ToList();
            int pitch = offsets.OrderBy(Math.Abs).First();
            if (offsets.Count > 1)
                lossy.Add($"step {s + 1}: notes need pitch offsets {string.Join("/", offsets.OrderBy(x => x))}, kept {pitch:+0;-0;0}");
            var kept = mapped.Where(m => m.Map.Offset == pitch).ToList();
            if (Math.Abs(pitch) > 48) { lossy.Add($"step {s + 1}: pitch offset {pitch} clamped to ±48"); pitch = Math.Clamp(pitch, -48, 48); }
            WriteStep(d, a, s, pitch, kept.Aggregate(0, (acc, m) => acc | 1 << m.Map.Tone), kept.Select(m => m.Value));
        }
        return (d, lossy);
    }

    static (byte[], List<string>) BuildFixed(Analysis a, string name)
    {
        var d = Header(a, name, 0b0011_0000);   // Tone Mode = Fixed Note, Fixed Note Mode = All Tones
        var lossy = new List<string>();
        var tones = a.Loop.Values.SelectMany(c => c.Keys).GroupBy(p => p)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(12).Select(g => g.Key).OrderBy(p => p).ToList();
        for (int i = 0; i < tones.Count; i++) d[28 + i] = (byte)tones[i];
        foreach (var (s, content) in a.Loop)
        {
            var kept = content.Where(kv => tones.Contains(kv.Key)).ToList();
            if (kept.Count < content.Count) lossy.Add($"step {s + 1}: more than 12 distinct notes, dropped {content.Count - kept.Count}");
            if (kept.Count == 0) continue;
            WriteStep(d, a, s, 0, kept.Aggregate(0, (acc, kv) => acc | 1 << tones.IndexOf(kv.Key)), kept.Select(kv => kv.Value));
        }
        return (d, lossy);
    }

    // What the Nautilus plays on each step for `held` (sorted), per the hardware-confirmed rules:
    // Normal = held[tone] + offset, silent for tones beyond the held count; Fixed/All Tones =
    // tone note + offset; Fixed/As Played = the same but only for tones below the held count.
    public static HashSet<int> SimulateStep(byte[] pattern, IReadOnlyList<int> held, int step)
    {
        int length = Math.Max(1, (int)pattern[25]);
        int o = StepBase + step % length * StepSize;
        int mask = pattern[o + 4] | pattern[o + 5] << 8, offset = (sbyte)pattern[o];
        bool isFixed = (pattern[24] & 0x10) != 0, allTones = (pattern[24] & 0x20) != 0;
        var sorted = held.OrderBy(n => n).ToArray();
        var set = new HashSet<int>();
        for (int k = 0; k < 12; k++)
        {
            if ((mask >> k & 1) == 0) continue;
            if (isFixed) { if (allTones || k < sorted.Length) set.Add(pattern[28 + k] + offset); }
            else if (k < sorted.Length) set.Add(sorted[k] + offset);
        }
        return set;
    }

    static double Score(byte[] pattern, int[] held, Analysis a)
    {
        var range = Enumerable.Range(a.Skip, Math.Max(0, a.Total - 1 - a.Skip)).ToList();
        if (range.Count == 0) return 0;
        return range.Count(s => SimulateStep(pattern, held, ((s - a.Phase) % a.Length + a.Length) % a.Length)
                .SetEquals(a.Captured.GetValueOrDefault(s) ?? new HashSet<int>()))
            / (double)range.Count;
    }

    // Pairs note-ons with their note-offs from a raw MIDI capture (status bytes with channel).
    public static List<Note> NotesFromMidi(IEnumerable<(double Sec, byte[] Msg)> events)
    {
        // Note-ons with no note-off between them, arriving within ChordWindowSec, are one step's chord.
        const double ChordWindowSec = 0.015;
        var open = new Dictionary<int, (double On, int Vel, int Group)>();
        var notes = new List<Note>();
        int group = -1; double groupStart = double.NegativeInfinity; bool offSinceOn = true;
        foreach (var (t, m) in events)
        {
            if (m.Length < 3) continue;
            int st = m[0] & 0xF0, p = m[1];
            bool on = st == 0x90 && m[2] > 0, off = st == 0x80 || (st == 0x90 && m[2] == 0);
            if ((on || off) && open.Remove(p, out var o)) notes.Add(new Note(o.On, p, o.Vel, t - o.On, o.Group));
            if (off) offSinceOn = true;
            if (on)
            {
                if (offSinceOn || t - groupStart > ChordWindowSec) { group++; groupStart = t; }
                offSinceOn = false;
                open[p] = (t, m[2], group);
            }
        }
        return notes;
    }
}
