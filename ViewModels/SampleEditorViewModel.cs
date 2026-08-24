using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    // Feeds Views/SampleKeymapControl.cs - see SelectNode's own comment on how this is
    // resolved. Not [ObservableProperty] since the keymap control is refreshed directly
    // by the window's RefreshDetailPanels() (same as every other detail-panel field
    // here), not via WPF binding.
    public List<KmpZone>? CurrentMultisampleZones { get; private set; }
    public KmpZone? SelectedZoneObject => _selectedZone;

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
    // Hardware-confirmed 2026-08-22 (kronosology doc §3.1a) - the REAL Kronos Reverse/
    // +12dB flags (SMD1 flags byte bits 0x40/0x01). Reverse is distinct from the
    // pre-existing "Reverse" BUTTON (destructive PCM Array.Reverse, ReverseEffect.cs -
    // a totally different DSP operation that just happens to share the English word),
    // but it IS what the "Reverse Loop" checkbox drives (SampleEditorWindow.xaml.cs's
    // OnLoopReverseChanged calls SetReversed) - one flag, one checkbox, both preview
    // and persist together. SampleLoopTune is SMD1 offset 5, -99..+99 (KsfSample.
    // LoopTune enforces the clamp).
    [ObservableProperty] bool sampleReverseEnabled;
    [ObservableProperty] bool sample12dbBoostEnabled;
    [ObservableProperty] int sampleLoopTune;
    [ObservableProperty] short[]? sampleWaveform;

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    SampleEditUndo _sampleUndo = new(Storage.LoadSettings().SampleUndoByteCapMb * 1024L * 1024L);
    readonly SamplePlayback _playback = new();

    // Zone-list undo (KmpZone edits - boundary drag today) is a SEPARATE stack from
    // _sampleUndo (different object graph entirely: a multisample's zone list vs. one
    // KsfSample's PCM/fields). _undoDomains/_redoDomains record, in order, WHICH stack
    // each logical edit belongs to - so Ctrl+Z pops the right one and walks back through
    // a mixed history of sample and zone edits in the actual order they happened,
    // without needing one unified snapshot type for two genuinely different kinds of
    // data. Every call site that records a sample edit also pushes EditDomain.Sample
    // here (see RecordBeforeEdit call sites); MoveZoneBoundary pushes EditDomain.Zone.
    enum EditDomain { Sample, Zone }
    SampleZoneUndo _zoneUndo = new();
    List<KmpZone>? _zoneUndoScope; // which zone list _zoneUndo currently belongs to
    // List used as a stack (append/remove-last), not Stack<T> - SelectNode needs to
    // selectively drop just the Sample or just the Zone entries (whichever underlying
    // stack it's resetting) while preserving the other kind's relative order, and
    // List.RemoveAll does that in one call; Stack<T> has no equivalent.
    readonly List<EditDomain> _undoDomains = new();
    readonly List<EditDomain> _redoDomains = new();

    static EditDomain PopDomain(List<EditDomain> stack)
    {
        var top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return top;
    }

    [ObservableProperty] bool canUndo;
    [ObservableProperty] bool canRedo;
    [ObservableProperty] int selectionStartFrame;
    [ObservableProperty] int selectionEndFrame;
    [ObservableProperty] bool isPlaying;

    // "Use Zero": when set, SetMarker snaps every proposed Sample Start/Loop Start/Loop
    // End value to the nearest zero-crossing (where the waveform crosses its center
    // line) rather than the raw dragged/typed frame - avoids audible clicks at loop/
    // playback boundaries. "Loop Lock": editing one loop edge shifts the OTHER edge by
    // the same amount, preserving loop length - see SetMarker's own comment for how the
    // two interact when both are on. There used to be a third, separate
    // LoopReverseEnabled property here for the "Reverse Loop" checkbox's own preview
    // playback - removed 2026-08-22 (Opus redundancy review) once the 2026-08-22 UI
    // merge (see SampleReverseEnabled's own comment) made it a pure, provably-always-
    // equal alias of SampleReverseEnabled: same value written in the same place
    // (LoadSampleDetailState), same UI checkbox reading it, no XAML binding on either
    // (this window has no DataContext - the whole detail panel is driven imperatively
    // by RefreshDetailPanels). PlaySelectedSample's preview now reads
    // SampleReverseEnabled directly.
    [ObservableProperty] bool useZeroCrossing;
    [ObservableProperty] bool loopLockEnabled;

    // ── Stereo pair view (doc §2.2: same Name, opposite -L/-R Suffix, adjacent MNO1) ──
    //
    // _selectedSample/_selectedZone (above) are whichever zone was actually clicked in
    // the tree - could be the "-L" or "-R" half. _partnerSample is the OTHER half,
    // auto-resolved here whenever the selected zone's owning multisample turns out to
    // be part of a real stereo pair. IsPrimaryLeftChannel says which one _selectedSample
    // is, so the window can always display "-L" on top / "-R" on bottom regardless of
    // which side the user actually clicked.
    KsfSample? _partnerSample;
    string? _partnerSamplePath;
    KmpZone? _partnerZone;
    string? _partnerKmpPath;
    SampleEditUndo _partnerUndo = new(Storage.LoadSettings().SampleUndoByteCapMb * 1024L * 1024L);

    [ObservableProperty] bool hasStereoPair;
    [ObservableProperty] bool isPrimaryLeftChannel;
    [ObservableProperty] short[]? partnerSampleWaveform;

    // The resolved stereo partner's own zone/path - exposed so the window's Split L/R
    // channel picker (Views/SampleEditorWindow.xaml.cs) can find its tree node
    // (FindNodeForZone) and select it directly, the same way every other combo-driven
    // selection in this window already works, instead of requiring the user to go hunt
    // for the sibling zone in the tree themselves.
    public (KmpZone Zone, string KmpPath)? PartnerZoneRef =>
        _partnerZone != null && _partnerKmpPath != null ? (_partnerZone, _partnerKmpPath) : null;

    // Combine (false, default) vs Split (true). Combine mode is a single logical
    // stereo view: BOTH waveform panes are always shown (L top / R bottom), sharing one
    // selection and one set of Sample Start/Loop Start/Loop End markers - toolbar edits
    // (Crop/Normalize/Fade/SilenceTrim/TempoPitch/GainAdjust/loop-from-selection/marker
    // drags) apply to BOTH the selected zone's sample AND its stereo partner, using the
    // same parameters - correctness matters here, not just convenience: mismatched loop
    // points between stereo channels would be a real playback bug on the Kronos. Split
    // mode shows only ONE pane - whichever channel is currently selected in the tree -
    // and edits apply only to that sample; select the OTHER channel's zone in the tree
    // to edit it instead, the same way you'd select any other zone. See
    // SampleEditorWindow.xaml.cs's RefreshDetailPanels for the pane-visibility split.
    [ObservableProperty] bool splitLR;

    // Fixed L-on-top/R-on-bottom regardless of which side is "primary" - the window
    // reads these instead of SampleWaveform/PartnerSampleWaveform directly so it never
    // has to duplicate the IsPrimaryLeftChannel branch itself.
    public short[]? LeftSampleWaveform => !HasStereoPair ? null : IsPrimaryLeftChannel ? SampleWaveform : PartnerSampleWaveform;
    public short[]? RightSampleWaveform => !HasStereoPair ? null : IsPrimaryLeftChannel ? PartnerSampleWaveform : SampleWaveform;

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
        if (_dirtySamples.ContainsKey(_selectedSamplePath)) { StatusText = "Save the sample locally first (use Save Sample), then push."; return; }
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
        if (_dirtyMultisamples.ContainsKey(path)) { StatusText = "Save the multisample locally first (use Save Multisample), then push."; return; }
        if (!_remoteMap.TryGetValue(path, out var remotePath))
        { StatusText = "This multisample wasn't pulled from the Kronos - nowhere to push it back to."; return; }

        StatusText = "Pushing to Kronos...";
        var result = await source.PushAsync(path, remotePath);
        StatusText = result.StatusMessage;
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    // A live streamed shortcut to library content on the Kronos's own SSD, not real
    // sample data of its own - hardware-confirmed Kronos-generated-output-only (same
    // guard KscCollection.ToBytes already enforces on the write side). Editing/pulling
    // one makes no sense: there's nothing here to actually edit, only a pointer.
    public static bool IsUserBank(string path) =>
        Path.GetFileName(path).EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase);

    public void OpenCollection(string path)
    {
        if (IsUserBank(path))
        {
            StatusText = $"'{Path.GetFileName(path)}' is a _UserBank.KSC - a live shortcut to Kronos SSD "
                + "library content, not real sample data. Nothing to edit here; open the actual .KSC instead.";
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var collection = KscCollection.Open(bytes);
            _collection = collection;
            _collectionPath = path;
            RebuildTreeFromCollection(path, collection);
            AddRecentFile(path);
            StatusText = $"Loaded '{Path.GetFileName(path)}' ({collection.Entries.Count} entr{(collection.Entries.Count == 1 ? "y" : "ies")})."
                + (_lastRebuildSkipped.Count > 0
                    ? $" WARNING: {_lastRebuildSkipped.Count} of them couldn't be loaded: {string.Join("; ", _lastRebuildSkipped)}"
                    : "");
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

    // Rebuilds/replaces just the ONE root corresponding to `kscPath`, leaving every
    // OTHER already-open collection's root untouched - opening a second .KSC (or
    // editing zones in one) must not discard whatever else is currently open, which is
    // what unconditionally clearing the whole Roots collection used to do. If no root
    // for this path exists yet, the new one is appended (a fresh Open); if one already
    // exists, it's replaced in place at the same index (a re-open or a post-edit
    // refresh) and its expansion state is carried forward onto the new nodes.
    void RebuildTreeFromCollection(string kscPath, KscCollection collection)
    {
        int existingIndex = Roots.ToList().FindIndex(r =>
            string.Equals(r.CollectionRef?.Path, kscPath, StringComparison.OrdinalIgnoreCase));
        var oldRoot = existingIndex >= 0 ? Roots[existingIndex] : null;

        // Every multisample/zone node gets rebuilt as a brand-new object below (each
        // .KMP is re-opened from disk), so any node the caller was still holding as
        // "currently selected" - including this VM's OWN _selectedNode/_selectedZone/
        // _selectedSample fields - becomes stale: reference-unequal to anything in the
        // new tree, yet the ViewModel's own IsHeaderOnly/SampleXxx/HasZoneSelected
        // properties would otherwise still show the pre-rebuild state. Left alone, a
        // subsequent SaveSelectedMultisample/SaveSelectedSample would silently act on
        // that stale (pre-rebuild) copy - discarding whatever this rebuild's own
        // caller just wrote to disk. Only clears selection when it was actually
        // pointing INTO the collection being rebuilt - a rebuild triggered by editing
        // collection A must not blow away the user's selection in an unrelated,
        // untouched collection B.
        if (oldRoot != null && IsDescendant(oldRoot, _selectedNode)) SelectNode(null);

        // Carry forward which multisample nodes (keyed by their stable .KMP path, not
        // object identity - every node below is freshly constructed) were expanded, so
        // an edit-triggered rebuild doesn't visually collapse the tree the user was
        // looking at (WPF's TreeViewItem expansion is otherwise pure UI state, thrown
        // away whenever the underlying items are replaced).
        var expandedKmpPaths = oldRoot == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : oldRoot.Children.Where(c => c.IsExpanded && c.MultisampleRef != null)
                .Select(c => c.MultisampleRef!.Value.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool rootWasExpanded = oldRoot?.IsExpanded ?? true; // default expanded for a freshly-opened collection

        var root = SampleTreeNode.ForCollection(Path.GetFileName(kscPath), collection, kscPath);
        root.IsExpanded = rootWasExpanded;
        _lastRebuildSkipped.Clear();
        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var kmpDir = KscCollection.ContentDirFor(kscPath);
            var kmpPath = Path.Combine(kmpDir, entry);
            if (!File.Exists(kmpPath))
            {
                if (!IsIgnorablePlaceholderKmp(entry))
                {
                    AppLog.Warn($"Sample Editor: skipping missing multisample '{kmpPath}'");
                    _lastRebuildSkipped.Add($"{entry} (not found on disk)");
                }
                continue;
            }
            try
            {
                // A multisample with pending zone edits keeps its LIVE edited object
                // rather than being re-read from disk. A rebuild is triggered by
                // unrelated operations (Add Zone on a sibling, New Multisample), and
                // re-reading here would silently drop another multisample's unsaved zone
                // edits while leaving them registered as pending - the tree and the
                // pending-save registry would then disagree about the same file.
                // Reverting explicitly clears the registry first, so revert still wins.
                var m = _dirtyMultisamples.TryGetValue(kmpPath, out var pendingMultisample)
                    ? pendingMultisample
                    : KmpMultisample.Open(File.ReadAllBytes(kmpPath));
                if (m == null)
                {
                    if (!IsIgnorablePlaceholderKmp(entry)) _lastRebuildSkipped.Add($"{entry} (not a recognizable .KMP)");
                    continue;
                }
                var node = BuildMultisampleNode(m, kmpPath);
                node.IsExpanded = expandedKmpPaths.Contains(kmpPath);
                root.Children.Add(node);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Sample Editor: skipping unreadable multisample '{kmpPath}': {ex.Message}");
                if (!IsIgnorablePlaceholderKmp(entry)) _lastRebuildSkipped.Add($"{entry} ({ex.Message})");
            }
        }

        if (existingIndex >= 0) Roots[existingIndex] = root; else Roots.Add(root);
        TreeRefreshed?.Invoke();
    }

    // Filled by the most recent RebuildTreeFromCollection call - which .KMP entries
    // listed in the .KSC couldn't be turned into a tree node (missing on disk, or
    // unreadable) and why. OpenCollection reads this right after rebuilding to put a
    // visible warning in StatusText - previously these were ONLY logged (AppLog.Warn),
    // so "Loaded 'X.KSC' (N entries)" (the .KSC's own raw entry count, regardless of
    // what actually resolved) could report success over a tree that's silently empty
    // or missing multisamples, with no indication anything went wrong.
    readonly List<string> _lastRebuildSkipped = [];

    // NEWMS000.KMP/NEWMS001.KMP are the Kronos's own default placeholder multisample
    // names, always present in a brand-new library - missing or unreadable is the
    // NORMAL state for them (they're never actually populated unless the user renames
    // past them), not a real data problem worth surfacing as a warning. Any OTHER
    // missing/unreadable .KMP still warns exactly as before.
    internal static bool IsIgnorablePlaceholderKmp(string entryName)
    {
        var baseName = Path.GetFileNameWithoutExtension(entryName);
        return string.Equals(baseName, "NEWMS000", StringComparison.OrdinalIgnoreCase)
            || string.Equals(baseName, "NEWMS001", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsDescendant(SampleTreeNode root, SampleTreeNode? node)
    {
        if (node == null) return false;
        if (ReferenceEquals(root, node)) return true;
        foreach (var child in root.Children)
            if (IsDescendant(child, node)) return true;
        return false;
    }

    static SampleTreeNode BuildMultisampleNode(KmpMultisample m, string path)
    {
        var node = SampleTreeNode.ForMultisample($"{m.Name}{m.Suffix} ({Path.GetFileName(path)})", m, path);
        foreach (var z in m.Zones)
        {
            var label = z.IsSkipped ? $"(skipped) up to {MidiNoteName.ToName(z.TopKey)}" : $"{z.Filename}  up to {MidiNoteName.ToName(z.TopKey)}";
            node.Children.Add(SampleTreeNode.ForZone(label, z, path));
        }
        return node;
    }

    // ── Unload / Revert ─────────────────────────────────────────────────────────

    // "The active collection" - wherever the current selection is actually pointing
    // (see SelectNode's own comment), or whichever was most recently opened/created if
    // nothing's selected. Read by the window's File > Unload Collection menu item,
    // which has no specific tree node of its own to resolve from.
    public string? ActiveCollectionPath => _collectionPath;
    public bool HasActiveCollection => _collection != null && _collectionPath != null;

    // Which already-open collection (if any) `node` belongs to - used by the tree's
    // right-click "Unload KSC" context menu, which only knows the node the user
    // right-clicked, not which root owns it (SampleTreeNode has no parent pointer).
    public string? FindOwningCollectionPath(SampleTreeNode? node)
    {
        if (node == null) return null;
        var owningRoot = Roots.FirstOrDefault(r => IsDescendant(r, node));
        return owningRoot?.CollectionRef?.Path;
    }

    // Removes an open collection from the tree entirely (File > Unload Collection, or
    // right-click a node > Unload KSC) - a session-only action, nothing on disk is
    // touched. If the collection being unloaded is the currently-active one, clears
    // selection first (same staleness reasoning RebuildTreeFromCollection's own comment
    // documents - a lingering selection into a root that no longer exists would leave
    // the ViewModel's HasZoneSelected/SampleXxx state pointing at nothing).
    public void UnloadCollection(string kscPath)
    {
        var root = Roots.FirstOrDefault(r => string.Equals(r.CollectionRef?.Path, kscPath, StringComparison.OrdinalIgnoreCase));
        if (root == null) { StatusText = "That collection isn't open."; return; }

        if (IsDescendant(root, _selectedNode)) SelectNode(null);
        // Pending edits under a collection that's no longer open have nowhere to be
        // saved from - leaving them registered would keep the close-guard warning about
        // a collection the user explicitly unloaded (and, worse, let a later Save write
        // them back out).
        DiscardPendingEditsUnder(kscPath);
        Roots.Remove(root);
        if (string.Equals(_collectionPath, kscPath, StringComparison.OrdinalIgnoreCase)) { _collection = null; _collectionPath = null; }
        TreeRefreshed?.Invoke();
        StatusText = $"Unloaded '{Path.GetFileName(kscPath)}' (files on disk are untouched - re-open it any time).";
    }

    // Discards every unsaved edit under ONE open collection by re-reading its .KSC (and
    // every referenced .KMP) fresh from disk, replacing just that one root - every OTHER
    // open collection is left untouched, same scoping RebuildTreeFromCollection already
    // gives every other per-root operation. Unsaved sample-field/PCM edits are also
    // discarded since re-reading rebuilds brand-new KmpMultisample/KmpZone objects and
    // SelectNode(null) (called internally by RebuildTreeFromCollection, since the old
    // selection is a descendant of the root being replaced) drops the in-memory
    // KsfSample along with it.
    public void RevertActiveCollectionChanges()
    {
        if (_collectionPath == null) { StatusText = "No collection is open to revert."; return; }
        var path = _collectionPath;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var collection = KscCollection.Open(bytes);
            _collection = collection;
            _collectionPath = path;
            // Discarding the pending edits is what actually makes this a revert now that
            // they outlive navigation - re-reading the tree alone would leave them
            // registered, so the next Save would write them straight back.
            DiscardPendingEditsUnder(path);
            RebuildTreeFromCollection(path, collection);
            StatusText = $"Reverted '{Path.GetFileName(path)}' - unsaved changes discarded."
                + (_lastRebuildSkipped.Count > 0
                    ? $" WARNING: {_lastRebuildSkipped.Count} of them couldn't be loaded: {string.Join("; ", _lastRebuildSkipped)}"
                    : "");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: revert '{path}' failed: {ex}");
            StatusText = $"Revert failed: {ex.Message}";
        }
    }

    // Closes every open collection/multisample and resets every piece of session state
    // that could otherwise leak across a "start from scratch" - the literal "begin
    // again" the user asked for, not just a per-collection revert. Doesn't touch disk;
    // whatever was already saved is untouched, only this session's in-memory/open-tab
    // state is cleared.
    public void RevertAllChanges()
    {
        SelectNode(null);
        Roots.Clear();
        _collection = null;
        _collectionPath = null;
        _remoteMap.Clear();
        _sampleUndo = new SampleEditUndo(_sampleUndo.ByteCap);
        _partnerUndo = new SampleEditUndo(_partnerUndo.ByteCap);
        _zoneUndo = new SampleZoneUndo();
        _zoneUndoScope = null;
        _undoDomains.Clear();
        _redoDomains.Clear();
        CanUndo = false;
        CanRedo = false;
        DiscardPendingEditsUnder(null); // null = every collection
        _zoneDirtyField = false;
        _sampleDirtyField = false;
        TreeRefreshed?.Invoke();
        StatusText = "All collections closed - starting fresh.";
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

        // Now that more than one .KSC can be open at once (see RebuildTreeFromCollection),
        // "the active collection" for the collection-LEVEL operations below (Export
        // Collection, New Stereo Pair, Add Multisample, the normalization report) has to
        // track wherever the user is actually looking, not just whichever was opened
        // most recently - otherwise those operations would silently keep targeting the
        // FIRST collection even after the user navigated into a second one.
        if (node != null)
        {
            var owningRoot = Roots.FirstOrDefault(r => IsDescendant(r, node));
            if (owningRoot?.CollectionRef is { } cref) { _collection = cref.Collection; _collectionPath = cref.Path; }
        }

        // Feeds the keymap visualization (Views/SampleKeymapControl.cs) - whichever
        // multisample is "in context" for a multisample node OR one of its zone nodes,
        // same resolution SaveSelectedMultisample already uses for the zone case.
        CurrentMultisampleZones = node?.MultisampleRef is { } ms ? ms.Multisample.Zones
            : _selectedZone != null ? FindMultisampleContaining(Roots, _selectedZone)?.Zones
            : null;

        // Undo history is scoped to whichever sample is currently loaded - switching
        // to a different zone starts a fresh history rather than letting Undo reach
        // back into an unrelated sample's edits.
        _sampleUndo = new SampleEditUndo(_sampleUndo.ByteCap);
        _partnerUndo = new SampleEditUndo(_partnerUndo.ByteCap);
        _cursorFrame = -1; // a different sample/zone means "no scrub position chosen yet" again
        // Drop only the Sample-domain entries (that stack always resets above) - NOT a
        // blanket Clear(), which would also wipe any Zone-domain entries for a
        // multisample whose zone-undo history is surviving this navigation (see below).
        _undoDomains.RemoveAll(d => d == EditDomain.Sample);
        _redoDomains.RemoveAll(d => d == EditDomain.Sample);
        // Zone-list undo is scoped to the MULTISAMPLE, not the zone selection -
        // clicking between zones within the same multisample (e.g. after a boundary
        // drag, to inspect the result) must NOT wipe the history of that drag before
        // Ctrl+Z gets a chance to run. Only reset when CurrentMultisampleZones is about
        // to point at a genuinely different list (a different multisample, or none) -
        // and only THEN drop the Zone-domain entries too, in lockstep with the stack
        // they refer to.
        if (!ReferenceEquals(CurrentMultisampleZones, _zoneUndoScope))
        {
            _zoneUndo = new SampleZoneUndo();
            _zoneUndoScope = CurrentMultisampleZones;
            _undoDomains.RemoveAll(d => d == EditDomain.Zone);
            _redoDomains.RemoveAll(d => d == EditDomain.Zone);
        }
        CanUndo = _undoDomains.Count > 0;
        CanRedo = _redoDomains.Count > 0;
        SelectionStartFrame = 0;
        SelectionEndFrame = 0;

        _partnerSample = null;
        _partnerSamplePath = null;
        _partnerZone = null;
        _partnerKmpPath = null;
        HasStereoPair = false;
        PartnerSampleWaveform = null;

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
                // An unsaved edit for this path wins over what's on disk - see
                // _dirtySamples' own comment. _sampleDirtyField is set directly rather
                // than through the property: the sample is already enrolled, and going
                // through the setter here would just re-register it.
                if (_dirtySamples.TryGetValue(ksfPath, out var pending))
                {
                    _selectedSample = pending;
                    _selectedSamplePath = ksfPath;
                    _sampleDirtyField = true;
                    LoadSampleDetailState(pending, reloadWaveform: true);
                    ResolveStereoPartner(zone, _selectedKmpPath);
                }
                else if (File.Exists(ksfPath))
                {
                    var s = KsfSample.Open(File.ReadAllBytes(ksfPath));
                    if (s != null)
                    {
                        _selectedSample = s;
                        _selectedSamplePath = ksfPath;
                        LoadSampleDetailState(s, reloadWaveform: true);
                        ResolveStereoPartner(zone, _selectedKmpPath);
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

    // Doc §2.2: a stereo instrument is two complete multisamples (same Name, opposite
    // -L/-R Suffix, adjacent MNO1), each zone matched by key range, not by index -
    // matching on (OriginalKey, TopKey) is the same invariant FindStereoSibling itself
    // relies on for the multisample-level match. Best-effort: any failure here (no
    // collection open, sibling not found, partner .KSF missing/unreadable) just leaves
    // HasStereoPair false - the primary sample is already loaded fine regardless.
    void ResolveStereoPartner(KmpZone zone, string kmpPath)
    {
        if (_collection == null) return;
        var owningMultisample = _selectedNode?.MultisampleRef?.Multisample ?? FindMultisampleContaining(Roots, zone);
        if (owningMultisample is not { Suffix: "-L" or "-R" }) return;

        // Prefer the LIVE in-tree sibling over SampleImportBuilder.FindStereoSibling's
        // fresh disk read - otherwise an unsaved key-range edit on either half makes the
        // exact (OriginalKey, TopKey) match below fail against the stale on-disk copy,
        // and the pair silently drops back to a mono view.
        var (sibling, siblingPath) = ResolveStereoSibling(owningMultisample, kmpPath);
        if (sibling == null || siblingPath == null) return;

        // Exact key-range match is the semantically correct correspondence when the two
        // halves' keymaps agree (immune to reordering, since it's a value match, not a
        // positional one) - but real, hand-edited or hand-pulled content routinely has
        // the two channels split at slightly different points, and that's still
        // legitimately a stereo pair. An exact-match-or-nothing rule silently dropped to
        // a mono view for any such pair, which is wrong: being part of a -L/-R pair
        // should always be enough to show both channels. Falls back to the SAME INDEX in
        // the sibling's own zone list - the same correspondence rule
        // ResolveSiblingZonesFor already uses for mirroring key-range edits, so "which
        // zone is this zone's partner" is answered the same way for both editing and
        // display.
        var matchZone = sibling.Zones.FirstOrDefault(z =>
            !z.IsSkipped && z.OriginalKey == zone.OriginalKey && z.TopKey == zone.TopKey);
        if (matchZone == null)
        {
            int idx = owningMultisample.Zones.IndexOf(zone);
            if (idx >= 0 && idx < sibling.Zones.Count && !sibling.Zones[idx].IsSkipped)
                matchZone = sibling.Zones[idx];
        }
        if (matchZone == null) return;

        var partnerKsfPath = matchZone.KsfPath(siblingPath);

        // Same pending-edit-wins rule the primary uses in SelectNode - without it, a
        // mirrored stereo edit would be shown for L and silently re-read from disk
        // (i.e. reverted on screen) for R after any navigation.
        if (_dirtySamples.TryGetValue(partnerKsfPath, out var pendingPartner))
        {
            _partnerSample = pendingPartner;
            _partnerSamplePath = partnerKsfPath;
            _partnerZone = matchZone;
            _partnerKmpPath = siblingPath;
            HasStereoPair = true;
            IsPrimaryLeftChannel = owningMultisample.Suffix == "-L";
            PartnerSampleWaveform = pendingPartner.IsHeaderOnly ? null : pendingPartner.Samples();
            return;
        }

        if (!File.Exists(partnerKsfPath)) return;

        try
        {
            var ps = KsfSample.Open(File.ReadAllBytes(partnerKsfPath));
            if (ps == null) return;
            _partnerSample = ps;
            _partnerSamplePath = partnerKsfPath;
            _partnerZone = matchZone;
            _partnerKmpPath = siblingPath;
            HasStereoPair = true;
            IsPrimaryLeftChannel = owningMultisample.Suffix == "-L";
            PartnerSampleWaveform = ps.IsHeaderOnly ? null : ps.Samples();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Sample Editor: stereo partner load '{partnerKsfPath}' failed: {ex.Message}");
        }
    }

    // The stereo sibling's LIVE in-tree KmpMultisample, as opposed to
    // SampleImportBuilder.FindStereoSibling, which re-OPENS the sibling .KMP from disk
    // and hands back a fresh object. A fresh object is correct for an operation that
    // saves immediately (Add Zone); it is wrong for every live in-memory edit, because
    // mirroring onto a copy the tree doesn't hold makes the change invisible in the UI
    // and then discards it when the tree's own instance is saved instead. Matching rule
    // is identical to FindStereoSibling's (same Name, opposite Suffix, adjacent Mno1,
    // same folder - doc §2.2).
    (KmpMultisample? Sibling, string? Path) FindLiveStereoSibling(KmpMultisample m, string kmpPath)
    {
        if (m.Suffix is not ("-L" or "-R")) return (null, null);
        var wantSuffix = m.Suffix == "-L" ? "-R" : "-L";
        var kmpDir = Path.GetDirectoryName(kmpPath) ?? "";

        foreach (var node in EnumerateNodes(Roots))
        {
            if (node.MultisampleRef is not { } ms) continue;
            if (string.Equals(ms.Path, kmpPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(Path.GetDirectoryName(ms.Path) ?? "", kmpDir, StringComparison.OrdinalIgnoreCase)) continue;

            var c = ms.Multisample;
            bool mno1Adjacent = c.Mno1 == m.Mno1 + 1 || (m.Mno1 > 0 && c.Mno1 == m.Mno1 - 1);
            if (c.Name == m.Name && c.Suffix == wantSuffix && mno1Adjacent) return (c, ms.Path);
        }
        return (null, null);
    }

    // Live-sibling-first, disk-fallback second - extracted 2026-08-22 (Opus redundancy
    // review) from four identical copies of this exact two-step lookup. Prefer the LIVE
    // in-tree sibling over SampleImportBuilder.FindStereoSibling's fresh disk read -
    // otherwise an unsaved key-range/content edit on either half makes the live object
    // stale relative to what a caller needs, and mutating a fresh disk copy instead of
    // the tree's own instance makes the change invisible in the UI and then discards it
    // when the tree's own instance is saved instead. Falls back to the disk lookup for a
    // sibling that genuinely isn't in the tree. Returns (null, null) with no collection
    // open, same as a not-found sibling - callers don't need to null-check _collection
    // separately.
    (KmpMultisample? Sibling, string? Path) ResolveStereoSibling(KmpMultisample m, string kmpPath)
    {
        var (sibling, siblingPath) = FindLiveStereoSibling(m, kmpPath);
        if (sibling != null && siblingPath != null) return (sibling, siblingPath);
        return _collection != null ? SampleImportBuilder.FindStereoSibling(_collection, m, kmpPath) : (null, null);
    }

    static IEnumerable<SampleTreeNode> EnumerateNodes(IEnumerable<SampleTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children)) yield return child;
        }
    }

    // Flat list of every multisample node across every open collection, for the
    // Sample Editor's "Multisample (MS)" dropdown (Views/SampleEditorWindow.xaml.cs) -
    // picking one there is functionally the same as selecting its .KMP in the tree.
    public IEnumerable<SampleTreeNode> AllMultisampleNodes() => EnumerateNodes(Roots).Where(n => n.MultisampleRef != null);

    // The stereo sibling's own zone list, for the three key-range edits that must mirror
    // onto it (ApplyZoneEdits / MoveZoneBoundary / ReorderZone). Returns nothing unless
    // the pair is well-formed - same zone count - which is entry 19's established
    // precedent for Add Zone: a pair that's ALREADY out of parity is left alone rather
    // than guessed at, and the primary's own edit still goes through either way.
    (List<KmpZone>? Zones, KmpMultisample? Multisample, string? Path) ResolveSiblingZonesFor(List<KmpZone> primaryZones)
    {
        var (m, path) = ResolveContextMultisample();
        if (m == null || path == null || !ReferenceEquals(m.Zones, primaryZones)) return (null, null, null);

        var (sibling, sibPath) = FindLiveStereoSibling(m, path);
        if (sibling == null || sibPath == null || sibling.Zones.Count != primaryZones.Count) return (null, null, null);
        return (sibling.Zones, sibling, sibPath);
    }

    // reloadWaveform: whether to re-decode SampleWaveform from s.Samples() this call.
    // Performance fix 2026-08-22 (Opus review): KsfSample.Samples() decodes a BRAND-NEW
    // array from raw big-endian bytes every time it's called, with no caching - for a
    // real multi-minute 44.1kHz sample that's millions of iterations plus a large
    // allocation. Every header-only-field caller of this method (SetMarker,
    // SetLoopEnabled, SetReversed, Set12dbBoostEnabled, SetLoopTune, MoveLoopRegion) was
    // paying that cost on EVERY call - including every keystroke, now that those fields
    // commit live - despite never touching Pcm at all. Worse, a freshly-decoded array is
    // never reference-equal to the old one, which silently defeated
    // SampleWaveformControl's own trace-geometry cache too (keyed on array identity),
    // forcing a full O(sample-length) re-render on every such call as well. Only the
    // genuine PCM-mutating callers (initial selection, ApplyEffect, fades, tempo/pitch,
    // Undo/Redo) pass true - everything else leaves SampleWaveform (and therefore the
    // render cache) untouched, since nothing in the buffer actually changed.
    void LoadSampleDetailState(KsfSample s, bool reloadWaveform)
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
        SampleReverseEnabled = s.IsReversed;
        Sample12dbBoostEnabled = s.Is12dbBoostEnabled;
        SampleLoopTune = s.LoopTune;
        if (reloadWaveform) SampleWaveform = s.IsHeaderOnly ? null : s.Samples();
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
    //
    // Properties (not plain fields) specifically so every existing "_zoneDirty = true"/
    // "_sampleDirty = true" call site - there are over a dozen, scattered across every
    // edit method in this file - ALSO enrols what it just edited in the pending-save
    // registries below, without having to touch each of those sites individually.
    // SelectNode resets the plain fields on every navigation (a stale "unsaved" badge on
    // whatever you're CURRENTLY looking at would be confusing); the registries survive
    // it, which is what makes "edited zone A, navigated to zone B" safe.
    bool _zoneDirtyField;
    bool _zoneDirty { get => _zoneDirtyField; set { _zoneDirtyField = value; if (value) RegisterDirtyMultisample(); } }
    bool _sampleDirtyField;
    bool _sampleDirty { get => _sampleDirtyField; set { _sampleDirtyField = value; if (value) RegisterDirtySample(); } }

    // Every sample / multisample edited this session and not yet written back, keyed by
    // its own file path. These hold the LIVE edited objects, and they are load-bearing
    // in three separate ways:
    //
    //  1. SelectNode reads _dirtySamples back instead of re-opening the .KSF, so
    //     navigating to another zone and returning keeps your edits. Before this, it
    //     unconditionally re-read from disk - every unsaved PCM/field edit was silently
    //     destroyed the instant a different tree node was clicked, and the close-guard
    //     then still warned about edits that no longer existed anywhere.
    //  2. Save writes EVERY entry, not just the selection. Save previously wrote only
    //     _selectedSamplePath, so in stereo Combine mode the partner's half of every
    //     mirrored edit was thrown away and the pair diverged on disk - defeating all
    //     the careful mirroring the edit methods do.
    //  3. HasUnsavedChanges becomes exact rather than best-effort (see below).
    readonly Dictionary<string, KsfSample> _dirtySamples = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, KmpMultisample> _dirtyMultisamples = new(StringComparer.OrdinalIgnoreCase);

    // Now EXACT, where it used to be documented as "best-effort, never stale-negative"
    // and in fact went stale-negative: saving multisample B cleared one global session
    // flag, silently covering an unsaved multisample A whose live KmpZone objects were
    // still mutated in the tree. Keying on what's actually pending makes that
    // impossible to express.
    public bool HasUnsavedChanges => _dirtySamples.Count > 0 || _dirtyMultisamples.Count > 0;

    // Called from the _sampleDirty setter, so every edit method that already marks the
    // sample dirty enrols it here with no per-site change. The stereo partner is
    // enrolled alongside it whenever mirroring is active, because every mirrored edit
    // writes to BOTH objects.
    void RegisterDirtySample()
    {
        if (_selectedSample != null && _selectedSamplePath != null)
            _dirtySamples[_selectedSamplePath] = _selectedSample;
        if (ShouldMirrorToPartner && _partnerSample != null && _partnerSamplePath != null)
            _dirtySamples[_partnerSamplePath] = _partnerSample;
    }

    void RegisterDirtyMultisample()
    {
        var (m, path) = ResolveContextMultisample();
        if (m != null && path != null) _dirtyMultisamples[path] = m;
    }

    // Explicit enrolment for something that ISN'T the current selection - the stereo
    // sibling that the zone-edit mirroring below writes to. The _zoneDirty setter can
    // only ever resolve the selected side.
    void RegisterDirtyMultisample(KmpMultisample m, string path) => _dirtyMultisamples[path] = m;

    // "Whichever multisample the current selection is about" - the selected multisample
    // node, or the one owning the selected zone. This resolution was repeated verbatim
    // in half a dozen methods; factored out so the dirty-tracking setter uses exactly
    // the same rule every edit path already does.
    (KmpMultisample? Multisample, string? Path) ResolveContextMultisample()
    {
        if (_selectedNode?.MultisampleRef is { } ms) return (ms.Multisample, ms.Path);
        if (_selectedZone == null) return (null, null);
        var found = FindMultisampleAndPathContaining(Roots, _selectedZone);
        return found.Multisample != null ? found : (FindMultisampleContaining(Roots, _selectedZone), _selectedKmpPath);
    }

    // Full display name (Name+Suffix) of whichever multisample is in context right now -
    // same resolution ResolveContextMultisample uses - for the Edit > Rename Multisample
    // dialog's pre-fill and its enabled state.
    public string? CurrentMultisampleName
    {
        get { var (m, _) = ResolveContextMultisample(); return m == null ? null : $"{m.Name}{m.Suffix}"; }
    }

    // Same resolution/display as CurrentMultisampleName - separate property so the
    // Delete Multisample confirm dialog (code-behind) doesn't read a property named for
    // Rename's own purpose.
    public string? SelectedMultisampleLabel => CurrentMultisampleName;

    // Renames the in-context multisample's Name field (Suffix - the "-L"/"-R" stereo
    // marker - is left alone; editing it here would silently break stereo pairing,
    // which matches by exact Suffix, not something a plain rename box should risk). A
    // live edit like every other field here: marks the .KMP dirty, doesn't save
    // immediately - Save Changes/Save Multisample writes it. Mirrored onto a resolved
    // stereo sibling the same way every other zone/name edit is, since the sibling
    // match (FindLiveStereoSibling) requires an EXACT Name match - renaming only one
    // half would silently break the pairing the same way an unmirrored key-range edit
    // does (see ApplyZoneEdits' own comment).
    public void RenameSelectedMultisample(string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return;
        var (m, path) = ResolveContextMultisample();
        if (m == null || path == null) { StatusText = "Select a multisample (or one of its zones) first."; return; }
        if (m.Name == newName) return;

        var (sibling, sibPath) = FindLiveStereoSibling(m, path);
        m.Name = newName;
        RegisterDirtyMultisample(m, path);
        if (sibling != null && sibPath != null)
        {
            sibling.Name = newName;
            RegisterDirtyMultisample(sibling, sibPath);
        }
        StatusText = $"Renamed multisample to '{newName}{m.Suffix}'"
            + (sibling != null ? " (mirrored to stereo partner)" : "") + " - not yet saved.";
    }

    // Same idea for the currently loaded sample's Name (Suffix left alone, mirrored to
    // the resolved stereo partner sample when one's active) - marks it dirty via the
    // same _sampleDirty setter every other sample field edit here already uses, so Save
    // Sample/Save Changes picks it up with no separate code path.
    public void RenameSelectedSample(string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || _selectedSample == null) return;
        if (_selectedSample.Name == newName) return;

        _selectedSample.Name = newName;
        if (ShouldMirrorToPartner && _partnerSample != null) _partnerSample.Name = newName;
        _sampleDirty = true;
        SampleName = _selectedSample.Name;
        StatusText = $"Renamed sample to '{newName}{_selectedSample.Suffix}'"
            + (ShouldMirrorToPartner && _partnerSample != null ? " (mirrored to stereo partner)" : "") + " - not yet saved.";
    }

    // Drops pending edits belonging to one collection (Unload/Revert KSC) or all of them
    // (Revert ALL). Scoped by path prefix, matching the app's own
    // <kscDir>/<kscBasename>/ folder convention for a collection's .KMP/.KSF content.
    void DiscardPendingEditsUnder(string? kscPath)
    {
        if (kscPath == null) { _dirtySamples.Clear(); _dirtyMultisamples.Clear(); return; }
        var contentDir = KscCollection.ContentDirFor(kscPath);
        foreach (var key in _dirtySamples.Keys.Where(k => IsUnder(k, contentDir)).ToList()) _dirtySamples.Remove(key);
        foreach (var key in _dirtyMultisamples.Keys.Where(k => IsUnder(k, contentDir)).ToList()) _dirtyMultisamples.Remove(key);
    }

    // The trailing separator is load-bearing: collections "Foo.KSC" and "FooBar.KSC"
    // have content dirs "<...>/Foo" and "<...>/FooBar", and a bare StartsWith would make
    // unloading or reverting Foo silently discard FooBar's pending edits.
    static bool IsUnder(string filePath, string dir)
    {
        var fileDir = Path.GetDirectoryName(filePath) ?? "";
        if (string.Equals(fileDir, dir, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return fileDir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // Writing a multisample straight to disk (the add/import paths, which save
    // immediately rather than deferring to Save Changes) must also retire its pending-
    // save registration - otherwise the registry keeps a now-stale claim on that path,
    // and RebuildTreeFromCollection's "keep the pending object" rule would resurrect the
    // pre-save state over what was just written.
    void SaveMultisampleNow(KmpMultisample m, string path)
    {
        m.Save(path);
        _dirtyMultisamples.Remove(path);
    }

    // Top Key can never be typed lower than the PREVIOUS zone's own Top Key + 1 - a
    // zone's trigger range always runs from (previous zone's TopKey + 1) through its
    // own TopKey (KmpZone's own convention, same one the keymap's boundary-drag already
    // enforces by construction), so a Top Key at or below the previous zone's would
    // create a zero/negative-width or inverted range. The keymap's drag handle already
    // can't produce this (its min/max clamp the drag to the neighboring boundaries) -
    // this is the same floor applied to the manual Top Key text field, which had no
    // such neighbor-aware check before.
    // The ceiling is the mirror image of that floor, and was missing: with only a floor,
    // typing a Top Key ABOVE the next zone's own Top Key left that neighbour with an
    // inverted (negative-width) range - the exact failure the floor exists to prevent,
    // just in the other direction.
    public void ApplyZoneEdits(int originalKey, int topKey)
    {
        if (_selectedZone == null) return;
        int floor = 0, ceiling = 127;
        int idx = -1;
        if (CurrentMultisampleZones is { } bounds)
        {
            idx = bounds.IndexOf(_selectedZone);
            if (idx > 0) floor = bounds[idx - 1].TopKey + 1;
            if (idx >= 0 && idx < bounds.Count - 1) ceiling = Math.Max(floor, bounds[idx + 1].TopKey - 1);
        }

        byte newOrig = (byte)Math.Clamp(originalKey, 0, 127);
        byte newTop = (byte)Math.Clamp(Math.Clamp(topKey, floor, ceiling), 0, 127);

        // These fields commit on LostFocus, which fires on every focus change - not just
        // the ones that actually changed a value. Without this guard, tabbing through
        // the row pushed dead undo steps and flagged the file unsaved for edits that
        // never happened.
        if (newOrig == _selectedZone.OriginalKey && newTop == _selectedZone.TopKey)
        {
            ZoneOriginalKey = newOrig;
            ZoneTopKey = newTop;
            return;
        }

        // Mirrored onto the stereo sibling's matching zone, and both lists snapshotted
        // as ONE undo step. A stereo pair is matched by exact (OriginalKey, TopKey) -
        // see ResolveStereoPartner - so editing one half alone silently breaks the pair
        // and drops the shared L/R view back to mono, which is the bug class entry 19
        // fixed for Add Zone and left unfixed here.
        var (siblingZones, siblingM, siblingPath) = CurrentMultisampleZones is { } primary
            ? ResolveSiblingZonesFor(primary) : (null, null, null);

        if (CurrentMultisampleZones is { } zones)
        {
            _zoneUndo.RecordBeforeEdit(ZoneListSnapshot.Of(zones, siblingZones));
            _undoDomains.Add(EditDomain.Zone);
            _redoDomains.Clear();
        }

        _selectedZone.OriginalKey = newOrig;
        _selectedZone.TopKey = newTop;
        if (siblingZones != null && idx >= 0 && idx < siblingZones.Count)
        {
            siblingZones[idx].OriginalKey = newOrig;
            siblingZones[idx].TopKey = newTop;
        }

        ZoneOriginalKey = _selectedZone.OriginalKey;
        ZoneTopKey = _selectedZone.TopKey;
        _zoneDirty = true;
        if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
        RefreshUndoRedoState();
        StatusText = $"Zone key range updated{(siblingZones != null ? " (both L/R channels)" : "")} "
            + "(unsaved - use Save Multisample).";
    }

    // True when a stereo partner exists AND Combine mode is active AND the partner
    // actually has audio data to edit - the single gate every stereo-mirroring edit
    // method below checks before also touching _partnerSample.
    bool ShouldMirrorToPartner => HasStereoPair && !SplitLR && _partnerSample is { IsHeaderOnly: false };

    public void ApplySampleEdits(int sampleRate, bool loopEnabled, int sampleStart, int loopStart, int loopEnd)
    {
        if (_selectedSample == null) return;
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        ApplySampleFieldsTo(_selectedSample, sampleRate, loopEnabled, sampleStart, loopStart, loopEnd);
        _sampleDirty = true;
        if (mirror) ApplySampleFieldsTo(_partnerSample!, sampleRate, loopEnabled, sampleStart, loopStart, loopEnd);

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Sample fields updated{(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Sample).";
    }

    // The single place the "Loop Start can never precede Sample Start (and vice versa -
    // Sample Start is always the floor)" ordering invariant is enforced - every caller
    // (the bulk field-apply, SetLoopFromSelection, MoveLoopRegion, SetMarker) routes
    // through here, so the invariant can't be bypassed by editing one path and not
    // another. LoopEnd is likewise floored at the (possibly just-raised) LoopStart.
    static void ApplySampleFieldsTo(KsfSample sample, int sampleRate, bool loopEnabled, int sampleStart, int loopStart, int loopEnd)
    {
        sampleStart = Math.Max(0, sampleStart);
        loopStart = Math.Max(loopStart, sampleStart);
        loopEnd = Math.Max(loopEnd, loopStart);

        sample.SampleRate = (uint)Math.Max(1, sampleRate);
        // Bit 0x80 = one-shot/loop-off (doc §5.1) - preserve any other flag bits.
        sample.Flags = loopEnabled ? (byte)(sample.Flags & ~0x80) : (byte)(sample.Flags | 0x80);
        sample.SampleStart = (uint)sampleStart;
        sample.LoopStart = (uint)loopStart;
        sample.LoopEnd = (uint)loopEnd;
        sample.ClearPreservedLoopDuplicate();
    }

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    // Every effect application funnels through here: record the pre-edit PCM for
    // undo, apply, refresh the detail state (frame count/waveform change), mark
    // dirty. ISampleEffect works in host-order short[] - KsfPcm is only the on-disk
    // (KsfSample.Pcm) boundary, crossed once on the way in and once on the way out.
    //
    // In Combine mode with a stereo partner, the SAME effect instance is replayed
    // against the partner's own PCM too - ISampleEffect.Apply is a pure function of
    // (pcm, sampleRate), so reusing the exact instance against different data is
    // trivially correct, no per-effect stereo-aware variant needed.
    void ApplyEffect(ISampleEffect effect, string description)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        var host = _selectedSample.Samples();
        var edited = effect.Apply(host, (int)_selectedSample.SampleRate);
        _selectedSample.SetSamples(edited);
        ClampMarkersToBuffer(_selectedSample);
        _sampleDirty = true;

        if (mirror)
        {
            _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));
            var partnerHost = _partnerSample!.Samples();
            var partnerEdited = effect.Apply(partnerHost, (int)_partnerSample.SampleRate);
            _partnerSample.SetSamples(partnerEdited);
            ClampMarkersToBuffer(_partnerSample);
            PartnerSampleWaveform = _partnerSample.IsHeaderOnly ? null : _partnerSample.Samples();
        }

        LoadSampleDetailState(_selectedSample, reloadWaveform: true);
        RefreshUndoRedoState();
        StatusText = $"{description}{(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Sample).";
    }

    // A length-changing edit (Crop, Trim Silence, Cut, Paste, Insert Silence, Tempo/
    // Pitch) can leave Sample Start / Loop Start / Loop End pointing past the end of the
    // PCM. KsfSample.ToBytes deliberately writes all three verbatim - it HAS to, since a
    // header-only file's stale LoopEnd is real recoverable data (doc §3.3) - and its own
    // comment explicitly puts re-deriving them on "callers that resize Pcm". No caller
    // did, so a crop wrote out-of-range loop points straight into the .KSF. Playback
    // never showed it (LoopingSampleProvider re-clamps in its constructor); the saved
    // file carried the damage.
    //
    // Header-only samples are skipped rather than clamped to 0 - that would destroy
    // exactly the preserved pre-corruption value §3.3 exists to keep.
    static void ClampMarkersToBuffer(KsfSample sample)
    {
        if (sample.IsHeaderOnly) return;
        uint end = (uint)sample.FrameCount;
        if (sample.SampleStart <= end && sample.LoopStart <= end && sample.LoopEnd <= end) return;

        sample.SampleStart = Math.Min(sample.SampleStart, end);
        sample.LoopStart = Math.Min(sample.LoopStart, end);
        sample.LoopEnd = Math.Min(sample.LoopEnd, end);
        // LoopStart may have just moved, and the offset-24 duplicate slot mirrors it -
        // same reason ApplySampleFieldsTo clears it after every field write.
        sample.ClearPreservedLoopDuplicate();
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

    // A stereo pair normalizes as a SINGLE track, not two independent ones - the shared
    // peak (whichever channel is louder) is measured up front and baked into one effect
    // instance, so both channels scale by the same factor and their relative balance is
    // preserved. Independent per-channel measurement would boost a quieter channel more
    // than its louder partner, audibly shifting the stereo image.
    public void ApplyNormalize(double targetPeakDb = -0.1)
    {
        int? sharedPeak = null;
        if (ShouldMirrorToPartner && _selectedSample is { IsHeaderOnly: false })
        {
            int primaryPeak = GainNormalizeEffect.ComputePeak(_selectedSample.Samples());
            int partnerPeak = GainNormalizeEffect.ComputePeak(_partnerSample!.Samples());
            sharedPeak = Math.Max(primaryPeak, partnerPeak);
        }
        ApplyEffect(new GainNormalizeEffect(targetPeakDb, sharedPeak), "Normalized gain");
    }

    // A stereo pair trims as a SINGLE track: only delete a leading/trailing run that's
    // silent in BOTH channels, or the two channels get cropped to different lengths and
    // end up offset relative to each other. Computed as the UNION of each channel's own
    // non-silent bounds (the narrower of the two silence runs on each end wins) and
    // baked into one shared instance, same shape as ApplyNormalize's shared peak.
    public void ApplySilenceTrim(short thresholdAmplitude = 32)
    {
        (int Start, int End)? sharedBounds = null;
        if (ShouldMirrorToPartner && _selectedSample is { IsHeaderOnly: false })
        {
            var primaryBounds = SilenceTrimEffect.ComputeBounds(_selectedSample.Samples(), thresholdAmplitude);
            var partnerBounds = SilenceTrimEffect.ComputeBounds(_partnerSample!.Samples(), thresholdAmplitude);
            sharedBounds = (Math.Min(primaryBounds.Start, partnerBounds.Start), Math.Max(primaryBounds.End, partnerBounds.End));
        }
        ApplyEffect(new SilenceTrimEffect(thresholdAmplitude, sharedBounds), "Trimmed silence");
    }

    // Amplify (+dB) and Soften (-dB) are the same operation - the context menu just
    // passes a positive or negative decibels value for its presets (1/3/6 dB either way).
    public void ApplyGainAdjust(double decibels) =>
        ApplyEffect(new GainAdjustEffect(decibels), $"Applied {(decibels >= 0 ? "+" : "")}{decibels:0.#} dB gain");

    // Fade In/Out on the current SELECTION - ramp gain across exactly the highlighted
    // range, leaving everything outside it untouched. The ONLY fade path now (a
    // separate whole-buffer-edges-by-typed-frame-count "Apply Fade" used to exist here
    // too, wired to its own frame-count fields that ignored the waveform selection
    // entirely - confusing and redundant with highlighting, per explicit feedback, so
    // it was removed rather than kept alongside this one). Reachable from the Edit
    // toolbar's Fade In/Fade Out buttons and the waveform's right-click context menu -
    // both call this same pair of methods.
    public void ApplyFadeInSelection() => ApplySelectionFade(fadeIn: true);
    public void ApplyFadeOutSelection() => ApplySelectionFade(fadeIn: false);

    void ApplySelectionFade(bool fadeIn)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }
        if (SelectionEndFrame <= SelectionStartFrame) { StatusText = "Select a range in the waveform first."; return; }
        bool mirror = ShouldMirrorToPartner;

        ApplyFadeTo(_selectedSample, _sampleUndo, SelectionStartFrame, SelectionEndFrame, fadeIn);
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        _sampleDirty = true;
        if (mirror)
        {
            ApplyFadeTo(_partnerSample!, _partnerUndo, SelectionStartFrame, SelectionEndFrame, fadeIn);
            PartnerSampleWaveform = _partnerSample!.IsHeaderOnly ? null : _partnerSample.Samples();
        }

        LoadSampleDetailState(_selectedSample, reloadWaveform: true);
        RefreshUndoRedoState();
        StatusText = $"Applied fade {(fadeIn ? "in" : "out")} to the selection{(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Sample).";
    }

    static void ApplyFadeTo(KsfSample sample, SampleEditUndo undo, int selStart, int selEnd, bool fadeIn)
    {
        undo.RecordBeforeEdit(SampleFieldSnapshot.Of(sample));
        var host = sample.Samples();
        int start = Math.Clamp(selStart, 0, host.Length);
        int end = Math.Clamp(selEnd, start, host.Length);
        int len = end - start;
        for (int i = 0; i < len; i++)
        {
            double t = len <= 1 ? 1.0 : (double)i / (len - 1);
            double gain = fadeIn ? t : 1.0 - t;
            host[start + i] = (short)Math.Clamp(host[start + i] * gain, short.MinValue, short.MaxValue);
        }
        sample.SetSamples(host);
    }

    // Sets the sample's own Loop Start/End to the current waveform selection - a
    // right-click "Loop Selected Area" shortcut for the same fields the Sample panel
    // already edits, so the user doesn't have to read frame numbers off the waveform
    // and retype them. Mirrored to the stereo partner in Combine mode - mismatched
    // loop points between stereo channels would be a real playback bug on the Kronos,
    // not just a cosmetic inconsistency.
    public void SetLoopFromSelection()
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (SelectionEndFrame <= SelectionStartFrame) { StatusText = "Select a range in the waveform first."; return; }
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        ApplySampleFieldsTo(_selectedSample, (int)_selectedSample.SampleRate, SampleLoopEnabled, SampleSampleStart, SelectionStartFrame, SelectionEndFrame);
        _sampleDirty = true;
        if (mirror)
            ApplySampleFieldsTo(_partnerSample!, (int)_partnerSample!.SampleRate, SampleLoopEnabled, SampleSampleStart, SelectionStartFrame, SelectionEndFrame);

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Loop set to [{SelectionStartFrame}, {SelectionEndFrame}){(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Changes).";
    }

    // ── Clipboard (Cut/Copy/Paste) ──────────────────────────────────────────────

    public void CopySelection()
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) { StatusText = "No sample loaded."; return; }
        if (SelectionEndFrame <= SelectionStartFrame) { StatusText = "Select a range in the waveform first."; return; }
        var host = _selectedSample.Samples();
        int start = Math.Clamp(SelectionStartFrame, 0, host.Length);
        int end = Math.Clamp(SelectionEndFrame, start, host.Length);
        SampleClipboard.Set(host[start..end], (int)_selectedSample.SampleRate);
        StatusText = $"Copied {end - start} frame(s) to the clipboard.";
    }

    // Cut and Paste both go through ApplyEffect now, like every other edit in this file.
    // They used to splice _selectedSample's array inline, which meant they were the only
    // two length-changing edits that did NOT mirror onto the stereo partner - so in
    // Combine mode L and R ended up different lengths and everything after the edit
    // point played back time-offset between the channels (Interleave pads the shorter
    // one). See SpliceEffects.cs for the rest of that reasoning.
    public void CutSelection()
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) { StatusText = "No sample loaded."; return; }
        if (SelectionEndFrame <= SelectionStartFrame) { StatusText = "Select a range in the waveform first."; return; }

        // Clipboard capture happens BEFORE the effect runs, and reads the primary
        // channel only - the clipboard is mono by design (SampleClipboard holds one
        // short[] plus a rate), so a stereo Combine-mode cut removes both channels but
        // copies the selected one.
        var host = _selectedSample.Samples();
        int start = Math.Clamp(SelectionStartFrame, 0, host.Length);
        int end = Math.Clamp(SelectionEndFrame, start, host.Length);
        SampleClipboard.Set(host[start..end], (int)_selectedSample.SampleRate);

        ApplyEffect(new DeleteRangeEffect(start, end), $"Cut {end - start} frame(s) to the clipboard");
        SelectionStartFrame = 0;
        SelectionEndFrame = 0;
    }

    // Replaces the current selection with the clipboard's content, or inserts at
    // SelectionStartFrame if nothing is selected (a bare cursor position).
    public void PasteAtSelection()
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) { StatusText = "No sample loaded."; return; }
        if (!SampleClipboard.HasContent) { StatusText = "Clipboard is empty."; return; }

        int frameCount = _selectedSample.FrameCount;
        int start = Math.Clamp(SelectionStartFrame, 0, frameCount);
        int end = Math.Clamp(SelectionEndFrame, start, frameCount);
        var clip = SampleClipboard.Pcm!;

        ApplyEffect(new PasteRangeEffect(start, end, clip), $"Pasted {clip.Length} frame(s)");
        SelectionStartFrame = start;
        SelectionEndFrame = start + clip.Length;
    }

    // ── Additional standard waveform operations ─────────────────────────────────────

    // Reverse / Silence act on the SELECTION when there is one and the whole buffer
    // otherwise, the convention every waveform editor uses. Both preserve length, so
    // markers and stereo alignment are untouched.
    public void ApplyReverse()
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        var (start, end) = SelectionOrWholeBuffer();
        ApplyEffect(new ReverseEffect(start, end),
            end - start >= _selectedSample.FrameCount ? "Reversed the whole sample" : $"Reversed [{start}, {end})");
    }

    public void ApplySilenceSelection()
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (SelectionEndFrame <= SelectionStartFrame) { StatusText = "Select a range in the waveform first."; return; }
        ApplyEffect(new SilenceEffect(SelectionStartFrame, SelectionEndFrame),
            $"Silenced [{SelectionStartFrame}, {SelectionEndFrame})");
    }

    // Inserts silence at the selection start (or at the scrub cursor when nothing is
    // selected), pushing the rest later - the standard way to make room in a sample.
    public void ApplyInsertSilence(int frameCount)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (frameCount <= 0) { StatusText = "Enter a positive number of frames to insert."; return; }
        int at = SelectionEndFrame > SelectionStartFrame ? SelectionStartFrame
            : _cursorFrame >= 0 ? _cursorFrame : 0;
        ApplyEffect(new InsertSilenceEffect(at, frameCount), $"Inserted {frameCount} frame(s) of silence at {at}");
    }

    // Whole-buffer only, deliberately - see DcOffsetEffect's own comment.
    public void ApplyDcOffsetRemoval()
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }
        int offset = DcOffsetEffect.MeasureOffset(_selectedSample.Samples());
        if (offset == 0) { StatusText = "No DC offset to remove - the waveform is already centred."; return; }
        ApplyEffect(new DcOffsetEffect(), $"Removed a DC offset of {offset}");
    }

    (int Start, int End) SelectionOrWholeBuffer() =>
        SelectionEndFrame > SelectionStartFrame
            ? (SelectionStartFrame, SelectionEndFrame)
            : (0, _selectedSample?.FrameCount ?? 0);

    // Bounds matter here in a way they don't for the other effects: the output buffer
    // is sized by 1/tempoRatio, so a typo'd 0.001 asks for a thousand times the input -
    // a hang or an OutOfMemoryException on any real-length sample. The old code only
    // rejected tempoRatio <= 0. These limits are the musically useful range (two
    // octaves of pitch, quarter-to-quadruple speed); anything past them is a typo.
    public const double MinTempoRatio = 0.25, MaxTempoRatio = 4.0;
    public const double MaxPitchSemitones = 24.0;

    public void ApplyTempoPitch(double tempoRatio, double pitchSemitones)
    {
        if (_selectedSample == null) { StatusText = "No sample loaded."; return; }
        if (_selectedSample.IsHeaderOnly) { StatusText = "No audio data to edit (header-only sample)."; return; }

        double requestedTempo = tempoRatio, requestedPitch = pitchSemitones;
        tempoRatio = Math.Clamp(tempoRatio, MinTempoRatio, MaxTempoRatio);
        pitchSemitones = Math.Clamp(pitchSemitones, -MaxPitchSemitones, MaxPitchSemitones);
        if (tempoRatio != requestedTempo || pitchSemitones != requestedPitch)
        {
            StatusText = $"Out of range - clamped to tempo x{tempoRatio:0.##}, pitch {pitchSemitones:0.##} "
                + $"(limits: tempo {MinTempoRatio}-{MaxTempoRatio}, pitch ±{MaxPitchSemitones}).";
        }
        if (tempoRatio == 1.0 && pitchSemitones == 0.0) { StatusText = "Tempo x1 and pitch 0 - nothing to apply."; return; }

        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        var host = _selectedSample.Samples();
        var edited = TempoPitchProcessor.ChangeTempoAndPitch(host, (int)_selectedSample.SampleRate, tempoRatio, pitchSemitones);
        _selectedSample.SetSamples(edited);
        ClampMarkersToBuffer(_selectedSample); // a tempo change resizes the buffer
        _sampleDirty = true;

        if (mirror)
        {
            _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));
            var partnerHost = _partnerSample!.Samples();
            var partnerEdited = TempoPitchProcessor.ChangeTempoAndPitch(partnerHost, (int)_partnerSample.SampleRate, tempoRatio, pitchSemitones);
            _partnerSample.SetSamples(partnerEdited);
            ClampMarkersToBuffer(_partnerSample);
            PartnerSampleWaveform = _partnerSample.IsHeaderOnly ? null : _partnerSample.Samples();
        }

        LoadSampleDetailState(_selectedSample, reloadWaveform: true);
        RefreshUndoRedoState();
        StatusText = $"Applied tempo x{tempoRatio:0.##}, pitch {pitchSemitones:+0.##;-0.##;0} semitones{(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Sample).";
    }

    // Also undoes/redoes the stereo partner's own stack when one exists, Combine mode
    // is active, and that stack actually has something to undo/redo - the two stacks
    // stay in lockstep as long as every mirrored edit pushed to both, which every
    // ApplyEffect/ApplyTempoPitch/ApplySelectionFade call above does; guarding on the
    // partner's own CanUndo/CanRedo rather than assuming lockstep means a mid-session
    // Combine/Split toggle degrades gracefully (undoes what it can) instead of throwing.
    public void Undo()
    {
        if (_undoDomains.Count == 0) return;
        var domain = PopDomain(_undoDomains);

        if (domain == EditDomain.Zone)
        {
            if (CurrentMultisampleZones is not { } zones) return;
            // Both the current state and the restored one span the stereo sibling's list
            // too when there is one, so an undo puts the PAIR back rather than only the
            // half that was clicked - see ZoneListSnapshot's own comment.
            var (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(zones);
            var restoredZones = _zoneUndo.Undo(ZoneListSnapshot.Of(zones, siblingZones));
            if (restoredZones == null) return;
            restoredZones.ApplyTo();
            _zoneDirty = true;
            if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
            // ZoneOriginalKey/ZoneTopKey (the currently-selected zone's own displayed
            // fields) must be re-read too, since Undo can restore a TopKey the
            // selected zone itself had before the edit - not just repaint the keymap.
            if (_selectedZone != null) { ZoneOriginalKey = _selectedZone.OriginalKey; ZoneTopKey = _selectedZone.TopKey; }
            _redoDomains.Add(EditDomain.Zone);
            RefreshUndoRedoState();
            StatusText = "Undid zone edit (unsaved - use Save Multisample).";
            return;
        }

        if (_selectedSample == null) { RefreshUndoRedoState(); return; }
        var restored = _sampleUndo.Undo(SampleFieldSnapshot.Of(_selectedSample));
        if (restored == null) { RefreshUndoRedoState(); return; }
        restored.Value.ApplyTo(_selectedSample);
        _sampleDirty = true;

        bool partnerAlso = HasStereoPair && !SplitLR && _partnerSample != null && _partnerUndo.CanUndo;
        if (partnerAlso && _partnerUndo.Undo(SampleFieldSnapshot.Of(_partnerSample!)) is { } partnerRestored)
        {
            partnerRestored.ApplyTo(_partnerSample!);
            PartnerSampleWaveform = _partnerSample!.IsHeaderOnly ? null : _partnerSample.Samples();
        }

        LoadSampleDetailState(_selectedSample, reloadWaveform: true);
        _redoDomains.Add(EditDomain.Sample);
        RefreshUndoRedoState();

        var evicted = _sampleUndo.TakeEvictedCount();
        StatusText = $"Undid last edit{(partnerAlso ? " (both L/R channels)" : "")} (unsaved - use Save Sample)."
            + (evicted > 0 ? $" ({evicted} earlier step(s) no longer available - undo history is capped.)" : "");
    }

    public void Redo()
    {
        if (_redoDomains.Count == 0) return;
        var domain = PopDomain(_redoDomains);

        if (domain == EditDomain.Zone)
        {
            if (CurrentMultisampleZones is not { } zones) return;
            var (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(zones);
            var restoredZones = _zoneUndo.Redo(ZoneListSnapshot.Of(zones, siblingZones));
            if (restoredZones == null) return;
            restoredZones.ApplyTo();
            _zoneDirty = true;
            if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
            if (_selectedZone != null) { ZoneOriginalKey = _selectedZone.OriginalKey; ZoneTopKey = _selectedZone.TopKey; }
            _undoDomains.Add(EditDomain.Zone);
            RefreshUndoRedoState();
            StatusText = "Redid zone edit (unsaved - use Save Multisample).";
            return;
        }

        if (_selectedSample == null) { RefreshUndoRedoState(); return; }
        var restored = _sampleUndo.Redo(SampleFieldSnapshot.Of(_selectedSample));
        if (restored == null) { RefreshUndoRedoState(); return; }
        restored.Value.ApplyTo(_selectedSample);
        _sampleDirty = true;

        bool partnerAlso = HasStereoPair && !SplitLR && _partnerSample != null && _partnerUndo.CanRedo;
        if (partnerAlso && _partnerUndo.Redo(SampleFieldSnapshot.Of(_partnerSample!)) is { } partnerRestored)
        {
            partnerRestored.ApplyTo(_partnerSample!);
            PartnerSampleWaveform = _partnerSample!.IsHeaderOnly ? null : _partnerSample.Samples();
        }

        LoadSampleDetailState(_selectedSample, reloadWaveform: true);
        _undoDomains.Add(EditDomain.Sample);
        RefreshUndoRedoState();
        StatusText = $"Redid edit{(partnerAlso ? " (both L/R channels)" : "")} (unsaved - use Save Sample).";
    }

    void RefreshUndoRedoState()
    {
        CanUndo = _undoDomains.Count > 0;
        CanRedo = _redoDomains.Count > 0;
    }

    // Loops on playback whenever the sample's own Loop Enabled flag (SampleLoopEnabled -
    // how the Kronos itself will play it) is on - the separate "Loop Preview" checkbox
    // this used to also check was removed as redundant (checking Loop Enabled itself
    // already loops on Play with no second checkbox required, matching what a user
    // checking "Loop Enabled" actually expects to hear). Plays TRUE stereo (both
    // channels interleaved) whenever a stereo partner is resolved and actually has
    // audio - Split mode still plays only the tree-selected channel, matching what's
    // visible on screen.
    //
    // Starts from _cursorFrame (the grey scrub line, set by clicking the waveform or by
    // the transport bar - see SetCursorFrame/TransportSeekTo) whenever one has been set
    // this session, instead of always restarting from SampleSampleStart/frame 0 - "play
    // back from the last place the user selected," per the user's own framing, not just
    // an audition-on-click gesture.
    public void PlaySelectedSample()
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) return;
        _playback.BoostEnabled = Sample12dbBoostEnabled;
        bool stereo = HasStereoPair && !SplitLR && _partnerSample is { IsHeaderOnly: false };
        bool loop = SampleLoopEnabled;
        int loopStartFrame = _cursorFrame >= 0 ? _cursorFrame : SampleSampleStart;
        int oneShotStartFrame = _cursorFrame >= 0 ? _cursorFrame : 0;

        if (stereo)
        {
            var left = LeftSampleWaveform!;
            var right = RightSampleWaveform!;
            if (loop)
                _playback.PlayStereoLooped(left, right, (int)_selectedSample.SampleRate,
                    loopStartFrame, SampleLoopStart, SampleLoopEnd, SampleReverseEnabled);
            else
                _playback.PlayStereoFrom(left, right, (int)_selectedSample.SampleRate, oneShotStartFrame);
        }
        else if (loop)
        {
            _playback.PlayLooped(_selectedSample.Samples(), (int)_selectedSample.SampleRate,
                loopStartFrame, SampleLoopStart, SampleLoopEnd, SampleReverseEnabled);
        }
        else
        {
            _playback.PlayFrom(_selectedSample.Samples(), (int)_selectedSample.SampleRate, oneShotStartFrame);
        }
        IsPlaying = true;
        IsPaused = false;
    }

    // Moves the grey scrub-line cursor to `frame` WITHOUT starting playback - a plain
    // click on the waveform selects a playback starting point, it doesn't audition it.
    // The next Play (button, Space, or Pause's Resume) starts from here.
    public void SetCursorFrame(int frame)
    {
        if (_selectedSample == null) return;
        _cursorFrame = Math.Clamp(frame, 0, Math.Max(0, SampleFrameCount - 1));
    }

    public void StopPlayback()
    {
        _playback.Stop();
        IsPlaying = false;
        IsPaused = false;
    }

    // Scrub-click "play from here" (the grey line in SampleWaveformControl) - a plain
    // click anywhere on the waveform, outside any marker/loop hit, plays one-shot from
    // that frame to the end. Deliberately ignores loop state (an audition gesture, not
    // a statement about normal playback) and, like PlaySelectedSample, plays TRUE
    // stereo whenever a partner is resolved and Combine mode is active.
    public void PlayFromFrame(int frame)
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) return;
        _playback.BoostEnabled = Sample12dbBoostEnabled;
        bool stereo = HasStereoPair && !SplitLR && _partnerSample is { IsHeaderOnly: false };

        if (stereo) _playback.PlayStereoFrom(LeftSampleWaveform!, RightSampleWaveform!, (int)_selectedSample.SampleRate, frame);
        else _playback.PlayFrom(_selectedSample.Samples(), (int)_selectedSample.SampleRate, frame);
        IsPlaying = true;
        IsPaused = false;
    }

    // ── Transport bar (rewind-to-start/rewind/pause/fast-forward/go-to-end) ────────
    //
    // True while playback has been explicitly paused (Pause was pressed while playing) -
    // distinct from IsPlaying (false while paused) and from a full Stop (which forgets
    // the position entirely). Resuming continues as a one-shot from the paused frame,
    // NOT a resumed loop - if the sample was looping when paused, Resume plays from that
    // point to the end once rather than re-entering the loop, the same simplification
    // PlayFromFrame already makes for scrub-clicking. A stated trade-off for "basic
    // controls," not an oversight.
    [ObservableProperty] bool isPaused;

    // Last known playback position when NOT actively playing (paused, or simply never
    // started/explicitly relocated) - -1 means "no position yet this session." What
    // Rewind/Fast-Forward step relative to, and what Locate Start/End and Pause's
    // Resume read from.
    int _cursorFrame = -1;

    // Fires when the transport moves the cursor WITHOUT starting audio (Locate/Rewind/
    // FF pressed while fully stopped) - the window sets the grey scrub line to match,
    // same visual as a manual scrub-click, without calling PlayFromFrame.
    public event Action<int>? CursorMoved;

    public void TransportTogglePause()
    {
        if (IsPaused)
        {
            IsPaused = false;
            PlayFromFrame(_cursorFrame < 0 ? 0 : _cursorFrame);
        }
        else if (IsPlaying)
        {
            _cursorFrame = GetPlaybackFrame();
            _playback.Stop();
            IsPlaying = false;
            IsPaused = true;
        }
    }

    public void TransportLocateStart() => TransportSeekTo(0);
    public void TransportLocateEnd() => TransportSeekTo(Math.Max(0, SampleFrameCount - 1));

    // A fixed-fraction step (10% of the sample, floored at 1 frame) rather than a fixed
    // frame/time count - scales sensibly whether the sample is a short one-shot or a
    // multi-minute recording, matching how NiceInterval-style zoom steps already scale
    // with the data instead of an arbitrary constant.
    public void TransportSeekRelative(int direction)
    {
        int step = Math.Max(1, SampleFrameCount / 10);
        int current = IsPlaying ? GetPlaybackFrame() : _cursorFrame < 0 ? 0 : _cursorFrame;
        TransportSeekTo(current + direction * step);
    }

    void TransportSeekTo(int frame)
    {
        if (_selectedSample == null || _selectedSample.IsHeaderOnly) return;
        frame = Math.Clamp(frame, 0, Math.Max(0, SampleFrameCount - 1));
        _cursorFrame = frame;

        if (IsPlaying || IsPaused)
        {
            IsPaused = false;
            PlayFromFrame(frame);
        }
        else
        {
            CursorMoved?.Invoke(frame);
        }
    }

    // 0..1, persisted across selections/window sessions the same way SampleUndoByteCapMb
    // is (read fresh each time rather than cached, matching that field's own pattern) -
    // a loud sample shouldn't get a fresh chance to startle just because a different
    // zone was selected.
    public float Volume
    {
        get => _playback.Volume;
        set => _playback.Volume = value;
    }

    // Polled by the window's own timer to drive the VU meter - see SamplePlayback.
    // PeakLevel's own comment for why this is a plain poll, not an event/observable.
    public float GetPlaybackLevel() => _playback.PeakLevel;
    // Per-channel peaks for the stereo VU meter - both fall back to the same combined
    // PeakLevel for a mono sample (SamplePlayback mirrors Left into Right itself when
    // there's only one channel, so these two are never silently "half wired").
    public float GetPlaybackLevelLeft() => _playback.PeakLevelLeft;
    public float GetPlaybackLevelRight() => _playback.PeakLevelRight;

    // Polled the same way, to drive the waveform's playhead line.
    public int GetPlaybackFrame() => _playback.PositionFrame;

    // ── Saving ───────────────────────────────────────────────────────────────

    public void SaveSelectedMultisample()
    {
        KmpMultisample? m = null;
        string? path = null;
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

        // Nothing selected is only a hard stop when there's ALSO nothing pending - a
        // selection-clearing rebuild (DeleteSkippedZone's structural removal goes
        // through RefreshTreeAfterMutation, which clears selection) must not make
        // already-registered pending edits unreachable from Save Multisample/Save
        // Changes just because the tree happens to have nothing selected right now.
        if (m == null && _dirtyMultisamples.Count == 0)
        {
            StatusText = "No multisample selected.";
            return;
        }

        // Writes every multisample with pending edits, not just the selected one. Zone
        // edits mutate the LIVE KmpZone objects in the tree, so they survive navigating
        // away - meaning "the selected multisample" stopped being the same set as
        // "what's actually unsaved" the moment more than one got edited. Saving only the
        // selection while clearing a single global dirty flag is precisely how unsaved
        // work used to go silently missing. The selected one (if any) is included
        // explicitly so this menu item still means "save this" even when it isn't
        // independently pending.
        var pending = new Dictionary<string, KmpMultisample>(_dirtyMultisamples, StringComparer.OrdinalIgnoreCase);
        if (m != null && path != null) pending[path] = m;

        var saved = new List<string>();
        foreach (var (msPath, multisample) in pending)
        {
            try
            {
                multisample.Save(msPath);
                _dirtyMultisamples.Remove(msPath);
                saved.Add(Path.GetFileName(msPath));
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sample Editor: multisample save '{msPath}' failed: {ex}");
                StatusText = $"Save failed for '{Path.GetFileName(msPath)}': {ex.Message}";
                return;
            }
        }

        _zoneDirtyField = false;
        StatusText = $"Saved {string.Join(", ", saved.Select(n => $"'{n}'"))}."
            + (_dirtySamples.Count > 0
                ? $" NOTE: {_dirtySamples.Count} sample edit(s) are still unsaved - use Save Sample too."
                : "");
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

    static (KmpMultisample? Multisample, string? KmpPath) FindMultisampleAndPathContaining(IEnumerable<SampleTreeNode> nodes, KmpZone zone)
    {
        foreach (var node in nodes)
        {
            var found = FindMultisampleAndPathContaining(node, zone);
            if (found.Multisample != null) return found;
        }
        return (null, null);
    }

    static (KmpMultisample? Multisample, string? KmpPath) FindMultisampleAndPathContaining(SampleTreeNode node, KmpZone zone)
    {
        if (node.MultisampleRef is { } ms && ms.Multisample.Zones.Contains(zone)) return (ms.Multisample, ms.Path);
        foreach (var child in node.Children)
        {
            var found = FindMultisampleAndPathContaining(child, zone);
            if (found.Multisample != null) return found;
        }
        return (null, null);
    }

    // The window's single bottom-right "Save Changes" button - a live editing session
    // has no per-field Apply step anymore (see the Sample panel's field handlers in
    // SampleEditorWindow.xaml.cs), so this is the one action that commits whatever's
    // actually dirty: the sample's own fields/PCM, the zone's key range, or both.
    public void SaveAllChanges()
    {
        // Keyed off what's actually pending across the whole session, not off the
        // selected item's own two flags - those reset on every navigation, so a Save
        // Changes after switching zones used to report "no unsaved changes" while real
        // edits sat unwritten.
        bool hadSamples = _dirtySamples.Count > 0, hadZones = _dirtyMultisamples.Count > 0;
        if (!hadSamples && !hadZones) { StatusText = "No unsaved changes."; return; }

        var messages = new List<string>();
        if (hadSamples) { SaveSelectedSample(); messages.Add(StatusText); }
        if (hadZones) { SaveSelectedMultisample(); messages.Add(StatusText); }
        SweepOrphanedRepositoryFiles();
        StatusText = string.Join("  ", messages);
    }

    // Writes every sample with pending edits. Two separate bugs made "save just the
    // selected one" wrong: in stereo Combine mode the partner is edited by every
    // mirrored operation but was NEVER saved (so the pair diverged on disk, quietly
    // undoing all the mirroring), and edits now survive navigating between zones, so
    // several samples can legitimately be pending at once.
    public void SaveSelectedSample()
    {
        if (_dirtySamples.Count == 0)
        {
            StatusText = _selectedSample == null ? "No sample loaded." : "No unsaved sample changes.";
            return;
        }

        var saved = new List<string>();
        foreach (var (path, sample) in _dirtySamples.ToList())
        {
            try
            {
                sample.Save(path);
                _dirtySamples.Remove(path);
                saved.Add(Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sample Editor: sample save '{path}' failed: {ex}");
                StatusText = $"Save failed for '{Path.GetFileName(path)}': {ex.Message}";
                return;
            }
        }

        _sampleDirtyField = false;
        StatusText = $"Saved {string.Join(", ", saved.Select(n => $"'{n}'"))}."
            + (_dirtyMultisamples.Count > 0
                ? $" NOTE: {_dirtyMultisamples.Count} multisample(s) still have unsaved zone edits - use Save Multisample too."
                : "");
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
            SaveMultisampleNow(m, kmpPath);
            RefreshTreeAfterMutation(m, kmpPath);
            StatusText = $"Imported '{Path.GetFileName(audioPath)}' as zone '{zone.Filename}' (key {originalKey}-{topKey}).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: audio import '{audioPath}' failed: {ex}");
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    // Adds an EMPTY placeholder zone to the currently-selected multisample - Filename =
    // SKIPPEDSAMPLE, the doc's existing "no real .KSF backs this" convention (not a new
    // zone state to teach every consumer). Placed at the very END of the key range,
    // claiming up to one octave off the top of whatever the current last zone owns (or
    // half its range if it's narrower than that) rather than disturbing any OTHER
    // zone - "the new zone should be added to the end" per the user's own framing. A
    // real sample is attached afterward via ImportSampleIntoZone (right-click "Import
    // Sample..."), which keeps this zone's key range and just replaces SKIPPEDSAMPLE
    // with a real .KSF. Deliberately NOT wired into zone-list undo (Ctrl+Z) - same as
    // every other zone-ADDING method here (ImportAudioAsNewZone, AddZoneFromExistingKsf,
    // ImportStereoAudioAsNewZonePair, NewStereoMultisamplePairInCollection): they all
    // go through a full Save + RefreshTreeAfterMutation, which rebuilds the tree from a
    // fresh disk read and replaces every KmpZone instance - incompatible with
    // _zoneUndo's live-object-identity design. Delete Zone is the existing manual undo
    // for an accidental Add.
    //
    // Returns the target multisample's .KMP path on success (so the caller - the
    // window's code-behind - can re-select the newly added zone) or null if nothing was
    // added. LastAddedZoneIndex is set alongside it (see its own comment) since the
    // rebuild this triggers replaces every KmpZone with a fresh instance - a reference
    // can't survive that, only a position can.
    public int LastAddedZoneIndex { get; private set; } = -1;

    public string? AddPlaceholderZone()
    {
        LastAddedZoneIndex = -1;
        KmpMultisample? m;
        string? kmpPath;
        if (_selectedNode?.MultisampleRef is { } ms) { m = ms.Multisample; kmpPath = ms.Path; }
        else if (_selectedZone != null) { m = FindMultisampleContaining(Roots, _selectedZone); kmpPath = _selectedKmpPath; }
        else { StatusText = "Select a multisample (or one of its zones) first."; return null; }
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the target multisample."; return null; }
        if (m.Zones.Count >= SampleImportBuilder.MaxZonesPerMultisample)
        { StatusText = $"'{m.Name}{m.Suffix}' already has {SampleImportBuilder.MaxZonesPerMultisample} zones (the maximum) - remove one before adding another."; return null; }

        try
        {
            // Settings > Sample Editor > "Create Zone Preferences" - Position and Zone
            // Range shape the carve below; Original Key Position picks where OriginalKey
            // (the root/tracking key - independent of the trigger range TopKey defines)
            // lands within the new zone once its range is known.
            var prefs = Storage.LoadSettings();
            int rangeSetting = Math.Clamp(prefs.SampleZoneCreateRange, 1, 127);

            int newLow, newTop;
            int insertIndex = m.Zones.Count; // default: append at the end
            byte? shrunkPrevTopKey = null; // the previous last zone's post-shrink TopKey, if one was shrunk
            if (m.Zones.Count == 0)
            {
                newLow = 0; newTop = 127;
            }
            else
            {
                var last = m.Zones[^1];
                int lastLow = m.Zones.Count > 1 ? m.Zones[^2].TopKey + 1 : 0;
                int available = last.TopKey - lastLow + 1;
                int width = Math.Max(1, Math.Min(rangeSetting, available - 1)); // leave >=1 key for the existing zone

                if (prefs.SampleZoneCreatePosition == SampleZoneCreatePosition.Left)
                {
                    // New zone takes the BOTTOM `width` keys of the current last zone's
                    // range; that zone keeps its own TopKey (only its EFFECTIVE low end,
                    // derived from whatever precedes it, moves up) and stays last in the
                    // list - the new zone is inserted just before it so list order still
                    // matches the ascending-key-order the TopKey range convention assumes.
                    newLow = lastLow;
                    newTop = lastLow + width - 1;
                    insertIndex = m.Zones.Count - 1;
                }
                else
                {
                    newLow = last.TopKey - width + 1;
                    newTop = last.TopKey;
                    last.TopKey = (byte)Math.Max(lastLow, newLow - 1);
                    shrunkPrevTopKey = last.TopKey;
                }
            }

            byte origKey = prefs.SampleZoneOriginalKeyPosition switch
            {
                SampleZoneOriginalKeyPosition.Top => (byte)newTop,
                SampleZoneOriginalKeyPosition.Center => (byte)((newLow + newTop) / 2),
                _ => (byte)newLow,
            };

            var zone = new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = origKey, TopKey = (byte)newTop };
            m.Zones.Insert(insertIndex, zone);
            SaveMultisampleNow(m, kmpPath);

            // Mirror the SAME key-range change onto a resolved stereo sibling (Suffix
            // -L/-R, same collection). Without this, only ONE half of a stereo pair
            // gained the new zone (and only its own last zone shrank), so the two
            // halves' zone key ranges silently drifted out of exact parity.
            // ResolveStereoPartner/ShouldMirrorToPartner match a stereo partner by EXACT
            // (OriginalKey, TopKey) - once the ranges diverge, the shrunk last zone (and
            // every zone whose range no longer matches once re-selected) stops
            // resolving its partner at all, silently dropping the shared stereo
            // waveform view back to a plain mono one - the exact bug this closes.
            // Best-effort: only mirrors when the sibling's own zone count matches the
            // primary's count BEFORE this add (a well-formed pair) - a sibling that's
            // already out of sync is left alone rather than guessing how to reconcile
            // it; the primary's own add still succeeds either way.
            string? siblingPath = null;
            if (_collection != null && m.Suffix is "-L" or "-R")
            {
                // The LIVE in-tree sibling is preferred over the disk lookup here for the
                // same reason every other mirror uses it, plus one specific to this
                // method: if the sibling already has a PENDING zone edit, it's in
                // _dirtyMultisamples, and RebuildTreeFromCollection (which this method
                // triggers) keeps that pending object rather than re-reading. Mutating
                // and saving a fresh disk copy instead would leave the tree holding the
                // pending object WITHOUT the new zone - the two halves out of parity,
                // the stereo match broken, and a later Save writing the pending object
                // back over the zone this just added.
                var (sibling, sibPath) = ResolveStereoSibling(m, kmpPath);

                if (sibling != null && sibPath != null && sibling.Zones.Count == m.Zones.Count - 1)
                {
                    if (shrunkPrevTopKey is { } shrunk && sibling.Zones.Count > 0)
                        sibling.Zones[^1].TopKey = shrunk;
                    sibling.Zones.Insert(insertIndex, new KmpZone { Filename = "SKIPPEDSAMPLE", OriginalKey = origKey, TopKey = (byte)newTop });
                    SaveMultisampleNow(sibling, sibPath);
                    siblingPath = sibPath;
                }
            }

            LastAddedZoneIndex = insertIndex;
            RefreshTreeAfterMutation(m, kmpPath);
            StatusText = $"Added an empty zone ({MidiNoteName.ToName(newLow)}-{MidiNoteName.ToName(newTop)})"
                + (siblingPath != null ? " to both stereo channels" : "") + " - "
                + "right-click it and choose \"Import Sample...\" to attach audio.";
            return kmpPath;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: add placeholder zone failed: {ex}");
            StatusText = $"Failed to add zone: {ex.Message}";
            return null;
        }
    }

    // Attaches real audio to an EXISTING zone (typically a placeholder from
    // AddPlaceholderZone, but works on any zone, including replacing a normal one's
    // sample) - generates a fresh .KSF filename and writes the decoded audio, WITHOUT
    // touching the zone's own OriginalKey/TopKey. This is "give this slot audio," not
    // "add a new slot" (that's ImportAudioAsNewZone, right above).
    //
    // Returns the target multisample's .KMP path on success (null on failure) and sets
    // LastImportedZoneIndex alongside it (bug fix 2026-08-22, same pattern
    // AddPlaceholderZone already uses) - RefreshTreeAfterMutation's rebuild replaces
    // every KmpZone with a fresh instance and clears selection, so the caller (the
    // window's code-behind) can't re-select by holding onto the original `zone`
    // reference; only a position survives the rebuild. Without this, the editor view
    // appeared to "close" after every import - not actually broken, just left with
    // nothing selected, so every section gated on "a zone/multisample is selected"
    // collapsed.
    public int LastImportedZoneIndex { get; private set; } = -1;

    // Decides whether `zone` can safely be given new audio by overwriting its OWN
    // current .KSF file in place, or needs a fresh, collision-free filename instead
    // (bug fix 2026-08-22, shared by ImportSampleIntoZone and AssignExistingKsfToZone -
    // both replace an EXISTING zone's audio, so both need the identical safety check).
    // A placeholder has no real file to overwrite - obviously needs a fresh name. Less
    // obvious: overwriting a REAL existing filename in place is only safe if nothing
    // else depends on that exact name meaning what it currently means. Two ways it
    // could: (a) the zone's own current .KSF is itself a stub (SMF1-referencing another
    // file) - overwriting it with fresh full audio is harmless to THIS zone, but taking
    // the fresh-name path anyway is simpler/safer than reasoning about whether some
    // OTHER zone also stub-references this stub's name (unobserved in practice, but
    // untested); (b) some OTHER zone in the SAME multisample carries an SMF1 chunk
    // naming THIS zone's filename as ITS OWN audio source (the real sharing mechanism
    // - see kronosology doc §3.2/kronos_bytemap round 2) - overwriting in place would
    // silently change that sibling zone's sound too, with no warning. Cross-multisample
    // stub references are out of scope here (AddZoneFromExistingKsf's own established
    // precedent already duplicates rather than relying on that unproven case, doc
    // §2.2/§3.2's own open item).
    bool NeedsFreshFilename(KmpMultisample m, string kmpPath, KmpZone zone)
    {
        if (zone.IsSkipped) return true;

        var ownPath = zone.KsfPath(kmpPath);
        try
        {
            if (File.Exists(ownPath) && KsfSample.Open(File.ReadAllBytes(ownPath)) is { } own
                && own.StubTargetFilename != null)
                return true; // (a) this zone's own file is a stub
        }
        catch { /* unreadable existing file - fall through, treat as needing a fresh name below */ return true; }

        foreach (var sibling in m.Zones)
        {
            if (ReferenceEquals(sibling, zone) || sibling.IsSkipped) continue;
            try
            {
                var siblingPath = sibling.KsfPath(kmpPath);
                if (File.Exists(siblingPath) && KsfSample.Open(File.ReadAllBytes(siblingPath)) is { } sib
                    && string.Equals(sib.StubTargetFilename, zone.Filename, StringComparison.OrdinalIgnoreCase))
                    return true; // (b) a sibling zone's stub depends on this exact filename
            }
            catch { /* an unreadable sibling can't be depending on anything - keep checking others */ }
        }
        return false;
    }

    // Import Sample - redesigned 2026-08-22 per explicit feedback: every import now
    // ALSO populates the repository (bare .KSF entries, "Un-referenced Samples"), not
    // just the one zone it's assigned to - "importing a sample (or multiple) should
    // generate .KSF for them, and make them available to multisamples within that
    // session," with the separate "Import to Repository..." button/flow folded in here
    // as redundant. Multiple files: ALL of them are written to the repository, but only
    // the FIRST is assigned to `zone` (a single zone can only play one sample at a
    // time) - the rest sit in the repository ready to be picked for other zones via the
    // Sample combo. Delegates entirely to ImportSamplesToCollection (repo write) +
    // AssignExistingKsfToZone (the actual zone assignment, including the stub-safety/
    // collision-free-filename logic - see NeedsFreshFilename) rather than duplicating
    // that logic a third time.
    public string? ImportSampleIntoZone(KmpZone zone, IReadOnlyList<string> audioPaths)
    {
        LastImportedZoneIndex = -1;
        if (audioPaths.Count == 0) return null;

        var written = ImportSamplesToCollection(audioPaths);
        if (written.Count == 0) return null; // ImportSamplesToCollection already set a failure StatusText

        var kmpPath = AssignExistingKsfToZone(zone, written[0]);
        if (kmpPath != null)
        {
            StatusText = written.Count == 1
                ? $"Imported '{Path.GetFileName(written[0])}' and assigned it to zone '{zone.Filename}'."
                : $"Imported {written.Count} sample(s) to the repository; assigned '{Path.GetFileName(written[0])}' to zone '{zone.Filename}'.";
        }
        return kmpPath;
    }

    // Adds a new zone at a given key range to the currently-selected multisample, using
    // an EXISTING .KSF's audio (a duplicate copy, not a shared reference - the new
    // zone's own KsfPath, per the standard <kmp>/<zone>.KSF convention) rather than
    // importing a fresh audio file - the gap Import Audio doesn't cover: "assign an
    // already-existing sample to a new key range." Goes through AddSampleZone, so the
    // 128-zone cap is enforced here exactly the same way it is for audio import.
    public void AddZoneFromExistingKsf(string sourceKsfPath, int originalKey, int topKey)
    {
        KmpMultisample? m;
        string? kmpPath;
        if (_selectedNode?.MultisampleRef is { } ms) { m = ms.Multisample; kmpPath = ms.Path; }
        else if (_selectedZone != null) { m = FindMultisampleContaining(Roots, _selectedZone); kmpPath = _selectedKmpPath; }
        else { StatusText = "Select a multisample (or one of its zones) first."; return; }
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the target multisample."; return; }

        try
        {
            var src = KsfSample.Open(File.ReadAllBytes(sourceKsfPath));
            if (src == null || src.IsHeaderOnly)
            { StatusText = "That file isn't a readable .KSF with audio data."; return; }

            var zone = SampleImportBuilder.AddSampleZone(m, kmpPath, src.Name, src.Samples(), (int)src.SampleRate, originalKey, topKey, src.Suffix);
            SaveMultisampleNow(m, kmpPath);
            RefreshTreeAfterMutation(m, kmpPath);
            StatusText = $"Added zone '{zone.Filename}' from '{Path.GetFileName(sourceKsfPath)}' (key {originalKey}-{topKey}).";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: new zone from '{sourceKsfPath}' failed: {ex}");
            StatusText = $"Failed to add zone: {ex.Message}";
        }
    }

    // ── Sample repository (bare, un-referenced .KSF entries) ────────────────────
    //
    // A real Kronos .KSC can list a bare .KSF filename (not wrapped in any .KMP) - shown
    // in the real Disk-page browser as "Un-referenced Samples" (kronosology doc §1.2).
    // That's the format's OWN native "sample imported but not yet assigned to any
    // keymap" concept - reused here rather than inventing a new one, so importing many
    // files at once doesn't force an immediate key-range/zone decision per file, and a
    // later "assign this existing sample to a zone" (AssignExistingKsfToZone/
    // AddZoneFromExistingKsf) has something real to pick from. Deliberately NOT modeled
    // as tree nodes (SampleTreeNode's three existing ref kinds - Collection/Multisample/
    // Zone - are load-bearing across SelectNode/ResolveStereoPartner/IsDescendant/the
    // dirty-tracking registration paths; a fourth kind would need auditing every one of
    // those match sites) - just a plain path list read straight off the collection's own
    // Entries, same "list, not tree" scope RebuildTreeFromCollection already keeps by
    // filtering to ".KMP only".
    //
    // Written to <collection-dir>/<collection-basename>/ - the SAME folder .KMP files
    // live in, not a nested subfolder - confirmed against a real Kronos-authored
    // collection (SampleFixtures/ANDRE_K2_73/samplesfeb28_25.KSC's own bare
    // ONE_0005.KSF/AROU0008.KSF/... entries sit right there, not in any zone subfolder).

    // Every bare .KSF sitting directly in the collection's own content folder - the
    // repository picker's own data source. Full path, not just filename, so the
    // caller can pass it straight to KsfSample.Open/AssignExistingKsfToZone/
    // AddZoneFromExistingKsf without re-deriving the folder convention itself.
    //
    // Reads the FOLDER, not `_collection.Entries` (2026-08-23 fix): once a repository
    // sample has been assigned into a real zone, AssignExistingKsfToZone retires its
    // Entries line (RetireConsumedRepositoryEntry) so the SAVED .KSC matches real
    // Kronos output - but the picker still needs to offer that same audio for reuse
    // into ANOTHER zone. A zone's own .KSF always lives one level deeper (`<kmp-dir>/
    // <kmp-basename>/`, KmpZone.KsfPath) than a bare repository import (written
    // straight into `kmpDir`, ImportSamplesToCollection) - the two never collide, so a
    // non-recursive folder scan is exactly "every sample available to reuse",
    // independent of whether it's currently listed as unreferenced.
    public IEnumerable<string> BareSampleEntries()
    {
        if (_collection == null || _collectionPath == null) return [];
        var kmpDir = KscCollection.ContentDirFor(_collectionPath);
        if (!Directory.Exists(kmpDir)) return [];
        return Directory.GetFiles(kmpDir, "*.KSF").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    // Decodes each audio file and writes it as a standalone resident .KSF directly into
    // the collection's own content folder, adding each as a bare entry - does NOT touch
    // any multisample/zone by itself. This is the repository-populating half of Import
    // Sample (see ImportSampleIntoZone below, which calls this then assigns the first
    // result) - every import makes its file(s) available for ANY multisample's zones
    // for the rest of the session, not just the one currently selected. One collection
    // Save at the end, not per file - a multi-file import is one user action, not N.
    //
    // Returns the full path of each successfully-written bare .KSF, in the SAME order
    // as audioPaths (failures are skipped, not padded with null) - so a caller doing
    // "import then assign the first one" knows exactly which file that is.
    public List<string> ImportSamplesToCollection(IEnumerable<string> audioPaths)
    {
        var written = new List<string>();
        if (_collection == null || _collectionPath == null) { StatusText = "Open or create a collection first."; return written; }

        var kmpDir = KscCollection.ContentDirFor(_collectionPath);
        Directory.CreateDirectory(kmpDir);

        var failures = new List<string>();
        foreach (var audioPath in audioPaths)
        {
            try
            {
                var sampleName = Path.GetFileNameWithoutExtension(audioPath);

                // Genuinely stereo source (2026-08-22, per explicit feedback): preserve
                // both channels as a matched -L/-R bare pair (same Name, opposite
                // Suffix - the doc §2.2 convention every OTHER stereo pair in this app
                // already uses), rather than always downmixing to mono. This is what
                // lets AssignExistingKsfToZone auto-detect "this repository sample is
                // stereo" later and assign both channels at once. A mono source keeps
                // the single-file path unchanged.
                if (AudioImport.GetSourceChannelCount(audioPath) >= 2)
                {
                    var (left, right) = AudioImport.ImportStereoToLR44100(audioPath);
                    var leftFileName = UniqueBareKsfFileName($"{sampleName}-L");
                    var rightFileName = UniqueBareKsfFileName($"{sampleName}-R");
                    // Sno1 must be collection-unique (hardware-confirmed 2026-08-24, see
                    // KscCollection.NextFreeSno1) - re-derived AFTER saving the left half
                    // so the right half's scan sees it and can't collide with it.
                    var leftKsf = new KsfSample { Name = sampleName, Suffix = "-L", SampleRate = (uint)AudioImport.TargetSampleRate, Flags = 0x81, Sno1 = KscCollection.NextFreeSno1(kmpDir) };
                    leftKsf.SetSamples(left);
                    var leftPath = Path.Combine(kmpDir, leftFileName);
                    leftKsf.Save(leftPath);

                    var rightKsf = new KsfSample { Name = sampleName, Suffix = "-R", SampleRate = (uint)AudioImport.TargetSampleRate, Flags = 0x81, Sno1 = KscCollection.NextFreeSno1(kmpDir) };
                    rightKsf.SetSamples(right);
                    var rightPath = Path.Combine(kmpDir, rightFileName);
                    rightKsf.Save(rightPath);

                    _collection.Entries.Add(leftFileName);
                    _collection.Entries.Add(rightFileName);
                    written.Add(leftPath);
                    written.Add(rightPath);
                }
                else
                {
                    var pcm = AudioImport.ImportToMono44100(audioPath);
                    var ksfFileName = UniqueBareKsfFileName(sampleName);
                    var ksf = new KsfSample { Name = sampleName, SampleRate = (uint)AudioImport.TargetSampleRate, Flags = 0x81, Sno1 = KscCollection.NextFreeSno1(kmpDir) };
                    ksf.SetSamples(pcm);
                    var ksfPath = Path.Combine(kmpDir, ksfFileName);
                    ksf.Save(ksfPath);

                    _collection.Entries.Add(ksfFileName);
                    written.Add(ksfPath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sample Editor: import to repository '{audioPath}' failed: {ex}");
                failures.Add(Path.GetFileName(audioPath));
            }
        }

        if (written.Count > 0) _collection.Save(_collectionPath);
        StatusText = failures.Count == 0
            ? $"Imported {written.Count} sample(s) to the repository (Un-referenced Samples)."
            : $"Imported {written.Count} sample(s), {failures.Count} failed: {string.Join(", ", failures)}";
        return written;
    }

    // Bare-.KSF-entry filenames don't need the MS<multisample><zone> convention (they
    // belong to no multisample) - a filesystem-safe form of the source name, de-duped
    // against whatever's actually already sitting in the content folder (checking the
    // folder rather than `_collection.Entries` - 2026-08-23 - so a name doesn't collide
    // with a retired-but-still-on-disk repository file, per BareSampleEntries's own
    // fix, or with an existing .KMP).
    string UniqueBareKsfFileName(string sampleName)
    {
        var safe = new string(sampleName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        if (safe.Length == 0) safe = "Sample";
        var kmpDir = KscCollection.ContentDirFor(_collectionPath!);
        var candidate = $"{safe}.KSF";
        for (int n = 1; File.Exists(Path.Combine(kmpDir, candidate)); n++)
            candidate = $"{safe}_{n}.KSF";
        return candidate;
    }

    // Writes `src`'s audio into `zone` (NeedsFreshFilename decides whether that means
    // overwriting the zone's own current file in place or picking a fresh, collision-
    // free name first) - the shared "commit one zone's assignment" step used by both
    // the single-channel and stereo-dual-assign paths below, split out so the stereo
    // path can do two of these writes before ONE save+rebuild rather than two.
    void WriteAssignedSample(KmpMultisample m, string kmpPath, KmpZone zone, string name, string suffix, uint sampleRate, short[] pcm)
    {
        if (NeedsFreshFilename(m, kmpPath, zone)) zone.Filename = m.NextFreeZoneFileName();
        // Sno1 must be collection-unique (hardware-confirmed 2026-08-24, see
        // KscCollection.NextFreeSno1) - never leave it at the field's default.
        var contentDir = Path.GetDirectoryName(kmpPath) is { Length: > 0 } d ? d : ".";
        var ksf = new KsfSample { Name = name, Suffix = suffix, SampleRate = sampleRate, Flags = 0x81, Sno1 = KscCollection.NextFreeSno1(contentDir) };
        ksf.SetSamples(pcm);
        var ksfPath = zone.KsfPath(kmpPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ksfPath)!);
        ksf.Save(ksfPath);
    }

    // Repository stereo pairing (2026-08-22): a bare .KSF whose Suffix is -L/-R with a
    // same-Name, opposite-Suffix sibling ALSO sitting bare in the repository (both
    // written together by ImportSamplesToCollection's own stereo path) is one stereo
    // sample, not two unrelated mono ones - same Name+opposite-Suffix matching
    // SampleImportBuilder.FindStereoSibling already uses for multisample pairs.
    bool TryFindRepositoryStereoPartner(KsfSample src, out string? partnerPath, out KsfSample? partnerSrc)
    {
        partnerPath = null; partnerSrc = null;
        if (src.Suffix is not ("-L" or "-R")) return false;
        var wantSuffix = src.Suffix == "-L" ? "-R" : "-L";
        foreach (var path in BareSampleEntries())
        {
            try
            {
                if (KsfSample.Open(File.ReadAllBytes(path)) is { } candidate
                    && candidate.Name == src.Name && candidate.Suffix == wantSuffix)
                { partnerPath = path; partnerSrc = candidate; return true; }
            }
            catch { /* an unreadable candidate can't be this sample's partner */ }
        }
        return false;
    }

    // Which of `sibling`'s zones corresponds to `zone` (owned by `m`) - same
    // correspondence rule ResolveStereoPartner/ApplyZoneEdits already use (exact key
    // range first, same list position as fallback), EXCEPT without ResolveStereoPartner's
    // own `!IsSkipped` restriction: that restriction is right for "resolve an ALREADY-
    // populated partner to display," wrong here ("find where to WRITE a new
    // assignment" - a placeholder sibling zone, e.g. a freshly-created multisample
    // pair's own default first zone, is exactly the normal, expected target).
    static KmpZone? ResolveCorrespondingZone(KmpMultisample m, KmpZone zone, KmpMultisample sibling)
    {
        var matchZone = sibling.Zones.FirstOrDefault(z => z.OriginalKey == zone.OriginalKey && z.TopKey == zone.TopKey);
        if (matchZone != null) return matchZone;
        int idx = m.Zones.IndexOf(zone);
        return idx >= 0 && idx < sibling.Zones.Count ? sibling.Zones[idx] : null;
    }

    // Assigns an already-imported repository sample (or any other readable .KSF -
    // doesn't strictly require it came from BareSampleEntries) to the CURRENTLY
    // SELECTED zone, replacing whatever that zone had. Mirrors ImportSampleIntoZone's
    // own stub-safety logic exactly (NeedsFreshFilename) since this replaces an
    // existing zone's audio the same way.
    //
    // Auto stereo dual-assign (2026-08-22, explicit feedback): if `sourceKsfPath` is
    // one half of a repository stereo pair AND `zone`'s own multisample resolves a
    // real stereo sibling (doc §2.2), both channels are assigned at once - the L half
    // to whichever multisample is "-L", the R half to whichever is "-R" - revealing
    // the stereo waveform view immediately instead of leaving the pair looking mono
    // until the user manually repeats the assignment on the other side. Falls back to
    // single-channel assignment (this zone only) when there's no stereo context to
    // dual-assign into - a mono multisample, or no resolvable sibling.
    public string? AssignExistingKsfToZone(KmpZone zone, string sourceKsfPath)
    {
        LastImportedZoneIndex = -1;
        var (m, kmpPath) = FindMultisampleAndPathContaining(Roots, zone);
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve this zone's multisample."; return null; }

        try
        {
            var src = KsfSample.Open(File.ReadAllBytes(sourceKsfPath));
            if (src == null || src.IsHeaderOnly)
            { StatusText = "That file isn't a readable .KSF with audio data."; return null; }

            if (m.Suffix is "-L" or "-R" && TryFindRepositoryStereoPartner(src, out var partnerPath, out var partnerSrc) && partnerSrc != null)
            {
                var (sibling, siblingPath) = ResolveStereoSibling(m, kmpPath);

                var siblingZone = sibling != null && siblingPath != null ? ResolveCorrespondingZone(m, zone, sibling) : null;
                if (sibling != null && siblingPath != null && siblingZone != null)
                {
                    // BUG FIX 2026-08-22 (Opus redundancy review): this used to pick
                    // BOTH the target multisamples AND the source samples off the SAME
                    // discriminator (m.Suffix) - but m.Suffix says which multisample the
                    // CLICKED zone belongs to, not which channel `src` (the repository
                    // file the user actually picked) is. Whenever `src` was the "-R"
                    // half and `m` was the "-L" multisample (a real, reachable case -
                    // RefreshIndexAndSampleCombo groups a stereo pair by whichever half
                    // BareSampleEntries() happens to list first, which isn't always -L),
                    // the R audio got written into the L multisample's zone (and vice
                    // versa) - a silent, audible L/R inversion with no structural
                    // corruption to make it obvious. Sources and targets are two
                    // independent choices; keep them that way.
                    var (leftSrc, rightSrc) = src.Suffix == "-L" ? (src, partnerSrc) : (partnerSrc, src);
                    var (leftM, leftPath, leftZone, rightM, rightPath, rightZone) = m.Suffix == "-L"
                        ? (m, kmpPath, zone, sibling, siblingPath, siblingZone)
                        : (sibling, siblingPath, siblingZone, m, kmpPath, zone);

                    WriteAssignedSample(leftM, leftPath, leftZone, leftSrc.Name, "-L", leftSrc.SampleRate, leftSrc.Samples());
                    WriteAssignedSample(rightM, rightPath, rightZone, rightSrc.Name, "-R", rightSrc.SampleRate, rightSrc.Samples());

                    LastImportedZoneIndex = m.Zones.IndexOf(zone);
                    SaveMultisampleNow(leftM, leftPath);
                    if (!string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase)) SaveMultisampleNow(rightM, rightPath);
                    RetireConsumedRepositoryEntry(sourceKsfPath);
                    if (partnerPath != null) RetireConsumedRepositoryEntry(partnerPath);
                    RefreshTreeAfterMutation(m, kmpPath);
                    StatusText = $"Assigned stereo sample '{src.Name}' to both channels.";
                    return kmpPath;
                }
            }

            WriteAssignedSample(m, kmpPath, zone, src.Name, src.Suffix, src.SampleRate, src.Samples());

            LastImportedZoneIndex = m.Zones.IndexOf(zone);
            SaveMultisampleNow(m, kmpPath);
            RetireConsumedRepositoryEntry(sourceKsfPath);
            RefreshTreeAfterMutation(m, kmpPath);
            StatusText = $"Assigned '{Path.GetFileName(sourceKsfPath)}' to zone '{zone.Filename}'.";
            return kmpPath;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: assign existing .KSF '{sourceKsfPath}' to zone failed: {ex}");
            StatusText = $"Assign failed: {ex.Message}";
            return null;
        }
    }

    // Once a repository (bare) entry's audio has been copied into a real zone, it no
    // longer belongs in the SAVED .KSC's unreferenced-sample list (doc §1.2, "#>User."
    // companion lines) - a real Kronos-authored collection never carries a bare line
    // for audio a keymap already owns (confirmed 2026-08-23 against a real Kronos-
    // authored .KSC pulled over FTP: it listed only its .KMP files, never the bare
    // .KSF names an equivalent editor-built collection was leaving behind). The extra
    // bare lines this produced are suspected to confuse OA.ko's own array-sizing
    // pre-scan on import (kronosology doc §1.5) - a real repro showed exactly this
    // shape (3 .KMP + 3 bare .KSF lines) on a collection where only the mono
    // multisample came in correctly on real hardware.
    //
    // The underlying .KSF file is left untouched on disk (not deleted) - only the
    // manifest line is retired - so BareSampleEntries()/the Sample combo (now folder-
    // driven, see its own comment) can still offer the same audio for reuse into
    // another zone this session, matching this app's own repository convenience
    // feature without diverging from real Kronos output.
    void RetireConsumedRepositoryEntry(string ksfPath)
    {
        if (_collection == null || _collectionPath == null) return;
        var fileName = Path.GetFileName(ksfPath);
        if (_collection.Entries.RemoveAll(e => string.Equals(e, fileName, StringComparison.OrdinalIgnoreCase)) > 0)
            _collection.Save(_collectionPath);
    }

    // Deletes every bare .KSF sitting directly in the collection's own content folder
    // (KscCollection.ContentDirFor) that is BOTH no longer a listed repository entry
    // (RetireConsumedRepositoryEntry already removed its manifest line once its audio
    // was copied into a real zone) AND not referenced by any zone anywhere in the
    // currently-loaded tree - i.e. genuinely dead weight, not a legitimate
    // "Un-referenced Sample" (those stay listed in Entries and are left alone).
    //
    // Hardware-confirmed 2026-08-24 this is a real bug, not cosmetic: a collection
    // built entirely through Import Sample (never touching the bare-repository picker
    // for reuse) still left these retired originals on disk - RetireConsumedRepository
    // Entry only ever removed the .KSC manifest line, by design, so the audio stays
    // available to assign into ANOTHER zone later this session. But nothing ever swept
    // them back out afterward, so a real Kronos-authored equivalent collection (built by
    // loading each .KMP by hand, verified via a live diff against this exact scenario)
    // has NONE of these files, while this app's own output did. Called from
    // SaveAllChanges so the on-disk state matches real Kronos output by the time a bulk
    // folder push (File Manager) or FTP push picks it up - not from RetireConsumed
    // RepositoryEntry itself, which would break same-session reuse into a second zone.
    void SweepOrphanedRepositoryFiles()
    {
        if (_collection == null || _collectionPath == null) return;
        var contentDir = KscCollection.ContentDirFor(_collectionPath);
        if (!Directory.Exists(contentDir)) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _collection.Entries) referenced.Add(e);
        foreach (var node in AllMultisampleNodes())
            foreach (var zone in node.MultisampleRef!.Value.Multisample.Zones)
                if (!zone.IsSkipped) referenced.Add(zone.Filename);

        foreach (var path in Directory.GetFiles(contentDir, "*.KSF"))
        {
            if (referenced.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); }
            catch (Exception ex) { AppLog.Warn($"Sample Editor: couldn't remove orphaned repository file '{path}': {ex.Message}"); }
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
            var (left, leftPath, right, rightPath) = SampleImportBuilder.CreateStereoMultisamplePair(
                _collection, _collectionPath, baseName, mno1Left);

            // Auto-create the default first zone on BOTH halves (identical key range,
            // required for stereo parity, doc §2.2) - user-specified 2026-08-22, so a
            // freshly created multisample already has something the editor can show/
            // import into, without a separate manual Add Zone step. Deliberately NOT
            // inside CreateStereoMultisamplePair itself (Core builder primitive) -
            // several existing self-tests call that method directly expecting a
            // genuinely empty pair to build their own specific zone layout on top of;
            // this is purely the "Create Multisample" UI action's own default.
            left.Zones.Add(SampleImportBuilder.MakeDefaultFirstZone());
            right.Zones.Add(SampleImportBuilder.MakeDefaultFirstZone());
            left.Save(leftPath);
            right.Save(rightPath);

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

        var (sibling, siblingPath) = ResolveStereoSibling(m, kmpPath);
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
            SaveMultisampleNow(leftM, leftPath);
            SaveMultisampleNow(rightM, rightPath);
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
            Directory.CreateDirectory(KscCollection.ContentDirFor(kscPath));
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

    // Smallest non-negative MNO1 not currently used by any multisample in the LIVE tree
    // (not a disk rescan - a just-created-but-not-yet-saved multisample must count too,
    // same "live state over disk" preference every other lookup here already uses).
    // slotsNeeded=2 for a stereo pair - both the candidate and candidate+1 must be free,
    // matching NewStereoMultisamplePairInCollection's own mno1/mno1+1 adjacency
    // convention (doc §2.2). Reuses freed slots (a deleted multisample's old MNO1 is
    // eligible again), matching what real Kronos content shows after slots are freed.
    public uint NextFreeMno1(int slotsNeeded = 1)
    {
        var used = new HashSet<uint>(AllMultisampleNodes().Select(n => n.MultisampleRef!.Value.Multisample.Mno1));
        for (uint candidate = 0; ; candidate++)
        {
            bool allFree = true;
            for (int i = 0; i < slotsNeeded; i++)
                if (used.Contains(candidate + (uint)i)) { allFree = false; break; }
            if (allFree) return candidate;
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
            var kmpDir = KscCollection.ContentDirFor(_collectionPath);
            Directory.CreateDirectory(kmpDir);
            var kmpFileName = $"{name}.KMP";
            var kmpPath = Path.Combine(kmpDir, kmpFileName);
            var m = new KmpMultisample { Name = name, Mno1 = mno1 };
            m.Zones.Add(SampleImportBuilder.MakeDefaultFirstZone());
            SaveMultisampleNow(m, kmpPath);

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

    // Deletes the currently-selected multisample (.KMP) entirely: removes its filename
    // from the collection manifest, deletes the .KMP file, and deletes its own zone
    // subfolder (every .KSF the app itself ever wrote there lives ONLY under
    // <kmp-dir>/<kmp-basename>/ per KmpZone.KsfPath - never shared with another
    // multisample's own subfolder, so this can't orphan or break a sibling multisample's
    // audio). Deliberately does NOT cascade to a stereo sibling - "Delete" removes
    // exactly the one selected half, same one-thing-at-a-time granularity as zone
    // delete; a user wanting both halves gone deletes each separately. Not undoable
    // (real files leave disk) - the caller (code-behind) is expected to confirm with the
    // user before calling this, same as every other hard-delete in this app.
    public void DeleteSelectedMultisample()
    {
        var (m, kmpPathResolved) = ResolveContextMultisample();
        if (m == null || kmpPathResolved == null) { StatusText = "Select a multisample first."; return; }
        if (_collection == null || _collectionPath == null) { StatusText = "No collection is open."; return; }

        var kmpPath = kmpPathResolved;
        var label = m.Name + m.Suffix;
        try
        {
            var kmpFileName = Path.GetFileName(kmpPath);
            var kmpDir = Path.GetDirectoryName(kmpPath) ?? "";
            var zoneDir = Path.Combine(kmpDir, Path.GetFileNameWithoutExtension(kmpPath));

            _collection.Entries.RemoveAll(e => string.Equals(e, kmpFileName, StringComparison.OrdinalIgnoreCase));
            _collection.Save(_collectionPath);

            if (Directory.Exists(zoneDir)) Directory.Delete(zoneDir, recursive: true);
            if (File.Exists(kmpPath)) File.Delete(kmpPath);

            SelectNode(null);
            RebuildTreeFromCollection(_collectionPath, _collection);
            StatusText = $"Deleted multisample '{label}' ('{kmpFileName}').";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample Editor: delete multisample '{kmpPath}' failed: {ex}");
            StatusText = $"Failed to delete multisample: {ex.Message}";
        }
    }

    // ── Zone deletion (Phase 5) ──────────────────────────────────────────────────

    // Marks the selected zone SKIPPEDSAMPLE (the doc's own convention for "deliberately
    // unsampled key position") rather than physically removing it from the RLP1 list -
    // removing a zone entirely would silently expand its neighbors' key ranges to fill
    // the gap (each zone's trigger range runs from the PREVIOUS zone's TopKey+1 to its
    // own TopKey), which is surprising behavior for a "delete" action to cause as a side
    // effect. The underlying .KSF is left on disk untouched - orphaned, not destroyed.
    // Now recorded on the zone-undo stack (Ctrl+Z reaches it) and mirrored onto the
    // stereo sibling's matching zone. Neither was true before: Delete was the one zone
    // edit with no undo at all, and deleting one half of a stereo pair left the other
    // half still sounding - the pair's key ranges stayed in parity, so it kept resolving
    // as stereo while only one channel had audio.
    // Two stages, both reachable through the same "Delete Zone" action. First delete on
    // a real zone soft-skips it (below) - the underlying .KSF stays on disk and the key
    // range stays reserved, so nothing about a neighboring zone changes as a side effect
    // of what looks like "just marking this unused." Second delete - on a zone that's
    // ALREADY skipped - actually REMOVES it from the keymap (see DeleteSkippedZone
    // below). Without this second stage, an already-skipped placeholder could never be
    // cleared out again: the button was disabled outright once IsSkipped was true, so a
    // zone marked skipped was permanent clutter with no way back.
    public void DeleteSelectedZone()
    {
        if (_selectedZone == null) { StatusText = "No zone selected."; return; }
        if (_selectedZone.IsSkipped) { DeleteSkippedZone(); return; }

        List<KmpZone>? siblingZones = null;
        KmpMultisample? siblingM = null;
        string? siblingPath = null;
        int idx = -1;

        if (CurrentMultisampleZones is { } zones)
        {
            idx = zones.IndexOf(_selectedZone);
            (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(zones);
            _zoneUndo.RecordBeforeEdit(ZoneListSnapshot.Of(zones, siblingZones));
            _undoDomains.Add(EditDomain.Zone);
            _redoDomains.Clear();
        }

        _selectedZone.Filename = "SKIPPEDSAMPLE";
        if (siblingZones != null && idx >= 0 && idx < siblingZones.Count)
            siblingZones[idx].Filename = "SKIPPEDSAMPLE";

        ZoneIsSkipped = true;
        ZoneFilename = "(skipped - no sample)";
        HasSampleLoaded = false;
        SampleWaveform = null;
        HasStereoPair = false;
        PartnerSampleWaveform = null;
        _zoneDirty = true;
        if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
        RefreshUndoRedoState();
        StatusText = $"Zone marked as skipped{(siblingZones != null ? " on both L/R channels" : "")} "
            + "(unsaved - use Save Multisample). The underlying .KSF file was left on disk, not deleted. "
            + "Delete again to remove this empty zone from the keymap entirely.";
    }

    // Physically removes an already-skipped (empty) zone from its multisample's Zones
    // list. Each zone's trigger range is implicit - it runs from the PREVIOUS zone's own
    // TopKey + 1 through its own TopKey - so there is no explicit range math to do here:
    // once the entry is gone, whatever now follows it automatically absorbs the vacated
    // range down to its new predecessor, the same way it always has. That absorption is
    // exactly the side effect the soft-skip in DeleteSelectedZone exists to avoid on a
    // zone that might still matter; deliberate here, since there's nothing left in an
    // already-empty zone worth reserving a placeholder for.
    //
    // Deliberately NOT wired into zone-list undo (Ctrl+Z) - same reasoning
    // AddPlaceholderZone's own comment gives: removing an entry changes the
    // multisample's CHILD COUNT, so the tree has to be rebuilt via the same
    // RefreshTreeAfterMutation every zone-ADDING method already uses (a boundary drag/
    // reorder/key edit never changes count, which is the actual reason THOSE stay
    // undoable). RefreshTreeAfterMutation's rebuild calls SelectNode(null), which resets
    // _zoneUndo on the resulting scope change - recording an undo step here would be
    // immediately erased by that same call, not preserved. Revert KSC Changes remains
    // the available undo path for this specific edit, same as it is for every other
    // zone-ADDING method.
    void DeleteSkippedZone()
    {
        if (_selectedZone == null) return;
        var (m, kmpPath) = ResolveContextMultisample();
        if (m == null || kmpPath == null) { StatusText = "Couldn't resolve the owning multisample."; return; }

        int idx = m.Zones.IndexOf(_selectedZone);
        if (idx < 0) { StatusText = "This zone is no longer in the keymap."; return; }

        var (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(m.Zones);
        m.Zones.RemoveAt(idx);
        bool removedSiblingToo = siblingZones != null && idx < siblingZones.Count;
        if (removedSiblingToo) siblingZones!.RemoveAt(idx);

        // Bypasses the _zoneDirty property setter's usual auto-registration: that setter
        // calls RegisterDirtyMultisample(), which re-resolves the owning multisample by
        // walking Zones.Contains(_selectedZone) - but _selectedZone was JUST removed
        // from m.Zones above, so that lookup would now fail and silently skip
        // registering `m` at all. `m`/`kmpPath` are already known here, so register them
        // directly instead of asking the property setter to re-derive what's no longer
        // derivable.
        _zoneDirtyField = true;
        RegisterDirtyMultisample(m, kmpPath);
        if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
        StatusText = $"Removed the empty zone from the keymap{(removedSiblingToo ? " on both L/R channels" : "")} "
            + "(unsaved - use Save Multisample).";
        RefreshTreeAfterMutation(m, kmpPath); // rebuilds the tree for the new child count - see this method's own comment for why that also means no Ctrl+Z here
    }

    // Dragging a boundary in the piano keymap (Views/SampleKeymapControl.cs) changes
    // where `zone` ends - the next zone's own low edge is auto-derived from that
    // (KmpZone's own convention: previous zone's TopKey+1), so nothing else needs to
    // change here. The keymap control itself already clamped newTopKey against its
    // neighbors before firing this, so no further validation is needed - just apply it.
    // Mirrored onto the stereo sibling's zone at the SAME INDEX (see ApplyZoneEdits'
    // own comment for why every key-range edit has to be) - a well-formed pair has
    // identical zone lists, so index is the right correspondence here, and the pair is
    // only touched at all when ResolveSiblingZonesFor confirms it's well-formed.
    public void MoveZoneBoundary(KmpZone zone, int newTopKey)
    {
        byte newTop = (byte)Math.Clamp(newTopKey, 0, 127);
        if (zone.TopKey == newTop) return; // a drag that ended where it started

        List<KmpZone>? siblingZones = null;
        KmpMultisample? siblingM = null;
        string? siblingPath = null;
        int idx = -1;

        if (CurrentMultisampleZones is { } zones)
        {
            idx = zones.IndexOf(zone);
            (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(zones);
            _zoneUndo.RecordBeforeEdit(ZoneListSnapshot.Of(zones, siblingZones));
            _undoDomains.Add(EditDomain.Zone);
            _redoDomains.Clear();
        }

        zone.TopKey = newTop;
        if (siblingZones != null && idx >= 0 && idx < siblingZones.Count) siblingZones[idx].TopKey = newTop;
        if (ReferenceEquals(zone, _selectedZone)) ZoneTopKey = zone.TopKey;
        _zoneDirty = true;
        if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
        RefreshUndoRedoState();
        StatusText = $"Zone '{(zone.IsSkipped ? "(skipped)" : zone.Filename)}' now extends to "
            + $"{MidiNoteName.ToName(zone.TopKey)}{(siblingZones != null ? " (both L/R channels)" : "")} "
            + "(unsaved - use Save Multisample).";
    }

    // Drag/drop reordering a zone in the keymap (Views/SampleKeymapControl.cs) - moves
    // `dragged` to `dropTarget`'s position in the list. Confirmed semantics: each zone
    // keeps its own key-range WIDTH (TopKey minus the previous zone's TopKey) - only its
    // POSITION in the sequence changes, so its absolute range shifts to wherever that
    // width lands after the reorder. A 10-wide zone dragged in front of a 20-wide zone
    // stays 10-wide in its new (earlier) spot; the 20-wide zone stays 20-wide in ITS new
    // (later) spot - "the top key becomes what the zone it's replacing used to be" is
    // what falls out of that for the zone that ends up last, since the total width is
    // conserved by construction. Widths are captured BEFORE the move so this is correct
    // for a move anywhere in the list, not just a simple two-zone swap.
    public void ReorderZone(KmpZone dragged, KmpZone dropTarget)
    {
        if (CurrentMultisampleZones is not { } zones) return;
        if (ReferenceEquals(dragged, dropTarget)) return;
        int fromIndex = zones.IndexOf(dragged);
        int toIndex = zones.IndexOf(dropTarget);
        if (fromIndex < 0 || toIndex < 0) return;

        // Of the three key-range edits, this is the one that most needs mirroring: it
        // rewrites EVERY TopKey in the list, so leaving the sibling alone breaks the
        // exact (OriginalKey, TopKey) stereo match for the whole multisample at once,
        // not just for one zone. The sibling gets the identical index move and the
        // identical recomputed TopKey sequence (its own zone widths are the same by
        // construction in a well-formed pair), keeping its Filenames with their zones.
        var (siblingZones, siblingM, siblingPath) = ResolveSiblingZonesFor(zones);

        _zoneUndo.RecordBeforeEdit(ZoneListSnapshot.Of(zones, siblingZones));
        _undoDomains.Add(EditDomain.Zone);
        _redoDomains.Clear();

        var widthByZone = new Dictionary<KmpZone, int>();
        int prevTop = -1;
        foreach (var z in zones) { widthByZone[z] = z.TopKey - prevTop; prevTop = z.TopKey; }

        if (fromIndex < toIndex) toIndex--; // RemoveAt below shifts every later index down by one
        zones.RemoveAt(fromIndex);
        zones.Insert(toIndex, dragged);

        int running = -1;
        foreach (var z in zones)
        {
            running += widthByZone[z];
            z.TopKey = (byte)Math.Clamp(running, 0, 127);
        }

        if (siblingZones != null && fromIndex < siblingZones.Count && toIndex < siblingZones.Count)
        {
            var siblingDragged = siblingZones[fromIndex];
            siblingZones.RemoveAt(fromIndex);
            siblingZones.Insert(toIndex, siblingDragged);
            for (int i = 0; i < siblingZones.Count; i++) siblingZones[i].TopKey = zones[i].TopKey;
        }

        if (_selectedZone != null) { ZoneOriginalKey = _selectedZone.OriginalKey; ZoneTopKey = _selectedZone.TopKey; }
        _zoneDirty = true;
        if (siblingM != null && siblingPath != null) RegisterDirtyMultisample(siblingM, siblingPath);
        RefreshUndoRedoState();
        StatusText = $"Reordered zone '{(dragged.IsSkipped ? "(skipped)" : dragged.Filename)}'"
            + $"{(siblingZones != null ? " (both L/R channels)" : "")} (unsaved - use Save Multisample).";
    }

    // Drag-moving the loop region in the waveform (both LoopStart/LoopEnd shift by the
    // same delta, preserving length) - mirrored to the stereo partner in Combine mode,
    // same reasoning as SetLoopFromSelection.
    // Whole-region drag: both edges move by the same delta, so length must be preserved
    // EXPLICITLY here - ApplySampleFieldsTo's own floor (Loop Start can't precede Sample
    // Start) only clamps loopStart upward, it doesn't know this call means "shift the
    // block," so passing newLoopStart/newLoopEnd straight through would let the region
    // silently shrink (loopStart clamped up, loopEnd left where it was) instead of the
    // whole block stopping at the wall - recomputing loopEnd from the (possibly
    // clamped) start + the ORIGINAL length is what keeps it a block move.
    public void MoveLoopRegion(int newLoopStart, int newLoopEnd)
    {
        if (_selectedSample == null) return;
        bool mirror = ShouldMirrorToPartner;

        int len = newLoopEnd - newLoopStart;
        int start = Math.Max(newLoopStart, SampleSampleStart);
        int end = start + len;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        ApplySampleFieldsTo(_selectedSample, (int)_selectedSample.SampleRate, SampleLoopEnabled, SampleSampleStart, start, end);
        _sampleDirty = true;
        if (mirror)
            ApplySampleFieldsTo(_partnerSample!, (int)_partnerSample!.SampleRate, SampleLoopEnabled, SampleSampleStart, start, end);

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Loop moved to [{start}, {end}){(mirror ? " (both L/R channels)" : "")} (unsaved - use Save Changes).";
    }

    // ── Marker editing choke point (drag OR typed field, uniformly) ─────────────────

    // The single entry point every Sample Start/Loop Start/Loop End edit routes through
    // - whether it came from dragging a marker line in the waveform or typing a new
    // value into a field. Order matters and is fixed here, not left to emerge from
    // handler ordering: clamp to the buffer -> snap to the nearest zero-crossing (if
    // Use Zero) -> apply Loop Lock's length preservation (computed from the ALREADY-
    // SNAPPED edge, so the edge you actually placed lands exactly where you put it; the
    // linked edge is derived from it and may itself land a few frames off a crossing -
    // an accepted trade-off, not a bug) -> commit through ApplySampleFieldsTo, which
    // applies the final "Loop Start can never precede Sample Start" clamp regardless of
    // what Loop Lock computed.
    // Returns whether anything was actually committed (added 2026-08-22, Opus
    // redundancy review) - the code-behind's EnsureLoopVisible was firing even on a
    // genuine no-op (e.g. LostFocus with nothing typed), which could yank a manually-
    // zoomed view back to the loop region despite that method's own documented
    // "never fights a manual zoom/pan for an unrelated reason" contract. Callers that
    // don't need to know can still ignore the return value.
    public bool SetMarker(SampleMarkerKind kind, int proposedFrame)
    {
        if (_selectedSample == null) return false;
        proposedFrame = Math.Clamp(proposedFrame, 0, SampleFrameCount);
        if (UseZeroCrossing) proposedFrame = SnapToNearestZeroCrossing(proposedFrame);

        int sampleStart = SampleSampleStart, loopStart = SampleLoopStart, loopEnd = SampleLoopEnd;
        int loopLen = loopEnd - loopStart;

        switch (kind)
        {
            case SampleMarkerKind.SampleStart:
                sampleStart = proposedFrame;
                break;
            case SampleMarkerKind.LoopStart:
                loopStart = proposedFrame;
                if (LoopLockEnabled) loopEnd = loopStart + loopLen;
                break;
            case SampleMarkerKind.LoopEnd:
                loopEnd = proposedFrame;
                if (LoopLockEnabled) loopStart = loopEnd - loopLen;
                break;
        }

        // These fields commit on LostFocus, which fires on every focus change - not only
        // the ones that changed something. Without this guard, tabbing across Sample
        // Start / Loop Start / Loop End pushed a dead undo step per field and left the
        // window claiming unsaved changes for edits that never happened. ApplySampleFieldsTo's
        // own ordering clamp is applied here first so the comparison sees exactly the
        // values that would be committed - "typed something that clamps back to where it
        // already was" is a no-op too, not just "typed the identical number".
        sampleStart = Math.Max(0, sampleStart);
        loopStart = Math.Max(loopStart, sampleStart);
        loopEnd = Math.Max(loopEnd, loopStart);
        if (sampleStart == SampleSampleStart && loopStart == SampleLoopStart && loopEnd == SampleLoopEnd) return false;

        bool mirror = ShouldMirrorToPartner;
        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        ApplySampleFieldsTo(_selectedSample, (int)_selectedSample.SampleRate, SampleLoopEnabled, sampleStart, loopStart, loopEnd);
        _sampleDirty = true;
        if (mirror)
            ApplySampleFieldsTo(_partnerSample!, (int)_partnerSample!.SampleRate, SampleLoopEnabled, sampleStart, loopStart, loopEnd);

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"{kind} set to {proposedFrame}{(mirror ? " (both L/R channels)" : "")}.";
        return true;
    }

    // Same "did it actually commit" return as SetMarker, same reason.
    public bool SetLoopEnabled(bool enabled)
    {
        if (_selectedSample == null) return false;
        if (SampleLoopEnabled == enabled) return false; // re-asserting the current state isn't an edit
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        // Bit 0x80 = one-shot/loop-off (doc §5.1) - preserve any other flag bits.
        _selectedSample.Flags = enabled ? (byte)(_selectedSample.Flags & ~0x80) : (byte)(_selectedSample.Flags | 0x80);
        _sampleDirty = true;
        if (mirror)
            _partnerSample!.Flags = enabled ? (byte)(_partnerSample.Flags & ~0x80) : (byte)(_partnerSample.Flags | 0x80);

        // Hardware-confirmed 2026-08-24: checking "Loop" alone, with no loop markers
        // ever dragged, flips this flag correctly but leaves LoopStart==LoopEnd (both 0
        // on a fresh import) - a zero-length loop region, which plays as silent/no
        // audible loop on real hardware even though the flag itself is right. Default
        // to the whole sample (LoopStart unchanged, LoopEnd -> last frame) exactly the
        // one time enabling finds no real region already set, so the checkbox alone
        // produces audible looping - an explicit Loop Start/End drag afterward still
        // overrides this the normal way.
        if (enabled && _selectedSample.LoopEnd <= _selectedSample.LoopStart && _selectedSample.FrameCount > 0)
            _selectedSample.LoopEnd = (uint)(_selectedSample.FrameCount - 1);
        if (mirror && enabled && _partnerSample!.LoopEnd <= _partnerSample.LoopStart && _partnerSample.FrameCount > 0)
            _partnerSample.LoopEnd = (uint)(_partnerSample.FrameCount - 1);

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Loop {(enabled ? "enabled" : "disabled")}{(mirror ? " (both L/R channels)" : "")}.";
        return true;
    }

    public void SetReversed(bool enabled)
    {
        if (_selectedSample == null) return;
        if (SampleReverseEnabled == enabled) return;
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        _selectedSample.IsReversed = enabled;
        _sampleDirty = true;
        if (mirror) _partnerSample!.IsReversed = enabled;

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Reverse {(enabled ? "enabled" : "disabled")}{(mirror ? " (both L/R channels)" : "")}.";
    }

    public void Set12dbBoostEnabled(bool enabled)
    {
        if (_selectedSample == null) return;
        if (Sample12dbBoostEnabled == enabled) return;
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        _selectedSample.Is12dbBoostEnabled = enabled;
        _sampleDirty = true;
        if (mirror) _partnerSample!.Is12dbBoostEnabled = enabled;

        _playback.BoostEnabled = enabled; // live, so toggling mid-playback is audible for A/B
        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"+12dB boost {(enabled ? "enabled" : "disabled")}{(mirror ? " (both L/R channels)" : "")}.";
    }

    // Clamped to -99..+99 by KsfSample.LoopTune's own setter, matching the front-panel
    // UI's hard limit (doc §3.1a / kronosology round 13's own hardware-observed bound).
    public void SetLoopTune(int value)
    {
        if (_selectedSample == null) return;
        int clamped = Math.Clamp(value, -99, 99);
        if (SampleLoopTune == clamped) return;
        bool mirror = ShouldMirrorToPartner;

        _sampleUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_selectedSample));
        _undoDomains.Add(EditDomain.Sample);
        _redoDomains.Clear();
        if (mirror) _partnerUndo.RecordBeforeEdit(SampleFieldSnapshot.Of(_partnerSample!));

        _selectedSample.LoopTune = (sbyte)clamped;
        _sampleDirty = true;
        if (mirror) _partnerSample!.LoopTune = (sbyte)clamped;

        LoadSampleDetailState(_selectedSample, reloadWaveform: false);
        RefreshUndoRedoState();
        StatusText = $"Loop Tune set to {clamped}{(mirror ? " (both L/R channels)" : "")}.";
    }

    // Use Zero searches BOTH channels of a resolved stereo pair (Combine mode) - either
    // channel's crossing is a valid snap target, whichever is nearer to the raw
    // proposed frame wins; an exact tie picks the LOWER frame value, per explicit
    // instruction (rather than, say, preferring the primary channel on a tie).
    //
    // NearestZeroCrossing returns null (not the unchanged frame) when a channel has NO
    // crossing at all - critical here specifically: if "not found" were represented the
    // same way as "found exactly at the target" (distance 0), a channel with no
    // crossing whatsoever would falsely WIN every distance comparison against a real
    // crossing on the other channel, no matter how close that real crossing was.
    // Reuses SampleWaveform/PartnerSampleWaveform (already-decoded, cached by
    // LoadSampleDetailState) instead of calling KsfSample.Samples() again - this runs
    // on every marker drag/typed-field commit that has Use Zero on, so a fresh decode
    // here was a second full-buffer re-decode on top of LoadSampleDetailState's own
    // (Opus performance review, 2026-08-22). Falls back to a real decode only if the
    // cache is somehow unpopulated, so this can never regress to "wrong snap point".
    int SnapToNearestZeroCrossing(int proposedFrame)
    {
        if (_selectedSample == null) return proposedFrame;
        int? primary = _selectedSample.IsHeaderOnly ? null
            : NearestZeroCrossing(SampleWaveform ?? _selectedSample.Samples(), proposedFrame);
        int? partner = ShouldMirrorToPartner && _partnerSample is { IsHeaderOnly: false }
            ? NearestZeroCrossing(PartnerSampleWaveform ?? _partnerSample!.Samples(), proposedFrame) : null;

        if (primary == null && partner == null) return proposedFrame;
        if (primary == null) return partner!.Value;
        if (partner == null) return primary.Value;

        int distPrimary = Math.Abs(primary.Value - proposedFrame);
        int distPartner = Math.Abs(partner.Value - proposedFrame);
        if (distPrimary < distPartner) return primary.Value;
        if (distPartner < distPrimary) return partner.Value;
        return Math.Min(primary.Value, partner.Value);
    }

    // Nearest zero-crossing to `frame` (where consecutive samples straddle or touch the
    // center line), searched outward in both directions simultaneously so it's the
    // CLOSEST crossing, not just the first one found in one direction. Bounded: once
    // both directions are exhausted with no crossing found (a DC-offset signal, or a
    // buffer that never actually returns to zero), returns null - a real, checkable
    // failure mode, not a hypothetical, and distinct from "found one exactly at frame."
    static int? NearestZeroCrossing(short[] pcm, int frame)
    {
        if (pcm.Length < 2) return null;
        frame = Math.Clamp(frame, 0, pcm.Length - 1);
        for (int d = 0; d <= pcm.Length; d++)
        {
            int lo = frame - d, hi = frame + d;
            bool loInRange = lo >= 1, hiInRange = hi <= pcm.Length - 1 && hi >= 1;
            if (loInRange && IsZeroCrossing(pcm, lo)) return lo;
            if (hiInRange && IsZeroCrossing(pcm, hi)) return hi;
            // Terminate once BOTH directions have moved permanently out of the valid
            // crossing-index range [1, pcm.Length-1] - lo only ever decreases and hi
            // only ever increases as d grows, so once both are past the range they can
            // never come back into it. Checking loInRange/hiInRange (this d's clamped
            // booleans) instead would break after the very first d where frame itself
            // sits at an edge, long before the search range is actually exhausted.
            if (lo < 1 && hi > pcm.Length - 1) break;
        }
        return null;
    }

    static bool IsZeroCrossing(short[] pcm, int i) =>
        (pcm[i - 1] <= 0 && pcm[i] >= 0) || (pcm[i - 1] >= 0 && pcm[i] <= 0);

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
