using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// Sample Editor window's view-state. Disk-only for Phase 1 (no FTP yet - Phase 2).
// Same hybrid-MVVM shape as PcgPaneViewModel: plain methods for file operations
// (called from code-behind, which owns dialogs/Window), [ObservableProperty] for the
// bindable tree + selected-zone detail state.
partial class SampleEditorViewModel : ObservableObject
{
    [ObservableProperty] string statusText = "Ready";

    public ObservableCollection<SampleTreeNode> Roots { get; } = [];

    // The currently open collection (if opened via a .KSC) - null when a .KMP was
    // opened directly, matching the Python POC's own "open a .KMP without a .KSC"
    // path (doc §1.5 folder convention still applies for its zones).
    KscCollection? _collection;
    string? _collectionPath;

    // ── Selected-zone detail state (right pane) ─────────────────────────────────

    SampleTreeNode? _selectedNode;
    KmpZone? _selectedZone;
    string? _selectedKmpPath;
    KsfSample? _selectedSample;
    string? _selectedSamplePath;

    [ObservableProperty] bool hasZoneSelected;
    [ObservableProperty] string zoneFilename = "";
    [ObservableProperty] int zoneOriginalKey;
    [ObservableProperty] int zoneTopKey;
    [ObservableProperty] bool zoneIsSkipped;

    [ObservableProperty] bool hasSampleLoaded;
    [ObservableProperty] string sampleName = "";
    [ObservableProperty] int sampleRate;
    [ObservableProperty] int sampleFrameCount;
    [ObservableProperty] bool sampleIsHeaderOnly;
    [ObservableProperty] bool sampleLoopEnabled;
    [ObservableProperty] int sampleSampleStart;
    [ObservableProperty] int sampleLoopStart;
    [ObservableProperty] int sampleLoopEnd;
    [ObservableProperty] short[]? sampleWaveform;

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    SampleEditUndo _sampleUndo = new(Storage.LoadSettings().SampleUndoByteCapMb * 1024L * 1024L);
    readonly SamplePlayback _playback = new();

    [ObservableProperty] bool canUndo;
    [ObservableProperty] bool canRedo;
    [ObservableProperty] int selectionStartFrame;
    [ObservableProperty] int selectionEndFrame;
    [ObservableProperty] bool isPlaying;
    [ObservableProperty] bool loopPreviewEnabled;

    public event Action? TreeRefreshed;

    public SampleEditorViewModel()
    {
        // Catches playback finishing on its own (reached the end of the buffer), not
        // just an explicit StopPlayback() call - IsPlaying must track both. Fires on
        // NAudio's own thread, not the UI thread - PropertyChanged must be raised from
        // the dispatcher thread WPF bindings expect.
        _playback.PlaybackStopped += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsPlaying = false);
    }

    // ── Remote (FTP) pull/push (Phase 2) ────────────────────────────────────────

    // local path -> remote path, for every file pulled this session (across possibly
    // several pulls). Consulted by PushSelectedXAsync to find where an edited file
    // should go back to - content that was never pulled (new or local-only) simply
    // has no entry, so push is unavailable for it rather than guessing a destination.
    readonly Dictionary<string, string> _remoteMap = new(StringComparer.OrdinalIgnoreCase);

    public async Task PullCollectionFromKronosAsync(IRemoteSampleSource source)
    {
        var localRoot = SampleWorkspace.ResolveRoot(Storage.LoadSettings());
        var result = await source.PickAndPullAsync(".KSC", localRoot);
        if (result.LocalPath == null) { StatusText = result.StatusMessage; return; }
        foreach (var (local, remote) in result.RemoteMap!) _remoteMap[local] = remote;
        OpenCollection(result.LocalPath);
    }

    public async Task PullMultisampleFromKronosAsync(IRemoteSampleSource source)
    {
        var localRoot = SampleWorkspace.ResolveRoot(Storage.LoadSettings());
        var result = await source.PickAndPullAsync(".KMP", localRoot);
        if (result.LocalPath == null) { StatusText = result.StatusMessage; return; }
        foreach (var (local, remote) in result.RemoteMap!) _remoteMap[local] = remote;
        OpenMultisampleDirect(result.LocalPath);
    }

    public async Task PushSelectedSampleAsync(IRemoteSampleSource source)
    {
        if (_selectedSample == null || _selectedSamplePath == null) { StatusText = "No sample loaded."; return; }
        if (_sampleDirty) { StatusText = "Save the sample locally first (use Save Sample), then push."; return; }
        if (!_remoteMap.TryGetValue(_selectedSamplePath, out var remotePath))
        { StatusText = "This sample wasn't pulled from the Kronos - nowhere to push it back to."; return; }
        // Hardware-confirmed failure mode (doc §3.3): Eva's own Save can write a
        // zero-frame .KSF for a sample that was loaded but never fully read. Pushing
        // one of those over a good on-Kronos sample would silently destroy it.
        if (_selectedSample.IsHeaderOnly)
        { StatusText = "Refusing to push: this sample has no audio data (header-only)."; return; }

        StatusText = "Pushing to Kronos...";
        var result = await source.PushAsync(_selectedSamplePath, remotePath);
        StatusText = result.StatusMessage;
    }

    public async Task PushSelectedMultisampleAsync(IRemoteSampleSource source)
    {
        string? path;
        if (_selectedNode?.MultisampleRef is { } ms) path = ms.Path;
        else if (_selectedZone != null) path = _selectedKmpPath;
        else { StatusText = "No multisample selected."; return; }

        if (path == null) { StatusText = "Couldn't resolve the owning multisample."; return; }
        if (_zoneDirty) { StatusText = "Save the multisample locally first (use Save Multisample), then push."; return; }
        if (!_remoteMap.TryGetValue(path, out var remotePath))
        { StatusText = "This multisample wasn't pulled from the Kronos - nowhere to push it back to."; return; }

        StatusText = "Pushing to Kronos...";
        var result = await source.PushAsync(path, remotePath);
        StatusText = result.StatusMessage;
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    public void OpenCollection(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var collection = KscCollection.Open(bytes);
            _collection = collection;
            _collectionPath = path;
            RebuildTreeFromCollection(path, collection);
            AddRecentFile(path);
            StatusText = $"Loaded '{Path.GetFileName(path)}' ({collection.Entries.Count} entr{(collection.Entries.Count == 1 ? "y" : "ies")}).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: collection load '{path}' failed: {ex}");
            StatusText = $"Failed to load '{Path.GetFileName(path)}': {ex.Message}";
        }
    }

    public void OpenMultisampleDirect(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var m = KmpMultisample.Open(bytes);
            if (m == null)
            {
                StatusText = $"'{Path.GetFileName(path)}' isn't a recognizable .KMP file.";
                return;
            }
            _collection = null;
            _collectionPath = null;
            Roots.Clear();
            Roots.Add(BuildMultisampleNode(m, path));
            TreeRefreshed?.Invoke();
            AddRecentFile(path);
            StatusText = $"Loaded '{Path.GetFileName(path)}' directly ({m.Zones.Count} zone(s)).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: multisample load '{path}' failed: {ex}");
            StatusText = $"Failed to load '{Path.GetFileName(path)}': {ex.Message}";
        }
    }

    void RebuildTreeFromCollection(string kscPath, KscCollection collection)
    {
        // Every multisample/zone node gets rebuilt as a brand-new object below (each
        // .KMP is re-opened from disk), so any node the caller was still holding as
        // "currently selected" - including this VM's OWN _selectedNode/_selectedZone/
        // _selectedSample fields - becomes stale: reference-unequal to anything in the
        // new tree, yet the ViewModel's own IsHeaderOnly/SampleXxx/HasZoneSelected
        // properties would otherwise still show the pre-rebuild state. Left alone, a
        // subsequent SaveSelectedMultisample/SaveSelectedSample would silently act on
        // that stale (pre-rebuild) copy - discarding whatever this rebuild's own
        // caller just wrote to disk. Clearing selection forces an explicit re-select
        // before any further save, the safe failure mode instead of a silent one.
        SelectNode(null);
        Roots.Clear();
        var root = SampleTreeNode.ForCollection(Path.GetFileName(kscPath), collection);
        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var kmpDir = Path.Combine(Path.GetDirectoryName(kscPath) ?? "",
                Path.GetFileNameWithoutExtension(kscPath));
            var kmpPath = Path.Combine(kmpDir, entry);
            if (!File.Exists(kmpPath)) continue;
            try
            {
                var m = KmpMultisample.Open(File.ReadAllBytes(kmpPath));
                if (m != null) root.Children.Add(BuildMultisampleNode(m, kmpPath));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Sample Editor: skipping unreadable multisample '{kmpPath}': {ex.Message}");
            }
        }
        Roots.Add(root);
        TreeRefreshed?.Invoke();
    }

    static SampleTreeNode BuildMultisampleNode(KmpMultisample m, string path)
    {
        var node = SampleTreeNode.ForMultisample($"{m.Name}{m.Suffix} ({Path.GetFileName(path)})", m, path);
        foreach (var z in m.Zones)
        {
            var label = z.IsSkipped ? $"(skipped) key {z.TopKey}" : $"{z.Filename}  key {z.TopKey}";
            node.Children.Add(SampleTreeNode.ForZone(label, z, path));
        }
        return node;
    }

    // ── Selection ────────────────────────────────────────────────────────────

    public void SelectNode(SampleTreeNode? node)
    {
        _playback.Stop();
        IsPlaying = false;

        _selectedNode = node;
        _selectedZone = node?.ZoneRef?.Zone;
        _selectedKmpPath = node?.ZoneRef?.KmpPath;
        _selectedSample = null;
        _selectedSamplePath = null;
        _zoneDirty = false;
        _sampleDirty = false;

        // Undo history is scoped to whichever sample is currently loaded - switching
        // to a different zone starts a fresh history rather than letting Undo reach
        // back into an unrelated sample's edits.
        _sampleUndo = new SampleEditUndo(_sampleUndo.ByteCap);
        CanUndo = false;
        CanRedo = false;
        SelectionStartFrame = 0;
        SelectionEndFrame = 0;

        HasZoneSelected = _selectedZone != null;
        if (_selectedZone is { } z)
        {
            ZoneFilename = z.Filename;
            ZoneOriginalKey = z.OriginalKey;
            ZoneTopKey = z.TopKey;
            ZoneIsSkipped = z.IsSkipped;
        }

        HasSampleLoaded = false;
        SampleWaveform = null;
        if (_selectedZone is { IsSkipped: false } zone && _selectedKmpPath != null)
        {
            var ksfPath = zone.KsfPath(_selectedKmpPath);
            try
            {
                if (File.Exists(ksfPath))
                {
                    var s = KsfSample.Open(File.ReadAllBytes(ksfPath));
                    if (s != null)
                    {
                        _selectedSample = s;
                        _selectedSamplePath = ksfPath;
                        LoadSampleDetailState(s);
                    }
                    else
                    {
                        StatusText = $"'{Path.GetFileName(ksfPath)}' isn't a recognizable .KSF file.";
                    }
                }
                else
                {
                    StatusText = $"Referenced sample '{Path.GetFileName(ksfPath)}' not found on disk.";
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sample Editor: sample load '{ksfPath}' failed: {ex}");
                StatusText = $"Failed to load '{Path.GetFileName(ksfPath)}': {ex.Message}";
            }
        }
    }

    void LoadSampleDetailState(KsfSample s)
    {
        HasSampleLoaded = true;
        SampleName = s.Name + s.Suffix;
        SampleRate = (int)s.SampleRate;
        SampleFrameCount = s.FrameCount;
        SampleIsHeaderOnly = s.IsHeaderOnly;
        SampleLoopEnabled = s.IsLoopEnabled;
        SampleSampleStart = (int)s.SampleStart;
        SampleLoopStart = (int)s.LoopStart;
        SampleLoopEnd = (int)s.LoopEnd;
        SampleWaveform = s.IsHeaderOnly ? null : s.Samples();
        // Selection past the (possibly now-shorter, post-edit) frame count is invalid -
        // clamp rather than leave a stale out-of-range value from before an edit.
        SelectionStartFrame = Math.Clamp(SelectionStartFrame, 0, s.FrameCount);
        SelectionEndFrame = Math.Clamp(SelectionEndFrame, SelectionStartFrame, s.FrameCount);
    }

    // ── Editing (safe, confirmed fields only) ───────────────────────────────

    // "Save Sample" and "Save Multisample" are two independent menu items/save paths
    // (a zone's key range lives in the .KMP, a sample's fields live in its own .KSF) -
    // without tracking which side has an unsaved edit, hitting one Save after editing
    // BOTH silently saves only one and reports success with no hint the other change
    // was dropped. These flags exist purely to make that visible in StatusText, not to
    // block either save independently.
    bool _zoneDirty;
    bool _sampleDirty;

    public void ApplyZoneEdits(int originalKey, int topKey)
    {
        if (_selectedZone == null) return;
        _selectedZone.OriginalKey = (byte)Math.Clamp(originalKey, 0, 127);
        _selectedZone.TopKey = (byte)Math.Clamp(topKey, 0, 127);
        ZoneOriginalKey = _selectedZone.OriginalKey;
        ZoneTopKey = _selectedZone.TopKey;
        _zoneDirty = true;
        StatusText = "Zone key range updated (unsaved - use Save Multisample).";
    }

    public void ApplySampleEdits(int sampleRate, bool loopEnabled, int sampleStart, int loopStart, int loopEnd)
    {
        if (_selectedSample == null) return;
        _selectedSample.SampleRate = (uint)Math.Max(1, sampleRate);
        // Bit 0x80 = one-shot/loop-off (doc §5.1) - preserve any other flag bits.
        _selectedSample.Flags = loopEnabled
            ? (byte)(_selectedSample.Flags & ~0x80)
            : (byte)(_selectedSample.Flags | 0x80);
        _selectedSample.SampleStart = (uint)Math.Max(0, sampleStart);
        _selectedSample.LoopStart = (uint)Math.Max(0, loopStart);
        _selectedSample.LoopEnd = (uint)Math.Max(0, loopEnd);
        _selectedSample.ClearPreservedLoopDuplicate();
        LoadSampleDetailState(_selectedSample);
        _sampleDirty = true;
        StatusText = "Sample fields updated (unsaved - use Save Sample).";
    }

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    // Every effect application funnels through here: record the pre-edit PCM for
    // undo, apply, refresh the detail state (frame count/waveform change), mark
    // dirty. ISampleEffect works in host-order short[] - KsfPcm is only the on-disk
    // (KsfSample.Pcm) boundary, crossed once on the way in and once on the way out.
    void ApplyEffect(ISampleEffect effect, string description)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }

        _sampleUndo.RecordBeforeEdit(_selectedSample.Pcm);
        var host = _selectedSample.Samples();
        var edited = effect.Apply(host, (int)_selectedSample.SampleRate);
        _selectedSample.SetSamples(edited);
        _sampleDirty = true;
        LoadSampleDetailState(_selectedSample);
        RefreshUndoRedoState();
        StatusText = $"{description} (unsaved - use Save Sample).";
    }

    public void ApplyCrop()
    {
        if (SelectionEndFrame <= SelectionStartFrame)
        {
            StatusText = "Select a range in the waveform to crop first.";
            return;
        }
        ApplyEffect(new CropEffect(SelectionStartFrame, SelectionEndFrame),
            $"Cropped to [{SelectionStartFrame}, {SelectionEndFrame})");
    }

    public void ApplyNormalize(double targetPeakDb = -0.1) =>
        ApplyEffect(new GainNormalizeEffect(targetPeakDb), "Normalized gain");

    public void ApplyFade(int fadeInFrames, int fadeOutFrames) =>
        ApplyEffect(new FadeEffect(fadeInFrames, fadeOutFrames), "Applied fade");

    public void ApplySilenceTrim(short thresholdAmplitude = 32) =>
        ApplyEffect(new SilenceTrimEffect(thresholdAmplitude), "Trimmed silence");

    public void ApplyTempoPitch(double tempoRatio, double pitchSemitones)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }

        _sampleUndo.RecordBeforeEdit(_selectedSample.Pcm);
        var host = _selectedSample.Samples();
        var edited = TempoPitchProcessor.ChangeTempoAndPitch(host, (int)_selectedSample.SampleRate, tempoRatio, pitchSemitones);
        _selectedSample.SetSamples(edited);
        _sampleDirty = true;
        LoadSampleDetailState(_selectedSample);
        RefreshUndoRedoState();
        StatusText = $"Applied tempo x{tempoRatio:0.##}, pitch {pitchSemitones:+0.##;-0.##;0} semitones (unsaved - use Save Sample).";
    }

    public void Undo()
    {
        if (_selectedSample == null) return;
        var restored = _sampleUndo.Undo(_selectedSample.Pcm);
        if (restored == null) return;
        _selectedSample.Pcm = restored;
        _sampleDirty = true;
        LoadSampleDetailState(_selectedSample);
        RefreshUndoRedoState();

        var evicted = _sampleUndo.TakeEvictedCount();
        StatusText = "Undid last edit (unsaved - use Save Sample)."
            + (evicted > 0 ? $" ({evicted} earlier step(s) no longer available - undo history is capped.)" : "");
    }

    public void Redo()
    {
        if (_selectedSample == null) return;
        var restored = _sampleUndo.Redo(_selectedSample.Pcm);
        if (restored == null) return;
        _selectedSample.Pcm = restored;
        _sampleDirty = true;
        LoadSampleDetailState(_selectedSample);
        RefreshUndoRedoState();
        StatusText = "Redid edit (unsaved - use Save Sample).";
    }

    void RefreshUndoRedoState()
    {
        CanUndo = _sampleUndo.CanUndo;
        CanRedo = _sampleUndo.CanRedo;
    }

    // LoopPreviewEnabled decides which of SamplePlayback's two entry points this uses -
    // a plain one-shot play, or a loop between the sample's own LoopStart/LoopEnd
    // (regardless of whether SampleLoopEnabled/the one-shot flag is set on the Kronos
    // side; this is a preview aid, not a statement about how the Kronos will play it).
    public void PlaySelectedSample()
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) return;
        if (LoopPreviewEnabled)
            _playback.PlayLooped(_selectedSample.Samples(), (int)_selectedSample.SampleRate,
                (int)_selectedSample.LoopStart, (int)_selectedSample.LoopEnd);
        else
            _playback.Play(_selectedSample.Samples(), (int)_selectedSample.SampleRate);
        IsPlaying = true;
    }

    public void StopPlayback()
    {
        _playback.Stop();
        IsPlaying = false;
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    public void SaveSelectedMultisample()
    {
        KmpMultisample? m;
        string? path;
        if (_selectedNode?.MultisampleRef is { } ms)
        {
            (m, path) = (ms.Multisample, ms.Path);
        }
        else if (_selectedZone != null)
        {
            // A zone node only carries its own KmpZone + the owning .KMP's path, not a
            // back-reference to the parent multisample node - walk the tree to find
            // the SAME in-memory KmpMultisample instance (so edits already applied to
            // its Zones aren't lost by re-opening a fresh copy from disk).
            m = FindMultisampleContaining(Roots, _selectedZone);
            path = _selectedKmpPath;
        }
        else
        {
            StatusText = "No multisample selected.";
            return;
        }

        if (m == null || path == null)
        {
            StatusText = "Couldn't resolve the owning multisample to save.";
            return;
        }
        try
        {
            m.Save(path);
            _zoneDirty = false;
            StatusText = $"Saved '{Path.GetFileName(path)}'."
                + (_sampleDirty ? " NOTE: the selected sample's own edits are still unsaved - use Save Sample too." : "");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: multisample save '{path}' failed: {ex}");
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    static KmpMultisample? FindMultisampleContaining(IEnumerable<SampleTreeNode> nodes, KmpZone zone)
    {
        foreach (var node in nodes)
        {
            var found = FindMultisampleContaining(node, zone);
            if (found != null) return found;
        }
        return null;
    }

    static KmpMultisample? FindMultisampleContaining(SampleTreeNode node, KmpZone zone)
    {
        if (node.MultisampleRef is { } ms && ms.Multisample.Zones.Contains(zone)) return ms.Multisample;
        foreach (var child in node.Children)
        {
            var found = FindMultisampleContaining(child, zone);
            if (found != null) return found;
        }
        return null;
    }

    public void SaveSelectedSample()
    {
        if (_selectedSample == null || _selectedSamplePath == null)
        {
            StatusText = "No sample loaded.";
            return;
        }
        try
        {
            _selectedSample.Save(_selectedSamplePath);
            _sampleDirty = false;
            StatusText = $"Saved '{Path.GetFileName(_selectedSamplePath)}'."
                + (_zoneDirty ? " NOTE: the selected zone's key range is still unsaved - use Save Multisample too." : "");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: sample save '{_selectedSamplePath}' failed: {ex}");
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    // ── Import / Export (Phase 4) ───────────────────────────────────────────────

    // Adds a brand-new zone (decoded, downmixed, resampled to the Kronos's own
    // mono/44100 format) to whichever multisample is currently in context - the
    // selected multisample node, or the multisample owning the selected zone, same
    // resolution SaveSelectedMultisample already uses.
    public void ImportAudioAsNewZone(string audioPath, int originalKey, int topKey)
    {
        KmpMultisample? m;
        string? kmpPath;
        if (_selectedNode?.MultisampleRef is { } ms) { m = ms.Multisample; kmpPath = ms.Path; }
        else if (_selectedZone != null) { m = FindMultisampleContaining(Roots, _selectedZone); kmpPath = _selectedKmpPath; }
        else { StatusText = "Select a multisample (or one of its zones) first."; return; }

        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the target multisample."; return; }

        try
        {
            var pcm = AudioImport.ImportToMono44100(audioPath);
            var sampleName = Path.GetFileNameWithoutExtension(audioPath);
            var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, sampleName, pcm, AudioImport.TargetSampleRate, originalKey, topKey);
            m.Save(kmpPath);
            RefreshTreeAfterMutation(m, kmpPath);
            StatusText = $"Imported '{Path.GetFileName(audioPath)}' as zone '{zone.Filename}' (key {originalKey}-{topKey}).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: audio import '{audioPath}' failed: {ex}");
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    // Same tree-rebuild the Open* methods already do at load time - reused here since
    // adding a zone changes the tree shape exactly the way opening one freshly would.
    void RefreshTreeAfterMutation(KmpMultisample m, string kmpPath)
    {
        if (_collection != null && _collectionPath != null)
        {
            RebuildTreeFromCollection(_collectionPath, _collection); // already clears selection, see its own comment
        }
        else
        {
            // Same staleness reasoning as RebuildTreeFromCollection's own comment -
            // the direct-open-a-.KMP path rebuilds Roots with fresh node objects too.
            SelectNode(null);
            Roots.Clear();
            Roots.Add(BuildMultisampleNode(m, kmpPath));
            TreeRefreshed?.Invoke();
        }
    }

    public void ExportSelectedSampleToWav(string wavPath)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        try
        {
            if (!SampleExport.ExportSampleToWav(_selectedSample, wavPath))
            { StatusText = "Can't export: this sample has no audio data (header-only)."; return; }
            StatusText = $"Exported '{Path.GetFileName(wavPath)}'.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: WAV export '{wavPath}' failed: {ex}");
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    public void ExportCollectionToFolder(string outputDir)
    {
        if (_collection == null || _collectionPath == null) { StatusText = "Open a collection first."; return; }
        try
        {
            var (exported, skipped) = SampleExport.ExportCollection(_collection, _collectionPath, outputDir);
            StatusText = $"Exported {exported} WAV file(s) to '{outputDir}'"
                + (skipped > 0 ? $" ({skipped} skipped - header-only or unreadable)." : ".");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: collection export to '{outputDir}' failed: {ex}");
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    // ── Stereo pairs (see kronosology's ksc_kmp_ksf_file_format.md §2.2) ───────────
    //
    // A Kronos stereo instrument is two full multisamples - same Name, opposite
    // "-L"/"-R" Suffix, matching key ranges - never two zones inside one .KMP.

    public void NewStereoMultisamplePairInCollection(string baseName, uint mno1Left)
    {
        if (_collection == null || _collectionPath == null) { StatusText = "Open or create a collection first."; return; }
        try
        {
            var (_, leftPath, _, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                _collection, _collectionPath, baseName, mno1Left);
            RebuildTreeFromCollection(_collectionPath, _collection);
            StatusText = $"Created stereo multisample pair '{baseName}' "
                + $"('{Path.GetFileName(leftPath)}' + '{Path.GetFileName(rightPath)}').";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: new stereo multisample pair failed: {ex}");
            StatusText = $"Failed to create stereo pair: {ex.Message}";
        }
    }

    // Adds a matching zone to both halves of the stereo pair the currently-selected
    // multisample (or its selected zone's owning multisample) belongs to. The source
    // audio's own channels are preserved (a mono source gets duplicated into both -
    // see AudioImport.ConvertToStereo44100), unlike ImportAudioAsNewZone which always
    // downmixes to one channel.
    public void ImportStereoAudioAsNewZonePair(string audioPath, int originalKey, int topKey)
    {
        KmpMultisample? m;
        string? kmpPath;
        if (_selectedNode?.MultisampleRef is { } ms) { m = ms.Multisample; kmpPath = ms.Path; }
        else if (_selectedZone != null) { m = FindMultisampleContaining(Roots, _selectedZone); kmpPath = _selectedKmpPath; }
        else { StatusText = "Select one half of a stereo multisample pair first."; return; }
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the target multisample."; return; }
        if (_collection == null || _collectionPath == null)
        { StatusText = "Open a collection first - stereo pairing needs to search its other multisamples."; return; }

        var (sibling, siblingPath) = SampleImportBuilder.FindStereoSibling(_collection, m, kmpPath);
        if (sibling == null || siblingPath == null)
        {
            StatusText = $"'{m.Name}{m.Suffix}' has no matching stereo sibling in this collection "
                + "- create a stereo pair first (File > New Stereo Multisample Pair).";
            return;
        }

        var (leftM, leftPath, rightM, rightPath) = m.Suffix == "-L"
            ? (m, kmpPath, sibling, siblingPath)
            : (sibling, siblingPath, m, kmpPath);

        try
        {
            var (leftPcm, rightPcm) = AudioImport.ImportStereoToLR44100(audioPath);
            var sampleName = Path.GetFileNameWithoutExtension(audioPath);
            var (lz, rz) = SampleImportBuilder.AddStereoSampleZonePair(leftM, leftPath, rightM, rightPath,
                sampleName, leftPcm, rightPcm, AudioImport.TargetSampleRate, originalKey, topKey);
            leftM.Save(leftPath);
            rightM.Save(rightPath);
            RebuildTreeFromCollection(_collectionPath, _collection);
            StatusText = $"Imported '{Path.GetFileName(audioPath)}' as stereo zone pair "
                + $"'{lz.Filename}'/'{rz.Filename}' (key {originalKey}-{topKey}).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: stereo audio import '{audioPath}' failed: {ex}");
            StatusText = $"Stereo import failed: {ex.Message}";
        }
    }

    // ── New content ──────────────────────────────────────────────────────────

    public void NewCollection(string kscPath)
    {
        try
        {
            var collection = new KscCollection { Path = kscPath };
            Directory.CreateDirectory(Path.Combine(
                Path.GetDirectoryName(kscPath) ?? "", Path.GetFileNameWithoutExtension(kscPath)));
            collection.Save(kscPath);
            _collection = collection;
            _collectionPath = kscPath;
            RebuildTreeFromCollection(kscPath, collection);
            AddRecentFile(kscPath);
            StatusText = $"Created '{Path.GetFileName(kscPath)}'.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: new collection '{kscPath}' failed: {ex}");
            StatusText = $"Failed to create collection: {ex.Message}";
        }
    }

    public void NewMultisampleInCollection(string name, uint mno1)
    {
        if (_collection == null || _collectionPath == null)
        {
            StatusText = "Open or create a collection first.";
            return;
        }
        try
        {
            var kmpDir = Path.Combine(Path.GetDirectoryName(_collectionPath) ?? "",
                Path.GetFileNameWithoutExtension(_collectionPath));
            Directory.CreateDirectory(kmpDir);
            var kmpFileName = $"{name}.KMP";
            var kmpPath = Path.Combine(kmpDir, kmpFileName);
            var m = new KmpMultisample { Name = name, Mno1 = mno1 };
            m.Save(kmpPath);

            _collection.Entries.Add(kmpFileName);
            _collection.Save(_collectionPath);
            RebuildTreeFromCollection(_collectionPath, _collection);
            StatusText = $"Created multisample '{kmpFileName}'.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: new multisample failed: {ex}");
            StatusText = $"Failed to create multisample: {ex.Message}";
        }
    }

    // ── Zone deletion (Phase 5) ──────────────────────────────────────────────────

    // Marks the selected zone SKIPPEDSAMPLE (the doc's own convention for "deliberately
    // unsampled key position") rather than physically removing it from the RLP1 list -
    // removing a zone entirely would silently expand its neighbors' key ranges to fill
    // the gap (each zone's trigger range runs from the PREVIOUS zone's TopKey+1 to its
    // own TopKey), which is surprising behavior for a "delete" action to cause as a side
    // effect. The underlying .KSF is left on disk untouched - orphaned, not destroyed.
    public void DeleteSelectedZone()
    {
        if (_selectedZone == null) { StatusText = "No zone selected."; return; }
        if (_selectedZone.IsSkipped) { StatusText = "This zone is already marked as skipped."; return; }

        _selectedZone.Filename = "SKIPPEDSAMPLE";
        ZoneIsSkipped = true;
        ZoneFilename = "(skipped - no sample)";
        HasSampleLoaded = false;
        SampleWaveform = null;
        _zoneDirty = true;
        StatusText = "Zone marked as skipped (unsaved - use Save Multisample). "
            + "The underlying .KSF file was left on disk, not deleted.";
    }

    // ── Batch export + normalization report (Phase 5) ───────────────────────────

    // Every non-skipped zone in the currently-selected multisample, exported to WAV -
    // the middle ground between ExportSelectedSampleToWav (one sample) and
    // ExportCollectionToFolder (everything).
    public void ExportSelectedMultisampleToFolder(string outputDir)
    {
        KmpMultisample? m;
        string? kmpPath;
        if (_selectedNode?.MultisampleRef is { } ms) { m = ms.Multisample; kmpPath = ms.Path; }
        else if (_selectedZone != null) { m = FindMultisampleContaining(Roots, _selectedZone); kmpPath = _selectedKmpPath; }
        else { StatusText = "No multisample selected."; return; }
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the selected multisample."; return; }

        try
        {
            var (exported, skipped) = SampleExport.ExportMultisample(m, kmpPath, outputDir);
            StatusText = $"Exported {exported} WAV file(s) to '{outputDir}'"
                + (skipped > 0 ? $" ({skipped} skipped - header-only or unreadable)." : ".");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: multisample export to '{outputDir}' failed: {ex}");
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    public List<SampleNormalizationEntry> BuildNormalizationReport()
    {
        if (_collection == null || _collectionPath == null) return [];
        return SampleNormalizationReport.Build(_collection, _collectionPath);
    }

    // ── Recent Files (Phase 5) ───────────────────────────────────────────────────

    public List<string> GetRecentFiles() => Storage.LoadSettings().SampleRecentFiles;

    public void ClearRecentFiles()
    {
        var settings = Storage.LoadSettings();
        settings.SampleRecentFiles.Clear();
        Storage.SaveSettings(settings);
    }

    void AddRecentFile(string path)
    {
        var settings = Storage.LoadSettings();
        settings.SampleRecentFiles.Remove(path);
        settings.SampleRecentFiles.Insert(0, path);
        if (settings.SampleRecentFiles.Count > AppSettings.SampleRecentFilesMax)
            settings.SampleRecentFiles.RemoveRange(AppSettings.SampleRecentFilesMax,
                settings.SampleRecentFiles.Count - AppSettings.SampleRecentFilesMax);
        Storage.SaveSettings(settings);
    }
}
