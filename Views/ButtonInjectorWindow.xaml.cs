using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace KronosScreenRemote;

class ButtonInjectorEntry : INotifyPropertyChanged
{
    public string Category    { get; init; } = "";
    public string Token       { get; init; } = "";
    public int    Code        { get; init; }
    public string Description { get; init; } = "";

    string _label = "";
    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; Fire(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Fire([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n!));
}

// Sends "BUTTON <name>" for every token the daemon's ctrl port accepts (see
// ButtonReference / KronosScreenRemoteDaemon/docs/api.md §9), so which physical button
// each one fires can be confirmed by watching the mirrored screen - and, for Nautilus,
// discovered in the first place, since its panel silkscreens different labels over the
// same scan codes (see nautilus_button_mapping.md). Labels are per-layout
// (Kronos/Nautilus) and persisted to button_labels.json so a naming session survives a
// restart and eventually feeds a real Nautilus-specific front-panel skin
// (MainWindow.ApplyDeviceFamilyUi's seam).
internal partial class ButtonInjectorWindow : ThemedWindow
{
    const string KronosLayout   = "Kronos";
    const string NautilusLayout = "Nautilus";

    readonly ICtrlClient _ctrl;
    readonly ObservableCollection<ButtonInjectorEntry> _entries;
    readonly Dictionary<string, Dictionary<string, string>> _labelsByLayout;
    string _currentLayout = KronosLayout;
    bool _loadingLayout;   // suppresses save-on-change while ApplyLayout repopulates every row

    public ButtonInjectorWindow(ICtrlClient ctrl)
    {
        InitializeComponent();
        _ctrl = ctrl;
        _labelsByLayout = Storage.LoadButtonLabels();

        _entries = new ObservableCollection<ButtonInjectorEntry>(
            ButtonReference.All.Select(b => new ButtonInjectorEntry
            {
                Category    = b.Category,
                Token       = b.Token,
                Code        = b.Code,
                Description = b.Description,
            }));
        foreach (var entry in _entries)
            entry.PropertyChanged += Entry_PropertyChanged;

        EntryGrid.ItemsSource = _entries;

        ApplyLayout(KronosLayout);
    }

    void CboLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboLayout.SelectedItem is not ComboBoxItem item) return;
        string layout = item.Content?.ToString() ?? KronosLayout;
        if (layout != _currentLayout) ApplyLayout(layout);
    }

    void ApplyLayout(string layout)
    {
        _currentLayout = layout;
        var saved = _labelsByLayout.TryGetValue(layout, out var d) ? d : new Dictionary<string, string>();

        _loadingLayout = true;
        foreach (var entry in _entries)
        {
            if (saved.TryGetValue(entry.Token, out var label))
                entry.Label = label;
            else if (layout == KronosLayout)
                entry.Label = entry.Description;
            else if (layout == NautilusLayout
                     && ButtonReference.NautilusSeedLabels.TryGetValue(entry.Token, out var seed))
                entry.Label = seed;
            else
                entry.Label = "";
        }
        _loadingLayout = false;

        UpdateStatus();
    }

    void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingLayout || e.PropertyName != nameof(ButtonInjectorEntry.Label)) return;
        SaveCurrentLayout();
    }

    void SaveCurrentLayout()
    {
        var labels = new Dictionary<string, string>();
        foreach (var entry in _entries)
            if (!string.IsNullOrEmpty(entry.Label))
                labels[entry.Token] = entry.Label;
        _labelsByLayout[_currentLayout] = labels;
        Storage.SaveButtonLabels(_labelsByLayout);
        UpdateStatus();
    }

    void BtnSendRow_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ButtonInjectorEntry entry) return;
        _ctrl.Send(DaemonCommand.Button(entry.Token));
        TxtStatus.Text = $"Sent BUTTON {entry.Token}  ({DateTime.Now:HH:mm:ss})";
    }

    void UpdateStatus()
    {
        int labeled = _entries.Count(x => !string.IsNullOrEmpty(x.Label));
        TxtStatus.Text = $"{_currentLayout} layout - {labeled}/{_entries.Count} labeled";
    }
}
