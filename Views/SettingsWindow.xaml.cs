using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace KronosScreenRemote;

public partial class SettingsWindow : Window
{
    public AppSettings Result   { get; private set; }
    public bool        WasReset { get; private set; }

    readonly ObservableCollection<KeybindRow>  _rows      = new();
    readonly ObservableCollection<MacroRow>    _macroRows = new();
    readonly Action<MacroDefinition>?          _playMacro;

    KeybindRow? _listeningRow;
    MacroRow?   _selectedMacroRow;
    bool        _macroRecording;
    bool        _macroTriggerListening;

    // Raw key map edit state
    RawMapping? _rawEditOriginal;
    bool        _rawEditListening;
    string      _rawEditPrevBtn = "(none)";
    Key         _rawEditCapturedKey   = Key.None;
    bool        _rawEditCapturedShift;

    readonly Action? _showInputTester;

    // Keys that route to physical control-surface buttons and must never be raw-mapped.
    static readonly HashSet<Key> PhysicalKeys = new()
    {
        Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4,
        Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9,
        Key.Subtract, Key.Decimal,
    };

    public SettingsWindow(AppSettings settings, Action<MacroDefinition>? playMacro = null,
                          Action? showInputTester = null, SettingsTab initialTab = SettingsTab.General)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        _playMacro      = playMacro;
        _showInputTester = showInputTester;
        BtnInputTester.IsEnabled = showInputTester != null;

        Result = new AppSettings
        {
            KronosHost           = settings.KronosHost,
            StreamPort           = settings.StreamPort,
            CtrlPort             = settings.CtrlPort,
            PullMode             = settings.PullMode,
            MaxFps               = settings.MaxFps,
            PromptBeforeQuitting = settings.PromptBeforeQuitting,
            HideControls         = settings.HideControls,
            ScreenshotDirectory  = settings.ScreenshotDirectory,
            VgaMirrorEnabled     = settings.VgaMirrorEnabled,
            ScreensaverTimeout   = settings.ScreensaverTimeout,
            LayoutPreset         = settings.LayoutPreset,
            DebugLogging         = settings.DebugLogging,
            Keybinds             = new Dictionary<string, Keybind>(settings.Keybinds),
            ZoomDefaultLevel     = settings.ZoomDefaultLevel,
            ZoomWindowSize       = settings.ZoomWindowSize,
            // Pass-through fields not exposed in the settings UI — must be preserved
            // exactly, or they are silently reset to defaults when the dialog closes.
            FtpUsername     = settings.FtpUsername,
            FtpPassword     = settings.FtpPassword,
            FtpPort         = settings.FtpPort,
            VuDeviceId      = settings.VuDeviceId,
            WindowLeft      = settings.WindowLeft,
            WindowTop       = settings.WindowTop,
            WindowWidth     = settings.WindowWidth,
            WindowHeight    = settings.WindowHeight,
            WindowMaximized = settings.WindowMaximized,
            AlwaysOnTop     = settings.AlwaysOnTop,
            RecentHosts     = settings.RecentHosts,
        };

        // Connection
        TxtHost.Text       = Result.KronosHost;
        TxtStreamPort.Text = Result.StreamPort.ToString();
        TxtCtrlPort.Text   = Result.CtrlPort.ToString();

        // Streaming
        RbChange.IsChecked = !Result.PullMode;
        RbPull.IsChecked   = Result.PullMode;
        SlFps.Value        = Result.MaxFps;

        // General
        ChkPromptQuit.IsChecked   = Result.PromptBeforeQuitting;
        ChkHideControls.IsChecked = Result.HideControls;
        TxtScreenshotDir.Text     = Result.ScreenshotDirectory;

        // VGA output
        ChkVgaMirror.IsChecked = Result.VgaMirrorEnabled;
        TxtSsTimeout.Text      = Result.ScreensaverTimeout.ToString();

        // Debug
        ChkDebugLogging.IsChecked = Result.DebugLogging;

        // View
        SlZoomLevel.Value      = Result.ZoomDefaultLevel;
        SlZoomWindowSize.Value = Result.ZoomWindowSize;

        // Key bindings
        foreach (var (action, label, _) in AppSettings.Rebindable)
            _rows.Add(new KeybindRow { Action = action, Label = label, BoundKey = Result.GetKeybind(action) });
        KeyList.ItemsSource = _rows;

        // Macros — deep copy so Cancel leaves the originals untouched
        foreach (var m in settings.Macros)
            _macroRows.Add(new MacroRow(new MacroDefinition
            {
                Description = m.Description,
                Trigger     = m.Trigger,
                StepDelayMs = m.StepDelayMs,
                Steps       = m.Steps.Select(s => new MacroStep { Code = s.Code, Down = s.Down }).ToList(),
            }));
        MacroList.ItemsSource = _macroRows;

        // Raw key map
        RawList.ItemsSource = RawKeyMap.Entries;

        // Capture key events at the window level for macro recording/trigger-listen
        PreviewKeyDown += OnWinPreviewKeyDown;
        PreviewKeyUp   += OnWinPreviewKeyUp;

        if (initialTab != SettingsTab.General)
            Loaded += (_, _) => SettingsTabControl.SelectedIndex = (int)initialTab;
    }

    void SlFps_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFpsLabel != null)
            TxtFpsLabel.Text = $"{(int)SlFps.Value} fps";
    }

    void SlZoomLevel_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtZoomLabel != null)
            TxtZoomLabel.Text = $"{SlZoomLevel.Value:F1}×";
    }

    bool _snapInProgress;
    static readonly double[] ZoomWindowSnaps = { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5 };

    void SlZoomWindowSize_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_snapInProgress) return;
        double v = e.NewValue;
        foreach (var snap in ZoomWindowSnaps)
        {
            if (Math.Abs(v - snap) <= 0.1 && v != snap)
            {
                _snapInProgress = true;
                SlZoomWindowSize.Value = snap;
                _snapInProgress = false;
                v = snap;
                break;
            }
        }
        if (TxtZoomWindowSizeLabel != null)
            TxtZoomWindowSizeLabel.Text = $"{v:F1}×";
    }

    void OnKeyListDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (KeyList.SelectedItem is not KeybindRow row) return;
        if (_listeningRow != null) _listeningRow.IsListening = false;
        _listeningRow = row;
        row.IsListening = true;
    }

    void OnKeyListKeyDown(object s, KeyEventArgs e)
    {
        var target = _listeningRow ?? (KeyList.SelectedItem as KeybindRow);

        // Plain Delete clears the binding (confirm first)
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && target != null)
        {
            if (target.BoundKey.Key != Key.None)
            {
                var res = MessageBox.Show(
                    $"Clear keybinding for '{target.Label}'?",
                    "Clear Binding", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    target.BoundKey = Keybind.None;
                    if (_listeningRow != null) { _listeningRow.IsListening = false; _listeningRow = null; }
                }
            }
            e.Handled = true;
            return;
        }

        if (_listeningRow == null) return;

        if (e.Key == Key.Escape)
        {
            _listeningRow.IsListening = false;
            _listeningRow = null;
            e.Handled = true;
            return;
        }

        // Ignore standalone modifier keystrokes — wait for the base key
        Key baseKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (baseKey is Key.LeftShift or Key.RightShift or
                       Key.LeftCtrl  or Key.RightCtrl  or
                       Key.LeftAlt   or Key.RightAlt   or
                       Key.LWin      or Key.RWin)
            return;

        _listeningRow.BoundKey    = new Keybind(baseKey, Keyboard.Modifiers);
        _listeningRow.IsListening = false;
        _listeningRow = null;
        e.Handled = true;
    }

    void BtnOK_Click(object s, RoutedEventArgs e)
    {
        if (_listeningRow != null) { _listeningRow.IsListening = false; _listeningRow = null; }
        if (_macroRecording)       { _macroRecording = false; }
        if (_macroTriggerListening){ _macroTriggerListening = false; }
        if (_rawEditListening)     { _rawEditListening = false; }

        // Connection
        var h = TxtHost.Text.Trim();
        if (h.Length > 0) Result.KronosHost = h;
        if (int.TryParse(TxtStreamPort.Text, out int sp) && sp is > 0 and <= 65535)
            Result.StreamPort = sp;
        if (int.TryParse(TxtCtrlPort.Text, out int cp) && cp is > 0 and <= 65535)
            Result.CtrlPort = cp;

        // Streaming
        Result.PullMode = RbPull.IsChecked == true;
        Result.MaxFps   = (int)SlFps.Value;

        // General
        Result.PromptBeforeQuitting = ChkPromptQuit.IsChecked == true;
        Result.HideControls         = ChkHideControls.IsChecked == true;
        Result.ScreenshotDirectory  = TxtScreenshotDir.Text.Trim();

        // VGA output
        Result.VgaMirrorEnabled = ChkVgaMirror.IsChecked == true;
        if (int.TryParse(TxtSsTimeout.Text, out int sst) && sst >= 0)
            Result.ScreensaverTimeout = sst;

        // Debug
        Result.DebugLogging = ChkDebugLogging.IsChecked == true;

        // View
        Result.ZoomDefaultLevel = SlZoomLevel.Value;
        Result.ZoomWindowSize   = SlZoomWindowSize.Value;

        // Key bindings
        foreach (var row in _rows)
            Result.Keybinds[row.Action] = row.BoundKey;

        // Macros
        Result.Macros = _macroRows.Select(r => r.Definition).ToList();

        DialogResult = true;
    }

    void BtnCancel_Click(object s, RoutedEventArgs e) => DialogResult = false;

    // ── Macro tab ─────────────────────────────────────────────────────────────

    void OnMacroAdd(object s, RoutedEventArgs e)
    {
        var row = new MacroRow(new MacroDefinition { Description = "New Macro" });
        _macroRows.Add(row);
        MacroList.SelectedItem = row;
        TxtMacroDesc.Focus();
        TxtMacroDesc.SelectAll();
    }

    void OnMacroRemove(object s, RoutedEventArgs e)
    {
        if (_selectedMacroRow == null) return;
        int idx = _macroRows.IndexOf(_selectedMacroRow);
        _macroRows.Remove(_selectedMacroRow);
        if (_macroRows.Count > 0)
            MacroList.SelectedIndex = Math.Min(idx, _macroRows.Count - 1);
    }

    void OnMacroPlay(object s, RoutedEventArgs e)
    {
        if (_selectedMacroRow == null || _playMacro == null) return;
        _playMacro(_selectedMacroRow.Definition);
    }

    void OnMacroSelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_macroRecording)        { _macroRecording = false; BtnMacroRecord.Content = "● Record"; }
        if (_macroTriggerListening) { StopTriggerListen(); }

        _selectedMacroRow        = MacroList.SelectedItem as MacroRow;
        MacroEditor.IsEnabled    = _selectedMacroRow != null;
        BtnMacroRemove.IsEnabled = _selectedMacroRow != null;

        if (_selectedMacroRow != null)
        {
            TxtMacroDesc.Text       = _selectedMacroRow.Definition.Description;
            BtnMacroTrigger.Content = _selectedMacroRow.Definition.Trigger.ToDisplayString();
            SlMacroDelay.Value      = _selectedMacroRow.Definition.StepDelayMs;
            UpdateMacroStepsDisplay();
        }
        UpdateMacroPlayButton();
    }

    void OnMacroDescChanged(object s, TextChangedEventArgs e)
    {
        if (_selectedMacroRow == null) return;
        _selectedMacroRow.Description = TxtMacroDesc.Text;
    }

    void OnMacroTriggerClick(object s, RoutedEventArgs e)
    {
        if (_macroRecording) return;
        _macroTriggerListening = true;
        BtnMacroTrigger.Content = "[ press key… ]";
    }

    void OnMacroDelayChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedMacroRow == null || TxtMacroDelayLabel == null) return;
        int delay = (int)SlMacroDelay.Value;
        _selectedMacroRow.Definition.StepDelayMs = delay;
        _selectedMacroRow.Refresh();
        TxtMacroDelayLabel.Text = $"{delay}ms";
    }

    void OnMacroRecordToggle(object s, RoutedEventArgs e)
    {
        if (_macroTriggerListening) { StopTriggerListen(); return; }
        _macroRecording = !_macroRecording;
        BtnMacroRecord.Content = _macroRecording ? "■ Stop" : "● Record";
        if (_macroRecording)
        {
            _selectedMacroRow!.Definition.Steps.Clear();
            _selectedMacroRow.Refresh();
            TxtMacroSteps.Text = "(recording — press keys…)";
        }
        else
            UpdateMacroStepsDisplay();
        UpdateMacroPlayButton();
    }

    void OnMacroClear(object s, RoutedEventArgs e)
    {
        if (_selectedMacroRow == null) return;
        _selectedMacroRow.Definition.Steps.Clear();
        _selectedMacroRow.Refresh();
        UpdateMacroStepsDisplay();
        UpdateMacroPlayButton();
    }

    // Window-level key intercept for recording and trigger-listen
    void OnWinPreviewKeyDown(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Raw map host-key capture
        if (_rawEditListening)
        {
            if (key is Key.LeftShift or Key.RightShift or
                       Key.LeftCtrl  or Key.RightCtrl  or
                       Key.LeftAlt   or Key.RightAlt   or
                       Key.LWin      or Key.RWin) return;

            if (key == Key.Escape)
            {
                _rawEditListening        = false;
                BtnRawCaptureKey.Content = _rawEditPrevBtn;
                e.Handled = true;
                return;
            }

            if (PhysicalKeys.Contains(key))
            {
                MessageBox.Show(
                    "This key routes directly to a physical control-surface button and cannot be remapped.",
                    "Not Remappable", MessageBoxButton.OK, MessageBoxImage.Information);
                _rawEditListening        = false;
                BtnRawCaptureKey.Content = _rawEditPrevBtn;
                e.Handled = true;
                return;
            }

            _rawEditCapturedKey   = key;
            _rawEditCapturedShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _rawEditListening     = false;
            BtnRawCaptureKey.Content = _rawEditCapturedShift ? $"Shift+{key}" : key.ToString();
            e.Handled = true;
            return;
        }

        if (_macroTriggerListening)
        {
            if (key is Key.LeftShift or Key.RightShift or
                       Key.LeftCtrl  or Key.RightCtrl  or
                       Key.LeftAlt   or Key.RightAlt   or
                       Key.LWin      or Key.RWin) return;

            if (key == Key.Escape) { StopTriggerListen(); e.Handled = true; return; }

            var trigger = new Keybind(key, Keyboard.Modifiers);
            if (trigger.Modifiers == ModifierKeys.None)
            {
                MessageBox.Show(
                    "A macro trigger must include at least one modifier key (Ctrl, Alt, or Shift).",
                    "Modifier Required", MessageBoxButton.OK, MessageBoxImage.Information);
                StopTriggerListen();
                e.Handled = true;
                return;
            }
            SetTrigger(trigger);
            e.Handled = true;
            return;
        }

        if (!_macroRecording || e.IsRepeat) { if (_macroRecording) e.Handled = true; return; }
        var code = ResolveRecordCode(key);
        if (code.HasValue) RecordStep(new MacroStep { Code = code.Value, Down = true });
        e.Handled = true;
    }

    void OnWinPreviewKeyUp(object s, KeyEventArgs e)
    {
        if (!_macroRecording) return;
        var key  = e.Key == Key.System ? e.SystemKey : e.Key;
        var code = ResolveRecordCode(key);
        if (code.HasValue) RecordStep(new MacroStep { Code = code.Value, Down = false });
        e.Handled = true;
    }

    static int? ResolveRecordCode(Key k)
        => RawKeyMap.Get(k, false)?.RawCode ?? KeyMap.ToLinux(k);

    void RecordStep(MacroStep step)
    {
        _selectedMacroRow!.Definition.Steps.Add(step);
        _selectedMacroRow.Refresh();
        UpdateMacroStepsDisplay();
    }

    void SetTrigger(Keybind trigger)
    {
        if (_selectedMacroRow == null) return;
        _selectedMacroRow.Definition.Trigger = trigger;
        _selectedMacroRow.Refresh();
        StopTriggerListen();
    }

    void StopTriggerListen()
    {
        _macroTriggerListening  = false;
        BtnMacroTrigger.Content = _selectedMacroRow?.Definition.Trigger.ToDisplayString() ?? "(none)";
    }

    void UpdateMacroStepsDisplay()
    {
        if (_selectedMacroRow == null) { TxtMacroSteps.Text = "(no steps recorded)"; return; }
        var steps = _selectedMacroRow.Definition.Steps;
        TxtMacroSteps.Text = steps.Count == 0
            ? "(no steps recorded)"
            : string.Join(" ", steps.Select(st => st.Display));
    }

    void UpdateMacroPlayButton()
        => BtnMacroPlay.IsEnabled = _playMacro != null
                                 && _selectedMacroRow?.Definition.Steps.Count > 0
                                 && !_macroRecording;

    // ── Raw Key Map tab ───────────────────────────────────────────────────────

    void OnInputTesterClick(object s, RoutedEventArgs e) => _showInputTester?.Invoke();

    void OnRawSelectionChanged(object s, SelectionChangedEventArgs e)
        => BtnRawRemove.IsEnabled = RawList.SelectedItem != null;

    void OnRawDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (RawList.SelectedItem is RawMapping rm) BeginRawEdit(rm);
    }

    void OnRawAdd(object s, RoutedEventArgs e)    => BeginRawEdit(null);
    void OnRawRemove(object s, RoutedEventArgs e)
    {
        if (RawList.SelectedItem is RawMapping rm) RawKeyMap.Remove(rm);
    }

    void BeginRawEdit(RawMapping? existing)
    {
        _rawEditOriginal      = existing;
        _rawEditCapturedKey   = existing?.HostKey   ?? Key.None;
        _rawEditCapturedShift = existing?.HostShift ?? false;

        TxtRawLabel.Text = existing?.Label ?? "";
        TxtRawCode.Text  = existing?.RawCode.ToString() ?? "";
        ChkRawShift.IsChecked = existing?.RawShift ?? false;

        string keyStr = _rawEditCapturedKey == Key.None ? "(none)"
            : (_rawEditCapturedShift ? $"Shift+{_rawEditCapturedKey}" : _rawEditCapturedKey.ToString());
        BtnRawCaptureKey.Content = keyStr;
        _rawEditPrevBtn          = keyStr;

        RawEditor.Visibility = Visibility.Visible;
        TxtRawLabel.Focus();
    }

    void OnRawCaptureKey(object s, RoutedEventArgs e)
    {
        _rawEditPrevBtn          = BtnRawCaptureKey.Content?.ToString() ?? "(none)";
        _rawEditListening        = true;
        BtnRawCaptureKey.Content = "[ press key… ]";
    }

    void OnRawSave(object s, RoutedEventArgs e)
    {
        if (_rawEditCapturedKey == Key.None)
        {
            MessageBox.Show("Click 'Host key' and press the key you want to capture.",
                "Host Key Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(TxtRawCode.Text.Trim(), out int code) || code < 1 || code > 767)
        {
            MessageBox.Show("Raw code must be an integer from 1 to 767 (Linux keycode).",
                "Invalid Code", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_rawEditOriginal != null) RawKeyMap.Remove(_rawEditOriginal);
        RawKeyMap.Upsert(new RawMapping
        {
            HostKey   = _rawEditCapturedKey,
            HostShift = _rawEditCapturedShift,
            RawCode   = code,
            RawShift  = ChkRawShift.IsChecked == true,
            Label     = TxtRawLabel.Text.Trim(),
        });
        CloseRawEditor();
    }

    void OnRawCancelEdit(object s, RoutedEventArgs e) => CloseRawEditor();

    void CloseRawEditor()
    {
        _rawEditListening    = false;
        _rawEditOriginal     = null;
        RawEditor.Visibility = Visibility.Collapsed;
    }

    // ── Screenshot directory ──────────────────────────────────────────────────

    void OnBrowseScreenshotDir(object s, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select screenshot output folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        var current = TxtScreenshotDir.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
            dlg.InitialDirectory = current;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtScreenshotDir.Text = dlg.SelectedPath;
    }

    // ── Import / Export ───────────────────────────────────────────────────────

    void OnExport(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title    = "Export Settings",
            Filter   = "JSON Settings|*.json",
            FileName = "kronos_screenremote_settings.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Storage.SaveSettingsTo(Result, dlg.FileName);
            MessageBox.Show($"Settings exported to:\n{dlg.FileName}",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OnImport(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import Settings",
            Filter = "JSON Settings|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var imported = Storage.LoadSettingsFrom(dlg.FileName);
            Result   = imported;
            WasReset = false;
            MessageBox.Show(
                "Settings imported. Click OK to apply them.",
                "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}",
                "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Reset settings ────────────────────────────────────────────────────────

    void OnResetSettings(object s, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            "This will permanently remove all saved settings, key mappings, calibration data, and other customizations.\n\n" +
            "The app will return to its default state. Calibration changes take full effect on next launch.\n\n" +
            "This cannot be undone. Continue?",
            "Reset All Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res != MessageBoxResult.Yes) return;

        foreach (var name in new[]
        {
            "settings.json", "raw_key_mappings.json", "cal_data.json",
            "palette_override.json", "palette_lock.json",
        })
        {
            var path = Path.Combine(Storage.DataDir, name);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        RawKeyMap.Entries.Clear();
        Result   = new AppSettings();
        WasReset = true;
        DialogResult = true;
    }
}

class MacroRow : INotifyPropertyChanged
{
    public MacroDefinition Definition { get; }
    public MacroRow(MacroDefinition def) { Definition = def; }

    public string Description
    {
        get => Definition.Description;
        set { Definition.Description = value; Raise(); }
    }

    public string TriggerDisplay => Definition.Trigger.ToDisplayString();
    public int    StepCount      => Definition.Steps.Count;
    public string DelayDisplay   => $"{Definition.StepDelayMs}ms";

    public void Refresh()
    {
        Raise(nameof(TriggerDisplay));
        Raise(nameof(StepCount));
        Raise(nameof(DelayDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

class KeybindRow : INotifyPropertyChanged
{
    public required string Action { get; init; }
    public required string Label  { get; init; }

    Keybind _boundKey;
    bool    _listening;

    public Keybind BoundKey
    {
        get => _boundKey;
        set { _boundKey = value; Notify(); Notify(nameof(DisplayKey)); }
    }

    public bool IsListening
    {
        get => _listening;
        set { _listening = value; Notify(); Notify(nameof(DisplayKey)); }
    }

    public string DisplayKey => IsListening ? "[ Press a key… ]" : _boundKey.ToDisplayString();

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum SettingsTab
{
    General    = 0,
    Connection = 1,
    Streaming  = 2,
    View       = 3,
    KeyBindings = 4,
    Macros     = 5,
    Debug      = 6,
}
