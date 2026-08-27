using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KronosScreenRemote.Tools;

namespace KronosScreenRemote;

// Double-click properties editor for a Program/Combi/Set List. Two shapes, chosen by which
// factory method constructed the dialog:
//   ForProgramOrCombi - Name + Category/Sub-Category (numeric only; no name table exists
//     anywhere in the documented format for these values).
//   ForSetList - the Set List's own Name, plus its slot list; selecting a slot exposes
//     that slot's Name/Color/Comments for editing.
internal partial class PropertiesDialog : ThemedWindow
{
    // 16-slot color palette (Kronos Set List slot colors) - sourced from SetListColors
    static readonly Brush[] SlotColors = BuildPalette();

    static Brush[] BuildPalette()
    {
        var brushes = new Brush[16];
        for (int i = 0; i < 16; i++)
        {
            if (SetListColors.TryGetByIndex(i, out var color))
            {
                brushes[i] = ThemeBrushes.Frozen(color.R, color.G, color.B);
            }
        }
        return brushes;
    }

    bool _isSetListMode;
    int _categoryObjType = LibObj.Program;
    CategoryNames _categoryNames = CategoryNames.Numeric();
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
        Title = heading;
        TXT_Heading.Text = heading;
        TXT_Name.Text = currentName;
        Loaded += (_, _) => { TXT_Name.SelectAll(); TXT_Name.Focus(); };
    }

    // objType + names: the two category fields are plain numbers in the body, but
    // the instrument shows the user-editable NAMES its Global object holds for them ("Guitar /
    // Acoustic", not "5 / 2"). `names` is never null - CategoryNames.Numeric() supplies the old
    // numeric labels when nothing has been synced yet or the Kronos is unreachable - so there's no
    // "unsynced" branch anywhere below. Programs and Combis have INDEPENDENT category names, hence
    // objType; sub-category names belong to a specific category, so the sub list is rebuilt
    // whenever the category selection changes.
    public static PropertiesDialog ForProgramOrCombi(
        string heading, string currentName, int category, int subCategory, int objType = LibObj.Program, CategoryNames? names = null)
    {
        var dlg = new PropertiesDialog(heading, currentName)
        {
            _categoryObjType = objType,
            _categoryNames = names ?? CategoryNames.Numeric(),
        };
        dlg.PNL_Category.Visibility = Visibility.Visible;

        for (int i = 0; i < CategoryNames.CategoryCount; i++)
            dlg.CMB_Category.Items.Add(new CategoryChoice(i, dlg._categoryNames.CategoryLabel(objType, i)));
        dlg.CMB_Category.SelectedIndex = category >= 0 && category < CategoryNames.CategoryCount ? category : 0;
        dlg.FillSubCategories(subCategory);
        // Wired AFTER the initial fill so the pre-selected sub-category isn't clobbered by the
        // rebuild the handler performs.
        dlg.CMB_Category.SelectionChanged += (_, _) => dlg.FillSubCategories(0);
        return dlg;
    }

    // The sub-category names of whichever category is selected right now.
    void FillSubCategories(int select)
    {
        int category = CMB_Category.SelectedIndex >= 0 ? CMB_Category.SelectedIndex : 0;
        CMB_SubCategory.Items.Clear();
        for (int i = 0; i < CategoryNames.SubCategoryCount; i++)
            CMB_SubCategory.Items.Add(new CategoryChoice(i, _categoryNames.SubCategoryLabel(_categoryObjType, category, i)));
        CMB_SubCategory.SelectedIndex = select >= 0 && select < CategoryNames.SubCategoryCount ? select : 0;
    }

    // One dropdown row: the stored NUMBER (what actually goes into the body) plus the label shown.
    // ToString is what the ComboBox renders, so no ItemTemplate/DisplayMemberPath is needed.
    sealed record CategoryChoice(int Value, string Label)
    {
        public override string ToString() => $"{Value:D2}  {Label}";
    }

    // Drum Kit/Wave Sequence: Name only - neither has a Category/Sub-Category field in Korg's
    // documented object format (unlike Program/Combi). PNL_Category stays at its default
    // Collapsed visibility, so OnOk never sets NewCategory.
    public static PropertiesDialog ForNameOnly(string heading, string currentName) =>
        new(heading, currentName);

    public static PropertiesDialog ForSetList(string heading, string currentName, SetListData data)
    {
        var dlg = new PropertiesDialog(heading, currentName) { _isSetListMode = true, _slots = data.Slots };
        dlg.PNL_SetList.Visibility = Visibility.Visible;
        dlg.CMB_SlotColor.ItemsSource = null;
        for (int i = 0; i < SlotColors.Length; i++)
        {
            // Swatch + its Kronos color name (Default, Charcoal, Brick...) so the dropdown -
            // and the collapsed selected item - read exactly like the palette on the device,
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

    // ── Dependencies + "Scan PCG for missing..." ──────────────────────────────────────────────
    // Both lists are plain pre-formatted strings supplied by the caller: deciding what "requires"
    // and "used by" MEAN (transitive walks, ROM/INIT labelling, the referrer catalog) is
    // ViewModel work - this dialog only displays it, same split as everything else here.

    // Raised when "Scan PCG for missing..." is clicked. The owner runs the file picker and the scan
    // (a WPF + ViewModel concern), then calls SetDependencies again with the refreshed lists -
    // recovered dependencies are staged in the Merge Window, so what's still missing changes.
    public Action? ScanForDependenciesRequested { get; set; }

    public void SetDependencies(IReadOnlyList<string> requires, IReadOnlyList<string> usedBy, bool canScan)
    {
        PNL_Dependencies.Visibility = Visibility.Visible;
        PNL_Dependencies.Header = AppMessages.Librarian.Shell.DependenciesHeader;
        TXT_RequiresHeader.Text = AppMessages.Librarian.Shell.RequiresHeader;
        TXT_UsedByHeader.Text = AppMessages.Librarian.Shell.UsedByHeader;
        BTN_ScanPcg.Content = AppMessages.Librarian.Shell.ScanPcgButton;

        Fill(LST_Requires, requires, AppMessages.Librarian.Shell.RequiresNothing);
        Fill(LST_UsedBy, usedBy, AppMessages.Librarian.Shell.UsedByNothing);

        // Only offer the scan where it can do something: a Program references nothing, and an
        // object with no gaps has nothing to look for.
        BTN_ScanPcg.Visibility = canScan ? Visibility.Visible : Visibility.Collapsed;
    }

    // The "nothing here" placeholder is a disabled row rather than an empty box, so an empty list
    // reads as an answer instead of a control that failed to populate.
    static void Fill(ListBox list, IReadOnlyList<string> rows, string emptyText)
    {
        list.Items.Clear();
        if (rows.Count == 0)
        {
            list.Items.Add(new ListBoxItem { Content = emptyText, IsEnabled = false });
            return;
        }
        foreach (var row in rows) list.Items.Add(row);
    }

    void OnScanPcg(object sender, RoutedEventArgs e) => ScanForDependenciesRequested?.Invoke();

    void OnOk(object sender, RoutedEventArgs e)
    {
        NewName = TXT_Name.Text.Trim();

        if (PNL_Category.Visibility == Visibility.Visible &&
            CMB_Category.SelectedItem is CategoryChoice cat && CMB_SubCategory.SelectedItem is CategoryChoice sub)
            NewCategory = (cat.Value, sub.Value);

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
