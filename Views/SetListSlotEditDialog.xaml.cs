using System.Windows;

namespace KronosScreenRemote;

// Edit dialog for a Set List slot's Name and Notes. Performance is shown
// read-only in the header — see ISysExService.WriteSetListSlotAsync for why it
// can't be edited from here. A dumb input collector like PromptDialog: the
// caller (SetListWindow) performs the actual SysEx write after ShowDialog.
internal partial class SetListSlotEditDialog : Window
{
    // NOT "Name": that would hide FrameworkElement.Name (the DependencyProperty-backed
    // element name used by x:Name / FindName / ElementName bindings), so `dlg.Name`
    // and the element's real Name would silently mean different things (CS0108).
    public string SlotName { get; private set; } = "";
    public string Notes    { get; private set; } = "";

    public SetListSlotEditDialog(SetListSlot slot)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        Title = $"Edit Slot {slot.Number:D3}";
        TXT_Header.Text = $"Slot {slot.Number:D3}  —  {slot.TypeLabel} {slot.PerformanceLabel}   (performance is read-only)";
        TXT_Name.Text  = slot.Name;
        TXT_Notes.Text = slot.Comments;

        Loaded += (_, _) => { TXT_Name.Focus(); TXT_Name.SelectAll(); };
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        SlotName = TXT_Name.Text;
        Notes    = TXT_Notes.Text;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
