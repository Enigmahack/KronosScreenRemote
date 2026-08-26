namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this session's bug-fix/redesign batch: LoopingSampleProvider's
// new intro-then-loop (and reverse-loop) frame sequencing, OneShotSampleWaveProvider's
// multi-channel Read/PositionFrame, SampleEditorViewModel.SetMarker's choke-point
// behavior (Use Zero snapping, Loop Lock length preservation, the "Loop Start can never
// precede Sample Start" ordering invariant, and stereo mirroring), NearestZeroCrossing's
// bounded search, and the 128-zone cap. The interactive pieces (actual marker dragging,
// actual audio output, the Combine/Split pane-visibility swap) are verified visually
// instead (--sample-editor-visual-check). Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase8SelfTests
{
    public static List<string> SelfTest()
    {
        var settingsPath = Path.Combine(Storage.DataDir, "settings.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try { return RunChecks(); }
        finally
        {
            if (settingsBackup != null) File.WriteAllBytes(settingsPath, settingsBackup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    static List<string> RunChecks()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── LoopingSampleProvider: plays sampleStart -> loopEnd once (the "intro"),
        //    then repeats [loopStart, loopEnd) forward indefinitely ──
        {
            short[] samples = [0, 10, 20, 30, 40, 50]; // frames 0..5
            var provider = new LoopingSampleProvider(samples, null, 44100, sampleStartFrame: 0, loopStartFrame: 2, loopEndFrame: 5, reverse: false);
            var buf = new byte[22]; // 11 frames: 5-frame intro + two 3-frame loop passes
            provider.Read(buf, 0, buf.Length);
            var frames = new short[11];
            Buffer.BlockCopy(buf, 0, frames, 0, buf.Length);
            Check("loop-intro-then-forward-loop",
                frames.SequenceEqual(new short[] { 0, 10, 20, 30, 40, 20, 30, 40, 20, 30, 40 }));
        }

        // ── LoopingSampleProvider: reverse=true - the intro ALSO reads backward ──
        // The intro mirrors the FORWARD intro's own span [sampleStart, loopEnd) backward
        // - from the buffer's true last frame down to Loop Start - before handing off to
        // the loop-repeat (unchanged below). sampleStartFrame plays no role in the
        // reverse case (there's no "where reverse audio begins" marker on real
        // hardware).
        {
            short[] samples = [0, 10, 20, 30, 40, 50]; // frames 0..5, loop = frames [2,5)
            var provider = new LoopingSampleProvider(samples, null, 44100, sampleStartFrame: 0, loopStartFrame: 2, loopEndFrame: 5, reverse: true);
            var buf = new byte[22];
            provider.Read(buf, 0, buf.Length);
            var frames = new short[11];
            Buffer.BlockCopy(buf, 0, frames, 0, buf.Length);
            // Intro backward, frame 5 -> frame 2 inclusive: 50,40,30,20. Then the
            // loop-repeat (frame 4 -> frame 2, wrapping): 40,30,20,40,30,20,40.
            Check("loop-intro-then-reverse-loop",
                frames.SequenceEqual(new short[] { 50, 40, 30, 20, 40, 30, 20, 40, 30, 20, 40 }));
        }

        // ── LoopingSampleProvider: reverse loop on a STEREO (interleaved) buffer keeps
        //    L/R paired within each frame, through the (now also backward) intro too -
        //    the reverse branch writes whole frames (WriteFrame(buffer, ..., frame)),
        //    so this specifically catches an off-by-one that would swap channels or
        //    split a frame instead of reversing frame ORDER only. sampleStartFrame(0)
        //    < loopStartFrame(1) so there's a real intro before the loop. ──
        {
            // 4 stereo frames, L/R deliberately far apart so a channel swap is obvious:
            // frame0=(1,-1) [before the loop, never played in reverse], frame1=(100,-100),
            // frame2=(200,-200), frame3=(300,-300) [loop region].
            short[] left = [1, 100, 200, 300];
            short[] right = [-1, -100, -200, -300];
            var provider = new LoopingSampleProvider(left, right, 44100, sampleStartFrame: 0, loopStartFrame: 1, loopEndFrame: 4, reverse: true);
            var buf = new byte[28]; // 7 stereo frames: 3-frame backward intro + 4 of the reverse loop repeat
            provider.Read(buf, 0, buf.Length);
            var frames = new short[14];
            Buffer.BlockCopy(buf, 0, frames, 0, buf.Length);
            // Intro backward, frame 3 -> frame 1 inclusive: (300,-300)(200,-200)(100,-100)
            // - frame 0 (1,-1) is BEFORE Loop Start and is never reached, same as real
            // hardware never plays material before Loop Start once reversed. Then the
            // loop-repeat (frame 3 -> frame 1, wrapping): (300,-300)(200,-200)(100,-100)(300,-300)
            // - L and R stay paired throughout.
            Check("stereo-reverse-loop-keeps-channels-paired", frames.SequenceEqual(new short[]
            {
                300, -300, 200, -200, 100, -100,
                300, -300, 200, -200, 100, -100, 300, -300,
            }));
        }

        // ── OneShotSampleWaveProvider: multi-channel (interleaved) Read + PositionFrame,
        //    and correctly stops (returns 0) once the buffer is exhausted rather than
        //    looping or reading garbage ──
        {
            short[] left = [1, 3]; // 2 stereo frames: (1,2), (3,4)
            short[] right = [2, 4];
            var provider = new OneShotSampleWaveProvider(left, right, 44100);
            var buf = new byte[16]; // room for 4 frames, only 2 exist
            int read = provider.Read(buf, 0, buf.Length);
            Check("oneshot-stereo-reads-only-available-bytes", read == 8);
            var frames = new short[4];
            Buffer.BlockCopy(buf, 0, frames, 0, read);
            Check("oneshot-stereo-preserves-interleaving", frames.SequenceEqual(new short[] { 1, 2, 3, 4 }));
            Check("oneshot-position-after-full-read", provider.PositionFrame == 2);
            Check("oneshot-second-read-returns-zero", provider.Read(buf, 0, buf.Length) == 0);
        }

        // ── SampleImportBuilder: the 128-zone cap is enforced, not just documented ──
        {
            var m = new KmpMultisample { Name = "Cap", Mno1 = 77 };
            var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase8_selftest_cap");
            if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
            Directory.CreateDirectory(scratchRoot);
            var kmpPath = Path.Combine(scratchRoot, "Cap.KMP");

            for (int i = 0; i < SampleImportBuilder.MaxZonesPerMultisample; i++)
                SampleImportBuilder.AddSampleZone(m, kmpPath, $"S{i}", [1, 2, 3], 44100, i, i);
            Check("zone-cap-reached-exactly", m.Zones.Count == SampleImportBuilder.MaxZonesPerMultisample);

            bool threw = false;
            try { SampleImportBuilder.AddSampleZone(m, kmpPath, "Overflow", [1, 2, 3], 44100, 127, 127); }
            catch (InvalidOperationException) { threw = true; }
            Check("zone-cap-129th-throws", threw);
            Check("zone-cap-not-silently-added", m.Zones.Count == SampleImportBuilder.MaxZonesPerMultisample);
        }

        RunMarkerChokePointChecks(Check);

        return fails;
    }

    // Builds a minimal real collection (one mono multisample, one zone, a KsfSample
    // with hand-picked PCM) to exercise SampleEditorViewModel.SetMarker end-to-end -
    // the same scratch-collection discipline SamplePhase7SelfTests uses for the
    // stereo-mirroring checks, reused here for the marker choke point itself.
    static void RunMarkerChokePointChecks(Action<string, bool> check)
    {
        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase8_selftest_marker");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "Marker.KSC");
        var ksc = new KscCollection { Entries = ["Marker.KMP"] };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "Marker"));
        ksc.Save(kscPath);

        var kmpPath = Path.Combine(scratchRoot, "Marker", "Marker.KMP");
        var kmp = new KmpMultisample { Name = "Marker", Mno1 = 0 };
        kmp.Zones.Add(new KmpZone { Filename = "MS000000.KSF", OriginalKey = 60, TopKey = 60 });
        kmp.Save(kmpPath);

        var ksfDir = Path.Combine(scratchRoot, "Marker", "Marker");
        Directory.CreateDirectory(ksfDir);
        // A crossing at frame 3 (15 -> -5), and never crosses again after frame 3 -
        // deliberately shaped so NearestZeroCrossing(pcm, 0) has exactly one crossing to
        // find (frame 0 itself is the buffer edge, no i-1 to test against).
        var sample = new KsfSample { Name = "Marker", SampleRate = 44100 };
        sample.SetSamples([5, 10, 15, -5, -10, 5, 10]);
        sample.Save(Path.Combine(ksfDir, "MS000000.KSF"));

        SampleEditorViewModel NewVm()
        {
            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            var zoneNode = FindZone(vm.Roots, "MS000000.KSF");
            vm.SelectNode(zoneNode);
            return vm;
        }

        // ── Ordering invariant: Loop Start can never precede Sample Start ──
        {
            var vm = NewVm();
            vm.SetMarker(SampleMarkerKind.SampleStart, 3);
            check("marker-sample-start-set", vm.SampleSampleStart == 3);
            check("marker-loop-floored-to-new-sample-start", vm.SampleLoopStart >= 3 && vm.SampleLoopEnd >= 3);

            vm.SetMarker(SampleMarkerKind.LoopStart, 1); // 1 < SampleStart(3) - must clamp up to 3
            check("marker-loop-start-cannot-precede-sample-start", vm.SampleLoopStart == 3);
        }

        // ── Loop Lock: moving one edge shifts the other to preserve length ──
        {
            var vm = NewVm();
            vm.SetMarker(SampleMarkerKind.SampleStart, 0);
            vm.SetMarker(SampleMarkerKind.LoopStart, 1);
            vm.SetMarker(SampleMarkerKind.LoopEnd, 5); // length 4
            vm.LoopLockEnabled = true;
            vm.SetMarker(SampleMarkerKind.LoopStart, 2); // locked -> end should follow to keep length 4
            check("loop-lock-preserves-length", vm.SampleLoopStart == 2 && vm.SampleLoopEnd == 6);
        }

        // ── MoveLoopRegion (whole-region drag): dragging the block so its left edge
        //    would land before Sample Start must stop the WHOLE BLOCK at that wall,
        //    preserving its length - not clamp only the left edge and leave the right
        //    edge behind, which would silently shrink the loop toward zero length. The
        //    test sample here is only 7 frames, so these stay small on purpose. ──
        {
            var vm = NewVm();
            vm.SetMarker(SampleMarkerKind.SampleStart, 3);
            vm.MoveLoopRegion(4, 6); // length 2, both edges already >= SampleStart(3)
            check("move-loop-region-normal-case", vm.SampleLoopStart == 4 && vm.SampleLoopEnd == 6);

            vm.MoveLoopRegion(1, 3); // dragged past Sample Start(3) - length 2 must survive
            check("move-loop-region-stops-at-sample-start-wall", vm.SampleLoopStart == 3);
            check("move-loop-region-preserves-length-at-wall", vm.SampleLoopEnd - vm.SampleLoopStart == 2);
        }

        // ── Use Zero: snaps to the nearest zero-crossing, not the raw proposed frame ──
        {
            var vm = NewVm();
            vm.UseZeroCrossing = true;
            // pcm = [5, 10, 15, -5, -10, 5, 10] - from frame 0, the only crossing is at
            // frame 3 (15 -> -5): NearestZeroCrossing must walk outward and find it.
            vm.SetMarker(SampleMarkerKind.SampleStart, 0);
            check("use-zero-snaps-to-crossing", vm.SampleSampleStart == 3);
        }

        // ── Use Zero: a signal with NO crossing anywhere falls back to the original
        //    frame (bounded search, not an infinite loop) ──
        {
            var scratch2 = Path.Combine(scratchRoot, "NoCross");
            Directory.CreateDirectory(scratch2);
            var noCrossSample = new KsfSample { Name = "NoCross", SampleRate = 44100 };
            noCrossSample.SetSamples([5, 10, 15, 20, 25]); // never touches/crosses zero
            noCrossSample.Save(Path.Combine(ksfDir, "MS000001.KSF"));
            kmp.Zones.Add(new KmpZone { Filename = "MS000001.KSF", OriginalKey = 61, TopKey = 61 });
            kmp.Save(kmpPath);

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS000001.KSF"));
            vm.UseZeroCrossing = true;
            vm.SetMarker(SampleMarkerKind.SampleStart, 2);
            check("use-zero-no-crossing-falls-back-unchanged", vm.SampleSampleStart == 2);
        }
    }

    static SampleTreeNode? FindZone(IEnumerable<SampleTreeNode> nodes, string filename)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef?.Zone.Filename == filename) return node;
            var found = FindZone(node.Children, filename);
            if (found != null) return found;
        }
        return null;
    }
}
