using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

public partial class SampleEditorWindow : ThemedWindow
{
    readonly SampleEditorViewModel _vm = new();
    readonly DispatcherTimer _vuTimer;

    // Guards MultisampleCombo/ZoneSampleCombo against re-entering their own
    // SelectionChanged handlers while RefreshDetailPanels is programmatically
    // repopulating ItemsSource/SelectedItem to mirror the tree's real selection.
    bool _suppressComboEvents;

    // Guards OnZoneOrigKeyBoxChanged/OnZoneTopKeyBoxChanged against firing while
    // RefreshDetailPanels is programmatically setting ZoneOrigKeyBox.Text/
    // ZoneTopKeyBox.Text to mirror a newly-selected zone (both boxes are wired to
    // TextChanged, so each assignment fires its own handler). Without this, switching
    // zones corrupted the NEW zone's range: setting ZoneOrigKeyBox.Text first fired
    // OnZoneOrigKeyBoxChanged immediately, which read the new zone's (just-set) orig
    // key alongside ZoneTopKeyBox.Text - still showing the OLD zone's top key, not yet
    // overwritten - and committed that mismatched pair via ApplyZoneEdits onto the
    // already-switched _selectedZone, clamping/cascading the new zone's range against
    // stale data (the "zone creep" - and, once the list was corrupted enough, the
    // crash - moving between zones). The second box's own assignment then re-fired the
    // handler with correct values and usually self-corrected, but not before the first,
    // wrong ApplyZoneEdits call had already mutated - and could cascade into - the zone
    // list. Not needed on OnKeymapPianoKeyCtrlClicked's Text assignments (~line 1559/
    // 1564 below) - those deliberately want the commit to fire.
    bool _suppressZoneKeyFieldEvents;

    // Sample dropdown shows each zone's real .KSF Name, not its filename - reading that
    // means opening every zone's .KSF, which is too expensive to redo on every
    // RefreshDetailPanels call (fires on nearly every edit, not just navigation).
    // Cached per session, keyed by ksfPath; cleared wherever a zone's .KSF content or
    // Name could actually change (Import Sample, Rename Sample) rather than re-derived
    // from scratch on every refresh. Also backs the repository (bare .KSF) scan
    // RefreshIndexAndSampleCombo does, which would otherwise read and fully re-parse
    // (KsfSample.Open, including its PCM body) EVERY bare .KSF in the whole collection
    // on every single RefreshDetailPanels call, unbounded in collection size and
    // independent of the currently-loaded sample. Only Name/Suffix are ever needed from
    // either a zone's own sample or a repository entry, so this caches just that pair
    // rather than the full decoded KsfSample.
    readonly Dictionary<string, (string Name, string Suffix)> _repositorySampleCache = new(StringComparer.OrdinalIgnoreCase);

    // Returns null for anything unreadable rather than throwing - a bad/missing file is
    // expected repository-scan state, not a bug, same contract as KsfSample.Open itself.
    (string Name, string Suffix)? GetRepositorySampleInfo(string ksfPath)
    {
        // A rename (or any other field edit) is a live, unsaved edit sitting in the VM's
        // _dirtySamples - checked first and never cached here, since disk hasn't changed
        // and won't until Save Sample. Without this, a just-renamed sample's dropdown
        // entry kept showing its old on-disk name until it was actually saved.
        if (_vm.TryGetPendingSampleInfo(ksfPath) is { } pending) return pending;
        if (_repositorySampleCache.TryGetValue(ksfPath, out var cached)) return cached;
        KsfSample? s;
        try { s = File.Exists(ksfPath) ? KsfSample.Open(File.ReadAllBytes(ksfPath)) : null; }
        catch { s = null; }
        if (s == null) return null;
        var info = (s.Name, s.Suffix);
        _repositorySampleCache[ksfPath] = info;
        return info;
    }

    public SampleEditorWindow()
    {
        InitializeComponent();
        // Select is the default tool. Set directly rather than via a simulated click -
        // OnSelectToolClick/OnMoveToolClick are wired to Click (see the XAML's own
        // comment), which only fires from real user input, not from an IsChecked
        // assignment - so the pairing has to be established by hand here too.
        BtnSelectTool.IsChecked = true;
        BtnMoveTool.IsChecked = false;
        _vm.IsMoveToolActive = false;
        // Scroll to Zoom is checked by default (today's only behavior) - set here, after
        // InitializeComponent() has already built WaveformLeft/WaveformRight, rather than
        // inline in XAML, which would fire OnScrollToZoomChanged mid-parse before those
        // fields exist (same hazard BtnSelectTool's own comment describes).
        ChkScrollToZoom.IsChecked = true;
        SampleTree.ItemsSource = _vm.Roots;
        _vm.TreeRefreshed += () => { }; // ItemsSource already bound to the live collection - no rebind needed
        VolumeControl.Volume = _vm.Volume;
        PanControl.Pan = _vm.Pan;

        // IsPlaying can flip to false on its own (playback reaching the end of the
        // buffer), not just from clicking Stop - without this, the Play/Stop button
        // never reverted to "Play" when a one-shot sample finished on its own, since
        // RefreshDetailPanels was previously only ever called from explicit user-action
        // handlers, never in response to the ViewModel's own state changing.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SampleEditorViewModel.IsPlaying) or nameof(SampleEditorViewModel.IsPaused))
            { RefreshDetailPanels(); UpdateStatus(); }
        };

        // Transport Locate/Rewind/FF pressed while fully stopped - relocates the grey
        // scrub line without starting audio (see SampleEditorViewModel.CursorMoved's own
        // comment). Mirrored to both stereo panes the same way OnWaveformPaneScrubRequested
        // mirrors a manual scrub-click - both panes are visible together now regardless
        // of SplitLR (see RefreshDetailPanels), not just in Combine mode.
        _vm.CursorMoved += frame =>
        {
            WaveformLeft.ScrubFrame = frame;
            if (_vm.HasStereoPair) WaveformRight.ScrubFrame = frame;
        };

        // VU meter: polls SamplePlayback.PeakLevel (see its own comment on why this is
        // a plain poll, not an event) rather than pushing UI updates from the audio
        // thread. ~25 updates/sec reads as smooth without redrawing every frame.
        _vuTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _vuTimer.Tick += (_, _) => UpdateVuMeter();
        _vuTimer.Start();

        // Closing this window (the X button, Alt+F4, or the app quitting) previously
        // discarded any unsaved sample/zone edit with no warning at all - Save Changes
        // is a manual, separate action from every edit here (see the live-editing
        // redesign this window's own history documents), so nothing else ever
        // persisted them automatically.
        Closing += OnWindowClosing;

        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.HasUnsavedChanges)
        {
            var result = MessageBox.Show(this,
                "There are unsaved changes in the Sample Editor. Close anyway and discard them?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        // Break the Owner link before closing so WPF doesn't minimize the parent when
        // this window had focus (known WPF owner-activation bug) - same one-line fix
        // FileManagerWindow.OnClosing and LibrarianShellWindow already use.
        Owner = null;
    }

    public void OpenCollectionPath(string path) { _vm.OpenCollection(path); SelectFirstRoot(); UpdateStatus(); }
    public void OpenKmpPath(string path) { _vm.OpenMultisampleDirect(path); SelectFirstRoot(); UpdateStatus(); }

    // Loading a library used to leave the tree/MS dropdown/detail panels entirely blank
    // until the user manually clicked the new entry - RefreshDetailPanels only ever ran
    // off a real selection change, and nothing here ever made one. The tree is just a
    // flat list of loaded libraries now (see SampleTree's own XAML comment), so "select
    // the first entry" unambiguously means Roots[0] - reuses SelectTreeNode (same
    // _vm.SelectNode + RefreshDetailPanels/UpdateStatus + tree-highlight path every
    // other selection in this window already goes through), not a separate mechanism.
    void SelectFirstRoot()
    {
        if (_vm.Roots.Count > 0) SelectTreeNode(_vm.Roots[0]);
    }

    void OnOpenCollection(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open Collection", Filter = "Korg KSC Files|*.KSC|All Files|*.*" };
        if (dlg.ShowDialog(this) == true) OpenCollectionPath(dlg.FileName);
    }

    void OnOpenKmpDirect(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open Multisample", Filter = "Korg KMP Files|*.KMP|All Files|*.*" };
        if (dlg.ShowDialog(this) == true) OpenKmpPath(dlg.FileName);
    }

    void OnNewCollection(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "New Collection", Filter = "Korg KSC Files|*.KSC|All Files|*.*" };
        // SelectFirstRoot (same as OpenCollectionPath already does) - without it the
        // freshly-created collection's own root node sat there unselected, requiring an
        // extra click before anything showed.
        if (dlg.ShowDialog(this) == true) { _vm.NewCollection(dlg.FileName); SelectFirstRoot(); UpdateStatus(); }
    }

    // Tree right-click "Save as..." - `owningPath` is the collection the user actually
    // right-clicked (resolved by OnTreeContextMenuOpening the same way "Close
    // Collection" already is), not just "whatever's currently active," since selecting
    // the node to right-click it already re-syncs the VM's active-collection fields to
    // match (SelectNode) - but being explicit here matches this handler's own contract
    // with its caller rather than relying on that indirection staying true.
    void OnSaveCollectionAs(string owningPath)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Collection As",
            Filter = "Korg KSC Files|*.KSC|All Files|*.*",
            FileName = Path.GetFileName(owningPath),
        };
        if (dlg.ShowDialog(this) != true) return;
        _vm.SaveCollectionAs(dlg.FileName);
        // The new collection lands at the END of Roots (RebuildTreeFromCollection adds
        // rather than replaces, since its path differs from the old one) - SelectFirstRoot
        // would pick the WRONG root whenever another collection is already open first.
        var newRoot = _vm.Roots.FirstOrDefault(r => string.Equals(r.CollectionRef?.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (newRoot != null) SelectTreeNode(newRoot);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnNewMultisample(object sender, RoutedEventArgs e)
    {
        var nameDlg = new PromptDialog("Multisample name:", "NewMS") { Owner = this };
        if (nameDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDlg.Result)) return;

        var idDlg = new PromptDialog("Multisample ID # (0-3999):", "0") { Owner = this };
        uint mno1 = 0;
        if (idDlg.ShowDialog() == true) uint.TryParse(idDlg.Result, out mno1);
        mno1 = Math.Min(mno1, 3999); // doc's own "max of 3999 possible keymaps" ceiling - AutoFileName's 8-char DOS 8.3 stem depends on never exceeding it

        var msNode = _vm.NewMultisampleInCollection(nameDlg.Result, mno1);
        RefreshDetailPanels();
        UpdateStatus();
        if (msNode != null) SelectTreeNode(msNode);
    }

    void OnNewStereoMultisamplePair(object sender, RoutedEventArgs e)
    {
        var nameDlg = new PromptDialog("Stereo pair base name (no -L/-R suffix):", "NewStereoMS") { Owner = this };
        if (nameDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDlg.Result)) return;

        var idDlg = new PromptDialog("Left multisample ID # (0-3998; Right uses ID+1):", "0") { Owner = this };
        uint mno1Left = 0;
        if (idDlg.ShowDialog() == true) uint.TryParse(idDlg.Result, out mno1Left);
        mno1Left = Math.Min(mno1Left, 3998); // Right (mno1Left+1) must also stay within the 3999 ceiling

        var msNode = _vm.NewStereoMultisamplePairInCollection(nameDlg.Result, mno1Left);
        RefreshDetailPanels();
        UpdateStatus();
        if (msNode != null) SelectTreeNode(msNode);
    }

    // "Create" button on the MS panel itself - the next free slot is computed and shown
    // in the dialog's own title before the user decides anything, rather than asked for
    // (contrast OnNewMultisample/OnNewStereoMultisamplePair above, the older File-menu
    // flow that prompts for a name AND a manually-typed slot number). Name defaults to
    // "NEWMS<slot>", matching the real Kronos's own auto-generated name for a fresh
    // multisample (kronosology doc §2.2's NEWMS000/001 examples) - rename afterward via
    // Edit > Rename Multisample if wanted.
    void OnCreateMultisample(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasActiveCollection) { StatusBar.Text = "Open or create a collection first."; return; }

        // Preview slot assumes mono (1 slot) - the common case, and the dialog's own
        // title needs SOME number before it can ask mono-or-stereo. Recomputed below
        // with the REAL slot count once the answer is known.
        uint previewSlot = _vm.NextFreeMno1();
        var dlg = new CreateMultisampleDialog(previewSlot) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // A stereo pair needs 2
        // CONTIGUOUS slots (MNO1 and MNO1+1 - NewStereoMultisamplePairInCollection's own
        // convention), but this always computed `previewSlot` with the default
        // slotsNeeded=1. If a lower slot had freed up (e.g. a multisample was deleted)
        // while a HIGHER one stayed taken, previewSlot could take a 2-slot range that
        // collides with that still-existing multisample - exactly the false-positive
        // SampleImportBuilder.FindStereoSibling's own comment says "shouldn't happen for
        // app-created pairs". Recomputing with the real slotsNeeded closes that gap; the
        // previewed slot number can differ from the final one in that same rare case
        // (dialog title showed the mono-case slot) - accepted, not worth blocking Create
        // Multisample on a second round-trip through the dialog for.
        uint slot = dlg.Stereo ? _vm.NextFreeMno1(2) : previewSlot;
        var baseName = $"NEWMS{slot:D3}";
        var msNode = dlg.Stereo ? _vm.NewStereoMultisamplePairInCollection(baseName, slot)
                                 : _vm.NewMultisampleInCollection(baseName, slot);
        RefreshDetailPanels();
        UpdateStatus();

        // Select the new multisample right away (same courtesy OnAddPlaceholderZone
        // gives a new zone) - without this the tree/combo rebuild these two calls
        // trigger leaves nothing selected (RebuildTreeFromCollection's own entry-8
        // SelectNode(null) fix), so the user has to go find and click it themselves
        // before the editor panel has anything to show. Uses the node the VM itself just
        // created (returned directly), not a path reconstructed here - the VM already
        // knows exactly which node it built.
        if (msNode != null) SelectTreeNode(msNode);
    }

    void OnDeleteMultisample(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMultisampleLabel is not { } label) { StatusBar.Text = "Select a multisample first."; return; }

        var result = MessageBox.Show(this,
            $"Permanently delete '{label}' and all of its samples from disk?\nThis cannot be undone.",
            "Delete Multisample", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        AfterMutation(_vm.DeleteSelectedMultisample);
    }

    // RefreshDetailPanels (not just UpdateStatus) because saving changes HasUnsavedChanges,
    // which drives the title bar's dirty marker and the Save Changes button's enabled
    // state - without it both stayed stale after a successful save until some unrelated
    // action happened to refresh them.
    void OnSaveMultisample(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.SaveSelectedMultisample());
    void OnSaveSample(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.SaveSelectedSample());

    // The tree only shows root (loaded-library) nodes now - see SampleTree's own XAML
    // comment - so a genuine user click here can only ever select one of those; the old
    // auto-drill-into-first-zone logic that used to live here for a directly-clicked
    // multisample node moved into SelectTreeNode, the one remaining path that can select
    // a multisample (the MS dropdown). _suppressTreeSelectionEvent guards against
    // SelectTreeNode's own IsSelected write below re-entering this handler and
    // clobbering the zone/multisample it just selected with the owning ROOT instead -
    // see SelectTreeNode's own comment.
    bool _suppressTreeSelectionEvent;

    void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelectionEvent) return;
        _vm.SelectNode(e.NewValue as SampleTreeNode);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Live editing session - fields now commit as you type (TextChanged), not just on
    // LostFocus/tab-away (explicit feedback: "even pressing enter isn't affecting
    // them" - Enter did nothing at all before, since only LostFocus committed). Each
    // handler below is wired to BOTH TextChanged and LostFocus (same method), guarded
    // to bail out BEFORE calling RefreshDetailPanels whenever ITS OWN box's text
    // doesn't currently parse - RefreshDetailPanels resets every field's Text from the
    // model, so committing on a half-typed value (e.g. an empty box mid-retype, or
    // "C" before the octave digit is typed) would immediately stomp the user's own
    // in-progress keystrokes back to the last valid value. Save Changes (bottom-right)
    // is still what actually writes to disk - this only affects the live in-memory
    // model + undo, same as it always did.
    //
    // Split into two handlers (was one shared OnZoneKeyChanged) so each box's own
    // guard is independent - typing into Top Key must not be blocked by Orig.Key
    // being mid-edit, and vice versa. The OTHER box's value still falls back to its
    // last committed value exactly like the original shared handler did.
    void OnZoneOrigKeyBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressZoneKeyFieldEvents) return;
        if (MidiNoteName.TryParse(ZoneOrigKeyBox.Text) is not { } origKey) return;
        var topKey = MidiNoteName.TryParse(ZoneTopKeyBox.Text) ?? _vm.ZoneTopKey;
        _vm.ApplyZoneEdits(origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnZoneTopKeyBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressZoneKeyFieldEvents) return;
        if (MidiNoteName.TryParse(ZoneTopKeyBox.Text) is not { } topKey) return;
        var origKey = MidiNoteName.TryParse(ZoneOrigKeyBox.Text) ?? _vm.ZoneOriginalKey;
        _vm.ApplyZoneEdits(origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Wheel step = 1 semitone/1 index, always - deliberately NOT WheelStep()'s
    // percent-of-length scaling (Sample Start/Loop Start/Loop End below): those are
    // frame positions where a flat step is meaningless across wildly different sample
    // lengths, but a key number or zone index has a fixed, small, meaningful range
    // where "one notch = one step" is exactly what's expected (explicit request - no
    // skipping). Matches ZoneIndexBox's own OnZoneIndexWheel, already flat-1.
    void OnZoneOrigKeyWheel(object sender, MouseWheelEventArgs e)
    {
        if (MidiNoteName.TryParse(ZoneOrigKeyBox.Text) is not { } origKey) return;
        var topKey = MidiNoteName.TryParse(ZoneTopKeyBox.Text) ?? _vm.ZoneTopKey;
        _vm.ApplyZoneEdits(origKey + (e.Delta > 0 ? 1 : -1), topKey);
        RefreshDetailPanels();
        UpdateStatus();
        e.Handled = true;
    }

    void OnZoneTopKeyWheel(object sender, MouseWheelEventArgs e)
    {
        if (MidiNoteName.TryParse(ZoneTopKeyBox.Text) is not { } topKey) return;
        var origKey = MidiNoteName.TryParse(ZoneOrigKeyBox.Text) ?? _vm.ZoneOriginalKey;
        _vm.ApplyZoneEdits(origKey, topKey + (e.Delta > 0 ? 1 : -1));
        RefreshDetailPanels();
        UpdateStatus();
        e.Handled = true;
    }

    // Shared Enter-key commit for every live-updating field below - PreviewKeyDown so
    // it fires before the TextBox's own default handling, dispatched by sender since
    // each field already has its own (TextChanged-wired) commit method to reuse rather
    // than duplicating the parse/apply logic a second time.
    void OnFieldPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (sender == ZoneOrigKeyBox) OnZoneOrigKeyBoxChanged(sender, e);
        else if (sender == ZoneTopKeyBox) OnZoneTopKeyBoxChanged(sender, e);
        else if (sender == SampleStartBox) OnSampleStartBoxChanged(sender, e);
        else if (sender == LoopStartBox) OnLoopStartBoxChanged(sender, e);
        else if (sender == LoopEndBox) OnLoopEndBoxChanged(sender, e);
        else if (sender == LoopTuneBox) OnLoopTuneBoxChanged(sender, e);
    }

    // Index box (1-based position of the selected zone within CurrentMultisampleZones) -
    // out-of-range values snap to the nearest valid index rather than being rejected.
    // Committing selects the target zone the same way clicking it in the tree/keymap
    // does (SelectTreeNode), so everything downstream of a real selection change (undo
    // scope reset, stereo partner resolution, ...) happens exactly once, the normal way.
    void OnZoneIndexChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ZoneIndexBox.Text, out var idx)) CommitZoneIndex(idx);
        else { RefreshDetailPanels(); UpdateStatus(); }
    }

    // Shared by LostFocus, Enter, the wheel, and Up/Down - clamps to the valid range
    // and selects that zone's tree node, same as the original LostFocus-only behavior.
    void CommitZoneIndex(int idx)
    {
        if (_vm.CurrentMultisampleZones is { Count: > 0 } zones)
        {
            idx = Math.Clamp(idx, 1, zones.Count);
            var target = FindNodeForZone(_vm.Roots, zones[idx - 1]);
            if (target != null) SelectTreeNode(target);
        }
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Enter commits immediately (previously did nothing at all - only LostFocus/tabbing
    // away committed, which read as "the field doesn't work" per explicit feedback).
    // Up/Down arrow steps the index by one without needing to type - the "quick way of
    // changing the index" asked for, without the complexity/risk of swapping this out
    // for a whole new dropdown-style control.
    void OnZoneIndexPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(ZoneIndexBox.Text, out var idx)) CommitZoneIndex(idx);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Up or Key.Down)) return;
        int current = CurrentZoneIndexOrDefault();
        CommitZoneIndex(current + (e.Key == Key.Up ? 1 : -1));
        e.Handled = true;
    }

    // Mouse-wheel steps the index by one per notch - the same "quick way of changing
    // the index" as the arrow keys, for a mouse-first workflow.
    void OnZoneIndexWheel(object sender, MouseWheelEventArgs e)
    {
        int current = CurrentZoneIndexOrDefault();
        CommitZoneIndex(current + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    int CurrentZoneIndexOrDefault() =>
        _vm.CurrentMultisampleZones is { } zones && _vm.SelectedZoneObject is { } sel && zones.IndexOf(sel) >= 0
            ? zones.IndexOf(sel) + 1 : 1;

    // Sample dropdown is the ASSIGNMENT control, not zone navigation: the repository
    // listing is available immediately when the user clicks the dropdown for the sample
    // selection, replacing a separate "Assign from Repository..." button/dialog.
    // Zone navigation is still available
    // via the Index box, the tree, and the Keymap piano - this dropdown now answers
    // "what does the CURRENT zone play", same as typing into Orig.Key/Top Key answers
    // "what key range does it own".
    void OnZoneSampleComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (_vm.SelectedZoneObject is not { } zone) return;
        if (ZoneSampleCombo.SelectedItem is not ZoneSampleOption option) return;

        var kmpPath = _vm.AssignExistingKsfToZone(zone, option.Path);
        var zoneIndex = _vm.LastImportedZoneIndex;
        _repositorySampleCache.Clear(); // this zone's own path may now hold different content
        RefreshDetailPanels();
        UpdateStatus();
        if (kmpPath == null) return;

        // Same re-select-by-position treatment as every other action that triggers
        // RefreshTreeAfterMutation (AddPlaceholderZone/ImportSampleIntoZone) - the
        // rebuild replaces every KmpZone instance, only a position survives it.
        var msNode = FindMultisampleNode(_vm.Roots, kmpPath);
        if (msNode != null && zoneIndex >= 0 && zoneIndex < msNode.Children.Count) SelectTreeNode(msNode.Children[zoneIndex]);
    }

    // One entry in the Sample combo: either the CURRENTLY assigned sample
    // (first entry, whether or not it happens to also be a bare repository file) or a
    // repository sample not yet assigned to this zone. ToString() override is
    // deliberate, not decorative - WPF's SelectionBoxItem/closed-combo display falls
    // back to the record's own default ToString() ("ZoneSampleOption { Path = ... }")
    // for the currently-selected item even with DisplayMemberPath set on the ComboBox
    // in XAML; overriding ToString() directly is a guaranteed fix regardless of which
    // internal WPF path is actually consulting it.
    sealed record ZoneSampleOption(string Path, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    // MS dropdown - picking a different multisample is functionally the same as
    // selecting its .KMP node in the tree.
    void OnMultisampleComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (MultisampleCombo.SelectedItem is SampleTreeNode node) SelectTreeNode(node);
    }

    // Whichever multisample node owns CurrentMultisampleZones - matched by reference
    // (SelectNode hands CurrentMultisampleZones the multisample's OWN Zones list, same
    // identity AllMultisampleNodes' MultisampleRef.Multisample.Zones exposes). Shared by
    // both combo refreshers below rather than each re-deriving it separately - the
    // Sample combo needs the multisample's .KMP path too (to resolve each zone's .KSF),
    // not just the MS combo.
    //
    // Takes the already-enumerated node list rather than calling AllMultisampleNodes()
    // itself - that walks the WHOLE tree (every multisample AND every zone under it),
    // and RefreshDetailPanels used to call it here AND again inside
    // RefreshMultisampleCombo, i.e. twice per keystroke. One walk, shared.
    static SampleTreeNode? FindCurrentMultisampleNode(List<SampleTreeNode> allMsNodes, List<KmpZone>? zones)
    {
        if (zones == null) return null;
        return allMsNodes.FirstOrDefault(n => ReferenceEquals(n.MultisampleRef!.Value.Multisample.Zones, zones));
    }

    // Index/"of N"/Range fields and the Sample dropdown all describe the SAME selected
    // zone's position within CurrentMultisampleZones - refreshed together so Range (which
    // depends on the PREVIOUS zone's Top Key, doc §2.1 - see KmpZone.TopKey's own comment)
    // stays consistent with whatever Index/Sample now show.
    void RefreshIndexAndSampleCombo(string? kmpPath)
    {
        var zones = _vm.CurrentMultisampleZones;
        var selected = _vm.SelectedZoneObject;
        int zoneIndex = zones != null && selected != null ? zones.IndexOf(selected) : -1;

        _suppressComboEvents = true;
        if (kmpPath != null && selected != null)
        {
            // First entry is always whatever this zone currently plays (even a skipped
            // placeholder, which has no real path - "own" is null in that case, so
            // nothing in the repository list below can collide with it) - selecting it
            // again is a harmless no-op assignment (OnZoneSampleComboChanged just
            // re-writes the same content). Every OTHER entry is a repository sample that
            // ISN'T already what "own" represents (by Name/Suffix identity, not by path -
            // see the repository loop's own comment for why path alone isn't enough).
            string? ownPath = selected.IsSkipped ? null : selected.KsfPath(kmpPath);
            var options = new List<ZoneSampleOption>();
            // Hoisted out of the `if` below so the repository loop further down can also
            // exclude by identity, not just by ownPath - see its own comment.
            (string Name, string Suffix)? ownInfo = ownPath != null ? GetRepositorySampleInfo(ownPath) : null;
            if (ownPath != null)
            {
                // Falls back to the raw filename if the .KSF can't be read - same
                // "unreadable file, not a bug" contract GetRepositorySampleInfo itself
                // documents; there's no separate name source to fall back to here since
                // ownPath != null already implies !selected.IsSkipped (checked above).
                string ownName = ownInfo?.Name ?? selected.Filename;
                options.Add(new ZoneSampleOption(ownPath, $"{ownName} {StereoTag(ownInfo?.Suffix)}"));
            }

            // Group repository stereo pairs (same Name, opposite -L/-R Suffix - written
            // together by ImportSamplesToCollection's own stereo path) into
            // ONE entry - picking either half auto-assigns both channels
            // (AssignExistingKsfToZone), so listing them as two separate rows would just
            // be two confusing routes to the identical result. "(S)"/"(M)" tag (not
            // "(Stereo)") per explicit feedback. Routed through GetRepositorySampleInfo's
            // cache (not a fresh KsfSample.Open per entry per refresh) - see that
            // cache's own comment.
            var repoInfo = _vm.BareSampleEntries()
                .Select(p => (Path: p, Info: GetRepositorySampleInfo(p)))
                .Where(t => t.Info != null)
                .ToList();
            var alreadyGrouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, info) in repoInfo)
            {
                if (alreadyGrouped.Contains(path)) continue;
                // Excludes by IDENTITY (Name, and Suffix for a mono sample; Name alone
                // for a stereo pair, since assigning EITHER repository half reassigns
                // BOTH channels anyway) - NOT by path. Assigning a
                // repository sample copies its audio into the zone's own file rather
                // than referencing the repository file directly (AssignExistingKsfToZone/
                // WriteAssignedSample), so "this zone's own file" and "the repository
                // entry it came from" are two different paths with identical content -
                // an exact-path check let the exact same sample show up TWICE (once as
                // "own", once as a still-present, now-redundant repository entry).
                if (ownInfo != null && info!.Value.Name == ownInfo.Value.Name
                    && (info.Value.Suffix == ownInfo.Value.Suffix
                        || (ownInfo.Value.Suffix is "-L" or "-R" && info.Value.Suffix is "-L" or "-R")))
                    continue;

                options.Add(new ZoneSampleOption(path, $"{info!.Value.Name} {StereoTag(info.Value.Suffix)}"));

                if (info.Value.Suffix is "-L" or "-R")
                {
                    var wantSuffix = info.Value.Suffix == "-L" ? "-R" : "-L";
                    var partner = repoInfo.FirstOrDefault(t => t.Info!.Value.Name == info.Value.Name && t.Info.Value.Suffix == wantSuffix);
                    if (partner.Path != null) alreadyGrouped.Add(partner.Path);
                }
            }
            ZoneSampleCombo.ItemsSource = options;
            ZoneSampleCombo.SelectedIndex = options.Count > 0 && ownPath != null ? 0 : -1;
        }
        else
        {
            ZoneSampleCombo.ItemsSource = null;
        }
        _suppressComboEvents = false;

        ZoneIndexBox.Text = zoneIndex >= 0 ? (zoneIndex + 1).ToString() : "";
        ZoneIndexTotalText.Text = zones != null ? $"/ {zones.Count}" : "";

        if (zoneIndex >= 0 && zones != null)
        {
            int low = zoneIndex == 0 ? 0 : zones[zoneIndex - 1].TopKey + 1;
            ZoneRangeText.Text = $"({MidiNoteName.ToName(low)} - {MidiNoteName.ToName(selected!.TopKey)})";
        }
        else ZoneRangeText.Text = "";
    }

    // "(S)" for one half of a stereo pair (Suffix -L/-R), "(M)" otherwise - explicit
    // feedback's exact requested tag, replacing the earlier "(Stereo)"/plain-name
    // labeling. A null suffix (couldn't read the file) reads as mono, the safer default.
    static string StereoTag(string? suffix) => suffix is "-L" or "-R" ? "(S)" : "(M)";

    // MS dropdown - same node resolution FindCurrentMultisampleNode already does, and
    // now the same already-enumerated list (see its own comment).
    void RefreshMultisampleCombo(SampleTreeNode? currentMsNode, List<SampleTreeNode> allMsNodes)
    {
        _suppressComboEvents = true;
        MultisampleCombo.ItemsSource = allMsNodes;
        MultisampleCombo.SelectedItem = currentMsNode;
        _suppressComboEvents = false;
    }

    // Sample Start/Loop Start/Loop End each commit independently through SetMarker (the
    // same choke point a marker drag in the waveform uses) - Use Zero snapping, Loop
    // Lock, and the Sample-Start-is-the-floor ordering rule all apply here exactly the
    // same way they do for a drag. Sample Rate stays read-only/informational - retyping
    // the declared number alone would desync it from the real PCM data without actually
    // resampling anything; an actual rate change only ever happens via a real resample.
    // Guarded (bail out before RefreshDetailPanels on unparseable text) so this is safe
    // to fire on every keystroke (TextChanged), not just LostFocus - see the block
    // comment above OnZoneOrigKeyBoxChanged for why that guard matters.
    void OnSampleStartBoxChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SampleStartBox.Text, out var v)) return;
        _vm.SetMarker(SampleMarkerKind.SampleStart, v);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Wheel step = ~1% of the sample's total length, not a flat 1 frame - explicit
    // feedback: a fixed step is meaningless once sample length varies by orders of
    // magnitude (1 frame per notch is imperceptible on a multi-minute sample, or a
    // huge jump on a one-second one). Percent-of-length scales with it automatically:
    // 1000 frames -> step 10, 100000 frames -> step 1000, always ~1% either way.
    int WheelStep() => Math.Max(1, _vm.SampleFrameCount / 100);

    void OnSampleStartWheel(object sender, MouseWheelEventArgs e)
    {
        if (!int.TryParse(SampleStartBox.Text, out var v)) return;
        _vm.SetMarker(SampleMarkerKind.SampleStart, v + (e.Delta > 0 ? WheelStep() : -WheelStep()));
        RefreshDetailPanels();
        UpdateStatus();
        e.Handled = true;
    }

    void OnLoopStartWheel(object sender, MouseWheelEventArgs e)
    {
        if (!int.TryParse(LoopStartBox.Text, out var v)) return;
        bool committed = _vm.SetMarker(SampleMarkerKind.LoopStart, v + (e.Delta > 0 ? WheelStep() : -WheelStep()));
        RefreshDetailPanels();
        UpdateStatus();
        if (committed) EnsureLoopVisible();
        e.Handled = true;
    }

    void OnLoopEndWheel(object sender, MouseWheelEventArgs e)
    {
        if (!int.TryParse(LoopEndBox.Text, out var v)) return;
        bool committed = _vm.SetMarker(SampleMarkerKind.LoopEnd, v + (e.Delta > 0 ? WheelStep() : -WheelStep()));
        RefreshDetailPanels();
        UpdateStatus();
        if (committed) EnsureLoopVisible();
        e.Handled = true;
    }

    void OnLoopStartBoxChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LoopStartBox.Text, out var v)) return;
        bool committed = _vm.SetMarker(SampleMarkerKind.LoopStart, v);
        RefreshDetailPanels();
        UpdateStatus();
        // Only on an actual commit (otherwise tabbing through the field with nothing
        // typed could yank a manually-zoomed view back to the loop region) and only on
        // LostFocus/Enter,
        // not every live TextChanged keystroke (panning the view on every digit typed
        // would be far more disruptive than helpful mid-edit).
        if (committed && e is not TextChangedEventArgs) EnsureLoopVisible();
    }

    void OnLoopEndBoxChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LoopEndBox.Text, out var v)) return;
        bool committed = _vm.SetMarker(SampleMarkerKind.LoopEnd, v);
        RefreshDetailPanels();
        UpdateStatus();
        if (committed && e is not TextChangedEventArgs) EnsureLoopVisible();
    }

    void OnLoopEnabledChanged(object sender, RoutedEventArgs e)
    {
        bool committed = _vm.SetLoopEnabled(LoopEnabledBox.IsChecked == true);
        RefreshDetailPanels();
        UpdateStatus();
        if (committed) EnsureLoopVisible();
    }

    // Pans/zooms the waveform so the WHOLE current loop region is visible, when it
    // isn't already - explicit feedback: enabling Loop (or typing a new Loop Start/End)
    // showed nothing until the user manually zoomed or dragged a selection there
    // themselves ("Loop Selected" only worked because the selection they'd just
    // dragged was, by construction, already inside the current view - LoopEnd itself
    // wasn't being scrolled to, the user's own workaround just happened to already be
    // looking at the right place). Never fights a manual zoom/pan done for an
    // unrelated reason - only called right after an action that actually changes
    // whether Loop is on or where its points are.
    void EnsureLoopVisible()
    {
        if (!_vm.SampleLoopEnabled) return;
        int start = _vm.SampleLoopStart, end = _vm.SampleLoopEnd;
        if (end <= start) return;
        if (start >= WaveformLeft.ViewStartFrame && end <= WaveformLeft.ViewEndFrame) return; // already fully visible
        WaveformLeft.SetView(start, end); // ViewChanged mirrors to the ruler/scrollbar/right pane
    }

    void OnUseZeroChanged(object sender, RoutedEventArgs e) => _vm.UseZeroCrossing = UseZeroBox.IsChecked == true;

    // Pure UI preference, no VM state - both panes read SampleWaveformControl.ScrollToZoom
    // directly (see its own comment for what unchecked does).
    void OnScrollToZoomChanged(object sender, RoutedEventArgs e)
    {
        bool on = ChkScrollToZoom.IsChecked == true;
        WaveformLeft.ScrollToZoom = on;
        WaveformRight.ScrollToZoom = on;
    }

    // Unlike UseZeroCrossing (pure VM-side state with no immediate visual feedback),
    // LoopLockEnabled also drives WaveformLeft/Right directly (the whole-region-drag
    // gate and its green/blue fill - see SampleWaveformControl's own comments) - that
    // only happens inside RefreshDetailPanels, so skipping it here left the waveform
    // showing stale drag-gate behavior for one extra click after checking the box,
    // until whatever that next click did happened to call RefreshDetailPanels anyway.
    void OnLoopLockChanged(object sender, RoutedEventArgs e)
    {
        _vm.LoopLockEnabled = LoopLockBox.IsChecked == true;
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Drives the real, persisted Kronos Reverse flag (SetReversed) - checking this box
    // previews reversed playback immediately (PlaySelectedSample reads the same
    // SampleReverseEnabled SetReversed just set) AND persists it for when the Kronos
    // itself plays the sample back after Save. One flag, one checkbox.
    void OnLoopReverseChanged(object sender, RoutedEventArgs e)
    {
        _vm.SetReversed(LoopReverseBox.IsChecked == true);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnSample12dbBoostChanged(object sender, RoutedEventArgs e)
    {
        _vm.Set12dbBoostEnabled(Sample12dbBoostBox.IsChecked == true);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnLoopTuneBoxChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LoopTuneBox.Text, out var v)) return;
        _vm.SetLoopTune(v);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Ctrl+S deliberately fires ahead of this window's focus-in-a-TextBox guard, so a
    // value typed into a field but not yet committed by LostFocus wouldn't be included.
    // Moving focus off the field first makes that commit happen, so the save covers what
    // the user can actually see typed.
    void OnSaveChanges(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) BtnSaveChanges.Focus();
        _vm.SaveAllChanges();
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Marker drag (Sample Start line, or either loop edge independently) from either
    // waveform pane - routes through the same SetMarker choke point the typed fields
    // use, so Use Zero/Loop Lock/ordering apply identically regardless of origin.
    //
    // Real bug: this used to be ONE shared handler for both panes with no idea which
    // one actually fired, so it always committed onto whichever channel happened to be
    // _selectedSample already - not necessarily the pane the user just dragged. In
    // Split L/R with R active, dragging L's Sample Start line visibly snapped L back to
    // its old value (RefreshDetailPanels redrew it from the still-unchanged VM state)
    // and moved R instead. ActivateSplitChannel (same fix OnWaveformPaneSelectionChanged
    // already applies to a selection drag) makes the DRAGGED pane's channel the active
    // one BEFORE the commit, so SetMarker always lands on the sample whose line actually
    // moved. A no-op outside Split L/R, same as every other ActivateSplitChannel call.
    void OnWaveformLeftMarkerDragged(SampleMarkerKind kind, int frame) => OnWaveformPaneMarkerDragged(WaveformLeft, kind, frame);
    void OnWaveformRightMarkerDragged(SampleMarkerKind kind, int frame) => OnWaveformPaneMarkerDragged(WaveformRight, kind, frame);

    void OnWaveformPaneMarkerDragged(SampleWaveformControl pane, SampleMarkerKind kind, int frame)
    {
        ActivateSplitChannel(ReferenceEquals(pane, WaveformLeft));
        _vm.SetMarker(kind, frame);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Scrub-click "select a playback starting point" - a plain click on the waveform
    // sets the grey cursor line (mirrored onto the sibling stereo pane) WITHOUT starting
    // playback. The next Play (button, Space,
    // or Pause's Resume) starts from here - see SampleEditorViewModel.SetCursorFrame/
    // PlaySelectedSample. Also doubles as Split's dual-pane "click either pane to make
    // it the active channel" gesture (ActivateSplitChannel) - a plain click was already
    // the natural "I'm pointing at THIS one" gesture, no separate click target needed.
    void OnWaveformLeftScrubRequested(int frame) => OnWaveformPaneScrubRequested(WaveformLeft, frame);
    void OnWaveformRightScrubRequested(int frame) => OnWaveformPaneScrubRequested(WaveformRight, frame);

    void OnWaveformPaneScrubRequested(SampleWaveformControl pane, int frame)
    {
        ActivateSplitChannel(ReferenceEquals(pane, WaveformLeft));
        WaveformLeft.ScrubFrame = frame;
        if (_vm.HasStereoPair) WaveformRight.ScrubFrame = frame;
        _vm.SetCursorFrame(frame);
        UpdateStatus();
    }

    // Move tool's whole-waveform drag (SampleWaveformControl.WaveformMoved) - fires with
    // the delta relative to whichever PANE was actually dragged, which may be the
    // partner's pane rather than the tree-active one (RefreshDetailPanels shows both
    // panes at once in Split - see its own comment). ApplyChannelMove now always targets
    // whichever channel the dragged PANE represents (targetPartner), with the delta's
    // sign passed straight through unchanged (+later/-toward zero on that SAME channel) -
    // no more cross-channel sign flip. Deliberately does NOT activate the dragged pane
    // first (unlike a plain click) - a move-drag is a self-contained gesture that
    // shouldn't also reset undo history as a side effect (SelectNode does, via
    // ActivateSplitChannel). internal (not private) so SampleEditorVisualCheck.cs can
    // exercise the exact same path a real mouse-drag commit uses (ApplyChannelMove +
    // RefreshDetailPanels) without needing to synthesize actual WPF mouse input - same
    // reasoning as x:Name fields like BtnAddZone already being internal for that file's
    // own "real handler, not a bare VM call" checks.
    internal void OnWaveformLeftMoved(int deltaFrames) => OnWaveformPaneMoved(WaveformLeft, deltaFrames);
    void OnWaveformRightMoved(int deltaFrames) => OnWaveformPaneMoved(WaveformRight, deltaFrames);

    void OnWaveformPaneMoved(SampleWaveformControl pane, int deltaFrames)
    {
        bool paneIsActive = ReferenceEquals(pane, WaveformLeft) == _vm.IsPrimaryLeftChannel;

        // Explicit request: a scrub-cursor line placed before a Move-mode waveform drag
        // must survive it. ApplyChannelMove pads/trims the moved channel's PCM, changing
        // its frame count - RefreshDetailPanels below reassigns that pane's Samples DP to
        // the new array, and SampleWaveformControl.OnSamplesChanged treats ANY frame-
        // count change as "this counts as loading a different sample" and clears
        // ScrubFrame to -1 (correct for a real navigation/Crop, wrong here: a channel
        // move repositions content under a fixed line, same "markers are frame numbers,
        // not content pointers" reasoning ApplyChannelMove's own doc comment already
        // applies to Sample Start/Loop points). Capture both panes' scrub position first
        // and restore it after the reset RefreshDetailPanels triggers.
        int leftScrub = WaveformLeft.ScrubFrame, rightScrub = WaveformRight.ScrubFrame;
        _vm.ApplyChannelMove(targetPartner: !paneIsActive, deltaFrames);
        RefreshDetailPanels();
        WaveformLeft.ScrubFrame = leftScrub;
        WaveformRight.ScrubFrame = rightScrub;
        UpdateStatus();
    }

    void RefreshDetailPanels()
    {
        // Nothing loaded at all - the pane shows ONLY the empty-state message; every
        // other section (header, MS picker, keymap, zone detail, waveform/tabs) lives
        // under EditorContent, collapsed as one unit rather than piece by piece.
        bool anythingLoaded = _vm.Roots.Count > 0;
        EmptyStateText.Visibility = anythingLoaded ? Visibility.Collapsed : Visibility.Visible;
        EditorContent.Visibility = anythingLoaded ? Visibility.Visible : Visibility.Collapsed;
        // Moved ahead of the early return below: with nothing loaded, HasUnsavedChanges is
        // false, and Save Changes must show that (faded/disabled) from the moment the window
        // opens rather than sitting enabled - its XAML default - until the first title update
        // some later action happens to trigger.
        UpdateWindowTitle();
        if (!anythingLoaded) return; // nothing else below has anything meaningful to set

        // The Index/Sample/Orig.Key/Top Key panel is available as soon as a multisample
        // is in context (same condition as the keymap below), not only once a specific
        // zone is selected within it - OnTreeSelectionChanged already auto-selects the
        // first zone the instant a multisample is picked, so in practice this is almost
        // always populated; this gate just also covers a multisample with zero zones
        // gracefully (panel shows, fields blank, nothing to select).
        //
        // MUST be "!= null" (a multisample is in context), NOT "is { Count: > 0 }" (has
        // at least one zone) - the two conditions differ EXACTLY for a brand-new,
        // just-created multisample (Create Multisample button), which has a real
        // CurrentMultisampleZones list that's simply empty. A Count>0 check would
        // collapse this whole panel - and the keymap below - for that one case, with no
        // way to reach "Create Zone"/"Import Sample..." to populate it.
        ZonePanel.Visibility = _vm.CurrentMultisampleZones != null ? Visibility.Visible : Visibility.Collapsed;
        var sampleTabs = _vm.HasSampleLoaded ? Visibility.Visible : Visibility.Collapsed;
        EditingFrame.Visibility = sampleTabs;
        WaveformPanel.Visibility = sampleTabs;
        // Keymap now lives inside MsSection itself (see that Border's own XAML comment -
        // merged in from the old standalone KeymapSection). Same "!= null" fix as
        // ZonePanel above, same reason - toggles the piano control directly rather than
        // a wrapping section, since MsSection's dropdown/Create/Rename/Delete row above
        // it stays visible either way.
        Keymap.Visibility = _vm.CurrentMultisampleZones != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionText.Visibility = _vm.HasZoneSelected ? Visibility.Collapsed : Visibility.Visible;
        MNU_UnloadCollection.IsEnabled = _vm.HasActiveCollection;
        MNU_RevertKsc.IsEnabled = _vm.HasActiveCollection;
        MNU_RevertAll.IsEnabled = _vm.Roots.Count > 0;
        MNU_RenameMultisample.IsEnabled = _vm.CurrentMultisampleName != null;
        BtnRenameMultisample.IsEnabled = _vm.CurrentMultisampleName != null;
        MNU_RenameSample.IsEnabled = _vm.HasSampleLoaded;

        // Both boxes are wired to TextChanged (see _suppressZoneKeyFieldEvents' own
        // comment) - suppressed here so mirroring the model into the UI can't be
        // misread as the user editing it, which corrupted the newly-selected zone by
        // committing the first box's new value against the second box's still-stale one.
        _suppressZoneKeyFieldEvents = true;
        if (_vm.HasZoneSelected)
        {
            ZoneOrigKeyBox.Text = MidiNoteName.ToName(_vm.ZoneOriginalKey);
            ZoneTopKeyBox.Text = MidiNoteName.ToName(_vm.ZoneTopKey);
        }
        else
        {
            // The panel can now be visible with no zone selected yet (a multisample
            // with zero zones) - blank rather than showing a stale key from whatever
            // was selected before.
            ZoneOrigKeyBox.Text = "";
            ZoneTopKeyBox.Text = "";
        }
        _suppressZoneKeyFieldEvents = false;

        var allMsNodes = _vm.AllMultisampleNodes().ToList();
        var currentMsNode = FindCurrentMultisampleNode(allMsNodes, _vm.CurrentMultisampleZones);
        RefreshIndexAndSampleCombo(currentMsNode?.MultisampleRef?.Path);
        RefreshMultisampleCombo(currentMsNode, allMsNodes);

        Keymap.Zones = _vm.CurrentMultisampleZones;
        Keymap.SelectedZone = _vm.SelectedZoneObject;
        // Zone-list undo/redo mutates the SAME List<KmpZone> instance in place (see
        // ZoneListSnapshot.ApplyTo) rather than assigning a new list - the DP above
        // therefore sees no reference change and won't auto-invalidate on its own, so
        // this is needed explicitly to actually repaint the keymap after Ctrl+Z/Y.
        Keymap.InvalidateVisual();

        SplitLRBox.Visibility = _vm.HasStereoPair ? Visibility.Visible : Visibility.Collapsed;
        SplitLRBox.IsChecked = _vm.SplitLR;
        VuMeterLeft.Visibility = _vm.HasStereoPair ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.HasSampleLoaded)
        {
            SampleNameText.Text = _vm.SampleName;
            SampleFramesText.Text = _vm.SampleFrameCount.ToString();
            SampleWarningText.Text = _vm.SampleIsHeaderOnly
                ? "No audio data (header-only save - see doc §3.3)" : "";
            SampleRateBox.Text = _vm.SampleRate.ToString();
            LoopEnabledBox.IsChecked = _vm.SampleLoopEnabled;
            // Sample Start/Loop Start/Loop End/Use Zero/Loop Lock/Reverse Loop/Loop
            // Selected only show up once Loop Enabled is actually checked - collapsed
            // as one group rather than each field gating itself separately.
            LoopFieldsRow.Visibility = _vm.SampleLoopEnabled ? Visibility.Visible : Visibility.Collapsed;
            SampleStartBox.Text = _vm.SampleSampleStart.ToString();
            LoopStartBox.Text = _vm.SampleLoopStart.ToString();
            LoopEndBox.Text = _vm.SampleLoopEnd.ToString();
            UseZeroBox.IsChecked = _vm.UseZeroCrossing;
            LoopLockBox.IsChecked = _vm.LoopLockEnabled;
            LoopReverseBox.IsChecked = _vm.SampleReverseEnabled;
            Sample12dbBoostBox.IsChecked = _vm.Sample12dbBoostEnabled;
            LoopTuneBox.Text = _vm.SampleLoopTune.ToString();

            if (_vm.HasStereoPair)
            {
                // Both panes are always shown together now, L fixed top / R fixed
                // bottom - Combine mirrors one shared selection/marker set onto both
                // (toolbar edits apply to both channels via ApplyEffect's own
                // mirroring); Split shows each channel's OWN independent markers
                // (LeftSampleStartFrame/RightSampleStartFrame etc. - edits still apply
                // only to whichever channel is tree-active, same as before, just visible
                // side by side now instead of hiding the inactive one). SetStereoRowsVisible's
                // wideGap gives Split a visibly wider seam so the two reading as
                // independent, not just mirrored, is obvious without opening the tree.
                WaveformRightRow.Visibility = Visibility.Visible;
                SetStereoRowsVisible(true, _vm.SplitLR);
                WaveformDivider.Visibility = Visibility.Visible;
                LeftChannelLabel.Visibility = Visibility.Visible;
                LeftChannelLabel.Text = "L";

                // ViewFrameCount MUST be set on both panes BEFORE Samples below - assigning
                // Samples fires OnSamplesChanged synchronously (which reads ViewFrameCount
                // to reset/reclamp the view), and Split's Move tool can leave the two
                // channels with different real lengths (SampleWaveformControl.ViewFrameCount's
                // own comment) - without the shared override in place first, the pane that
                // happens to update first resets to ITS OWN (possibly shorter) length, and
                // SyncWaveformViews's mirrored SetView onto the other pane then clamps the
                // shared view back down to match, silently erasing the very offset a move
                // just created.
                int pairFrameCount = Math.Max(_vm.LeftSampleWaveform?.Length ?? 0, _vm.RightSampleWaveform?.Length ?? 0);
                WaveformLeft.ViewFrameCount = pairFrameCount;
                WaveformRight.ViewFrameCount = pairFrameCount;
                WaveformLeft.Samples = _vm.LeftSampleWaveform;
                WaveformRight.Samples = _vm.RightSampleWaveform;

                foreach (var pane in new[] { WaveformLeft, WaveformRight })
                {
                    pane.MoveToolActive = _vm.IsMoveToolActive;
                    pane.CanMoveWaveform = _vm.SplitLR;
                }

                if (!_vm.SplitLR)
                {
                    foreach (var pane in new[] { WaveformLeft, WaveformRight })
                    {
                        pane.SelectionStartFrame = _vm.SelectionStartFrame;
                        pane.SelectionEndFrame = _vm.SelectionEndFrame;
                        pane.SampleStartFrame = _vm.SampleSampleStart;
                        pane.LoopStartFrame = _vm.SampleLoopStart;
                        pane.LoopEndFrame = _vm.SampleLoopEnd;
                        pane.LoopEnabled = _vm.SampleLoopEnabled;
                        pane.LoopLockEnabled = _vm.LoopLockEnabled;
                        // No single/both-channel concept in Combine - everything already
                        // mirrors unconditionally, so there's nothing to highlight as
                        // "the" active channel and no channel-picking double-click either.
                        pane.IsSplitChannelPane = false;
                        pane.IsActiveChannel = false;
                    }
                }
                else
                {
                    // The active channel's selection lives on the VM (SelectionStartFrame/
                    // EndFrame, same as any other zone - Crop/Fade/etc only ever touch the
                    // active channel(s)). Explicit request: with Both active (a double-
                    // click on the already-active pane, or a drag that crossed into the
                    // other pane - see ActivateBothSplitChannels/OnWaveformPaneSelectionChanged)
                    // BOTH panes show it, not just one - matching that edits now actually
                    // apply to both (ShouldMirrorToPartner). Otherwise only the one pane
                    // whose channel is actually active shows it; the other has none.
                    bool leftActive = _vm.IsPrimaryLeftChannel;
                    bool both = _vm.SplitBothActive;
                    WaveformLeft.SelectionStartFrame = (leftActive || both) ? _vm.SelectionStartFrame : 0;
                    WaveformLeft.SelectionEndFrame = (leftActive || both) ? _vm.SelectionEndFrame : 0;
                    WaveformRight.SelectionStartFrame = (!leftActive || both) ? _vm.SelectionStartFrame : 0;
                    WaveformRight.SelectionEndFrame = (!leftActive || both) ? _vm.SelectionEndFrame : 0;
                    WaveformLeft.SampleStartFrame = _vm.LeftSampleStartFrame;
                    WaveformRight.SampleStartFrame = _vm.RightSampleStartFrame;
                    WaveformLeft.LoopStartFrame = _vm.LeftLoopStartFrame;
                    WaveformRight.LoopStartFrame = _vm.RightLoopStartFrame;
                    WaveformLeft.LoopEndFrame = _vm.LeftLoopEndFrame;
                    WaveformRight.LoopEndFrame = _vm.RightLoopEndFrame;
                    WaveformLeft.LoopEnabled = _vm.LeftLoopEnabled;
                    WaveformRight.LoopEnabled = _vm.RightLoopEnabled;
                    WaveformLeft.LoopLockEnabled = _vm.LoopLockEnabled;
                    WaveformRight.LoopLockEnabled = _vm.LoopLockEnabled;
                    WaveformLeft.IsSplitChannelPane = true;
                    WaveformRight.IsSplitChannelPane = true;
                    WaveformLeft.IsActiveChannel = leftActive || both;
                    WaveformRight.IsActiveChannel = !leftActive || both;
                }

                // Explicit, unconditional - every property write above is a WPF
                // DependencyProperty that skips its own change callback (and therefore
                // any redraw) when the new value happens to equal the old one by
                // reference/value, which is exactly the common case for the pane that
                // DIDN'T just change (its Samples array, its markers, everything is
                // identical to a moment ago). Reported bug: after a channel move, the
                // MOVED pane's grid/scale lines correctly reflect the new frame count
                // (its own Samples DP genuinely changed, forcing OnSamplesChanged's own
                // reset+redraw), but the OTHER, untouched pane's grid could be left
                // showing a stale interval if it happens to reach this point without any
                // of ITS OWN property writes having actually differed from what they
                // already were - ViewFrameCount in particular is a plain CLR property,
                // not a DP, so reassigning it alone (even to a genuinely NEW pair-wide
                // value) never invalidates anything on its own. Forcing both panes to
                // redraw here removes any dependency on some OTHER write happening to
                // differ - correct, if occasionally one redundant repaint.
                WaveformLeft.InvalidateVisual();
                WaveformRight.InvalidateVisual();
            }
            else
            {
                WaveformRightRow.Visibility = Visibility.Collapsed;
                SetStereoRowsVisible(false, false);
                WaveformDivider.Visibility = Visibility.Collapsed;
                LeftChannelLabel.Visibility = Visibility.Collapsed;
                // Clears any pair-timeline override left over from a previous stereo
                // selection - this control instance is reused across tree selections.
                WaveformLeft.ViewFrameCount = 0;
                WaveformLeft.Samples = _vm.SampleWaveform;
                WaveformLeft.SelectionStartFrame = _vm.SelectionStartFrame;
                WaveformLeft.SelectionEndFrame = _vm.SelectionEndFrame;
                WaveformLeft.SampleStartFrame = _vm.SampleSampleStart;
                WaveformLeft.LoopStartFrame = _vm.SampleLoopStart;
                WaveformLeft.LoopEndFrame = _vm.SampleLoopEnd;
                WaveformLeft.LoopEnabled = _vm.SampleLoopEnabled;
                WaveformLeft.LoopLockEnabled = _vm.LoopLockEnabled;
                WaveformLeft.MoveToolActive = _vm.IsMoveToolActive;
                WaveformLeft.CanMoveWaveform = false;
            }
            // WaveformLeft.Samples above already reset ITS view (OnSamplesChanged fires
            // ViewChanged), which syncs the ruler/scrollbar/right pane via
            // OnWaveformLeftViewChanged - no separate call needed here.
        }
        else
        {
            WaveformLeft.Samples = null;
            WaveformRight.Samples = null;
            WaveformRightRow.Visibility = Visibility.Collapsed;
            SetStereoRowsVisible(false, false);
            WaveformDivider.Visibility = Visibility.Collapsed;
            LeftChannelLabel.Visibility = Visibility.Collapsed;
        }

        BtnUndo.IsEnabled = _vm.CanUndo;
        BtnRedo.IsEnabled = _vm.CanRedo;
        MNU_Undo.IsEnabled = _vm.CanUndo;
        MNU_Redo.IsEnabled = _vm.CanRedo;
        // Play/Stop toggle - green triangle while stopped (about to Play), red square
        // while playing (about to Stop), matching Kronos/transport convention.
        PlayIcon.Visibility = _vm.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        StopIcon.Visibility = _vm.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        BtnPlayStop.ToolTip = _vm.IsPlaying ? "Stop" : "Play";
        bool transportUsable = _vm.HasSampleLoaded && (!_vm.SampleIsHeaderOnly || _vm.IsPlaying);
        BtnPlayStop.IsEnabled = transportUsable;
        BtnLocateStart.IsEnabled = transportUsable;
        BtnRewind.IsEnabled = transportUsable;
        BtnFastForward.IsEnabled = transportUsable;
        BtnLocateEnd.IsEnabled = transportUsable;
        BtnPause.IsEnabled = transportUsable && (_vm.IsPlaying || _vm.IsPaused);
        // A held/lit look while actually paused (waiting to be resumed), same "active
        // background" language IsPressed/IsMouseOver already use in the button's style.
        PauseIcon.Fill = _vm.IsPaused ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
        // Delete Zone is a two-stage action now (SampleEditorViewModel.DeleteSelectedZone):
        // enabled for ANY selected zone, not just an un-skipped one - the first delete
        // soft-skips, the second (on an already-skipped zone) actually removes it from
        // the keymap. It used to disable outright once a zone was skipped, which meant an
        // empty placeholder could never be cleared back out. Label/tooltip switch to make
        // the state-dependent action visible rather than silently different.
        BtnDeleteZone.IsEnabled = _vm.HasZoneSelected;
        BtnDeleteZone.Content = _vm.ZoneIsSkipped ? "Remove" : "Delete";
        BtnDeleteZone.ToolTip = _vm.ZoneIsSkipped
            ? "Removes this empty zone from the keymap entirely - the neighboring zone's key range expands to fill the gap. Undo with Ctrl+Z if that's not what you want."
            : "Marks this zone as empty (no sample) - the underlying .KSF is left on disk. Delete again on an empty zone to remove it from the keymap entirely.";
        BtnImportSampleIntoZone.IsEnabled = _vm.HasZoneSelected;
        MNU_DeleteZone.IsEnabled = _vm.HasZoneSelected;
        MNU_DeleteZone.Header = _vm.ZoneIsSkipped ? "_Remove Zone" : "_Delete Zone";
        BtnZoomSelection.IsEnabled = _vm.SelectionEndFrame > _vm.SelectionStartFrame;
        // Duration alongside the raw frame count - frames alone say nothing about how
        // long the selection actually is, and the sample rate is right there to convert
        // with. Guarded against a zero rate rather than dividing by it.
        if (_vm.SelectionEndFrame > _vm.SelectionStartFrame)
        {
            int frames = _vm.SelectionEndFrame - _vm.SelectionStartFrame;
            string duration = _vm.SampleRate > 0 ? $" = {frames / (double)_vm.SampleRate:0.###} s" : "";
            SelectionInfoText.Text = $"Selection: [{_vm.SelectionStartFrame}, {_vm.SelectionEndFrame})  ({frames} frames{duration})"
                + "  -  drag on the waveform to change, scroll to zoom, double-click to reset zoom";
        }
        else
        {
            SelectionInfoText.Text = "No selection - drag on the waveform to select a range. Scroll to zoom, double-click to reset.";
        }
    }

    // Keeps the two stereo-only Grid rows' heights in step with their children's
    // Visibility - a Collapsed child does NOT shrink a fixed-height RowDefinition, so
    // these have to be driven explicitly or a mono sample keeps the R pane's dead space.
    // wideGap: Split L/R gets a visibly wider seam (14px vs Combine's 2px) - the two
    // panes now show independently-editable channels rather than one mirrored view, and
    // the gap is the visual cue for that, not just a style flourish.
    void SetStereoRowsVisible(bool visible, bool wideGap)
    {
        WaveformDividerRow.Height = new GridLength(!visible ? 0 : wideGap ? 14 : 2);
        WaveformRightRowDef.Height = new GridLength(visible ? 170 : 0);
    }

    void UpdateStatus() => StatusBar.Text = _vm.StatusText;

    // Most edit/transport handlers below are exactly "mutate, then RefreshDetailPanels(),
    // then UpdateStatus()" - this preserves that ordering in one place instead of repeating
    // it, so a future handler can't land with one of the three steps missing (RefreshDetailPanels
    // in particular: without it a mutation can go through with the panels/title-bar dirty
    // marker/Save button state left stale - see OnSaveMultisample's own comment). Handlers
    // that react to the ViewModel's OWN state changing (no local mutation call to wrap) or
    // that don't fit this exact shape stay as their own methods, unconverted.
    void AfterMutation(Action mutate)
    {
        mutate();
        RefreshDetailPanels();
        UpdateStatus();
    }

    // File > Close. Alt+F4, the title-bar X and the tree's own "Close Editor" entry all
    // reach the same place - this is the discoverable one.
    void OnCloseWindowMenuItem(object sender, RoutedEventArgs e) => Close();

    // Menu enable-state is recomputed when the menu drops rather than pushed from every
    // state change: RefreshDetailPanels and friends already set a handful of these, but
    // widening that to every File/Edit item means auditing every call site that mutates
    // state and missing one silently. Computing at display time cannot go stale. The
    // existing per-refresh assignments are left alone - they agree with these and are
    // harmless overlap. MNU_RecentFiles has used the same SubmenuOpened idiom all along.
    void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        bool collection  = _vm.HasActiveCollection;
        bool multisample = _vm.CurrentMultisampleName != null;
        bool sample      = _vm.HasSampleLoaded;

        MNU_NewMultisample.IsEnabled    = collection;
        MNU_NewStereoPair.IsEnabled     = collection;
        MNU_UnloadCollection.IsEnabled  = collection;

        MNU_SaveChanges.IsEnabled       = _vm.HasUnsavedChanges;
        MNU_SaveMultisample.IsEnabled   = multisample;
        MNU_SaveSample.IsEnabled        = sample;

        // Both push paths only ever put an already-pulled file back where it came from,
        // and refuse a dirty or header-only one - see CanPushSelected*.
        MNU_PushSample.IsEnabled        = _vm.CanPushSelectedSample;
        MNU_PushMultisample.IsEnabled   = _vm.CanPushSelectedMultisample;

        MNU_ImportAudio.IsEnabled       = multisample;
        MNU_ImportStereoAudio.IsEnabled = multisample;
        MNU_NewZone.IsEnabled           = multisample;
        MNU_ExportSample.IsEnabled      = sample && !_vm.SampleIsHeaderOnly;
        MNU_ExportMultisample.IsEnabled = multisample;
        MNU_ExportCollection.IsEnabled  = collection;
        MNU_NormalizationReport.IsEnabled = _vm.Roots.Count > 0;
    }

    void OnEditMenuOpened(object sender, RoutedEventArgs e)
    {
        // A header-only .KSF has no PCM at all, so every waveform edit below is a no-op on
        // it - the same predicate the transport buttons already use.
        bool audio        = _vm.HasSampleLoaded && !_vm.SampleIsHeaderOnly;
        bool hasSelection = audio && _vm.SelectionEndFrame > _vm.SelectionStartFrame;

        MNU_Undo.IsEnabled  = _vm.CanUndo;
        MNU_Redo.IsEnabled  = _vm.CanRedo;

        MNU_Cut.IsEnabled       = hasSelection;
        MNU_Copy.IsEnabled      = hasSelection;
        MNU_Paste.IsEnabled     = audio && SampleClipboard.HasContent;
        MNU_SelectAll.IsEnabled = audio;

        MNU_Reverse.IsEnabled          = audio;
        MNU_SilenceSelection.IsEnabled = hasSelection;
        MNU_InsertSilence.IsEnabled    = audio;
        MNU_RemoveDcOffset.IsEnabled   = audio;
        MNU_Gain.IsEnabled             = audio;
        MNU_Zoom.IsEnabled             = audio;

        MNU_DeleteZone.IsEnabled        = _vm.HasZoneSelected;
        MNU_RenameMultisample.IsEnabled = _vm.CurrentMultisampleName != null;
        MNU_RenameSample.IsEnabled      = _vm.HasSampleLoaded;
        MNU_RevertKsc.IsEnabled         = _vm.HasActiveCollection;
        MNU_RevertAll.IsEnabled         = _vm.Roots.Count > 0;
    }


    // Filename and a dirty marker in the title bar, the convention every editor uses -
    // this window previously showed one constant string, so neither "which file am I in"
    // nor "do I have unsaved work" was answerable without reading the status bar.
    void UpdateWindowTitle()
    {
        var name = _vm.ActiveCollectionPath is { } ksc ? System.IO.Path.GetFileName(ksc) : null;
        Title = (_vm.HasUnsavedChanges ? "*" : "")
            + (name == null ? "Sample Editor - Kronos" : $"{name} - Sample Editor - Kronos");
        BtnSaveChanges.IsEnabled = _vm.HasUnsavedChanges;
        MNU_SaveChanges.IsEnabled = _vm.HasUnsavedChanges;
    }

    // ── Waveform editing ─────────────────────────────────────────────────────────

    // Combine mode shows the SAME selection on both panes (a single logical stereo
    // view - see RefreshDetailPanels). Split mode shows it on the ACTIVE channel(s) only -
    // a drag on either pane activates that pane's channel first (ActivateSplitChannel),
    // UNLESS the drag crosses into the other pane, which activates BOTH instead
    // (_splitDragCrossedIntoOtherPane, tracked below).
    void OnWaveformLeftSelectionChanged() => OnWaveformPaneSelectionChanged(WaveformLeft);
    void OnWaveformRightSelectionChanged() => OnWaveformPaneSelectionChanged(WaveformRight);

    void OnWaveformPaneSelectionChanged(SampleWaveformControl pane)
    {
        // Read the pane's just-committed selection BEFORE activating - ActivateSplitChannel
        // (when it actually switches channel) routes through SelectNode, which zeroes
        // SelectionStartFrame/EndFrame and calls RefreshDetailPanels, which then writes
        // that zero straight back into both panes' own DPs. Reading pane.SelectionStartFrame/
        // EndFrame AFTER that call would see the just-cleared 0/0, silently discarding the
        // selection this handler exists to commit.
        int newStart = pane.SelectionStartFrame;
        int newEnd = pane.SelectionEndFrame;

        // A selection drawn (or Move-tool-relocated) on the INACTIVE pane in Split's
        // dual view must activate that channel first - otherwise the selection just
        // drawn on, say, the R pane would silently become the L channel's own
        // SelectionStartFrame/EndFrame (Crop/Fade/etc. only ever act on the active
        // channel), cropping the wrong audio. A no-op in Combine (ActivateSplitChannel
        // itself gates on SplitLR) and when the pane clicked is already active. Except:
        // if the drag crossed into the OTHER pane at some point, explicit request is
        // that BOTH channels become active instead of just whichever pane the mouse
        // happened to be over at release.
        if (_vm.SplitLR && _vm.HasStereoPair && _splitDragCrossedIntoOtherPane)
            ActivateBothSplitChannels();
        else
            ActivateSplitChannel(ReferenceEquals(pane, WaveformLeft));
        _vm.SelectionStartFrame = newStart;
        _vm.SelectionEndFrame = newEnd;
        RefreshDetailPanels();

        // MirrorSelectionPreview (below) mirrors the LIVE drag highlight onto the
        // sibling pane via SetPreviewSelection, which bypasses SelectionStartFrame/
        // EndFrame entirely - committing here never implicitly clears it. In Split L/R
        // with only ONE channel active, RefreshDetailPanels above always writes 0/0 onto
        // the INACTIVE pane's real selection, which is a no-op (and so never fires the
        // DP's own ClearPreviewSelection callback - see SelectionStartFrameProperty)
        // whenever it was already 0/0, the common case. Without this, the sibling's
        // preview rectangle from the drag that just ended would stay stuck on screen.
        (ReferenceEquals(pane, WaveformLeft) ? WaveformRight : WaveformLeft).ClearPreviewSelection();
    }

    // Whether the drag currently in progress has ever crossed from its own pane into the
    // sibling's - reset on every mouse-down (OnWaveformAreaPreviewMouseDown) and latched
    // true for the rest of that one gesture by OnWaveformAreaPreviewMouseMove. Read by
    // OnWaveformPaneSelectionChanged (commit) and MirrorSelectionPreview (live) so a
    // Split-mode drag highlights only the pane it's actually on UNTIL it crosses, per
    // explicit correction - the previous behavior (always mirroring the preview to both
    // panes regardless) had it backwards.
    bool _splitDragCrossedIntoOtherPane;

    // Hooked as Preview (tunneling) handlers on the Grid wrapping both waveform panes
    // (XAML) rather than on the panes themselves - mouse CAPTURE during a drag routes
    // ordinary Mouse* events only to the captured element, so a plain MouseMove handler
    // on the sibling pane would never fire while the OTHER pane holds capture. Preview
    // events still tunnel down to (and past) whichever element is captured regardless,
    // and Mouse.Captured/e.GetPosition don't care about capture at all - GetPosition is
    // a pure coordinate transform, valid for any visual regardless of who's capturing.
    void OnWaveformAreaPreviewMouseDown(object sender, MouseButtonEventArgs e) => _splitDragCrossedIntoOtherPane = false;

    void OnWaveformAreaPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_splitDragCrossedIntoOtherPane) return; // latched - no need to keep checking
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!_vm.HasStereoPair || !_vm.SplitLR) return;
        var capturedPane = Mouse.Captured switch
        {
            var c when ReferenceEquals(c, WaveformLeft) => WaveformRight,
            var c when ReferenceEquals(c, WaveformRight) => WaveformLeft,
            _ => null,
        };
        if (capturedPane == null) return;
        double y = e.GetPosition(capturedPane).Y;
        if (y >= 0 && y <= capturedPane.ActualHeight) _splitDragCrossedIntoOtherPane = true;
    }

    // Live mirror of the crop-selection highlight onto the sibling stereo pane WHILE
    // dragging, not just once at mouse-up (OnWaveformPaneSelectionChanged above) - goes
    // through the source/other panes' own preview-only state (SetPreviewSelection),
    // not their committed DPs, so it stays cheap enough to run on every MouseMove
    // without writing either pane's real selection until the drag actually commits.
    // Split L/R only shows the highlight on the pane actually being dragged UNTIL the
    // drag crosses into the sibling pane (_splitDragCrossedIntoOtherPane) - explicit
    // correction: mirroring unconditionally from the start of every Split-mode drag had
    // it backwards (both highlighted even when the mouse never left the origin pane).
    // Combine mode is unaffected - always mirrors, no crossing concept there.
    void OnWaveformLeftSelectionPreview() => MirrorSelectionPreview(WaveformLeft, WaveformRight);
    void OnWaveformRightSelectionPreview() => MirrorSelectionPreview(WaveformRight, WaveformLeft);

    void MirrorSelectionPreview(SampleWaveformControl source, SampleWaveformControl other)
    {
        if (!_vm.HasStereoPair) return;
        if (_vm.SplitLR && !_splitDragCrossedIntoOtherPane) { other.ClearPreviewSelection(); return; }
        other.SetPreviewSelection(source.EffectiveSelectionStart, source.EffectiveSelectionEnd);
    }

    // Live mirror of a Sample Start/Loop Start/Loop End marker drag onto the sibling
    // stereo pane WHILE dragging - previously the two panes only caught back up once
    // the drag ended (SetMarker's own mirroring, on mouse-up), so in stereo Combine
    // mode only the pane actually being dragged visibly moved until you let go.
    void OnWaveformLeftMarkersChanging() => MirrorMarkersPreview(WaveformLeft, WaveformRight);
    void OnWaveformRightMarkersChanging() => MirrorMarkersPreview(WaveformRight, WaveformLeft);

    void MirrorMarkersPreview(SampleWaveformControl source, SampleWaveformControl other)
    {
        if (!_vm.HasStereoPair || _vm.SplitLR) return;
        other.SampleStartFrame = source.SampleStartFrame;
        other.LoopStartFrame = source.LoopStartFrame;
        other.LoopEndFrame = source.LoopEndFrame;
    }

    bool _syncingWaveformViews;

    void OnWaveformLeftViewChanged() => SyncWaveformViews(WaveformLeft);
    void OnWaveformRightViewChanged() => SyncWaveformViews(WaveformRight);

    // Keeps the ruler footer, horizontal scrollbar, AND the other stereo pane all
    // showing exactly the same time window as whichever pane just zoomed/panned
    // (wheel zoom, double-click reset, or a scrollbar drag via SetView below) - a
    // reentrancy guard since mirroring into the other pane fires ITS OWN ViewChanged,
    // which would otherwise call back into here for that pane too.
    void SyncWaveformViews(SampleWaveformControl source)
    {
        if (_syncingWaveformViews) return;
        _syncingWaveformViews = true;
        try
        {
            if (_vm.HasStereoPair)
            {
                var other = ReferenceEquals(source, WaveformLeft) ? WaveformRight : WaveformLeft;
                other.SetView(source.ViewStartFrame, source.ViewEndFrame);
            }

            WaveformRuler.SampleRate = _vm.SampleRate;
            WaveformRuler.ViewStartFrame = source.ViewStartFrame;
            WaveformRuler.ViewEndFrame = source.ViewEndFrame;

            // ViewSpanFrameCount, not Samples.Length - once Split's Move tool leaves the
            // two channels different lengths, the scrollbar's range must reach the end of
            // the LONGER one on both panes, not just whichever pane raised this event.
            int frameCount = source.ViewSpanFrameCount;
            int viewLen = Math.Max(1, source.ViewEndFrame - source.ViewStartFrame);
            WaveformHScroll.Minimum = 0;
            WaveformHScroll.Maximum = Math.Max(0, frameCount - viewLen);
            WaveformHScroll.ViewportSize = viewLen;
            WaveformHScroll.Value = source.ViewStartFrame;
            // Collapsed (not just disabled) until the user actually zooms in - same
            // "Collapsed child" treatment WaveformDividerRow/WaveformRightRowDef already
            // use for the mono/stereo row, for the same reason: a merely-disabled-but-
            // still-visible scrollbar sat there doing nothing at full view, permanently
            // occupying its row's space and (per user report) visually overlapping the
            // ruler's text right above it. Unlike those two, this row is Height="Auto"
            // (XAML) rather than a hand-picked pixel value toggled from here - a fixed
            // pixel height (14, previously) was slightly SHORTER than the ScrollBar's own
            // real rendered height, clipping its bottom edge; Auto measures to whatever
            // the ScrollBar (Visible) or nothing (Collapsed) actually needs, so it can
            // never clip again regardless of theme/DPI. The ScrollBar's own top Margin
            // (XAML) is the fixed gap between it and the ruler above.
            WaveformHScroll.Visibility = frameCount > viewLen ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingWaveformViews = false; }
    }

    // ScrollBar.Scroll (not ValueChanged) fires only from user interaction - dragging
    // the thumb or clicking the track/arrows - never from the programmatic Value sets
    // in SyncWaveformViews above, so this can't loop back into itself.
    void OnWaveformHScroll(object sender, ScrollEventArgs e)
    {
        int viewLen = Math.Max(1, WaveformLeft.ViewEndFrame - WaveformLeft.ViewStartFrame);
        WaveformLeft.SetView((int)e.NewValue, (int)e.NewValue + viewLen); // its own ViewChanged mirrors to WaveformRight
    }

    // Drag-moving or arrow-nudging the loop region - mirrors ApplyEffect's own "which
    // pane fired this" indifference: the loop always belongs to whichever sample the
    // pane that raised this event is currently showing (primary or partner), same
    // resolution OnWaveformPaneSelectionChanged uses. Only WaveformLeft is wired to
    // this in XAML (loop editing only shows markers on the primary pane - see
    // RefreshDetailPanels's own comment), so this always targets the VM's primary
    // MoveLoopRegion, which itself mirrors to the stereo partner in Combine mode.
    void OnWaveformLoopRegionChanged(int newStart, int newEnd)
    {
        _vm.MoveLoopRegion(newStart, newEnd);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnVolumeChanged(double volume) => _vm.Volume = (float)volume;

    void OnPanChanged(int pan) => _vm.Pan = pan;

    void UpdateVuMeter()
    {
        VuMeterLeft.Level = _vm.GetPlaybackLevelLeft();
        VuMeterRight.Level = _vm.GetPlaybackLevelRight();
        int frame = _vm.IsPlaying && _vm.PlaybackMatchesSelection ? _vm.GetPlaybackFrame() : -1;
        WaveformLeft.PlayheadFrame = frame;
        WaveformRight.PlayheadFrame = frame;
        if (frame >= 0) FollowPlayhead(frame);
    }

    // Auto-scroll so the playhead stays visible while zoomed in. Without this, playing a
    // zoomed-in view showed the line for a fraction of a second and then nothing at all
    // for the rest of the sample - the standard behaviour of every waveform editor, and
    // the whole point of watching playback against the trace.
    //
    // Pages by a whole view-width rather than re-centring every tick: continuous
    // recentring makes the trace scroll under a fixed line, which is far harder to read
    // (and repaints the whole waveform 25x/sec). Only acts when actually zoomed in.
    void FollowPlayhead(int frame)
    {
        int viewStart = WaveformLeft.ViewStartFrame, viewEnd = WaveformLeft.ViewEndFrame;
        int viewLen = viewEnd - viewStart;
        if (viewLen <= 0 || viewLen >= (WaveformLeft.Samples?.Length ?? 0)) return; // not zoomed in
        if (frame >= viewStart && frame < viewEnd) return;

        int newStart = Math.Max(0, frame - viewLen / 8); // a little lead-in, not hard against the edge
        WaveformLeft.SetView(newStart, newStart + viewLen); // ViewChanged mirrors to the ruler/scrollbar/right pane
    }

    // ── Zoom ────────────────────────────────────────────────────────────────────

    // All four route through SampleWaveformControl.SetView (which clamps for itself) on
    // the LEFT pane only - its ViewChanged already mirrors onto the ruler, the scrollbar
    // and the sibling stereo pane, exactly as the horizontal scrollbar's own handler
    // relies on. Nothing here needs to know about stereo.
    void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(0.5);
    void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(2.0);

    void ZoomBy(double factor)
    {
        int total = WaveformLeft.Samples?.Length ?? 0;
        if (total == 0) return;
        int viewLen = Math.Max(1, WaveformLeft.ViewEndFrame - WaveformLeft.ViewStartFrame);
        int centre = WaveformLeft.ViewStartFrame + viewLen / 2;
        int newLen = Math.Clamp((int)(viewLen * factor), 1, total);
        WaveformLeft.SetView(centre - newLen / 2, centre - newLen / 2 + newLen);
        RefreshDetailPanels();
    }

    void OnZoomToSelection(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectionEndFrame <= _vm.SelectionStartFrame)
        { StatusBar.Text = "Select a range in the waveform first."; return; }
        WaveformLeft.SetView(_vm.SelectionStartFrame, _vm.SelectionEndFrame);
    }

    void OnZoomFit(object sender, RoutedEventArgs e) =>
        WaveformLeft.SetView(0, Math.Max(1, WaveformLeft.Samples?.Length ?? 1));

    // ── Selection / additional edit operations ──────────────────────────────────

    void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSampleLoaded) return;
        // RefreshDetailPanels (via AfterMutation) mirrors onto both stereo panes, same as any
        // other selection change.
        AfterMutation(() =>
        {
            _vm.SelectionStartFrame = 0;
            _vm.SelectionEndFrame = _vm.SampleFrameCount;
        });
    }

    void OnReverse(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplyReverse);
    void OnSilenceSelection(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplySilenceSelection);
    void OnRemoveDcOffset(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplyDcOffsetRemoval);

    // Frames is what actually gets applied (InsertSilenceEffect, matching every other
    // position field in this window); the dialog's Seconds box is a linked, live-updating
    // convenience that computes frames rather than a value this method ever sees
    // directly - see InsertSilenceDialog. Seeded with a quarter-second at the sample's
    // own rate so the common case is one Enter press.
    void OnInsertSilence(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSampleLoaded) { StatusBar.Text = "No sample loaded."; return; }
        int suggested = Math.Max(1, _vm.SampleRate / 4);
        var dlg = new InsertSilenceDialog(_vm.SampleRate, suggested, _vm.HasStereoPair) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _vm.ApplyInsertSilence(dlg.Frames, dlg.ApplyToLeft, dlg.ApplyToRight);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // The waveform's right-click menu only offers the +/-1, 3 and 6 dB presets; this is
    // the arbitrary-amount path. Parsed with InvariantCulture for the same reason the
    // Tempo/Pitch fields are - see OnTempoPitch.
    void OnGainDialog(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSampleLoaded) { StatusBar.Text = "No sample loaded."; return; }
        var dlg = new PromptDialog("Gain change in dB (negative to attenuate):", "0") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!double.TryParse(dlg.Result, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
        { StatusBar.Text = "That isn't a number of decibels."; return; }
        if (db == 0) { StatusBar.Text = "0 dB - nothing to apply."; return; }

        _vm.ApplyGainAdjust(db);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // ── Drag and drop ───────────────────────────────────────────────────────────

    // Dropping a .KSC/.KMP opens it; dropping audio goes through the same key prompts
    // Import Audio uses. Only file drops are accepted, and the cursor says so before the
    // button is released rather than after.
    static readonly string[] DroppableAudioExtensions = [".wav", ".mp3", ".mp4", ".m4a", ".wma"];

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Handled = true;

        // One file per drop: opening several collections at once is fine, but importing
        // several audio files would need one key-range prompt pair EACH, which is a
        // worse experience than dropping them one at a time.
        var path = paths[0];
        var ext = System.IO.Path.GetExtension(path);
        if (string.Equals(ext, ".KSC", StringComparison.OrdinalIgnoreCase)) OpenCollectionPath(path);
        else if (string.Equals(ext, ".KMP", StringComparison.OrdinalIgnoreCase)) OpenKmpPath(path);
        else if (DroppableAudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            var (origKey, topKey) = PromptForZoneKeys();
            if (origKey < 0) return; // either prompt cancelled
            _vm.ImportAudioAsNewZone(path, origKey, topKey);
            RefreshDetailPanels();
            UpdateStatus();
        }
        else StatusBar.Text = $"Don't know what to do with '{System.IO.Path.GetFileName(path)}' - drop a .KSC, .KMP, or an audio file.";
    }

    // The Original Key / Top Key prompt pair, previously repeated verbatim in three
    // import handlers and now needed by drag-drop as a fourth. Returns (-1, -1) when
    // either prompt is cancelled.
    (int OrigKey, int TopKey) PromptForZoneKeys()
    {
        var origDlg = new PromptDialog("Original key (note name, e.g. C4):", "C4") { Owner = this };
        if (origDlg.ShowDialog() != true) return (-1, -1);
        int origKey = MidiNoteName.TryParse(origDlg.Result) ?? 60;

        var topDlg = new PromptDialog("Top key (note name, e.g. C4):", MidiNoteName.ToName(origKey)) { Owner = this };
        if (topDlg.ShowDialog() != true) return (-1, -1);
        return (origKey, MidiNoteName.TryParse(topDlg.Result) ?? origKey);
    }

    // Clicking a band in the keymap selects the same zone in the tree - finds the
    // matching tree node by reference (CurrentMultisampleZones holds the SAME KmpZone
    // instances currently in the tree) and drives selection through the real
    // TreeViewItem container so OnTreeSelectionChanged fires normally, rather than
    // calling _vm.SelectNode directly and risking the tree's own visual selection
    // drifting out of sync with the ViewModel's.
    void OnKeymapZoneClicked(KmpZone zone)
    {
        var target = FindNodeForZone(_vm.Roots, zone);
        if (target != null) SelectTreeNode(target);
    }

    // Piano-key trigger (item 8): plays the clicked zone's own sample the way it would
    // actually sound at that key, pitch-shifted from its Original Key - see
    // SampleEditorViewModel.PlayZoneAtKey/SamplePlayback.PlayAtKey for the tape-style
    // speed-change mechanism. Deliberately does NOT change tree/zone selection (unlike
    // OnKeymapZoneClicked above) - a click meant purely as an audition trigger shouldn't
    // also yank the editor panel over to a different zone.
    void OnKeymapPianoKeyClicked(KmpZone zone, int key)
    {
        _vm.PlayZoneAtKey(zone, key);
        RefreshDetailPanels();
    }

    // Mouse-up/lost-capture counterpart to the click above - "play only while the key
    // is held" (explicit request), including loops. See
    // SampleEditorViewModel.ReleasePianoKey's own comment for why this is safe to call
    // unconditionally: it's a no-op if the transport Play button (or anything else) has
    // already taken over the single playback slot since the key was pressed.
    void OnKeymapPianoKeyReleased()
    {
        _vm.ReleasePianoKey();
    }

    // Ctrl+Click assignment: click into Orig.Key or Top Key first (giving it focus),
    // then Ctrl+Click a key on the piano to write that key's note name into whichever
    // field is still focused - SampleKeymapControl checks/fires this BEFORE it takes
    // its own Focus(), so the field clicked in step one is still the live focused
    // element here. Reuses the exact same commit path (OnZoneOrigKeyBoxChanged/
    // OnZoneTopKeyBoxChanged) the typed field itself already uses, so ApplyZoneEdits'
    // floor/cascade rules apply identically either way. Neither field focused (Ctrl+
    // Click without clicking a field first) is a silent no-op, not an error.
    void OnKeymapPianoKeyCtrlClicked(int key)
    {
        if (ZoneOrigKeyBox.IsKeyboardFocusWithin)
        {
            ZoneOrigKeyBox.Text = MidiNoteName.ToName(key);
            OnZoneOrigKeyBoxChanged(ZoneOrigKeyBox, new RoutedEventArgs());
        }
        else if (ZoneTopKeyBox.IsKeyboardFocusWithin)
        {
            ZoneTopKeyBox.Text = MidiNoteName.ToName(key);
            OnZoneTopKeyBoxChanged(ZoneTopKeyBox, new RoutedEventArgs());
        }
    }

    void OnKeymapBoundaryMoved(KmpZone zone, int newTopKey)
    {
        _vm.MoveZoneBoundary(zone, newTopKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnKeymapZoneReordered(KmpZone dragged, KmpZone dropTarget)
    {
        _vm.ReorderZone(dragged, dropTarget);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Select/Move tool toggle - two independent ToggleButtons (not RadioButtons, see the
    // XAML's own comment), kept mutually exclusive by hand. Wired to Click, not
    // Checked: a ToggleButton flips its own IsChecked BEFORE Click fires, so clicking
    // whichever tool is already active would otherwise just uncheck it - Checked never
    // fires again to put it back, leaving NEITHER button checked and IsMoveToolActive
    // stale. Forcing both buttons' IsChecked (and IsMoveToolActive) unconditionally
    // here, on every click regardless of which one was clicked or what it toggled
    // itself to, makes "exactly one of the two is always active" hold no matter what
    // the ToggleButton's own state machine just did.
    void OnSelectToolClick(object sender, RoutedEventArgs e)
    {
        BtnSelectTool.IsChecked = true;
        BtnMoveTool.IsChecked = false;
        _vm.IsMoveToolActive = false;
        RefreshDetailPanels();
    }

    void OnMoveToolClick(object sender, RoutedEventArgs e)
    {
        BtnMoveTool.IsChecked = true;
        BtnSelectTool.IsChecked = false;
        _vm.IsMoveToolActive = true;
        RefreshDetailPanels();
    }

    void OnSplitLRChanged(object sender, RoutedEventArgs e)
    {
        _vm.SplitLR = SplitLRBox.IsChecked == true;
        _vm.SplitBothActive = false; // stale either direction - re-entering Split starts single-active, and Combine has no such state at all
        RefreshDetailPanels(); // without this, the pane layout only caught up on the NEXT
                                // click/selection - toggling the checkbox itself had no
                                // visible effect until some unrelated action refreshed it
        UpdateStatus();
    }

    // Split's only way to make a channel the "active" one for markers/text-fields/
    // Crop-Fade-etc now that the L/R dropdown is gone (both panes are always visible, so
    // picking a channel by clicking directly on its own pane is the whole affordance) -
    // called from a plain click on either waveform pane (OnWaveformPaneScrubRequested)
    // and from drawing a selection on one (OnWaveformPaneSelectionChanged). Routes
    // through the same real TreeViewItem selection the MS/Sample dropdowns use (not a
    // direct field write), which IS what resets undo/RefreshDetailPanels/etc - a known
    // cost (SelectNode resets _sampleUndo on any scope change), not a bug. A no-op if the
    // requested channel is already the one active.
    void ActivateSplitChannel(bool wantLeft)
    {
        if (!_vm.HasStereoPair || !_vm.SplitLR) return;
        // A plain click always means "just this one channel" - explicit request - so
        // this drops Both even when wantLeft already matches the active side (a no-op
        // single-side re-click shouldn't leave Both active behind it).
        _vm.SplitBothActive = false;
        if (wantLeft == _vm.IsPrimaryLeftChannel) return;
        if (_vm.PartnerZoneRef is not { } partner) return;
        var target = FindNodeForZone(_vm.Roots, partner.Zone);
        if (target != null) SelectTreeNode(target);
    }

    // Both channels at once, while staying in the Split dual-pane view - reached by
    // double-clicking the pane that's already the sole active channel
    // (OnWaveformPaneChannelDoubleClicked) or by a selection drag that crosses into the
    // other pane (OnWaveformPaneSelectionChanged). Doesn't need to change which zone is
    // tree-selected/"primary" - both _selectedSample and _partnerSample are already
    // resolved, ShouldMirrorToPartner picks them both up from here on.
    void ActivateBothSplitChannels()
    {
        if (!_vm.HasStereoPair || !_vm.SplitLR) return;
        _vm.SplitBothActive = true;
    }

    // Double-click on a Split-mode pane, outside the loop region (that still takes
    // priority - see SampleWaveformControl.OnMouseLeftButtonDown's own loop-select
    // branch). By the time this fires, click 1 of the double-click has ALREADY run
    // through OnWaveformPaneScrubRequested -> ActivateSplitChannel for this exact pane
    // (a double-click's first mouse-down/up cycle is a real, ordinary single click as
    // far as WPF/this window are concerned) - so this pane is already guaranteed to be
    // the sole active channel by construction, and the only meaningful action left for
    // the second click is promoting to Both.
    void OnWaveformLeftChannelDoubleClicked() => OnWaveformPaneChannelDoubleClicked();
    void OnWaveformRightChannelDoubleClicked() => OnWaveformPaneChannelDoubleClicked();

    void OnWaveformPaneChannelDoubleClicked()
    {
        ActivateBothSplitChannels();
        RefreshDetailPanels();
        UpdateStatus();
    }

    static SampleTreeNode? FindNodeForZone(IEnumerable<SampleTreeNode> nodes, KmpZone zone)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.ZoneRef?.Zone, zone)) return node;
            var found = FindNodeForZone(node.Children, zone);
            if (found != null) return found;
        }
        return null;
    }

    // The tree only shows root (loaded-library) nodes now - selecting a zone/
    // multisample deep in the actual data (MS dropdown, Sample dropdown, keymap click,
    // Split L/R channel picker, Add Zone's own reselect) has nothing to expand/reveal in
    // the tree any more; it just drives the ViewModel + detail panels directly, and
    // makes sure whichever library the target belongs to is the one highlighted in the
    // tree, matching whatever SelectNode just made "active" for collection-scoped menu
    // actions (Export Collection, Unload, ...). The IsSelected write is wrapped in
    // _suppressTreeSelectionEvent because it would otherwise re-enter
    // OnTreeSelectionChanged and call _vm.SelectNode(root) right after this method's own
    // _vm.SelectNode(target) - clobbering the real (zone/multisample) selection with the
    // root the instant a genuinely different library needs highlighting.
    void SelectTreeNode(SampleTreeNode target)
    {
        var path = new List<SampleTreeNode>();
        if (BuildPath(_vm.Roots, target, path) && path.Count > 0
            && SampleTree.ItemContainerGenerator.ContainerFromItem(path[0]) is TreeViewItem container)
        {
            _suppressTreeSelectionEvent = true;
            container.IsSelected = true;
            _suppressTreeSelectionEvent = false;
        }

        _vm.SelectNode(target);
        RefreshDetailPanels();
        UpdateStatus();

        // Selecting a multisample directly drops straight into its first zone, so the
        // Index/Sample/Orig.Key/Top Key panel is available right away rather than
        // needing an extra step - matches the Kronos's own behavior (picking a
        // multisample shows zone 1).
        if (target.MultisampleRef != null && target.Children.Count > 0)
            SelectTreeNode(target.Children[0]);
    }

    static bool BuildPath(IEnumerable<SampleTreeNode> nodes, SampleTreeNode target, List<SampleTreeNode> path)
    {
        foreach (var node in nodes)
        {
            path.Add(node);
            if (ReferenceEquals(node, target)) return true;
            if (BuildPath(node.Children, target, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    // ── Unload / Revert ──────────────────────────────────────────────────────────

    // Right-clicking a TreeViewItem doesn't select it by default in WPF (unlike a left-
    // click) - without this, ContextMenuOpening below would see whatever was LAST
    // left-clicked, not the node actually under the cursor. Walks up from the raw hit
    // (which is usually some child TextBlock/Border inside the item's template, not the
    // TreeViewItem itself) to find its containing TreeViewItem.
    void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualUpwardSearch<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    static T? VisualUpwardSearch<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source is not T) source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    // Builds the tree's right-click menu on the fly rather than a static XAML
    // ContextMenu resource - "Close Collection" only makes sense once we know which
    // collection the right-clicked node actually belongs to (resolved the same way
    // SelectNode resolves "the active collection" for the collection-level menu
    // actions). The other items are the same File-menu actions already reachable from
    // the top menu bar/toolbar - the tree only ever shows collection ROOTS (see the
    // TreeView's own XAML comment), so "New"/"Open" here unambiguously mean the
    // collection-level versions, not "new multisample" or "open a bare .KMP".
    void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        SampleTree.ContextMenu = null;
        if (SampleTree.SelectedItem is not SampleTreeNode node) { e.Handled = true; return; }
        var owningPath = _vm.FindOwningCollectionPath(node);
        if (owningPath == null) { e.Handled = true; return; }

        var menu = new ContextMenu();
        void Add(string header, RoutedEventHandler click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += click;
            menu.Items.Add(item);
        }

        Add("New Collection (.KSC)...", OnNewCollection);
        Add("Open Collection (.KSC)...", OnOpenCollection);
        menu.Items.Add(new Separator());
        Add("Save Changes", OnSaveChanges, _vm.HasUnsavedChanges);
        Add("Save as...", (_, _) => OnSaveCollectionAs(owningPath));
        Add("Push to Kronos...", OnPushCollectionToKronos);
        menu.Items.Add(new Separator());
        Add("Close Collection", (_, _) => UnloadCollectionWithConfirm(owningPath));
        Add("Close Editor", (_, _) => Close());
        SampleTree.ContextMenu = menu;
    }

    void OnUnloadActiveCollection(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveCollectionPath is not { } path) { StatusBar.Text = "No collection is open to unload."; return; }
        UnloadCollectionWithConfirm(path);
    }

    // Session-only (nothing on disk is touched - the collection can always be re-opened),
    // so this doesn't need the same weight as the Closing-window guard, but a confirm is
    // still worth it whenever there COULD be unsaved edits in play this session (the
    // dirty tracking is session-wide, not per-collection - see HasUnsavedChanges's own
    // comment - so this can't tell whether THIS specific collection is the dirty one and
    // errs toward asking, same trade-off that comment documents).
    void UnloadCollectionWithConfirm(string kscPath)
    {
        if (_vm.HasUnsavedChanges)
        {
            var result = MessageBox.Show(this,
                $"There may be unsaved changes this session. Unload '{System.IO.Path.GetFileName(kscPath)}' anyway?",
                "Unload Collection", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        _vm.UnloadCollection(kscPath);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnRevertKscChanges(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveCollectionPath is not { } path) { StatusBar.Text = "No collection is open to revert."; return; }
        var result = MessageBox.Show(this,
            $"Discard all unsaved changes in '{System.IO.Path.GetFileName(path)}' and reload it from disk?",
            "Revert KSC Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        _vm.RevertActiveCollectionChanges();
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnRevertAllChanges(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Close every open collection/multisample and start fresh, discarding any unsaved changes? "
            + "Nothing already saved to disk is affected.",
            "Revert ALL Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        _vm.RevertAllChanges();
        RefreshDetailPanels();
        UpdateStatus();
    }

    // ── Waveform right-click context menu ────────────────────────────────────────

    // Shared between WaveformLeft and WaveformRight (same ContextMenu resource) - all
    // its actions target the primary (tree-selected) sample regardless of which pane
    // was actually right-clicked, same as every other toolbar action; `sender` isn't
    // needed here beyond being the element the ContextMenu is attached to.
    //
    // The menu always opens (right-click must still reach Paste with no selection) -
    // every item that only makes sense against a highlighted range is individually
    // disabled here instead, refreshed on every open rather than bound, matching this
    // window's existing "code-behind reads VM state" style for this menu. Paste (by
    // clipboard content) and Undo/Redo (by undo/redo history) are the exceptions - see
    // WaveformContextMenu's own XAML comment for why each item is grouped the way it is.
    void OnWaveformContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu }) return;
        bool hasSelection = _vm.SelectionEndFrame > _vm.SelectionStartFrame;

        MenuItem? ByHeader(string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == header);

        foreach (var header in new[] { "Cu_t", "_Copy", "Cro_p to Selection", "Fade _In", "Fade _Out", "_Silence", "_Loop Selected Area", "_Normalize", "Amplify", "Soften" })
            if (ByHeader(header) is { } item) item.IsEnabled = hasSelection;

        if (ByHeader("_Paste") is { } pasteItem) pasteItem.IsEnabled = SampleClipboard.HasContent;
        if (ByHeader("_Undo") is { } undoItem) undoItem.IsEnabled = _vm.CanUndo;
        if (ByHeader("_Redo") is { } redoItem) redoItem.IsEnabled = _vm.CanRedo;
    }

    void OnWaveformCut(object sender, RoutedEventArgs e) => AfterMutation(_vm.CutSelection);
    void OnWaveformCopy(object sender, RoutedEventArgs e) { _vm.CopySelection(); UpdateStatus(); }
    void OnWaveformPaste(object sender, RoutedEventArgs e) => AfterMutation(_vm.PasteAtSelection);
    void OnWaveformFadeIn(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplyFadeInSelection);
    void OnWaveformFadeOut(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplyFadeOutSelection);
    void OnLoopSelectedArea(object sender, RoutedEventArgs e) => AfterMutation(_vm.SetLoopFromSelection);

    void OnWaveformAmplify(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tagStr } || !double.TryParse(tagStr, out var db)) return;
        AfterMutation(() => _vm.ApplyGainAdjust(db));
    }

    // BtnAmplify/BtnSoften are plain Buttons carrying their preset list as their own
    // ContextMenu (+1/+3/+6 dB and -1/-3/-6 dB respectively) rather than being
    // ToggleButtons/ComboBoxes - a Button's own Click doesn't open its ContextMenu on
    // its own, so this opens it explicitly, anchored under the button that was
    // clicked. OnWaveformAmplify (unchanged) still handles the actual preset clicks.
    void OnAmplifyDropdownClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button; // Placement="Bottom" is already set in XAML
        menu.IsOpen = true;
    }

    // TempoBox/PitchBox aren't VM-backed (see OnTempoPitch's own comment) - Undo/Redo
    // correctly reverts the PCM either applied (self-test-verified: tempo change is one
    // of the edits round-tripped bit-exact in --sample-editor-smoketest), but the boxes
    // themselves never get told, so they kept showing whatever was last typed/applied -
    // "2.0"/"12" - even after undoing that exact change back out, reading as "undo
    // didn't work" even though the sample data genuinely had. Reset to neutral here so
    // the fields never claim a pending multiplier/transpose that isn't real.
    void OnUndo(object sender, RoutedEventArgs e) => AfterMutation(() => { _vm.Undo(); ResetTempoPitchBoxes(); });
    void OnRedo(object sender, RoutedEventArgs e) => AfterMutation(() => { _vm.Redo(); ResetTempoPitchBoxes(); });

    void ResetTempoPitchBoxes()
    {
        TempoBox.Text = "1.0";
        PitchBox.Text = "0";
    }

    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl-chords stay live even with focus in a text box: the FieldBox style turns
        // the TextBox's own undo stack off precisely so Ctrl+Z means the app's Undo
        // everywhere, and Ctrl+S must work while a field still has focus - that's the
        // exact moment a user reaches for it.
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                // Delegates to the button handlers rather than duplicating their body - this
                // used to skip OnUndo/OnRedo's ResetTempoPitchBoxes() call (see their own
                // comment), so Ctrl+Z/Ctrl+Y left a stale Tempo/Pitch box reading exactly the
                // "undo didn't work" symptom that call exists to prevent, while the Undo/Redo
                // BUTTONS already avoided it.
                case Key.Z: OnUndo(sender, e); e.Handled = true; return;
                case Key.Y: OnRedo(sender, e); e.Handled = true; return;
                case Key.S: OnSaveChanges(sender, e); e.Handled = true; return;
                case Key.O: OnOpenCollection(sender, e); e.Handled = true; return;
                case Key.OemPlus or Key.Add: OnZoomIn(sender, e); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: OnZoomOut(sender, e); e.Handled = true; return;
                case Key.D0 or Key.NumPad0: OnZoomFit(sender, e); e.Handled = true; return;
            }
        }

        // Space (play/stop), Delete, the clipboard chords and Home/End only act as
        // shortcuts when focus isn't in a text box - otherwise they need to type a
        // literal space / delete a character / cut the typed text, same as they would in
        // any other editor.
        if (Keyboard.FocusedElement is TextBox) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.A: OnSelectAll(sender, e); e.Handled = true; return;
                case Key.X: OnWaveformCut(sender, e); e.Handled = true; return;
                case Key.C: OnWaveformCopy(sender, e); e.Handled = true; return;
                case Key.V: OnWaveformPaste(sender, e); e.Handled = true; return;
            }
        }

        if (e.Key == Key.Home) { OnTransportLocateStart(sender, e); e.Handled = true; }
        else if (e.Key == Key.End) { OnTransportLocateEnd(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; }
        else if (e.Key == Key.Delete)
        {
            // Delete meant "remove the highlighted waveform range" whenever one exists -
            // this was previously always routed to Delete Zone regardless of an active
            // selection, so highlighting a range and pressing Delete silently discarded
            // the WHOLE sample instead (a real, undo-covered-only-by-luck data-loss bug:
            // CutSelection records undo, but the user had no way to reach it since
            // Delete Zone fired instead). Only falls back to Delete Zone when there's
            // nothing selected to cut.
            if (_vm.HasSampleLoaded && _vm.SelectionEndFrame > _vm.SelectionStartFrame)
                OnWaveformCut(sender, e);
            else
                OnDeleteZone(sender, e);
            e.Handled = true;
        }
    }

    void TogglePlayback() => AfterMutation(() =>
    {
        if (_vm.IsPlaying) _vm.StopPlayback();
        else _vm.PlaySelectedSample();
    });

    void OnPlayStop(object sender, RoutedEventArgs e) => TogglePlayback();

    void OnTransportLocateStart(object sender, RoutedEventArgs e) => AfterMutation(_vm.TransportLocateStart);
    void OnTransportLocateEnd(object sender, RoutedEventArgs e) => AfterMutation(_vm.TransportLocateEnd);
    void OnTransportRewind(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.TransportSeekRelative(-1));
    void OnTransportFastForward(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.TransportSeekRelative(1));
    void OnTransportPause(object sender, RoutedEventArgs e) => AfterMutation(_vm.TransportTogglePause);

    void OnDeleteZone(object sender, RoutedEventArgs e) => AfterMutation(_vm.DeleteSelectedZone);

    void OnCrop(object sender, RoutedEventArgs e) => AfterMutation(_vm.ApplyCrop);
    void OnNormalize(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.ApplyNormalize());
    void OnTrimSilence(object sender, RoutedEventArgs e) => AfterMutation(() => _vm.ApplySilenceTrim());

    // InvariantCulture, explicitly: these boxes are seeded with "1.0" and "0" from XAML,
    // so a culture-sensitive parse on a comma-decimal locale reads that literal "1.0" as
    // TEN - a 10x tempo change from a field the user never touched. The range itself is
    // enforced (and reported) by ApplyTempoPitch, not silently coerced here; only a
    // genuinely unparseable field falls back to the neutral value.
    void OnTempoPitch(object sender, RoutedEventArgs e)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;

        if (!double.TryParse(TempoBox.Text, style, invariant, out var tempo) || tempo <= 0)
        {
            StatusBar.Text = $"'{TempoBox.Text}' isn't a usable tempo multiplier - using 1.0.";
            tempo = 1.0;
        }
        if (!double.TryParse(PitchBox.Text, style, invariant, out var pitch))
        {
            StatusBar.Text = $"'{PitchBox.Text}' isn't a number of semitones - using 0.";
            pitch = 0;
        }

        _vm.ApplyTempoPitch(tempo, pitch);
        // Tempo Multiplier/Pitch are a one-shot "apply this next" value, not persisted
        // sample state (unlike Sample Start/Loop points) - resetting to neutral after
        // every apply attempt means the fields never keep claiming a change that's
        // already been folded into the PCM (or that failed to parse and fell back).
        ResetTempoPitchBoxes();
        RefreshDetailPanels();
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vuTimer.Stop();
        _vm.StopPlayback();
        base.OnClosed(e);
    }

    // ── Remote (FTP) pull/push ───────────────────────────────────────────────────

    // KronosRemoteSampleSource is built fresh per call rather than cached - it just
    // captures owner/settings/host, all of which can change between calls (host
    // switched via the main window's Bank Select menu), and it's cheap to construct.
    // Returns null (and sets a status message) if no Kronos host is configured yet -
    // LoginDialog has nowhere useful to point at without one.
    KronosRemoteSampleSource? MakeRemoteSampleSource()
    {
        var settings = Storage.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.KronosHost))
        {
            StatusBar.Text = "No Kronos host configured - set one from the main window first.";
            return null;
        }
        return new KronosRemoteSampleSource(this, settings, settings.KronosHost);
    }

    async void OnPullCollectionFromKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PullCollectionFromKronosAsync(source);
        SelectFirstRoot();
        RefreshDetailPanels();
        UpdateStatus();
    }

    async void OnPullMultisampleFromKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PullMultisampleFromKronosAsync(source);
        SelectFirstRoot();
        RefreshDetailPanels();
        UpdateStatus();
    }

    async void OnPushSampleToKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PushSelectedSampleAsync(source);
        UpdateStatus();
    }

    async void OnPushMultisampleToKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PushSelectedMultisampleAsync(source);
        UpdateStatus();
    }

    async void OnPushCollectionToKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PushCollectionToKronosAsync(source);
        UpdateStatus();
    }

    // ── Import / Export ──────────────────────────────────────────────────────────

    void OnImportAudio(object sender, RoutedEventArgs e)
    {
        var fileDlg = new OpenFileDialog
        {
            Title = "Import Audio",
            Filter = "Audio Files|*.wav;*.mp3;*.mp4;*.m4a;*.wma|WAV Files|*.wav|All Files|*.*",
        };
        if (fileDlg.ShowDialog(this) != true) return;

        var (origKey, topKey) = PromptForZoneKeys();
        if (origKey < 0) return; // either prompt cancelled

        _vm.ImportAudioAsNewZone(fileDlg.FileName, origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnImportStereoAudio(object sender, RoutedEventArgs e)
    {
        var fileDlg = new OpenFileDialog
        {
            Title = "Import Audio as Stereo Pair",
            Filter = "Audio Files|*.wav;*.mp3;*.mp4;*.m4a;*.wma|WAV Files|*.wav|All Files|*.*",
        };
        if (fileDlg.ShowDialog(this) != true) return;

        var (origKey, topKey) = PromptForZoneKeys();
        if (origKey < 0) return; // either prompt cancelled

        _vm.ImportStereoAudioAsNewZonePair(fileDlg.FileName, origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Re-selects the newly added zone (rather than leaving the tree with nothing
    // selected, which is what a rebuild-triggered SelectNode(null) - see
    // RebuildTreeFromCollection's own comment - otherwise leaves) so it: (a) shows up
    // immediately in the tree/keymap instead of the click visually "snapping" to the
    // parent multisample node with nothing usable selected, and (b) makes the new
    // zone's own boundary immediately draggable in the keymap (Zones only repaints for
    // whichever multisample is currently in context - see CurrentMultisampleZones).
    // The tree rebuild this triggers preserves each multisample's zone order, so
    // LastAddedZoneIndex (set by AddPlaceholderZone right before the rebuild) still
    // names the right position afterward - reference identity can't be used here since
    // the rebuild re-reads the .KMP from disk into brand-new KmpZone objects, and with
    // the Create Zone "Position: Left" preference the new zone isn't necessarily last
    // any more (see AddPlaceholderZone's own comment).
    void OnAddPlaceholderZone(object sender, RoutedEventArgs e)
    {
        var kmpPath = _vm.AddPlaceholderZone();
        var zoneIndex = _vm.LastAddedZoneIndex;
        RefreshDetailPanels();
        UpdateStatus();
        if (kmpPath == null) return;

        var msNode = FindMultisampleNode(_vm.Roots, kmpPath);
        if (msNode != null && zoneIndex >= 0 && zoneIndex < msNode.Children.Count) SelectTreeNode(msNode.Children[zoneIndex]);
    }

    static SampleTreeNode? FindMultisampleNode(IEnumerable<SampleTreeNode> nodes, string kmpPath)
    {
        foreach (var node in nodes)
        {
            if (node.MultisampleRef?.Path is { } p && string.Equals(p, kmpPath, StringComparison.OrdinalIgnoreCase)) return node;
            var found = FindMultisampleNode(node.Children, kmpPath);
            if (found != null) return found;
        }
        return null;
    }

    // Always populates the repository (Un-referenced Samples) too, not just the
    // selected zone - the whole point is that importing a sample should already make
    // it available to be used as an assignable sample elsewhere. Multi-
    // select: every chosen file joins the repository, the first is assigned to the
    // selected zone (the rest are pickable afterward via the Sample combo).
    void OnImportSampleIntoZone(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedZoneObject is not { } zone) { UpdateStatus(); return; }
        var fileDlg = new OpenFileDialog
        {
            Title = "Import Sample(s)",
            Filter = "Audio Files|*.wav;*.mp3;*.mp4;*.m4a;*.wma|WAV Files|*.wav|All Files|*.*",
            Multiselect = true,
        };
        if (fileDlg.ShowDialog(this) != true) return;

        var kmpPath = _vm.ImportSampleIntoZone(zone, fileDlg.FileNames);
        var zoneIndex = _vm.LastImportedZoneIndex;
        _repositorySampleCache.Clear(); // this zone's content changed and the repository just gained new entries
        RefreshDetailPanels();
        UpdateStatus();
        if (kmpPath == null) return;

        // Re-select the zone just imported into - same reasoning/pattern as
        // OnAddPlaceholderZone (the rebuild this triggers replaces every KmpZone
        // instance, so only a position, not the original `zone` reference, survives
        // it). Without this the editor appeared to reset/close after every import.
        var msNode = FindMultisampleNode(_vm.Roots, kmpPath);
        if (msNode != null && zoneIndex >= 0 && zoneIndex < msNode.Children.Count) SelectTreeNode(msNode.Children[zoneIndex]);
    }

    // Edit > Rename Multisample/Rename Sample - a plain rename of the Name field stored
    // inside the .KMP/.KSF (Suffix left alone; see SampleEditorViewModel.
    // RenameSelectedMultisample/RenameSelectedSample's own comments for why), meant to
    // give content a real name before importing it into the Kronos. A live edit like
    // every other field in this window - marks the file dirty, doesn't save immediately.
    void OnRenameMultisample(object sender, RoutedEventArgs e)
    {
        // Pre-fills with the BARE name (no "-L"/"-R") - RenameSelectedMultisample treats
        // the whole dialog result as the new Name and appends Suffix itself, so pre-
        // filling with CurrentMultisampleName's Suffix-included form let an un-stripped
        // "-L"/"-R" get baked into Name and then doubled on display ("Foo-L-L").
        var current = _vm.CurrentMultisampleBareName;
        if (current == null) return;
        var dlg = new PromptDialog("New multisample name:", current) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        _vm.RenameSelectedMultisample(dlg.Result);
        UpdateStatus();

        // A rename inside a loaded collection rebuilds the whole tree (see
        // RenameSelectedMultisample's own comment), which throws away the previously-
        // selected node - LastRenamedMultisamplePath names its replacement so it can be
        // reselected through THIS window's own SelectTreeNode (same pattern
        // OnAddPlaceholderZone uses), which also calls RefreshDetailPanels and - for a
        // multisample node - auto-drops into its first zone. Without this, the rename
        // left the multisample "selected" with no zone within it selected, so every
        // zone/sample detail field went blank until manually reselected via the tree.
        var renamedPath = _vm.LastRenamedMultisamplePath;
        if (renamedPath == null) { RefreshDetailPanels(); return; }
        var msNode = FindMultisampleNode(_vm.Roots, renamedPath);
        if (msNode != null) SelectTreeNode(msNode);
        else RefreshDetailPanels();
    }

    void OnRenameSample(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSampleLoaded) return;
        // Same bare-name reasoning as OnRenameMultisample above - _vm.SampleName carries
        // Suffix, RenameSelectedSample doesn't expect it back.
        var dlg = new PromptDialog("New sample name:", _vm.CurrentSampleBareName ?? "") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        _vm.RenameSelectedSample(dlg.Result);
        _repositorySampleCache.Clear(); // the Sample dropdown's cached name for this .KSF is now stale
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnNewZoneFromExistingKsf(object sender, RoutedEventArgs e)
    {
        var fileDlg = new OpenFileDialog { Title = "New Zone from Existing Sample", Filter = "Korg KSF Files|*.KSF|All Files|*.*" };
        if (fileDlg.ShowDialog(this) != true) return;

        var (origKey, topKey) = PromptForZoneKeys();
        if (origKey < 0) return; // either prompt cancelled

        _vm.AddZoneFromExistingKsf(fileDlg.FileName, origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnExportSample(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Export Sample to WAV", Filter = "WAV Files|*.wav", FileName = _vm.SampleName };
        if (dlg.ShowDialog(this) == true) { _vm.ExportSelectedSampleToWav(dlg.FileName); UpdateStatus(); }
    }

    void OnExportCollection(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Export Collection to Folder",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _vm.ExportCollectionToFolder(dlg.SelectedPath);
            UpdateStatus();
        }
    }

    void OnExportMultisample(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Export Multisample to Folder",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _vm.ExportSelectedMultisampleToFolder(dlg.SelectedPath);
            UpdateStatus();
        }
    }

    // ── Normalization report + Recent Files ──────────────────────────────────────

    void OnNormalizationReport(object sender, RoutedEventArgs e)
    {
        var report = _vm.BuildNormalizationReport();
        new SampleNormalizationReportWindow(report) { Owner = this }.Show();
    }

    void OnRecentFilesSubmenuOpened(object sender, RoutedEventArgs e)
    {
        MNU_RecentFiles.Items.Clear();
        var recent = _vm.GetRecentFiles();
        if (recent.Count == 0)
        {
            MNU_RecentFiles.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var p = path;
            var mi = new MenuItem { Header = p };
            mi.Click += (_, _) =>
            {
                if (p.EndsWith(".KSC", StringComparison.OrdinalIgnoreCase)) OpenCollectionPath(p);
                else if (p.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) OpenKmpPath(p);
            };
            MNU_RecentFiles.Items.Add(mi);
        }
        MNU_RecentFiles.Items.Add(new Separator());
        var miClear = new MenuItem { Header = "C_lear All" };
        miClear.Click += (_, _) => _vm.ClearRecentFiles();
        MNU_RecentFiles.Items.Add(miClear);
    }
}
