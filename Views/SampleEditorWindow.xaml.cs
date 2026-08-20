using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

public partial class SampleEditorWindow : ThemedWindow
{
    readonly SampleEditorViewModel _vm = new();

    public SampleEditorWindow()
    {
        InitializeComponent();
        SampleTree.ItemsSource = _vm.Roots;
        _vm.TreeRefreshed += () => { }; // ItemsSource already bound to the live collection - no rebind needed
        RefreshDetailPanels();
        UpdateStatus();
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

    void OnSaveMultisample(object sender, RoutedEventArgs e) { _vm.SaveSelectedMultisample(); UpdateStatus(); }
    void OnSaveSample(object sender, RoutedEventArgs e) { _vm.SaveSelectedSample(); UpdateStatus(); }

    void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectNode(e.NewValue as SampleTreeNode);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnApplyZone(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ZoneOrigKeyBox.Text, out var origKey)) origKey = _vm.ZoneOriginalKey;
        if (!int.TryParse(ZoneTopKeyBox.Text, out var topKey)) topKey = _vm.ZoneTopKey;
        _vm.ApplyZoneEdits(origKey, topKey);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnApplySample(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SampleRateBox.Text, out var rate)) rate = _vm.SampleRate;
        if (!int.TryParse(SampleStartBox.Text, out var sampleStart)) sampleStart = _vm.SampleSampleStart;
        if (!int.TryParse(LoopStartBox.Text, out var loopStart)) loopStart = _vm.SampleLoopStart;
        if (!int.TryParse(LoopEndBox.Text, out var loopEnd)) loopEnd = _vm.SampleLoopEnd;
        _vm.ApplySampleEdits(rate, LoopEnabledBox.IsChecked == true, sampleStart, loopStart, loopEnd);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void RefreshDetailPanels()
    {
        ZonePanel.Visibility = _vm.HasZoneSelected ? Visibility.Visible : Visibility.Collapsed;
        SamplePanel.Visibility = _vm.HasSampleLoaded ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionText.Visibility = _vm.HasZoneSelected ? Visibility.Collapsed : Visibility.Visible;

        if (_vm.HasZoneSelected)
        {
            ZoneFilenameText.Text = _vm.ZoneIsSkipped ? "(skipped - no sample)" : _vm.ZoneFilename;
            ZoneOrigKeyBox.Text = _vm.ZoneOriginalKey.ToString();
            ZoneTopKeyBox.Text = _vm.ZoneTopKey.ToString();
        }

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
            Waveform.Samples = _vm.SampleWaveform;
            Waveform.SelectionStartFrame = _vm.SelectionStartFrame;
            Waveform.SelectionEndFrame = _vm.SelectionEndFrame;
        }
        else
        {
            Waveform.Samples = null;
        }

        BtnUndo.IsEnabled = _vm.CanUndo;
        BtnRedo.IsEnabled = _vm.CanRedo;
        MNU_Undo.IsEnabled = _vm.CanUndo;
        MNU_Redo.IsEnabled = _vm.CanRedo;
        BtnPlay.IsEnabled = _vm.HasSampleLoaded && !_vm.SampleIsHeaderOnly && !_vm.IsPlaying;
        BtnStop.IsEnabled = _vm.IsPlaying;
        BtnDeleteZone.IsEnabled = _vm.HasZoneSelected && !_vm.ZoneIsSkipped;
        MNU_DeleteZone.IsEnabled = _vm.HasZoneSelected && !_vm.ZoneIsSkipped;
        SelectionInfoText.Text = _vm.SelectionEndFrame > _vm.SelectionStartFrame
            ? $"Selection: [{_vm.SelectionStartFrame}, {_vm.SelectionEndFrame})  ({_vm.SelectionEndFrame - _vm.SelectionStartFrame} frames)  -  drag on the waveform to change, scroll to zoom, double-click to reset zoom"
            : "No selection - drag on the waveform to select a range for Crop. Scroll to zoom, double-click to reset.";
    }

    void UpdateStatus() => StatusBar.Text = _vm.StatusText;

    // ── Waveform editing (Phase 3) ──────────────────────────────────────────────

    void OnWaveformSelectionChanged()
    {
        _vm.SelectionStartFrame = Waveform.SelectionStartFrame;
        _vm.SelectionEndFrame = Waveform.SelectionEndFrame;
        RefreshDetailPanels();
    }

    void OnUndo(object sender, RoutedEventArgs e) { _vm.Undo(); RefreshDetailPanels(); UpdateStatus(); }
    void OnRedo(object sender, RoutedEventArgs e) { _vm.Redo(); RefreshDetailPanels(); UpdateStatus(); }

    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { _vm.Undo(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; return; }
        if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { _vm.Redo(); RefreshDetailPanels(); UpdateStatus(); e.Handled = true; return; }

        // Space (play/stop) and Delete (delete zone) only act as shortcuts when focus
        // isn't in a text box - otherwise they need to type a literal space / delete a
        // character, same as they would in any other editor.
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; }
        else if (e.Key == Key.Delete) { OnDeleteZone(sender, e); e.Handled = true; }
    }

    void TogglePlayback()
    {
        if (_vm.IsPlaying) _vm.StopPlayback();
        else
        {
            _vm.LoopPreviewEnabled = LoopPreviewBox.IsChecked == true;
            _vm.PlaySelectedSample();
        }
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnPlay(object sender, RoutedEventArgs e)
    {
        _vm.LoopPreviewEnabled = LoopPreviewBox.IsChecked == true;
        _vm.PlaySelectedSample();
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnStop(object sender, RoutedEventArgs e) { _vm.StopPlayback(); RefreshDetailPanels(); UpdateStatus(); }

    void OnDeleteZone(object sender, RoutedEventArgs e) { _vm.DeleteSelectedZone(); RefreshDetailPanels(); UpdateStatus(); }

    void OnCrop(object sender, RoutedEventArgs e) { _vm.ApplyCrop(); RefreshDetailPanels(); UpdateStatus(); }
    void OnNormalize(object sender, RoutedEventArgs e) { _vm.ApplyNormalize(); RefreshDetailPanels(); UpdateStatus(); }
    void OnTrimSilence(object sender, RoutedEventArgs e) { _vm.ApplySilenceTrim(); RefreshDetailPanels(); UpdateStatus(); }

    void OnFade(object sender, RoutedEventArgs e)
    {
        int.TryParse(FadeInBox.Text, out var fadeIn);
        int.TryParse(FadeOutBox.Text, out var fadeOut);
        _vm.ApplyFade(fadeIn, fadeOut);
        RefreshDetailPanels();
        UpdateStatus();
    }

    void OnTempoPitch(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TempoBox.Text, out var tempo) || tempo <= 0) tempo = 1.0;
        if (!double.TryParse(PitchBox.Text, out var pitch)) pitch = 0;
        _vm.ApplyTempoPitch(tempo, pitch);
        RefreshDetailPanels();
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
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

        var origDlg = new PromptDialog("Original key (0-127, C4 = 60):", "60") { Owner = this };
        if (origDlg.ShowDialog() != true) return;
        int.TryParse(origDlg.Result, out var origKey);

        var topDlg = new PromptDialog("Top key (0-127, top of this zone's trigger range):", origKey.ToString()) { Owner = this };
        if (topDlg.ShowDialog() != true) return;
        int.TryParse(topDlg.Result, out var topKey);

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

        var origDlg = new PromptDialog("Original key (0-127, C4 = 60):", "60") { Owner = this };
        if (origDlg.ShowDialog() != true) return;
        int.TryParse(origDlg.Result, out var origKey);

        var topDlg = new PromptDialog("Top key (0-127, top of this zone's trigger range):", origKey.ToString()) { Owner = this };
        if (topDlg.ShowDialog() != true) return;
        int.TryParse(topDlg.Result, out var topKey);

        _vm.ImportStereoAudioAsNewZonePair(fileDlg.FileName, origKey, topKey);
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
