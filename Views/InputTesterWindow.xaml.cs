using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

class TestEntry : INotifyPropertyChanged
{
    public string   Display    { get; init; } = "";
    public char     Ch         { get; init; }
    public string   CodeHex    { get; init; } = "";
    public string   SendSeq    { get; init; } = "";
    public string[]? Cmds      { get; init; }
    public bool     HasMapping => Cmds != null;
    public string   StatusDot  => "●";

    string _observed = "";
    public string Observed
    {
        get => _observed;
        set { _observed = value; Fire(); Fire(nameof(StatusBrush)); }
    }

    // Amber = no mapping, green = tested, gray = untested
    public Brush StatusBrush => !HasMapping
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00))
        : (_observed.Length > 0
            ? new SolidColorBrush(Color.FromRgb(0x55, 0xCC, 0x77))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Fire([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new(n!));
}

internal partial class InputTesterWindow : Window
{
    static string ResultsPath =>
        Path.Combine(Storage.DataDir, "input_test_results.json");

    // Named options for non-printable / ambiguous observed outputs.
    static readonly string[] ObservedOptions =
    [
        "N/A",
        "Space", "Tab", "Enter", "Backspace", "Delete", "Escape",
        "Up", "Down", "Left", "Right", "Home", "End", "Page Up", "Page Down",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
    ];

    readonly ICtrlClient _ctrl;
    readonly ObservableCollection<TestEntry> _entries = new();

    Key  _captureKey   = Key.None;
    bool _captureShift = false;

    public InputTesterWindow(ICtrlClient ctrl)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        _ctrl = ctrl;
        BuildEntries();
        EntryGrid.ItemsSource   = _entries;
        CboObserved.ItemsSource = ObservedOptions;
        MappingsGrid.ItemsSource = RawKeyMap.Entries;
        UpdateCount();
    }

    void BuildEntries()
    {
        Add('\b', "Backspace");
        Add('\t', "Tab");
        Add('\n', "Enter");
        for (char c = ' '; c <= '~'; c++)
            Add(c, c == ' ' ? "Space" : c.ToString());
    }

    void Add(char c, string display) =>
        _entries.Add(new TestEntry
        {
            Ch      = c,
            Display = display,
            CodeHex = $"0x{(int)c:X2}",
            SendSeq = CharMap.GetDescription(c),
            Cmds    = CharMap.GetCommands(c),
        });

    // ── Grid events ───────────────────────────────────────────────────────────

    void EntryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var entry = EntryGrid.SelectedItem as TestEntry;
        BtnSend.IsEnabled = entry?.HasMapping == true;
        CboObserved.Text  = entry?.Observed ?? "";
        if (entry != null) FocusObserved();
    }

    // ── Observed combo box ────────────────────────────────────────────────────

    // Commit whatever is currently in the combo box text to the selected entry.
    void CommitObserved()
    {
        if (EntryGrid.SelectedItem is TestEntry entry)
        {
            entry.Observed = CboObserved.Text;
            UpdateCount();
        }
    }

    void AdvanceSelection()
    {
        int idx = EntryGrid.SelectedIndex;
        if (idx >= 0 && idx + 1 < _entries.Count)
        {
            EntryGrid.SelectedIndex = idx + 1;
            EntryGrid.ScrollIntoView(EntryGrid.SelectedItem);
        }
    }

    // Fires when the user picks an item from the dropdown (dropdown closes after selection).
    void CboObserved_DropDownClosed(object sender, EventArgs e) => CommitObserved();

    // Fires when the combo loses keyboard focus (covers typed text the user didn't Enter to confirm).
    void CboObserved_LostFocus(object sender, RoutedEventArgs e) => CommitObserved();

    void CboObserved_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While the dropdown is open, Enter selects the highlighted item — let ComboBox handle
        // it naturally (DropDownClosed will fire and CommitObserved).
        if (e.Key == Key.Return && CboObserved.IsDropDownOpen) return;
        if (e.Key == Key.Tab   && CboObserved.IsDropDownOpen) return;

        if (e.Key == Key.Return || e.Key == Key.Tab)
        {
            CommitObserved();
            AdvanceSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !CboObserved.IsDropDownOpen)
        {
            // Physical Escape key → record "Escape" as the observed result.
            CboObserved.Text = "Escape";
            CommitObserved();
            AdvanceSelection();
            e.Handled = true;
        }
    }

    void FocusObserved()
    {
        CboObserved.Focus();
        // Select all text in the editable part so the next keystroke replaces it.
        Dispatcher.BeginInvoke(() =>
        {
            if (CboObserved.Template?.FindName("PART_EditableTextBox", CboObserved)
                    is System.Windows.Controls.TextBox tb)
                tb.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Send buttons ──────────────────────────────────────────────────────────

    void BtnSend_Click(object sender, RoutedEventArgs e) => SendSelected();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F5 && !e.IsRepeat) { SendSelected(); e.Handled = true; }
    }

    void SendSelected()
    {
        if (EntryGrid.SelectedItem is not TestEntry entry || entry.Cmds == null) return;
        foreach (var cmd in entry.Cmds)
            _ctrl.Send(cmd);
    }

    void BtnSendRaw_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtRawCode.Text.Trim(), out int code) || code < 1 || code > 767) return;
        bool shift = ChkRawShift.IsChecked == true;
        if (shift) _ctrl.Send("KEY 42 1");
        _ctrl.Send($"KEY {code} 1");
        _ctrl.Send($"KEY {code} 0");
        if (shift) _ctrl.Send("KEY 42 0");
        FocusObserved();
    }

    // ── Host key capture ──────────────────────────────────────────────────────

    void TxtHostKey_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtHostKey.Text == "[click, then press key]") TxtHostKey.Text = "";
        TxtHostKey.Text = "[press a key]";
        _captureKey = Key.None;
    }

    void TxtHostKey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
            return;
        _captureShift  = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _captureKey    = k;
        TxtHostKey.Text = _captureShift ? $"Shift+{k}" : k.ToString();
    }

    void BtnAddMapping_Click(object sender, RoutedEventArgs e)
    {
        if (_captureKey == Key.None)
        { TxtStatus.Text = "Click the host-key field and press the key you want to assign."; return; }
        if (!int.TryParse(TxtRawCode.Text.Trim(), out int code) || code < 1 || code > 767)
        { TxtStatus.Text = "Enter a valid raw keycode (1–767) first."; return; }

        string label = (EntryGrid.SelectedItem as TestEntry)?.Observed ?? "";

        RawKeyMap.Upsert(new RawMapping
        {
            HostKey   = _captureKey,
            HostShift = _captureShift,
            RawCode   = code,
            RawShift  = ChkRawShift.IsChecked == true,
            Label     = label,
        });

        string keyStr = _captureShift ? $"Shift+{_captureKey}" : _captureKey.ToString();
        TxtStatus.Text = $"Mapped: {keyStr} → KEY {code}  (active immediately)";
        _captureKey = Key.None;
        TxtHostKey.Text = "[click, then press key]";
    }

    void BtnDeleteMapping_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is RawMapping m)
            RawKeyMap.Remove(m);
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var obj = new JsonObject();
        foreach (var entry in _entries)
            if (entry.Observed.Length > 0)
                obj[$"{(int)entry.Ch}"] = JsonValue.Create(entry.Observed);

        try
        {
            File.WriteAllText(ResultsPath,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            TxtStatus.Text = $"Saved → {ResultsPath}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ResultsPath)) { TxtStatus.Text = "No results file found."; return; }

        JsonObject? obj;
        try { obj = JsonNode.Parse(File.ReadAllText(ResultsPath))?.AsObject(); }
        catch { obj = null; }
        if (obj == null) { TxtStatus.Text = "Parse error."; return; }

        var map = new Dictionary<int, string>();
        foreach (var kv in obj)
            if (int.TryParse(kv.Key, out int cp) && kv.Value != null)
                map[cp] = kv.Value.GetValue<string>();

        foreach (var entry in _entries)
            if (map.TryGetValue((int)entry.Ch, out var obs))
                entry.Observed = obs;

        // Refresh the combo box for the currently selected row.
        if (EntryGrid.SelectedItem is TestEntry sel)
            CboObserved.Text = sel.Observed;

        UpdateCount();
        TxtStatus.Text = $"Loaded {map.Count} entries — {ResultsPath}";
    }

    void UpdateCount()
    {
        int tested = _entries.Count(x => x.Observed.Length > 0);
        TxtStatus.Text = $"{tested} / {_entries.Count} tested";
    }
}
