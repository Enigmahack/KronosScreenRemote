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

    public SampleEditorWindow()
    {
        InitializeComponent();
        SampleTree.ItemsSource = _vm.Roots;
        _vm.TreeRefreshed += () => { }; // ItemsSource already bound to the live collection - no rebind needed
        VolumeControl.Volume = _vm.Volume;

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
        // comment). Mirrored to both stereo panes the same way OnWaveformScrubRequested
        // mirrors a manual scrub-click.
        _vm.CursorMoved += frame =>
        {
            WaveformLeft.ScrubFrame = frame;
            if (_vm.HasStereoPair && !_vm.SplitLR) WaveformRight.ScrubFrame = frame;
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
        if (!_vm.HasUnsavedChanges) return;
        var result = MessageBox.Show(this,
            "There are unsaved changes in the Sample Editor. Close anyway and discard them?",
            "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) e.Cancel = true;
    }

    public void OpenCollectionPath(string path) { _vm.OpenCollection(path); UpdateStatus(); }
    public void OpenKmpPath(string path) { _vm.OpenMultisampleDirect(path); UpdateStatus(); }

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
        if (dlg.ShowDialog(this) == true) { _vm.NewCollection(dlg.FileName); UpdateStatus(); }
    }

    void OnNewMultisample(object sender, RoutedEventArgs e)
    {
        var nameDlg = new PromptDialog("Multisample name:", "NewMS") { Owner = this };
        if (nameDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDlg.Result)) return;

        var idDlg = new PromptDialog("Multisample ID # (0-999):", "0") { Owner = this };
        uint mno1 = 0;
        if (idDlg.ShowDialog() == true) uint.TryParse(idDlg.Result, out mno1);

        _vm.NewMultisampleInCollection(nameDlg.Result, mno1);
        UpdateStatus();
    }

    void OnNewStereoMultisamplePair(object sender, RoutedEventArgs e)
    {
        var nameDlg = new PromptDialog("Stereo pair base name (no -L/-R suffix):", "NewStereoMS") { Owner = this };
        if (nameDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDlg.Result)) return;

        var idDlg = new PromptDialog("Left multisample ID # (0-998; Right uses ID+1):", "0") { Owner = this };
        uint mno1Left = 0;
        if (idDlg.ShowDialog() == true) uint.TryParse(idDlg.Result, out mno1Left);

        _vm.NewStereoMultisamplePairInCollection(nameDlg.Result, mno1Left);
        UpdateStatus();
    }

    // RefreshDetailPanels (not just UpdateStatus) because saving changes HasUnsavedChanges,
    // which drives the title bar's dirty marker and the Save Changes button's enabled
    // state - without it both stayed stale after a successful save until some unrelated
    // action happened to refresh them.
    void OnSaveMultisample(object sender, RoutedEventArgs e) { _vm.SaveSelectedMultisample(); RefreshDetailPanels(); UpdateStatus(); }
    void OnSaveSample(object sender, RoutedEventArgs e) { _vm.SaveSelectedSample(); RefreshDetailPanels(); UpdateStatus(); }

    void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectNode(e.NewValue as SampleTreeNode);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Live editing session - no Apply button, fields commit on LostFocus (Save Changes,
    // bottom-right, is what actually writes to disk). Zone key range lives in the .KMP.
    void OnZoneKeyChanged(object sender, RoutedEventArgs e)
    {
        var origKey = MidiNoteName.TryParse(ZoneOrigKeyBox.Text) ?? _vm.ZoneOriginalKey;
        var topKey = MidiNoteName.TryParse(ZoneTopKeyBox.Text) ?? _vm.ZoneTopKey;
        _vm.ApplyZoneEdits(origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // Sample Start/Loop Start/Loop End each commit independently through SetMarker (the
    // same choke point a marker drag in the waveform uses) - Use Zero snapping, Loop
    // Lock, and the Sample-Start-is-the-floor ordering rule all apply here exactly the
    // same way they do for a drag. Sample Rate stays read-only/informational - retyping
    // the declared number alone would desync it from the real PCM data without actually
    // resampling anything; an actual rate change only ever happens via a real resample.
    void OnSampleStartBoxChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SampleStartBox.Text, out var v)) _vm.SetMarker(SampleMarkerKind.SampleStart, v);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnLoopStartBoxChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(LoopStartBox.Text, out var v)) _vm.SetMarker(SampleMarkerKind.LoopStart, v);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnLoopEndBoxChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(LoopEndBox.Text, out var v)) _vm.SetMarker(SampleMarkerKind.LoopEnd, v);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnLoopEnabledChanged(object sender, RoutedEventArgs e)
    {
        _vm.SetLoopEnabled(LoopEnabledBox.IsChecked == true);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnUseZeroChanged(object sender, RoutedEventArgs e) => _vm.UseZeroCrossing = UseZeroBox.IsChecked == true;
    void OnLoopLockChanged(object sender, RoutedEventArgs e) => _vm.LoopLockEnabled = LoopLockBox.IsChecked == true;
    void OnLoopReverseChanged(object sender, RoutedEventArgs e) => _vm.LoopReverseEnabled = LoopReverseBox.IsChecked == true;

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
    void OnWaveformMarkerDragged(SampleMarkerKind kind, int frame)
    {
        _vm.SetMarker(kind, frame);
        RefreshDetailPanels();
        UpdateStatus();
    }

    // LoopSelected (the loop region's click-to-select green highlight) is per-control
    // state, not VM state - in Combine mode, clicking to select it on ONE pane must show
    // green on BOTH (it's the same shared loop), which plain per-pane DPs alone don't
    // give you. The `!=` guard means setting a pane to a value it already has is a
    // no-op (WPF skips the DP-changed callback for an unchanged value), so this can't
    // recurse forever even though both panes point back at this same handler.
    void OnWaveformLoopSelectedChanged(bool selected)
    {
        if (!_vm.HasStereoPair || _vm.SplitLR) return;
        if (WaveformLeft.LoopSelected != selected) WaveformLeft.LoopSelected = selected;
        if (WaveformRight.LoopSelected != selected) WaveformRight.LoopSelected = selected;
    }

    // Scrub-click "select a playback starting point" - a plain click on the waveform
    // sets the grey cursor line (mirrored onto the sibling stereo pane, same convention
    // as LoopSelected above) WITHOUT starting playback. The next Play (button, Space,
    // or Pause's Resume) starts from here - see SampleEditorViewModel.SetCursorFrame/
    // PlaySelectedSample.
    void OnWaveformScrubRequested(int frame)
    {
        WaveformLeft.ScrubFrame = frame;
        if (_vm.HasStereoPair && !_vm.SplitLR) WaveformRight.ScrubFrame = frame;
        _vm.SetCursorFrame(frame);
        UpdateStatus();
    }

    void RefreshDetailPanels()
    {
        ZonePanel.Visibility = _vm.HasZoneSelected ? Visibility.Visible : Visibility.Collapsed;
        // The TAB ITEMS are collapsed, not just the StackPanels inside them. Collapsing
        // only the content left three clickable but completely blank tabs sitting above
        // the "Select a zone..." hint whenever nothing was loaded.
        var sampleTabs = _vm.HasSampleLoaded ? Visibility.Visible : Visibility.Collapsed;
        WaveformPanel.Visibility = sampleTabs;
        TabSamples.Visibility = sampleTabs;
        TabLooping.Visibility = sampleTabs;
        SamplePanel.Visibility = sampleTabs;
        LoopingPanel.Visibility = sampleTabs;
        TabKeymap.Visibility = _vm.CurrentMultisampleZones is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        // A TabControl whose every item is collapsed still draws its own chrome (an
        // empty header strip and border), so hide the whole thing rather than leave a
        // stray box under the hint text.
        EditorTabs.Visibility = TabKeymap.Visibility == Visibility.Visible || sampleTabs == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        // Selecting a collapsed tab leaves the control showing blank content, which is
        // reachable by navigating away from a sample while the Samples tab was active.
        if (EditorTabs.SelectedItem is TabItem { Visibility: not Visibility.Visible })
            EditorTabs.SelectedItem = EditorTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        NoSelectionText.Visibility = _vm.HasZoneSelected ? Visibility.Collapsed : Visibility.Visible;
        UpdateWindowTitle();
        MNU_UnloadCollection.IsEnabled = _vm.HasActiveCollection;
        MNU_RevertKsc.IsEnabled = _vm.HasActiveCollection;
        MNU_RevertAll.IsEnabled = _vm.Roots.Count > 0;

        if (_vm.HasZoneSelected)
        {
            ZoneFilenameText.Text = _vm.ZoneIsSkipped ? "(skipped - no sample)" : _vm.ZoneFilename;
            ZoneOrigKeyBox.Text = MidiNoteName.ToName(_vm.ZoneOriginalKey);
            ZoneTopKeyBox.Text = MidiNoteName.ToName(_vm.ZoneTopKey);
        }

        Keymap.Zones = _vm.CurrentMultisampleZones;
        Keymap.SelectedZone = _vm.SelectedZoneObject;
        // Zone-list undo/redo mutates the SAME List<KmpZone> instance in place (see
        // ZoneListSnapshot.ApplyTo) rather than assigning a new list - the DP above
        // therefore sees no reference change and won't auto-invalidate on its own, so
        // this is needed explicitly to actually repaint the keymap after Ctrl+Z/Y.
        Keymap.InvalidateVisual();

        SplitLRBox.Visibility = _vm.HasStereoPair ? Visibility.Visible : Visibility.Collapsed;
        SplitLRBox.IsChecked = _vm.SplitLR;

        if (_vm.HasSampleLoaded)
        {
            SampleNameText.Text = _vm.SampleName;
            SampleFramesText.Text = _vm.SampleFrameCount.ToString();
            SampleWarningText.Text = _vm.SampleIsHeaderOnly
                ? "No audio data (header-only save - see doc §3.3)" : "";
            SampleRateBox.Text = _vm.SampleRate.ToString();
            LoopEnabledBox.IsChecked = _vm.SampleLoopEnabled;
            SampleStartBox.Text = _vm.SampleSampleStart.ToString();
            LoopStartBox.Text = _vm.SampleLoopStart.ToString();
            LoopEndBox.Text = _vm.SampleLoopEnd.ToString();
            UseZeroBox.IsChecked = _vm.UseZeroCrossing;
            LoopLockBox.IsChecked = _vm.LoopLockEnabled;
            LoopReverseBox.IsChecked = _vm.LoopReverseEnabled;

            if (_vm.HasStereoPair && !_vm.SplitLR)
            {
                // Combine: a single logical stereo view - both panes always shown, L
                // fixed top / R fixed bottom, sharing one selection and one set of
                // markers (Sample Start/Loop region) on BOTH panes - dragging either
                // pane's markers/loop edits the shared primary+partner pair via
                // SetMarker/MoveLoopRegion's own mirroring.
                WaveformRightRow.Visibility = Visibility.Visible;
                SetStereoRowsVisible(true);
                WaveformDivider.Visibility = Visibility.Visible;
                LeftChannelLabel.Visibility = Visibility.Visible;
                LeftChannelLabel.Text = "L";
                WaveformLeft.Samples = _vm.LeftSampleWaveform;
                WaveformRight.Samples = _vm.RightSampleWaveform;

                foreach (var pane in new[] { WaveformLeft, WaveformRight })
                {
                    pane.SelectionStartFrame = _vm.SelectionStartFrame;
                    pane.SelectionEndFrame = _vm.SelectionEndFrame;
                    pane.SampleStartFrame = _vm.SampleSampleStart;
                    pane.LoopStartFrame = _vm.SampleLoopStart;
                    pane.LoopEndFrame = _vm.SampleLoopEnd;
                    pane.LoopEnabled = _vm.SampleLoopEnabled;
                    // HasLoop already gates rendering/interaction off when disabled, but
                    // LoopSelected itself is sticky DP state - without clearing it here,
                    // re-checking Loop Enabled makes the green highlight reappear with no
                    // click, since the toggle in OnMouseLeftButtonUp never ran meanwhile.
                    if (!_vm.SampleLoopEnabled) pane.LoopSelected = false;
                }
            }
            else if (_vm.HasStereoPair && _vm.SplitLR)
            {
                // Split: only the tree-selected channel is shown, in the (only visible)
                // top slot - the OTHER channel's zone must be selected in the tree to
                // see/edit it, same as any other zone.
                WaveformRightRow.Visibility = Visibility.Collapsed;
                SetStereoRowsVisible(false);
                WaveformDivider.Visibility = Visibility.Collapsed;
                LeftChannelLabel.Visibility = Visibility.Visible;
                LeftChannelLabel.Text = _vm.IsPrimaryLeftChannel ? "L" : "R";
                WaveformLeft.Samples = _vm.SampleWaveform;
                WaveformLeft.SelectionStartFrame = _vm.SelectionStartFrame;
                WaveformLeft.SelectionEndFrame = _vm.SelectionEndFrame;
                WaveformLeft.SampleStartFrame = _vm.SampleSampleStart;
                WaveformLeft.LoopStartFrame = _vm.SampleLoopStart;
                WaveformLeft.LoopEndFrame = _vm.SampleLoopEnd;
                WaveformLeft.LoopEnabled = _vm.SampleLoopEnabled;
                if (!_vm.SampleLoopEnabled) WaveformLeft.LoopSelected = false;
            }
            else
            {
                WaveformRightRow.Visibility = Visibility.Collapsed;
                SetStereoRowsVisible(false);
                WaveformDivider.Visibility = Visibility.Collapsed;
                LeftChannelLabel.Visibility = Visibility.Collapsed;
                WaveformLeft.Samples = _vm.SampleWaveform;
                WaveformLeft.SelectionStartFrame = _vm.SelectionStartFrame;
                WaveformLeft.SelectionEndFrame = _vm.SelectionEndFrame;
                WaveformLeft.SampleStartFrame = _vm.SampleSampleStart;
                WaveformLeft.LoopStartFrame = _vm.SampleLoopStart;
                WaveformLeft.LoopEndFrame = _vm.SampleLoopEnd;
                WaveformLeft.LoopEnabled = _vm.SampleLoopEnabled;
                if (!_vm.SampleLoopEnabled) WaveformLeft.LoopSelected = false;
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
            SetStereoRowsVisible(false);
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
        BtnDeleteZone.IsEnabled = _vm.HasZoneSelected && !_vm.ZoneIsSkipped;
        BtnImportSampleIntoZone.IsEnabled = _vm.HasZoneSelected;
        MNU_DeleteZone.IsEnabled = _vm.HasZoneSelected && !_vm.ZoneIsSkipped;
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
    void SetStereoRowsVisible(bool visible)
    {
        WaveformDividerRow.Height = new GridLength(visible ? 2 : 0);
        WaveformRightRowDef.Height = new GridLength(visible ? 170 : 0);
    }

    void UpdateStatus() => StatusBar.Text = _vm.StatusText;

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

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    // Combine mode shows the SAME selection on both panes (a single logical stereo
    // view - see RefreshDetailPanels), and Split mode only ever shows one pane at all,
    // so a drag on EITHER visible pane always means "set the shared/primary selection."
    void OnWaveformLeftSelectionChanged() => OnWaveformPaneSelectionChanged(WaveformLeft);
    void OnWaveformRightSelectionChanged() => OnWaveformPaneSelectionChanged(WaveformRight);

    void OnWaveformPaneSelectionChanged(SampleWaveformControl pane)
    {
        _vm.SelectionStartFrame = pane.SelectionStartFrame;
        _vm.SelectionEndFrame = pane.SelectionEndFrame;
        RefreshDetailPanels();
    }

    // Live mirror of the crop-selection highlight onto the sibling stereo pane WHILE
    // dragging, not just once at mouse-up (OnWaveformPaneSelectionChanged above) - a
    // direct DP-to-DP copy, deliberately not routed through the ViewModel, so it stays
    // cheap enough to run on every MouseMove.
    void OnWaveformLeftSelectionPreview() => MirrorSelectionPreview(WaveformLeft, WaveformRight);
    void OnWaveformRightSelectionPreview() => MirrorSelectionPreview(WaveformRight, WaveformLeft);

    void MirrorSelectionPreview(SampleWaveformControl source, SampleWaveformControl other)
    {
        if (!_vm.HasStereoPair || _vm.SplitLR) return;
        other.SelectionStartFrame = source.SelectionStartFrame;
        other.SelectionEndFrame = source.SelectionEndFrame;
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

            int frameCount = source.Samples?.Length ?? 0;
            int viewLen = Math.Max(1, source.ViewEndFrame - source.ViewStartFrame);
            WaveformHScroll.Minimum = 0;
            WaveformHScroll.Maximum = Math.Max(0, frameCount - viewLen);
            WaveformHScroll.ViewportSize = viewLen;
            WaveformHScroll.Value = source.ViewStartFrame;
            WaveformHScroll.IsEnabled = frameCount > viewLen;
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

    void UpdateVuMeter()
    {
        VuMeter.Level = _vm.GetPlaybackLevel();
        int frame = _vm.IsPlaying ? _vm.GetPlaybackFrame() : -1;
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
        _vm.SelectionStartFrame = 0;
        _vm.SelectionEndFrame = _vm.SampleFrameCount;
        RefreshDetailPanels(); // mirrors onto both stereo panes, same as any other selection change
        UpdateStatus();
    }

    void OnReverse(object sender, RoutedEventArgs e) { _vm.ApplyReverse(); RefreshDetailPanels(); UpdateStatus(); }
    void OnSilenceSelection(object sender, RoutedEventArgs e) { _vm.ApplySilenceSelection(); RefreshDetailPanels(); UpdateStatus(); }
    void OnRemoveDcOffset(object sender, RoutedEventArgs e) { _vm.ApplyDcOffsetRemoval(); RefreshDetailPanels(); UpdateStatus(); }

    // Prompted in frames rather than seconds to match every other position field in this
    // window (Sample Start / Loop Start / Loop End are all frames), and seeded with a
    // quarter-second at the sample's own rate so the common case is one Enter press.
    void OnInsertSilence(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSampleLoaded) { StatusBar.Text = "No sample loaded."; return; }
        int suggested = Math.Max(1, _vm.SampleRate / 4);
        var dlg = new PromptDialog("Frames of silence to insert:", suggested.ToString()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!int.TryParse(dlg.Result, out var frames)) { StatusBar.Text = "That isn't a whole number of frames."; return; }

        _vm.ApplyInsertSilence(frames);
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

    void OnSplitLRChanged(object sender, RoutedEventArgs e)
    {
        _vm.SplitLR = SplitLRBox.IsChecked == true;
        RefreshDetailPanels(); // without this, the pane layout only caught up on the NEXT
                                // click/selection - toggling the checkbox itself had no
                                // visible effect until some unrelated action refreshed it
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

    void SelectTreeNode(SampleTreeNode target)
    {
        var path = new List<SampleTreeNode>();
        if (!BuildPath(_vm.Roots, target, path)) return;

        ItemsControl parent = SampleTree;
        TreeViewItem? container = null;
        foreach (var node in path)
        {
            parent.UpdateLayout();
            container = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (container == null) return;
            if (!ReferenceEquals(node, target)) container.IsExpanded = true;
            parent = container;
        }
        container?.BringIntoView();
        if (container != null) container.IsSelected = true;
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
    // ContextMenu resource - "Unload KSC" only makes sense once we know which
    // collection the right-clicked node actually belongs to (resolved the same way
    // SelectNode resolves "the active collection" for the collection-level menu
    // actions), and there's nothing else on this menu yet to justify a static resource.
    void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        SampleTree.ContextMenu = null;
        if (SampleTree.SelectedItem is not SampleTreeNode node) { e.Handled = true; return; }
        var owningPath = _vm.FindOwningCollectionPath(node);
        if (owningPath == null) { e.Handled = true; return; }

        var menu = new ContextMenu();
        var unload = new MenuItem { Header = "Unload KSC" };
        unload.Click += (_, _) => UnloadCollectionWithConfirm(owningPath);
        menu.Items.Add(unload);
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

    // ── Waveform right-click context menu (Phase 6) ─────────────────────────────

    // Shared between WaveformLeft and WaveformRight (same ContextMenu resource) - all
    // its actions target the primary (tree-selected) sample regardless of which pane
    // was actually right-clicked, same as every other toolbar action; `sender` isn't
    // needed here beyond being the element the ContextMenu is attached to.
    void OnWaveformContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "_Paste") is { } pasteItem)
            pasteItem.IsEnabled = SampleClipboard.HasContent;
    }

    void OnWaveformCut(object sender, RoutedEventArgs e) { _vm.CutSelection(); RefreshDetailPanels(); UpdateStatus(); }
    void OnWaveformCopy(object sender, RoutedEventArgs e) { _vm.CopySelection(); UpdateStatus(); }
    void OnWaveformPaste(object sender, RoutedEventArgs e) { _vm.PasteAtSelection(); RefreshDetailPanels(); UpdateStatus(); }
    void OnWaveformFadeIn(object sender, RoutedEventArgs e) { _vm.ApplyFadeInSelection(); RefreshDetailPanels(); UpdateStatus(); }
    void OnWaveformFadeOut(object sender, RoutedEventArgs e) { _vm.ApplyFadeOutSelection(); RefreshDetailPanels(); UpdateStatus(); }
    void OnLoopSelectedArea(object sender, RoutedEventArgs e) { _vm.SetLoopFromSelection(); RefreshDetailPanels(); UpdateStatus(); }

    void OnWaveformAmplify(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tagStr } || !double.TryParse(tagStr, out var db)) return;
        _vm.ApplyGainAdjust(db);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnUndo(object sender, RoutedEventArgs e) { _vm.Undo(); RefreshDetailPanels(); UpdateStatus(); }
    void OnRedo(object sender, RoutedEventArgs e) { _vm.Redo(); RefreshDetailPanels(); UpdateStatus(); }

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
                case Key.Z: _vm.Undo(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; return;
                case Key.Y: _vm.Redo(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; return;
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

        if (e.Key == Key.Home) { _vm.TransportLocateStart(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; }
        else if (e.Key == Key.End) { _vm.TransportLocateEnd(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; }
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
            { _vm.CutSelection(); RefreshDetailPanels(); UpdateStatus(); }
            else
                OnDeleteZone(sender, e);
            e.Handled = true;
        }
    }

    void TogglePlayback()
    {
        if (_vm.IsPlaying) _vm.StopPlayback();
        else _vm.PlaySelectedSample();
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnPlayStop(object sender, RoutedEventArgs e) => TogglePlayback();

    void OnTransportLocateStart(object sender, RoutedEventArgs e) { _vm.TransportLocateStart(); RefreshDetailPanels(); UpdateStatus(); }
    void OnTransportLocateEnd(object sender, RoutedEventArgs e) { _vm.TransportLocateEnd(); RefreshDetailPanels(); UpdateStatus(); }
    void OnTransportRewind(object sender, RoutedEventArgs e) { _vm.TransportSeekRelative(-1); RefreshDetailPanels(); UpdateStatus(); }
    void OnTransportFastForward(object sender, RoutedEventArgs e) { _vm.TransportSeekRelative(1); RefreshDetailPanels(); UpdateStatus(); }
    void OnTransportPause(object sender, RoutedEventArgs e) { _vm.TransportTogglePause(); RefreshDetailPanels(); UpdateStatus(); }

    void OnDeleteZone(object sender, RoutedEventArgs e) { _vm.DeleteSelectedZone(); RefreshDetailPanels(); UpdateStatus(); }

    void OnCrop(object sender, RoutedEventArgs e) { _vm.ApplyCrop(); RefreshDetailPanels(); UpdateStatus(); }
    void OnNormalize(object sender, RoutedEventArgs e) { _vm.ApplyNormalize(); RefreshDetailPanels(); UpdateStatus(); }
    void OnTrimSilence(object sender, RoutedEventArgs e) { _vm.ApplySilenceTrim(); RefreshDetailPanels(); UpdateStatus(); }

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
        RefreshDetailPanels();
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vuTimer.Stop();
        _vm.StopPlayback();
        base.OnClosed(e);
    }

    // ── Remote (FTP) pull/push (Phase 2) ────────────────────────────────────────

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
        RefreshDetailPanels();
        UpdateStatus();
    }

    async void OnPullMultisampleFromKronos(object sender, RoutedEventArgs e)
    {
        if (MakeRemoteSampleSource() is not { } source) return;
        await _vm.PullMultisampleFromKronosAsync(source);
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

    // ── Import / Export (Phase 4) ───────────────────────────────────────────────

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
    // AddPlaceholderZone always appends the new zone LAST, and the tree rebuild it
    // triggers preserves each multisample's zone order, so the newly-added zone is
    // reliably the target multisample node's last child after the rebuild - reference
    // identity can't be used here since the rebuild re-reads the .KMP from disk into
    // brand-new KmpZone objects.
    void OnAddPlaceholderZone(object sender, RoutedEventArgs e)
    {
        var kmpPath = _vm.AddPlaceholderZone();
        RefreshDetailPanels();
        UpdateStatus();
        if (kmpPath == null) return;

        var msNode = FindMultisampleNode(_vm.Roots, kmpPath);
        if (msNode != null && msNode.Children.Count > 0) SelectTreeNode(msNode.Children[^1]);
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

    void OnImportSampleIntoZone(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedZoneObject is not { } zone) { UpdateStatus(); return; }
        var fileDlg = new OpenFileDialog
        {
            Title = "Import Sample into Zone",
            Filter = "Audio Files|*.wav;*.mp3;*.mp4;*.m4a;*.wma|WAV Files|*.wav|All Files|*.*",
        };
        if (fileDlg.ShowDialog(this) != true) return;

        _vm.ImportSampleIntoZone(zone, fileDlg.FileName);
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

    // ── Normalization report + Recent Files (Phase 5) ───────────────────────────

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
