namespace KronosScreenRemote;

using System.IO;
using NAudio.Utils;
using NAudio.Wave;

// Off-hardware checks for Phase 4's transcode/import/export pipeline. AudioImport's
// MP3/MP4 path needs a real file + Windows Media Foundation and can't be exercised
// in-process (no synthetic encoder available) - human click-test only, per the plan.
// What these checks cover is everything AudioImport owns beyond NAudio's own (already
// well-tested) codec/bit-depth conversion: channel downmix, resampling to the target
// rate, and the round-trip through SampleImportBuilder/SampleExport. Wired into
// App.xaml.cs's --librarian-selftest.
static class SampleTranscodeSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── AudioImport: 16-bit mono at the target rate is a pure passthrough ──
        {
            short[] original = [100, -200, 300, -400, 32767, -32768];
            using var reader = new WaveFileReader(BuildWav16(original, AudioImport.TargetSampleRate, 1));
            var result = AudioImport.ConvertToMono44100(reader);
            Check("import-mono-passthrough-length", result.Length == original.Length);
            Check("import-mono-passthrough-values", result.SequenceEqual(original));
        }

        // ── AudioImport: stereo downmix averages L/R, doesn't just drop one channel ──
        {
            // Interleaved L,R pairs: (10000,-10000)->~0, (20000,20000)->20000, (-30000,-30000)->-30000.
            short[] interleaved = [10000, -10000, 20000, 20000, -30000, -30000];
            using var reader = new WaveFileReader(BuildWav16(interleaved, AudioImport.TargetSampleRate, 2));
            var result = AudioImport.ConvertToMono44100(reader);
            Check("import-stereo-downmix-frame-count", result.Length == 3);
            Check("import-stereo-downmix-first-frame-near-zero", result.Length > 0 && Math.Abs(result[0]) < 50);
            Check("import-stereo-downmix-second-frame", result.Length > 1 && result[1] == 20000);
            Check("import-stereo-downmix-third-frame", result.Length > 2 && result[2] == -30000);
        }

        // ── AudioImport: a source at a different sample rate actually gets resampled
        //    to 44100 rather than left at its native rate - frame count should roughly
        //    double for a 22050 Hz source (not bit-exact, resampling has filter warm-
        //    up/tail, so this checks direction/proportion, not exact values) ──
        {
            var original = new short[2205]; // 0.1s at 22050 Hz
            for (int i = 0; i < original.Length; i++)
                original[i] = (short)(1000 * Math.Sin(2 * Math.PI * 440 * i / 22050.0));
            using var reader = new WaveFileReader(BuildWav16(original, 22050, 1));
            var result = AudioImport.ConvertToMono44100(reader);
            Check("import-resample-roughly-doubles-length", result.Length > 3500 && result.Length < 5500);
        }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_transcode_selftest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // ── SampleImportBuilder: new zone gets NextKsfFilename() and is inserted in
        //    TopKey order, not appended past a lower-keyed zone that comes after it in
        //    the list ──
        {
            var kmpPath = Path.Combine(scratchRoot, "Test.KMP");
            var m = new KmpMultisample { Name = "Test", Mno1 = 3 };
            m.Zones.Add(new KmpZone { Filename = "MS003000.KSF", OriginalKey = 40, TopKey = 40 });
            m.Zones.Add(new KmpZone { Filename = "MS003001.KSF", OriginalKey = 80, TopKey = 80 });

            var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, "New Sound", [1, 2, 3, 4], 44100, 60, 60);
            Check("import-builder-filename-convention", zone.Filename == "MS003002.KSF");
            Check("import-builder-sorted-insert", m.Zones.IndexOf(zone) == 1); // between key 40 and key 80
            Check("import-builder-ksf-written", File.Exists(zone.KsfPath(kmpPath)));

            var written = KsfSample.Open(File.ReadAllBytes(zone.KsfPath(kmpPath)));
            Check("import-builder-ksf-readable", written != null);
            Check("import-builder-ksf-samples", written != null && written.Samples().SequenceEqual(new short[] { 1, 2, 3, 4 }));
        }

        // ── SampleExport: single-sample export round-trips through AudioImport
        //    bit-exact (16-bit PCM WAV write/read is lossless) ──
        {
            var wavPath = Path.Combine(scratchRoot, "export_test.wav");
            var s = new KsfSample { Name = "Export Test", SampleRate = 44100 };
            s.SetSamples([1000, -1000, 2000, -2000, 32767, -32768]);
            Check("export-reports-success", SampleExport.ExportSampleToWav(s, wavPath));
            Check("export-file-written", File.Exists(wavPath));

            var reimported = AudioImport.ImportToMono44100(wavPath);
            Check("export-round-trip-bit-exact", reimported.SequenceEqual(s.Samples()));

            // Header-only samples must be skipped, never exported as a 0-sample WAV.
            var headerOnly = new KsfSample { Name = "Empty" };
            var headerOnlyPath = Path.Combine(scratchRoot, "header_only.wav");
            Check("export-refuses-header-only", !SampleExport.ExportSampleToWav(headerOnly, headerOnlyPath));
            Check("export-refuses-header-only-no-file", !File.Exists(headerOnlyPath));
        }

        // ── SampleExport: collection export walks every .KMP's every non-skipped
        //    zone, skips the skipped zone and the header-only sample, names files
        //    after the sample (not the zone's cryptic MSxxxyyy.KSF filename) ──
        {
            var collRoot = Path.Combine(scratchRoot, "coll");
            Directory.CreateDirectory(collRoot);

            var kscPath = Path.Combine(collRoot, "Coll.KSC");
            var ksc = new KscCollection { Entries = ["Coll.KMP"] };
            Directory.CreateDirectory(Path.Combine(collRoot, "Coll"));
            ksc.Save(kscPath);

            var kmpPath = Path.Combine(collRoot, "Coll", "Coll.KMP");
            var kmp = new KmpMultisample { Name = "Coll", Mno1 = 5 };
            kmp.Zones.Add(new KmpZone { Filename = "MS005000.KSF", OriginalKey = 40, TopKey = 40 });
            kmp.Zones.Add(new KmpZone { Filename = "MS005001.KSF", OriginalKey = 60, TopKey = 60 });
            kmp.Zones.Add(new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = 80, TopKey = 80 });
            kmp.Save(kmpPath);

            var ksfDir = Path.Combine(collRoot, "Coll", "Coll");
            Directory.CreateDirectory(ksfDir);
            var real = new KsfSample { Name = "RealVoice" };
            real.SetSamples([1, 2, 3]);
            real.Save(Path.Combine(ksfDir, "MS005000.KSF"));
            var empty = new KsfSample { Name = "EmptyVoice" };
            empty.Save(Path.Combine(ksfDir, "MS005001.KSF"));

            var outDir = Path.Combine(collRoot, "export_out");
            var (exported, skipped) = SampleExport.ExportCollection(ksc, kscPath, outDir);
            Check("export-collection-exports-one", exported == 1);
            Check("export-collection-skips-header-only", skipped == 1);
            Check("export-collection-named-after-sample", File.Exists(Path.Combine(outDir, "RealVoice.wav")));
        }

        return fails;
    }

    // IgnoreDisposeStream so the MemoryStream survives WaveFileWriter's Dispose() (it
    // closes whatever stream it was given) and can be read back afterward.
    static Stream BuildWav16(short[] samples, int sampleRate, int channels)
    {
        var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(sampleRate, 16, channels)))
            writer.WriteSamples(samples, 0, samples.Length);
        ms.Position = 0;
        return ms;
    }
}
