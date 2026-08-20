namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Headless diagnostic ONLY (see App.xaml.cs's `--sample-editor-smoketest` flag) - not
// part of the shipped feature set. Drives the real SampleEditorViewModel end-to-end
// against a real Kronos fixture the same way Tools/sample_editor/_gui_smoke_test.py
// drove the Python POC's Tkinter app: open a collection, select a zone, confirm the
// waveform loaded, edit safe fields, save, and reopen from disk to confirm the edit
// persisted. Copies the fixture into a scratch dir first - never mutates the source
// fixture in place.
static class SampleEditorSmokeTest
{
    public static void Run(string kscPath)
    {
        // OpenCollection (below) writes Recent Files to the REAL settings.json
        // (Storage.SaveSettings has no test-injectable override) - snapshot it now and
        // restore it right before every Environment.Exit in this method, since
        // Environment.Exit does NOT run pending try/finally blocks, so a finally-based
        // guard wouldn't fire. Same "never touch the real thing" discipline this method
        // already applies to the source fixture itself (copied into scratchRoot below).
        var settingsPath = Path.Combine(Storage.DataDir, "settings.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        void RestoreSettings()
        {
            if (settingsBackup != null) File.WriteAllBytes(settingsPath, settingsBackup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }

        void Fail(string msg) { Console.WriteLine($"FAIL: {msg}"); RestoreSettings(); Environment.Exit(1); }
        void Ok(string msg) => Console.WriteLine($"OK: {msg}");

        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_editor_smoketest");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);

        // Copy the .KSC plus its content folder into scratch so edits never touch the
        // real fixture.
        var kscName = Path.GetFileName(kscPath);
        var contentDirName = Path.GetFileNameWithoutExtension(kscPath);
        var srcContentDir = Path.Combine(Path.GetDirectoryName(kscPath) ?? "", contentDirName);
        var dstKscPath = Path.Combine(scratchRoot, kscName);
        var dstContentDir = Path.Combine(scratchRoot, contentDirName);
        File.Copy(kscPath, dstKscPath);
        CopyDirectory(srcContentDir, dstContentDir);

        var vm = new SampleEditorViewModel();
        vm.OpenCollection(dstKscPath);
        if (vm.Roots.Count == 0) { Fail("OpenCollection produced no tree roots"); return; }
        Ok($"OpenCollection -> {vm.Roots.Count} root(s), status: {vm.StatusText}");

        // Find the first non-skipped zone anywhere in the tree.
        SampleTreeNode? zoneNode = FindFirstZone(vm.Roots);
        if (zoneNode == null) { Fail("no zone node found in tree"); return; }
        Ok($"found zone node: {zoneNode.Label}");

        vm.SelectNode(zoneNode);
        if (!vm.HasZoneSelected) { Fail("SelectNode didn't set HasZoneSelected"); return; }
        Ok("zone selected");

        if (!vm.ZoneIsSkipped)
        {
            if (!vm.HasSampleLoaded) { Fail("selecting a non-skipped zone didn't load its sample"); return; }
            Ok($"sample loaded: frames={vm.SampleFrameCount} rate={vm.SampleRate}");

            if (!vm.SampleIsHeaderOnly)
            {
                if (vm.SampleWaveform == null || vm.SampleWaveform.Length == 0)
                { Fail("waveform data missing for a non-header-only sample"); return; }
                Ok($"waveform populated: {vm.SampleWaveform.Length} sample(s)");

                int originalFrameCount = vm.SampleFrameCount;
                var originalWaveform = vm.SampleWaveform;

                // ── Phase 4: export this real (hardware-pulled) sample to WAV, then
                //    import that WAV right back in as a brand-new zone - a real
                //    export->import round trip, not just the synthetic in-memory one
                //    SampleTranscodeSelfTests already covers. Since this sample is
                //    already mono/44100 (the Kronos's own native format), the round
                //    trip should be bit-exact - no resampling/downmix involved. ──
                var exportWavPath = Path.Combine(scratchRoot, "export_check.wav");
                vm.ExportSelectedSampleToWav(exportWavPath);
                if (!File.Exists(exportWavPath)) { Fail($"export didn't write a file: {vm.StatusText}"); return; }
                Ok($"exported real sample to WAV: {vm.StatusText}");

                int zoneCountBeforeImport = CountZones(vm.Roots);
                var originalZoneFilename = zoneNode.ZoneRef!.Value.Zone.Filename;
                vm.ImportAudioAsNewZone(exportWavPath, 72, 72);
                if (!vm.StatusText.StartsWith("Imported")) { Fail($"import didn't report success: {vm.StatusText}"); return; }
                Ok($"imported WAV back as a new zone: {vm.StatusText}");

                var importedZoneNode = FindZoneByKey(vm.Roots, 72);
                if (importedZoneNode == null) { Fail("newly imported zone not found in the refreshed tree"); return; }
                if (CountZones(vm.Roots) != zoneCountBeforeImport + 1)
                { Fail($"zone count didn't increase by exactly one: before={zoneCountBeforeImport}, after={CountZones(vm.Roots)}"); return; }

                vm.SelectNode(importedZoneNode);
                if (!vm.HasSampleLoaded) { Fail("imported zone's sample didn't load"); return; }
                if (vm.SampleWaveform == null || !vm.SampleWaveform.SequenceEqual(originalWaveform))
                { Fail("re-imported WAV isn't bit-exact against the original (mono/44100 source, should be lossless)"); return; }
                Ok("re-imported sample is bit-exact against the original hardware sample");

                // Importing rebuilds the whole tree from disk (RefreshTreeAfterMutation),
                // so `zoneNode` (captured before the import) is now a stale object -
                // still holding correct data, but no longer part of vm.Roots, and its
                // KmpZone is no longer reference-equal to anything the rebuilt tree
                // holds. Re-resolve it by filename so every check below operates on the
                // ViewModel's actual current tree state, not an orphaned snapshot.
                zoneNode = FindZoneByFilename(vm.Roots, originalZoneFilename);
                if (zoneNode == null) { Fail("original zone not found after tree rebuild"); return; }
                vm.SelectNode(zoneNode);
                Ok("re-selected original zone (post-rebuild) for remaining checks");

                // ── Phase 3: DSP edits + undo/redo, against the real in-memory PCM ──

                vm.SelectionStartFrame = 0;
                vm.SelectionEndFrame = originalFrameCount / 2;
                vm.ApplyCrop();
                if (vm.SampleFrameCount != originalFrameCount / 2)
                { Fail($"crop didn't produce expected frame count: expected {originalFrameCount / 2}, got {vm.SampleFrameCount}"); return; }
                if (!vm.CanUndo) { Fail("CanUndo false immediately after an edit"); return; }
                Ok($"crop applied: {originalFrameCount} -> {vm.SampleFrameCount} frames");

                vm.Undo();
                if (vm.SampleFrameCount != originalFrameCount)
                { Fail($"undo didn't restore original frame count: expected {originalFrameCount}, got {vm.SampleFrameCount}"); return; }
                if (vm.SampleWaveform == null || !vm.SampleWaveform.SequenceEqual(originalWaveform))
                { Fail("undo didn't restore bit-exact original PCM"); return; }
                if (!vm.CanRedo) { Fail("CanRedo false immediately after an undo"); return; }
                Ok("undo restored bit-exact original PCM");

                vm.Redo();
                if (vm.SampleFrameCount != originalFrameCount / 2)
                { Fail($"redo didn't restore the cropped state: expected {originalFrameCount / 2}, got {vm.SampleFrameCount}"); return; }
                Ok("redo restored the cropped state");

                vm.Undo(); // back to full length for the remaining checks
                if (vm.SampleFrameCount != originalFrameCount)
                { Fail("undo after redo didn't restore full length"); return; }

                var beforeTempo = vm.SampleFrameCount;
                vm.ApplyTempoPitch(0.5, 0); // half speed -> roughly 2x longer
                if (vm.SampleFrameCount <= beforeTempo)
                { Fail($"tempo change didn't lengthen the sample: before={beforeTempo}, after={vm.SampleFrameCount}"); return; }
                Ok($"tempo change applied: {beforeTempo} -> {vm.SampleFrameCount} frames");
                vm.Undo();

                vm.ApplyNormalize();
                if (vm.SampleWaveform == null) { Fail("normalize left no waveform"); return; }
                Ok("normalize applied without error");
                vm.Undo();

                vm.ApplyFade(100, 100);
                Ok("fade applied without error");
                vm.Undo();

                vm.ApplySilenceTrim();
                Ok("silence trim applied without error");
                vm.Undo();

                if (vm.SampleFrameCount != originalFrameCount || !vm.SampleWaveform!.SequenceEqual(originalWaveform))
                { Fail("PCM not back to the original bit-exact state after undoing every Phase 3 edit"); return; }
                Ok("PCM confirmed bit-exact-original after undoing every DSP edit made above");
            }

            // Edit + save + reopen round trip on the sample's safe fields.
            int newRate = vm.SampleRate == 22050 ? 44100 : 22050;
            vm.ApplySampleEdits(newRate, vm.SampleLoopEnabled, vm.SampleSampleStart, vm.SampleLoopStart, vm.SampleLoopEnd);
            vm.SaveSelectedSample();
            if (vm.StatusText.StartsWith("Save failed")) { Fail($"sample save failed: {vm.StatusText}"); return; }
            Ok($"sample saved: {vm.StatusText}");

            // Reload fresh from disk (not through the ViewModel, to independently verify
            // persistence rather than trusting in-memory state).
            var zonePath = zoneNode.ZoneRef!.Value.Zone.KsfPath(zoneNode.ZoneRef.Value.KmpPath);
            var reopened = KsfSample.Open(File.ReadAllBytes(zonePath));
            if (reopened == null) { Fail("reopened .KSF failed to parse"); return; }
            if (reopened.SampleRate != newRate) { Fail($"sample rate not persisted: expected {newRate}, got {reopened.SampleRate}"); return; }
            Ok($"sample rate edit persisted: {reopened.SampleRate}");
        }

        // ── Stereo pair creation + import, against the real (hardware-pulled)
        //    collection - see kronosology's ksc_kmp_ksf_file_format.md §2.2: a Kronos
        //    stereo instrument is two full multisamples, same Name, opposite -L/-R
        //    Suffix, matching key ranges, never two zones in one .KMP. ──
        {
            var origZoneFilename = zoneNode.ZoneRef!.Value.Zone.Filename;

            vm.NewStereoMultisamplePairInCollection("SmokeStereo", 50);
            if (!vm.StatusText.StartsWith("Created stereo multisample pair"))
            { Fail($"stereo pair creation didn't report success: {vm.StatusText}"); return; }
            Ok($"stereo pair created: {vm.StatusText}");

            var leftNode = FindMultisampleByNameSuffix(vm.Roots, "SmokeStereo", "-L");
            var rightNode = FindMultisampleByNameSuffix(vm.Roots, "SmokeStereo", "-R");
            if (leftNode?.MultisampleRef == null || rightNode?.MultisampleRef == null)
            { Fail("stereo pair's -L/-R multisample nodes not found in the tree"); return; }
            if (leftNode.MultisampleRef.Value.Multisample.Mno1 != 50 || rightNode.MultisampleRef.Value.Multisample.Mno1 != 51)
            { Fail($"stereo pair MNO1 not adjacent: L={leftNode.MultisampleRef.Value.Multisample.Mno1} R={rightNode.MultisampleRef.Value.Multisample.Mno1}"); return; }

            vm.SelectNode(leftNode);
            var stereoImportSource = Path.Combine(scratchRoot, "export_check.wav"); // written by the mono export/import block above
            vm.ImportStereoAudioAsNewZonePair(stereoImportSource, 64, 64);
            if (!vm.StatusText.StartsWith("Imported")) { Fail($"stereo import didn't report success: {vm.StatusText}"); return; }
            Ok($"stereo zone pair imported: {vm.StatusText}");

            // Importing rebuilds the tree again - re-resolve everything by identity,
            // same discipline as the mono import block above.
            leftNode = FindMultisampleByNameSuffix(vm.Roots, "SmokeStereo", "-L");
            rightNode = FindMultisampleByNameSuffix(vm.Roots, "SmokeStereo", "-R");
            var leftZone = leftNode?.MultisampleRef?.Multisample.Zones.FirstOrDefault(z => z.OriginalKey == 64);
            var rightZone = rightNode?.MultisampleRef?.Multisample.Zones.FirstOrDefault(z => z.OriginalKey == 64);
            if (leftZone == null || rightZone == null) { Fail("stereo zone pair not found after tree rebuild"); return; }

            var leftKsfPath = leftZone.KsfPath(leftNode!.MultisampleRef!.Value.Path);
            var rightKsfPath = rightZone.KsfPath(rightNode!.MultisampleRef!.Value.Path);
            var leftKsf = KsfSample.Open(File.ReadAllBytes(leftKsfPath));
            var rightKsf = KsfSample.Open(File.ReadAllBytes(rightKsfPath));
            if (leftKsf == null || rightKsf == null) { Fail("stereo zone pair's .KSF files failed to parse"); return; }
            if (leftKsf.Suffix != "-L" || rightKsf.Suffix != "-R")
            { Fail($"stereo zone suffixes wrong: L='{leftKsf.Suffix}' R='{rightKsf.Suffix}'"); return; }
            if (leftKsf.IsHeaderOnly || rightKsf.IsHeaderOnly) { Fail("stereo zone pair has no audio data"); return; }
            // The import source (export_check.wav) is mono, so both channels should be
            // identical per AudioImport.ConvertToStereo44100's documented mono->stereo
            // duplication behavior.
            if (!leftKsf.Samples().SequenceEqual(rightKsf.Samples()))
            { Fail("mono-sourced stereo pair's L/R channels aren't identical as expected"); return; }
            Ok("stereo zone pair's .KSF files verified on disk (L/R suffix, audio data, matching mono-sourced channels)");

            zoneNode = FindZoneByFilename(vm.Roots, origZoneFilename) ?? zoneNode;
            vm.SelectNode(zoneNode);
        }

        // Edit + save + reopen round trip on the zone's key fields.
        int newOrigKey = (zoneNode.ZoneRef!.Value.Zone.OriginalKey + 1) % 128;
        int newTopKey = (zoneNode.ZoneRef.Value.Zone.TopKey + 1) % 128;
        vm.ApplyZoneEdits(newOrigKey, newTopKey);
        vm.SaveSelectedMultisample();
        if (vm.StatusText.StartsWith("Save failed") || vm.StatusText.Contains("Couldn't resolve"))
        { Fail($"multisample save failed: {vm.StatusText}"); return; }
        Ok($"multisample saved: {vm.StatusText}");

        var kmpPath = zoneNode.ZoneRef.Value.KmpPath;
        var reopenedKmp = KmpMultisample.Open(File.ReadAllBytes(kmpPath));
        if (reopenedKmp == null) { Fail("reopened .KMP failed to parse"); return; }
        var matchingZone = reopenedKmp.Zones.FirstOrDefault(z => z.Filename == zoneNode.ZoneRef.Value.Zone.Filename);
        if (matchingZone == null) { Fail("edited zone not found after reopen"); return; }
        if (matchingZone.OriginalKey != newOrigKey || matchingZone.TopKey != newTopKey)
        { Fail($"key edit not persisted: expected ({newOrigKey},{newTopKey}), got ({matchingZone.OriginalKey},{matchingZone.TopKey})"); return; }
        Ok($"zone key edit persisted: OriginalKey={matchingZone.OriginalKey} TopKey={matchingZone.TopKey}");

        Console.WriteLine("\nALL SMOKE TESTS PASSED");
        RestoreSettings();
        Environment.Exit(0);
    }

    static SampleTreeNode? FindFirstZone(IEnumerable<SampleTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef != null) return node;
            var found = FindFirstZone(node.Children);
            if (found != null) return found;
        }
        return null;
    }

    static SampleTreeNode? FindZoneByKey(IEnumerable<SampleTreeNode> nodes, int originalKey)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef?.Zone.OriginalKey == originalKey) return node;
            var found = FindZoneByKey(node.Children, originalKey);
            if (found != null) return found;
        }
        return null;
    }

    static SampleTreeNode? FindMultisampleByNameSuffix(IEnumerable<SampleTreeNode> nodes, string name, string suffix)
    {
        foreach (var node in nodes)
        {
            if (node.MultisampleRef?.Multisample.Name == name && node.MultisampleRef?.Multisample.Suffix == suffix)
                return node;
            var found = FindMultisampleByNameSuffix(node.Children, name, suffix);
            if (found != null) return found;
        }
        return null;
    }

    static SampleTreeNode? FindZoneByFilename(IEnumerable<SampleTreeNode> nodes, string filename)
    {
        foreach (var node in nodes)
        {
            if (node.ZoneRef?.Zone.Filename == filename) return node;
            var found = FindZoneByFilename(node.Children, filename);
            if (found != null) return found;
        }
        return null;
    }

    static int CountZones(IEnumerable<SampleTreeNode> nodes) =>
        nodes.Sum(node => (node.ZoneRef != null ? 1 : 0) + CountZones(node.Children));

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}
