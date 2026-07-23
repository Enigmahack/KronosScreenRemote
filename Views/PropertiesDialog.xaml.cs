using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KronosScreenRemote.Tools;

namespace KronosScreenRemote;

// Double-click properties editor for a Program/Combi/Set List (item 3 of the rebuild:
// "edit its properties that we have control over"). Two shapes, chosen by which factory
// method constructed the dialog:
//   ForProgramOrCombi — Name + Category/Sub-Category (numeric only; no name table exists
//     anywhere in the documented format for these values).
//   ForSetList — the Set List's own Name, plus its slot list; selecting a slot exposes
//     that slot's Name/Color/Comments for editing. This absorbs Views/SetListWindow's
//     functionality (Phase 6 of the rebuild) into one dialog instead of a separate window
//     plus a second per-slot edit dialog (the retired SetListSlotEditDialog).
internal partial class PropertiesDialog : Window
{
    // 16-slot color palette (Kronos Set List slot colors) — sourced from SetListColors
    static readonly Brush[] SlotColors = BuildPalette();

    static Brush[] BuildPalette()
    {
        var brushes = new Brush[16];
        for (int i = 0; i < 16; i++)
        {
            if (SetListColors.TryGetByIndex(i, out var color))
            {
                var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                brush.Freeze();
                brushes[i] = brush;
            }
        }
        return brushes;
    }

    bool _isSetListMode;
    IReadOnlyList<SetListSlot> _slots = Array.Empty<SetListSlot>();
    int? _selectedSlotNumber;

    public string? NewName { get; private set; }
    public (int Category, int SubCategory)? NewCategory { get; private set; }
    public int? EditedSlotNumber { get; private set; }
    public string? NewSlotName { get; private set; }
    public int? NewSlotColor { get; private set; }
    public string? NewSlotComments { get; private set; }

    PropertiesDialog(string heading, string currentName)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        Title = heading;
        TXT_Heading.Text = heading;
        TXT_Name.Text = currentName;
        Loaded += (_, _) => { TXT_Name.SelectAll(); TXT_Name.Focus(); };
    }

    public static PropertiesDialog ForProgramOrCombi(string heading, string currentName, int category, int subCategory)
    {
        var dlg = new PropertiesDialog(heading, currentName);
        dlg.PNL_Category.Visibility = Visibility.Visible;
        for (int i = 0; i <= 0x11; i++) dlg.CMB_Category.Items.Add(i);
        for (int i = 0; i <= 7; i++) dlg.CMB_SubCategory.Items.Add(i);
        dlg.CMB_Category.SelectedItem = category;
        dlg.CMB_SubCategory.SelectedItem = subCategory;
        return dlg;
    }

    public static PropertiesDialog ForSetList(string heading, string currentName, SetListData data)
    {
        var dlg = new PropertiesDialog(heading, currentName) { _isSetListMode = true, _slots = data.Slots };
        dlg.PNL_SetList.Visibility = Visibility.Visible;
        dlg.CMB_SlotColor.ItemsSource = null;
        for (int i = 0; i < SlotColors.Length; i++)
        {
            // Swatch + its Kronos color name (Default, Charcoal, Brick…) so the dropdown —
            // and the collapsed selected item — read exactly like the palette on the device,
            // instead of an unlabeled colored bar. Names come from SetListColors, same source
            // as SlotColors, so swatch and label can't drift.
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 28, Height = 12, Fill = SlotColors[i],
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = SetListColors.GetByIndexOrDefault(i).DisplayName,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            dlg.CMB_SlotColor.Items.Add(new ComboBoxItem { Content = row, Tag = i });
        }
        foreach (var slot in data.Slots)
        {
            if (slot.IsEmpty) continue;
            dlg.LST_Slots.Items.Add($"{slot.Number:D3}  {slot.Name}");
        }
        dlg.LST_Slots.SelectionChanged += (_, _) => dlg.OnSlotSelected();
        return dlg;
    }

    void OnSlotSelected()
    {
        if (LST_Slots.SelectedItem is not string label) { SetSlotFieldsEnabled(false); return; }
        int number = int.Parse(label.Substring(0, 3));
        var slot = _slots.FirstOrDefault(s => s.Number == number);
        _selectedSlotNumber = number;
        TXT_SlotName.Text = slot.Name;
        TXT_SlotComments.Text = slot.Comments;
        CMB_SlotColor.SelectedIndex = slot.Color >= 0 && slot.Color < SlotColors.Length ? slot.Color : 0;
        SetSlotFieldsEnabled(true);
    }

    void SetSlotFieldsEnabled(bool enabled)
    {
        TXT_SlotName.IsEnabled = enabled;
        CMB_SlotColor.IsEnabled = enabled;
        TXT_SlotComments.IsEnabled = enabled;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        NewName = TXT_Name.Text.Trim();

        if (PNL_Category.Visibility == Visibility.Visible &&
            CMB_Category.SelectedItem is int cat && CMB_SubCategory.SelectedItem is int sub)
            NewCategory = (cat, sub);

        if (_isSetListMode && _selectedSlotNumber is int slotNumber)
        {
            EditedSlotNumber = slotNumber;
            NewSlotName = TXT_SlotName.Text;
            NewSlotComments = TXT_SlotComments.Text;
            if (CMB_SlotColor.SelectedItem is ComboBoxItem { Tag: int colorIndex }) NewSlotColor = colorIndex;
        }

        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
