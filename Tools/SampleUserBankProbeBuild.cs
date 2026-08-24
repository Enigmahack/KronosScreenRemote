namespace KronosScreenRemote;

using System.IO;

// One-off diagnostic, NOT a shipped feature (see App.xaml.cs's
// `--sample-userbank-probe-build <outputDir>` flag): builds a minimal, self-contained
// mono multisample plus BOTH a normal .KSC and its _UserBank.KSC sibling (via
// KscCollection.ToUserBankBytes/SaveUserBank), using the exact same production code
// path (KmpMultisample/KsfSample/SampleImportBuilder/KscCollection) the real Sample
// Editor uses - not hand-assembled bytes - so this probe is representative of what the
// shipped writer actually produces. Built to answer kronosology doc's open question
// ("can a tool hand-author a working _UserBank.KSC?") on real hardware: upload the
// output folder under a Kronos SSD (e.g. SSD2/<somewhere new>/), load ONLY the
// _UserBank.KSC file (not the normal .KSC) from the Disk page, and check whether the
// probe multisample shows up as selectable in Program mode's HD-1 oscillator
// Multisample picker.
static class SampleUserBankProbeBuild
{
    public static void Run(string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var baseName = "UBPROBE1"; // deliberately distinctive - avoids colliding with
                                        // any resident-RAM sample name already on the unit
                                        // (e.g. "NEWMS...", "ClaudeTestLoopOFF...")
            var kscPath = Path.Combine(outputDir, $"{baseName}.KSC");

            var m = new KmpMultisample { Name = baseName, Suffix = "", Mno1 = 0 };
            var kmpFileName = KmpMultisample.AutoFileName(baseName, 0);
            var kmpPath = Path.Combine(KscCollection.ContentDirFor(kscPath), kmpFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(kmpPath)!);

            // 1 second, 440Hz mono tone at 44100Hz - same synthetic-sample recipe the
            // kronosology doc's §5 external-construction recipe already hardware-confirmed
            // end-to-end (a synthetic .KSF/.KMP/.KSC with zero real Korg-authored bytes
            // loaded and played correctly), just reused here for the _UserBank case.
            const int sampleRate = 44100;
            const double freq = 440.0;
            var pcm = new short[sampleRate];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * 16000);

            // Original Key = C4 (MIDI 60), Top Key = C9 (MIDI 120) - hardware-confirmed
            // fix 2026-08-24: the first probe used Original Key = C-1 (MIDI 0), which put
            // the root pitch ~60 semitones off from wherever it was actually played.
            var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, baseName, pcm, sampleRate, 60, 120);
            m.Save(kmpPath);

            // LoopEnd = frame_count-1 fix (§ below, FixLoopEnd) - hardware-confirmed
            // 2026-08-24 (round 2): the C4/C9 fix alone still left the real unit showing
            // garbage Loop Start (2147483640) with Loop/Reverse both off. A byte-diff
            // against the user's own hand-corrected re-save (UBPROBEFIX) found exactly
            // ONE differing byte in the whole .KSF - the LAST byte of LoopEnd (file
            // offset 39), 0x00 -> 0x08. LoopStart itself was 0 in BOTH files, unchanged -
            // so the garbage "Loop Start" readout was never actually reading the
            // LoopStart field; it's some display/computation involving LoopEnd, and
            // LoopEnd=0 (KsfSample's own field default, left untouched by
            // AddSampleZone) is what triggered it. This corrects an unqualified claim in
            // kronosology's own format doc §5 that "0 for all four is a safe default...
            // with loop off" (doc corrected) - AddSampleZone/KsfSample's field default of
            // 0 is NOT safe for LoopEnd on a one-shot sample. Using frame_count-1 here
            // rather than the user's own quick "8" test value because it matches the
            // doc's own already-documented real-hardware one-shot convention (§3.1:
            // "equals frame_count-1...in every intact one-shot file when loop is off").
            FixLoopEnd(zone.KsfPath(kmpPath));

            var collection = new KscCollection
            {
                Path = kscPath,
                BankUuid = KscCollection.GenBankUuid(),
                Entries = [kmpFileName],
            };
            collection.Save(kscPath);
            collection.SaveUserBank();

            Console.WriteLine($"OK - wrote {kscPath}");
            Console.WriteLine($"OK - wrote {collection.UserBankPath}");
            Console.WriteLine($"OK - wrote {kmpPath}");
            Console.WriteLine($"BankUuid: {collection.BankUuid}");
            Console.WriteLine($"MultisampleName: {baseName}");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex}");
            Environment.Exit(1);
        }
    }

    static void FixLoopEnd(string ksfPath)
    {
        var ksf = KsfSample.Open(File.ReadAllBytes(ksfPath))
            ?? throw new InvalidOperationException($"just-written probe .KSF failed to re-open: {ksfPath}");
        ksf.LoopEnd = (uint)(ksf.FrameCount - 1);
        ksf.Save(ksfPath);
    }

    // Second probe (user-requested 2026-08-24): a single multisample with many zones
    // spanning virtually the whole keyboard, to test multi-zone assignment/pitch-shift
    // through both the normal .KSC and _UserBank.KSC load paths - the first probe only
    // ever tested one zone. 32 zones x 4 keys/zone = exactly 128 (0-127), the full
    // keyboard with no gaps or a stretched last zone. Each zone's Original Key is its
    // own OWN lowest key (as requested), and its tone is synthesized AT that key's real
    // pitch (A440 equal temperament) so playing up the keyboard should sound "in tune"
    // at each zone's own root and pitch-shift up within its own 4-key range - lets a
    // listener actually hear zone boundaries, not just trust the byte layout.
    public static void RunMultiZone(string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            const string baseName = "UPROBE2"; // exact name requested - distinct from UBPROBE1/UBPROBEFIX
            const int sampleRate = 44100;
            const int zoneWidth = 4;
            const int zoneCount = 128 / zoneWidth; // 32 - covers the full 0-127 keyboard exactly
            const double zoneSeconds = 0.3; // short per zone - 32 zones is already ~850KB of PCM

            var kscPath = Path.Combine(outputDir, $"{baseName}.KSC");
            var kmpFileName = KmpMultisample.AutoFileName(baseName, 0);
            var kmpPath = Path.Combine(KscCollection.ContentDirFor(kscPath), kmpFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(kmpPath)!);

            var m = new KmpMultisample { Name = baseName, Suffix = "", Mno1 = 0 };
            for (int i = 0; i < zoneCount; i++)
            {
                int origKey = i * zoneWidth;
                int topKey = origKey + zoneWidth - 1; // last zone: 124-127

                double freq = 440.0 * Math.Pow(2.0, (origKey - 69) / 12.0);
                int frames = (int)(sampleRate * zoneSeconds);
                var pcm = new short[frames];
                for (int s = 0; s < frames; s++)
                    pcm[s] = (short)(Math.Sin(2 * Math.PI * freq * s / sampleRate) * 16000);

                var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, $"{baseName}_{i:D2}", pcm, sampleRate, origKey, topKey);
                FixLoopEnd(zone.KsfPath(kmpPath));
            }
            m.Save(kmpPath);

            var collection = new KscCollection
            {
                Path = kscPath,
                BankUuid = KscCollection.GenBankUuid(),
                Entries = [kmpFileName],
            };
            collection.Save(kscPath);
            collection.SaveUserBank();

            Console.WriteLine($"OK - wrote {kscPath} ({zoneCount} zones, {zoneWidth} keys each, covering MIDI 0-127)");
            Console.WriteLine($"OK - wrote {collection.UserBankPath}");
            Console.WriteLine($"OK - wrote {kmpPath}");
            Console.WriteLine($"BankUuid: {collection.BankUuid}");
            Console.WriteLine($"MultisampleName: {baseName}");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex}");
            Environment.Exit(1);
        }
    }
}
