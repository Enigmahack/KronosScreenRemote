using System.Windows;

namespace KronosScreenRemote;

public partial class PromptDialog : Window
{
    public string? Result { get; private set; }

    public PromptDialog(string prompt, string initial = "")
    {
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);
        Title            = prompt;
        PromptLabel.Text = prompt;
        InputBox.Text    = initial;
        Loaded += (_, _) => { InputBox.SelectAll(); InputBox.Focus(); };
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        Result       = InputBox.Text.Trim();
        DialogResult = true;
    }

    void OnCancel(object s, RoutedEventArgs e) => DialogResult = false;
}
