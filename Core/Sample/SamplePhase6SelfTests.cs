namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware checks for this session's UI-feedback batch: note-name conversion,
// the _UserBank.KSC read guard, fixed-dB gain, and waveform clipboard (Cut/Copy/Paste)
// + selection-scoped fade + "loop from selection." The WPF controls themselves
// (SampleWaveformControl markers/zoom/grid, SampleKeymapControl, the VU meter) are
// verified visually instead (--sample-editor-visual-check) - nothing here re-tests
// rendering, only the underlying data/logic those controls display or that the
// waveform context menu drives. Wired into App.xaml.cs's --librarian-selftest.
static class SamplePhase6SelfTests
{
    public static List<string> SelfTest()
    {
        // Several blocks below call OpenCollection, which writes Recent Files to the
        // REAL settings.json (Storage.SaveSettings has no test-injectable override) -
        // same snapshot/restore discipline as SamplePhase5SelfTests, so a person
        // running --librarian-selftest on their own machine gets their real settings
        // back untouched.
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

        // ── MidiNoteName: round-trip against the app's own established C4=60
        //    convention ──
        {
            Check("note-c4-is-60", MidiNoteName.TryParse("C4") == 60);
            Check("note-60-is-c4", MidiNoteName.ToName(60) == "C4");
            Check("note-e1", MidiNoteName.TryParse("E1") == 28);
            Check("note-g5", MidiNoteName.TryParse("G5") == 79);
            Check("note-sharp", MidiNoteName.TryParse("C#4") == 61);
            Check("note-flat-equals-sharp-below", MidiNoteName.TryParse("Db4") == MidiNoteName.TryParse("C#4"));
            Check("note-negative-octave", MidiNoteName.TryParse("C-1") == 0);
            Check("note-roundtrip-all-127", Enumerable.Range(0, 128).All(m => MidiNoteName.TryParse(MidiNoteName.ToName(m)) == m));
            Check("note-garbage-null", MidiNoteName.TryParse("not a note") == null);
            Check("note-empty-null", MidiNoteName.TryParse("") == null);
            Check("note-out-of-range-null", MidiNoteName.TryParse("C11") == null); // (11+1)*12 = 144, out of 0-127
        }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_phase6_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── _UserBank.KSC guard: refused at the ViewModel level regardless of how a
        //    path reaches OpenCollection (direct open, FTP pull, Recent Files) ──
        {
            var userBankPath = Path.Combine(scratchRoot, "SomeBank_UserBank.KSC");
            File.WriteAllBytes(userBankPath, "#KORG Script Version 1.0\r\n#v2\r\n"u8.ToArray());
            Check("userbank-detected", SampleEditorViewModel.IsUserBank(userBankPath));
            Check("userbank-detection-case-insensitive", SampleEditorViewModel.IsUserBank(Path.Combine(scratchRoot, "x_userbank.ksc")));
            Check("userbank-normal-ksc-not-flagged", !SampleEditorViewModel.IsUserBank(Path.Combine(scratchRoot, "Normal.KSC")));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(userBankPath);
            Check("userbank-open-refused-no-roots", vm.Roots.Count == 0);
            Check("userbank-open-refused-status-message", vm.StatusText.Contains("_UserBank.KSC"));
            Check("userbank-open-refused-not-added-to-recent", !vm.GetRecentFiles().Contains(userBankPath));
        }

        // ── GainAdjustEffect: +6dB ~doubles amplitude, -6dB ~halves, clamps at full scale ──
        {
            short[] pcm = [1000, -1000, 16000, -16000];
            var up = new GainAdjustEffect(6.0).Apply(pcm, 44100);
            var down = new GainAdjustEffect(-6.0).Apply(pcm, 44100);
            Check("gain-up-6db-roughly-doubles", Math.Abs(up[0] - 2000) < 50);
            Check("gain-down-6db-roughly-halves", Math.Abs(down[0] - 500) < 50);

            var clamped = new GainAdjustEffect(24.0).Apply([30000], 44100);
            Check("gain-clamps-at-full-scale", clamped[0] == short.MaxValue);
        }

        // ── Waveform clipboard + selection-scoped ops, driven through the real
        //    ViewModel against a real in-memory sample ──
        {
            var kscPath = Path.Combine(scratchRoot, "Clip.KSC");
            var ksc = new KscCollection { Entries = ["Clip.KMP"] };
            Directory.CreateDirectory(Path.Combine(scratchRoot, "Clip"));
            ksc.Save(kscPath);
            var kmpPath = Path.Combine(scratchRoot, "Clip", "Clip.KMP");
            var kmp = new KmpMultisample { Name = "Clip", Mno1 = 1 };
            kmp.Zones.Add(new KmpZone { Filename = "MS001000.KSF", OriginalKey = 60, TopKey = 60 });
            kmp.Save(kmpPath);
            var ksfDir = Path.Combine(scratchRoot, "Clip", "Clip");
            Directory.CreateDirectory(ksfDir);
            var s = new KsfSample { Name = "S", SampleRate = 44100 };
            s.SetSamples([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
            s.Save(Path.Combine(ksfDir, "MS001000.KSF"));

            var vm = new SampleEditorViewModel();
            vm.OpenCollection(kscPath);
            vm.SelectNode(FindZone(vm.Roots, "MS001000.KSF"));
            var original = (short[])vm.SampleWaveform!.Clone();

            // Copy: non-destructive, captures exactly the selected range.
            vm.SelectionStartFrame = 2;
            vm.SelectionEndFrame = 5;
            vm.CopySelection();
            Check("copy-captures-exact-range", SampleClipboard.Pcm!.SequenceEqual(new short[] { 2, 3, 4 }));
            Check("copy-is-non-destructive", vm.SampleWaveform!.SequenceEqual(original));

            // Cut: removes the range, shrinks the sample, still on the clipboard.
            vm.CutSelection();
            Check("cut-shrinks-sample", vm.SampleWaveform!.SequenceEqual(new short[] { 0, 1, 5, 6, 7, 8, 9 }));
            Check("cut-clears-selection", vm.SelectionEndFrame <= vm.SelectionStartFrame);
            vm.Undo();
            Check("cut-undo-restores-original", vm.SampleWaveform!.SequenceEqual(original));

            // Paste: replaces a selection with the clipboard content (still {2,3,4}
            // from the copy above).
            vm.SelectionStartFrame = 0;
            vm.SelectionEndFrame = 2;
            vm.PasteAtSelection();
            Check("paste-replaces-selection", vm.SampleWaveform!.SequenceEqual(new short[] { 2, 3, 4, 2, 3, 4, 5, 6, 7, 8, 9 }));
            vm.Undo();
            Check("paste-undo-restores-original", vm.SampleWaveform!.SequenceEqual(original));

            // Paste at a bare cursor (no selection) inserts rather than replacing.
            vm.SelectionStartFrame = 3;
            vm.SelectionEndFrame = 3;
            vm.PasteAtSelection();
            Check("paste-inserts-at-cursor", vm.SampleWaveform!.SequenceEqual(new short[] { 0, 1, 2, 2, 3, 4, 3, 4, 5, 6, 7, 8, 9 }));
            vm.Undo();

            // Fade in/out on a selection only touches that range.
            vm.SelectionStartFrame = 4;
            vm.SelectionEndFrame = 9; // frames {4,5,6,7,8}, values {4,5,6,7,8}
            vm.ApplyFadeInSelection();
            var faded = vm.SampleWaveform!;
            Check("fade-in-selection-leaves-outside-untouched", faded[0] == 0 && faded[1] == 1 && faded[2] == 2 && faded[9] == 9);
            Check("fade-in-selection-starts-near-zero", faded[4] == 0); // t=0 at the first frame of the fade
            Check("fade-in-selection-ends-at-original", faded[8] == 8); // t=1 at the last frame of the fade
            vm.Undo();

            // Loop Selected Area sets LoopStart/LoopEnd to the current selection.
            vm.SelectionStartFrame = 2;
            vm.SelectionEndFrame = 7;
            vm.SetLoopFromSelection();
            Check("loop-from-selection-start", vm.SampleLoopStart == 2);
            Check("loop-from-selection-end", vm.SampleLoopEnd == 7);
        }

        return fails;
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
