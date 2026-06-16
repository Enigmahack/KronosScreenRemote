using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KronosScreenRemote;

public record CommandEntry(string Label, string KeyHint, Action Execute);

public partial class CommandPaletteWindow : Window
{
    readonly IReadOnlyList<CommandEntry> _all;
    bool _dismissed = false;

    public CommandPaletteWindow(IReadOnlyList<CommandEntry> commands)
    {
        _all = commands;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        Loaded      += (_, _) => { SearchBox.Focus(); Refresh(""); };
        Deactivated += (_, _) => Dismiss();

        SearchBox.TextChanged        += (_, _) => Refresh(SearchBox.Text);
        SearchBox.PreviewKeyDown     += OnSearchKeyDown;
        ResultsList.MouseDoubleClick += (_, _) => Invoke();
    }

    // Guard prevents the Close() call from re-entering Deactivated (which fires as the window
    // loses activation during Close()) and double-closing, which can crash WPF event routing.
    void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        AppLog.Debug("[palette] dismissed");
        Close();
    }

    void Refresh(string query)
    {
        IEnumerable<CommandEntry> filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered
                .OrderBy(c => c.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Label);
        }

        ResultsList.ItemsSource = filtered.ToList();
        if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
    }

    void OnSearchKeyDown(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;

            case Key.Return:
                Invoke();
                e.Handled = true;
                break;

            case Key.Down:
                if (ResultsList.SelectedIndex < ResultsList.Items.Count - 1)
                    ResultsList.SelectedIndex++;
                e.Handled = true;
                break;

            case Key.Up:
                if (ResultsList.SelectedIndex > 0)
                    ResultsList.SelectedIndex--;
                e.Handled = true;
                break;
        }
    }

    void Invoke()
    {
        if (ResultsList.SelectedItem is CommandEntry entry)
        {
            AppLog.Debug($"[palette] invoke: {entry.Label}");
            Dismiss();
            entry.Execute();
        }
    }
}
