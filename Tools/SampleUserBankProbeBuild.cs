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

            // 1 second, 440Hz mono tone at 44100Hz.
            const int sampleRate = 44100;
            const double freq = 440.0;
            var pcm = new short[sampleRate];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * 16000);

            // Original Key = C4 (MIDI 60), Top Key = C9 (MIDI 120) - the root pitch must match
            // wherever the tone is actually played.
            var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, baseName, pcm, sampleRate, 60, 120);
            m.Save(kmpPath);

            // KsfSample/AddSampleZone default LoopEnd to 0, which reads back as garbage Loop
            // Start on hardware for a one-shot sample with Loop off - LoopEnd must be
            // frame_count-1 instead.
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

    // A single multisample with many zones spanning virtually the whole keyboard, to test
    // multi-zone assignment/pitch-shift through both the normal .KSC and _UserBank.KSC load
    // paths. 32 zones x 4 keys/zone = exactly 128 (0-127), the full keyboard with no gaps or a
    // stretched last zone. Each zone's Original Key is its own lowest key, and its tone is
    // synthesized AT that key's real pitch (A440 equal temperament) so playing up the keyboard
    // should sound "in tune" at each zone's own root and pitch-shift up within its own 4-key
    // range - lets a listener actually hear zone boundaries, not just trust the byte layout.
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
