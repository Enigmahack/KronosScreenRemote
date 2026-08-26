namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for the Move tool's ApplyChannelMove
// (SampleEditorViewModel.ApplyChannelMove, driven by SampleWaveformControl's
// whole-waveform Move-mode drag via SampleEditorWindow.xaml.cs's OnWaveformPaneMoved).
// Twice-revised (2026-08-26) per explicit feedback, both times tightening the model:
//   1. A negative drag no longer touches the OTHER channel at all - to offset the pair
//      the other way, the user drags the OTHER channel's own pane instead, through this
//      exact same self-only path (targetPartner is chosen by which PANE was dragged, not
//      by the delta's sign).
//   2. SampleStart/LoopStart/LoopEnd are NEVER touched by a channel move, in either
//      direction - "the loop is an overlay pinned to a frame number, moving the audio
//      under it must not drag it along." And a leftward drag is clamped to EXACTLY how
//      much padding THIS channel move feature itself has added and not yet trimmed back
//      (_moveToolPadding) - not to the buffer's total length - so it can never eat into
//      real audio no matter how far past 0 it's dragged: a "hard stop," not a floor that
//      still lets the drag through partway into content that was never silence.
// Pins: Combine mode refuses; targetPartner selects self vs partner independently of
// sign; positive delta pads that channel's own leading edge (frame count grows, content
// is silence-then-original, SampleStart/LoopStart/LoopEnd all UNCHANGED, the OTHER
// channel untouched); negative delta trims up to the tracked padding balance off that
// SAME channel's own leading edge (never more, markers still unchanged, content reverts
// to byte-identical original once fully trimmed back) and becomes a genuine no-op once
// that balance hits 0, even when asked for far more; EditDomain.PartnerSample's undo path
// restores the right side of a partner-only edit; a partner-only edit registers as a
// pending save (RegisterDirtyPartnerSample) the same way every other mirrored edit
// already does; and Undo/Redo clear the padding balance (so a stale balance can never
// authorize trimming real audio after PCM has been rolled back to an earlier state by
// undo). Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase19SelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase19_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        var kscPath = Path.Combine(scratchRoot, "MoveTest.KSC");
        var collection = new KscCollection { Path = kscPath };
        Directory.CreateDirectory(Path.Combine(scratchRoot, "MoveTest"));
        collection.Save(kscPath);

        var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(collection, kscPath, "MoveTest", 0);
        static short[] MakeAudio(int seed) => Enumerable.Range(0, 100).Select(i => (short)(seed + i)).ToArray();

        void WriteRealZone(KmpMultisample m, string kmpPath, short[] audio)
        {
            var filename = m.NextKsfFilename();
            m.Zones.Add(new KmpZone { Filename = filename, OriginalKey = 36, TopKey = 36 });
            var ksfDir = Path.Combine(Path.GetDirectoryName(kmpPath)!, Path.GetFileNameWithoutExtension(kmpPath));
            Directory.CreateDirectory(ksfDir);
            var ksf = new KsfSample { Name = "MoveTest", SampleRate = 44100 };
            ksf.SetSamples(audio);
            // A real Sample Start/Loop region away from 0/end, kept comfortably below
            // every frame count this file ever shrinks the buffer to (>= 90 throughout),
            // so "markers never move" can be asserted as an exact, unconditional value
            // rather than "unless ClampMarkersToBuffer had to intervene."
            ksf.SampleStart = 5;
            ksf.LoopStart = 10;
            ksf.LoopEnd = 90;
            ksf.Flags = 0; // loop ON (0x80 clear)
            ksf.Save(Path.Combine(ksfDir, filename));
        }

        var leftAudio = MakeAudio(1000);
        var rightAudio = MakeAudio(2000);
        WriteRealZone(left, leftPath, leftAudio);
        WriteRealZone(right, rightPath, rightAudio);
        left.Save(leftPath);
        right.Save(rightPath);
        collection.Save(kscPath);

        var vm = new SampleEditorViewModel();
        vm.OpenCollection(kscPath);
        var leftZoneNode = vm.Roots.Single().Children.SingleOrDefault(c => c.MultisampleRef?.Path == leftPath)?.Children.FirstOrDefault();
        Check("fixture-left-zone-node-found", leftZoneNode != null);
        if (leftZoneNode == null) return fails;

        vm.SelectNode(leftZoneNode);
        Check("fixture-stereo-pair-resolved", vm.HasStereoPair);
        if (!vm.HasStereoPair) return fails;

        void CheckSelfMarkersUnchanged(string label) => Check($"{label}-self-markers-unchanged",
            vm.SampleSampleStart == 5 && vm.SampleLoopStart == 10 && vm.SampleLoopEnd == 90);
        void CheckPartnerMarkersUnchanged(string label) => Check($"{label}-partner-markers-unchanged",
            vm.PartnerSampleStart == 5 && vm.PartnerLoopStart == 10 && vm.PartnerLoopEnd == 90);

        // ── Combine mode: Move must refuse (ApplyChannelMove requires SplitLR) ──
        vm.SplitLR = false;
        int beforeCombine = vm.SampleFrameCount;
        vm.ApplyChannelMove(targetPartner: false, 10);
        Check("combine-mode-move-is-a-no-op", vm.SampleFrameCount == beforeCombine);

        // ── Split mode, self: pad, partial trim, exact-remaining trim, then a genuine
        //    no-op once the padding balance hits 0 - never touching markers or the
        //    partner at any point. ──
        vm.SplitLR = true;
        int origSelfFrames = vm.SampleFrameCount; // 100
        int origPartnerFrames = vm.PartnerSampleWaveform!.Length; // 100
        CheckSelfMarkersUnchanged("baseline");

        vm.ApplyChannelMove(targetPartner: false, 7); // pad +7 -> 107, padding=7
        Check("self-pad-grows-by-delta", vm.SampleFrameCount == origSelfFrames + 7);
        Check("self-pad-leaves-partner-untouched", vm.PartnerSampleWaveform!.Length == origPartnerFrames);
        CheckSelfMarkersUnchanged("self-pad");
        var afterPad = vm.SampleWaveform!;
        Check("self-pad-content-leading-silence", afterPad.Take(7).All(v => v == 0));
        Check("self-pad-content-original-audio-preserved", afterPad.Skip(7).SequenceEqual(leftAudio));

        vm.ApplyChannelMove(targetPartner: false, -4); // trim 4 of the 7 padded -> 103, padding=3
        Check("self-partial-trim-shrinks-by-delta", vm.SampleFrameCount == origSelfFrames + 3);
        CheckSelfMarkersUnchanged("self-partial-trim");

        vm.ApplyChannelMove(targetPartner: false, -100); // ask for way more than the remaining 3 -> clamps to 3, back to 100 exactly
        Check("self-remaining-trim-clamps-to-available-padding", vm.SampleFrameCount == origSelfFrames);
        CheckSelfMarkersUnchanged("self-remaining-trim");
        Check("self-fully-trimmed-content-byte-identical-to-original", vm.SampleWaveform!.SequenceEqual(leftAudio));

        int frameCountBeforeNoOpTrim = vm.SampleFrameCount;
        vm.ApplyChannelMove(targetPartner: false, -1); // padding balance is 0 now - hard stop, must be a true no-op
        Check("self-trim-past-zero-padding-is-a-hard-stop", vm.SampleFrameCount == frameCountBeforeNoOpTrim);
        Check("self-hard-stop-content-still-byte-identical", vm.SampleWaveform!.SequenceEqual(leftAudio));
        Check("self-hard-stop-leaves-partner-untouched", vm.PartnerSampleWaveform!.Length == origPartnerFrames);

        // ── Undo/Redo the self pad (ordinary EditDomain.Sample). Only 3 real edits
        //    pushed an undo entry above - the padding-past-zero attempt was a true no-op
        //    and pushed nothing - so 3 Undo() calls fully unwinds the clamp-trim, the
        //    partial trim, and the original pad, in that LIFO order. ──
        vm.Undo(); vm.Undo(); vm.Undo();
        Check("self-section-fully-unwound", vm.SampleFrameCount == origSelfFrames);
        CheckSelfMarkersUnchanged("post-undo");
        vm.Redo();
        Check("redo-self-pad-reapplies", vm.SampleFrameCount == origSelfFrames + 7);
        vm.Undo(); // back to baseline - also clears _moveToolPadding, confirmed by the next block's fresh pad below

        // ── Split mode, targetPartner: same self-only-per-target rule, applied to the
        //    OTHER object - the replacement for "drag left to push the sibling," now done
        //    by dragging the sibling's OWN pane instead. ──
        Check("partner-baseline-untouched", vm.PartnerSampleWaveform!.Length == origPartnerFrames);
        CheckPartnerMarkersUnchanged("partner-baseline");

        vm.ApplyChannelMove(targetPartner: true, 11); // pad partner +11
        Check("partner-pad-leaves-self-untouched", vm.SampleFrameCount == origSelfFrames);
        Check("partner-pad-grows-by-delta", vm.PartnerSampleWaveform!.Length == origPartnerFrames + 11);
        CheckPartnerMarkersUnchanged("partner-pad");
        var partnerAfterPad = vm.PartnerSampleWaveform!;
        Check("partner-pad-content-leading-silence", partnerAfterPad.Take(11).All(v => v == 0));
        Check("partner-pad-content-original-audio-preserved", partnerAfterPad.Skip(11).SequenceEqual(rightAudio));

        // Clamp-to-padding ("hard stop") test right after the pad, BEFORE any Undo/Redo -
        // Undo/Redo deliberately clear _moveToolPadding (its own comment: a stale balance
        // must never be trusted after PCM has been rolled back/forward by undo), so
        // trimming a REDONE pad is a separate, later concern (checked below) and must not
        // be conflated with this one.
        vm.ApplyChannelMove(targetPartner: true, -100); // ask for far more than the 11 padded -> clamps, never eats rightAudio
        Check("partner-trim-clamps-to-available-padding-not-buffer-length", vm.PartnerSampleWaveform!.Length == origPartnerFrames);
        Check("partner-trim-content-byte-identical-to-original", vm.PartnerSampleWaveform!.SequenceEqual(rightAudio));
        Check("partner-trim-leaves-self-untouched", vm.SampleFrameCount == origSelfFrames);

        // ── Undo/Redo across the partner-only domain - the whole reason
        //    EditDomain.PartnerSample exists: Ctrl+Z here must restore _partnerSample,
        //    NOT try (and silently fail) to restore _selectedSample, which neither edit
        //    above ever touched. Two real edits were pushed (the +11 pad, the clamped
        //    -11 trim); two Undo()s fully unwinds them. ──
        vm.Undo(); vm.Undo();
        Check("partner-section-fully-unwound", vm.PartnerSampleWaveform!.Length == origPartnerFrames);
        Check("undo-partner-section-self-still-untouched", vm.SampleFrameCount == origSelfFrames);
        vm.Redo();
        Check("redo-partner-pad-reapplies", vm.PartnerSampleWaveform!.Length == origPartnerFrames + 11);
        vm.Redo();
        Check("redo-partner-trim-reapplies", vm.PartnerSampleWaveform!.Length == origPartnerFrames);

        // ── Dirty tracking: a partner-only edit must register the PARTNER's .KSF path
        //    (not its .KMP - TryGetPendingSampleInfo/_dirtySamples key on the sample
        //    file) as pending, or Save Sample would silently drop it. ──
        var rightKsfPath = right.Zones.Single().KsfPath(rightPath);
        Check("partner-edit-marks-partner-path-pending-save", vm.TryGetPendingSampleInfo(rightKsfPath) != null);

        return fails;
    }
}
