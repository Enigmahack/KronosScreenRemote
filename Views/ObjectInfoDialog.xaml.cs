using System.Windows;

namespace KronosScreenRemote;

// "More Info..." for a row in the Object Dependencies panel (double-click or right-click a
// row - see LibrarianShellWindow.xaml.cs). selfInfo/parentInfo/children all come pre-formatted
// from ObjectDependencyRow - this window just displays them, no ObjLoc/lookup logic of its own.
internal partial class ObjectInfoDialog : ThemedWindow
{
    public ObjectInfoDialog(string selfInfo, string parentInfo, IReadOnlyList<string> children)
    {
        InitializeComponent();
        TXT_Self.Text = selfInfo;
        TXT_Parent.Text = string.IsNullOrEmpty(parentInfo) ? "Referenced by: (top-level selection)" : $"Referenced by: {parentInfo}";
        foreach (var line in children.Count > 0 ? children : new[] { "(references nothing)" })
            LST_Children.Items.Add(line);
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
