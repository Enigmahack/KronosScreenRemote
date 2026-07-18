using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

partial class SetListWindow : Window
{
    readonly ISysExService _sysEx;
    readonly string _host;
    readonly Dictionary<int, SetListData> _cache;
    bool _suppressSelChanged;

    public SetListWindow(ISysExService sysEx, string host, int initialNumber = 0)
    {
        _sysEx = sysEx;
        _host  = host;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        _cache = new Dictionary<int, SetListData>();

        for (int i = 0; i < SetListData.MaxCount; i++)
            CMB_SetList.Items.Add(FormatSetListLabel(i));   // names fill in after the async load
        CMB_SetList.SelectedIndex = Math.Clamp(initialNumber, 0, SetListData.MaxCount - 1);

        BTN_Load.Click     += async (_, _) => await LoadAsync(force: false);
        BTN_Refresh.Click  += async (_, _) => await LoadAsync(force: true);
        BTN_EditSlot.Click += (_, _) => EditSelectedSlot();
        CMB_SetList.SelectionChanged += (_, _) => { if (!_suppressSelChanged) ShowFromCacheIfPresent(); };
        DG_Slots.SelectionChanged    += (_, _) => BTN_EditSlot.IsEnabled = DG_Slots.SelectedItem is SlotRow;
        DG_Slots.MouseDoubleClick    += (_, _) => { if (DG_Slots.SelectedItem is SlotRow) EditSelectedSlot(); };

        // Read the on-disk cache off the UI thread on open, then paint labels + view.
        Loaded += async (_, _) => await ReloadCacheAsync();
    }

    int SelectedNumber => Math.Max(0, CMB_SetList.SelectedIndex);

    string FormatSetListLabel(int n) =>
        _cache.TryGetValue(n, out var d) && !string.IsNullOrWhiteSpace(d.Name)
            ? $"{n:D2}: {d.Name}"
            : $"Set List {n:D2}";

    // Re-read the on-disk cache (e.g. after a "Sync All" populated it) and refresh the
    // dropdown labels + current view. The disk read + JSON deserialize runs on a
    // background thread (heavy for a populated cache); only the UI apply is on the
    // dispatcher.
    public async Task ReloadCacheAsync()
    {
        var fresh = await Task.Run(() => Storage.LoadSetLists(_host));
        _cache.Clear();
        foreach (var kv in fresh) _cache[kv.Key] = kv.Value;

        _suppressSelChanged = true;
        for (int i = 0; i < SetListData.MaxCount; i++)
            CMB_SetList.Items[i] = FormatSetListLabel(i);
        _suppressSelChanged = false;

        ShowFromCacheIfPresent();
    }

    void ShowFromCacheIfPresent()
    {
        if (_cache.TryGetValue(SelectedNumber, out var data))
        {
            Render(data);
            TXT_Status.Text = "Cached";
        }
        else
        {
            DG_Slots.ItemsSource = null;
            BTN_EditSlot.IsEnabled = false;
            TXT_Name.Text   = "";
            TXT_Status.Text = "Not loaded — press Load";
        }
    }

    // Opens the edit dialog for the selected row, then saves any change. The
    // dialog's Performance display is read-only — see ISysExService
    // .WriteSetListSlotAsync for why Performance can't be written from here.
    void EditSelectedSlot()
    {
        if (DG_Slots.SelectedItem is not SlotRow row) return;
        if (!_cache.TryGetValue(SelectedNumber, out var data) || row.Number >= data.Slots.Count) return;
        var slot = data.Slots[row.Number];

        var dlg = new SetListSlotEditDialog(slot) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _ = SaveSlotAsync(SelectedNumber, slot, dlg.SlotName, dlg.Notes);
    }

    async Task SaveSlotAsync(int setListNumber, SetListSlot original, string newName, string newNotes)
    {
        if (!_sysEx.CanDump)
        {
            TXT_Status.Text = "MIDI monitoring is off — enable it in Settings → MIDI/SysEx";
            return;
        }

        string? nameArg     = newName  != original.Name     ? newName  : null;
        string? commentsArg = newNotes != original.Comments ? newNotes : null;
        if (nameArg == null && commentsArg == null) { TXT_Status.Text = "No changes"; return; }

        SetBusy(true, $"Saving Slot {original.Number:D3}…");
        try
        {
            var result = await _sysEx.WriteSetListSlotAsync(setListNumber, original.Number, nameArg, commentsArg);
            if (!result.Success)
            {
                TXT_Status.Text = $"Save failed: {result.Error}";
                return;
            }

            // Re-dump to confirm the write landed and refresh the cache from
            // ground truth, rather than patching the cached record by hand.
            TXT_Status.Text = "Saved — reloading…";
            var fresh = await _sysEx.DumpSetListAsync(setListNumber);
            if (fresh != null)
            {
                _cache[setListNumber] = fresh;
                var snapshot = new Dictionary<int, SetListData>(_cache);
                await Task.Run(() => Storage.SaveSetLists(_host, snapshot));

                _suppressSelChanged = true;
                CMB_SetList.Items[setListNumber] = FormatSetListLabel(setListNumber);
                _suppressSelChanged = false;

                if (setListNumber == SelectedNumber) Render(fresh);
            }
            TXT_Status.Text = "Saved";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[setlist] slot save failed: {ex.Message}");
            TXT_Status.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    async Task LoadAsync(bool force)
    {
        int number = SelectedNumber;

        if (!force && _cache.TryGetValue(number, out var cached))
        {
            Render(cached);
            TXT_Status.Text = "Cached";
            return;
        }

        if (!_sysEx.CanDump)
        {
            TXT_Status.Text = "MIDI monitoring is off — enable it in Settings → MIDI/SysEx";
            return;
        }

        SetBusy(true, $"Dumping Set List {number:D2}…");
        try
        {
            var data = await _sysEx.DumpSetListAsync(number);
            if (data == null)
            {
                TXT_Status.Text = "No response — is SysEx transmit enabled on the Kronos?";
                return;
            }

            _cache[number] = data;
            // Persist off the UI thread — SaveSetLists re-serializes the whole cache
            // (each Set List ~79 KB), which froze the window when run inline. Snapshot
            // first so the background write sees a stable dictionary.
            var snapshot = new Dictionary<int, SetListData>(_cache);
            await Task.Run(() => Storage.SaveSetLists(_host, snapshot));

            // Relabel the selected item with the loaded name without letting the
            // transient reselection fire ShowFromCacheIfPresent (would flash slot 0).
            _suppressSelChanged = true;
            CMB_SetList.Items[number] = FormatSetListLabel(number);
            CMB_SetList.SelectedIndex = number;
            _suppressSelChanged = false;

            Render(data);
            TXT_Status.Text = "Loaded";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[setlist] dump failed: {ex.Message}");
            TXT_Status.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    void Render(SetListData data)
    {
        TXT_Name.Text = string.IsNullOrWhiteSpace(data.Name)
            ? $"Set List {data.Number:D2}"
            : $"{data.Number:D2}  —  {data.Name}";

        var rows = new List<SlotRow>();
        foreach (var slot in data.Slots)
        {
            if (slot.IsEmpty) continue;   // hide unused slots
            rows.Add(new SlotRow(slot));
        }
        DG_Slots.ItemsSource = rows;
        BTN_EditSlot.IsEnabled = false;   // ItemsSource swap drops any prior selection
    }

    void SetBusy(bool busy, string? status)
    {
        BTN_Load.IsEnabled       = !busy;
        BTN_Refresh.IsEnabled    = !busy;
        CMB_SetList.IsEnabled    = !busy;
        BTN_EditSlot.IsEnabled   = !busy && DG_Slots.SelectedItem is SlotRow;
        Cursor = busy ? Cursors.Wait : null;
        if (status != null) TXT_Status.Text = status;
    }

    // 16-slot color palette (Kronos Set List slot colors, approximated).
    static readonly Brush[] SlotColors = BuildPalette();

    static Brush[] BuildPalette()
    {
        (byte r, byte g, byte b)[] rgb =
        {
            (0x55, 0x55, 0x55), (0xC0, 0x40, 0x40), (0xC8, 0x78, 0x30), (0xC8, 0xB0, 0x30),
            (0x88, 0xC0, 0x38), (0x40, 0xB0, 0x48), (0x38, 0xB0, 0x90), (0x40, 0x90, 0xC8),
            (0x40, 0x60, 0xC8), (0x70, 0x50, 0xC8), (0xA0, 0x48, 0xC0), (0xC0, 0x48, 0x98),
            (0x90, 0x90, 0x90), (0x80, 0x60, 0x40), (0x50, 0x70, 0x80), (0xD0, 0xD0, 0xD0),
        };
        var brushes = new Brush[rgb.Length];
        for (int i = 0; i < rgb.Length; i++)
        {
            var b = new SolidColorBrush(Color.FromRgb(rgb[i].r, rgb[i].g, rgb[i].b));
            b.Freeze();
            brushes[i] = b;
        }
        return brushes;
    }

    // Row view-model for the grid.
    sealed class SlotRow
    {
        public int    Number      { get; }
        public string Name        { get; }
        public string TypeLabel   { get; }
        public string Performance { get; }
        public string Notes       { get; }
        public Brush  ColorBrush  { get; }

        public SlotRow(SetListSlot s)
        {
            Number      = s.Number;
            Name        = s.Name;
            TypeLabel   = s.TypeLabel;
            Performance = s.PerformanceLabel;
            Notes       = s.Comments.Replace('\n', ' ').Replace('\r', ' ');
            ColorBrush  = SlotColors[s.Color >= 0 && s.Color < SlotColors.Length ? s.Color : 0];
        }
    }
}
