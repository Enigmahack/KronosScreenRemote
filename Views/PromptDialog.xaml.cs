using System.Windows;

namespace KronosScreenRemote;

public partial class PromptDialog : ThemedWindow
{
    public string? Result { get; private set; }

    // maxLength: WPF TextBox.MaxLength enforces this on both typing AND paste, so
    // callers with a hard field-width limit (Kronos Sample/MS names - 22 characters,
    // hardware-verified 2026-09-04) don't need their own separate validation.
    public PromptDialog(string prompt, string initial = "", int? maxLength = null)
    {
        InitializeComponent();
        Title            = prompt;
        PromptLabel.Text = prompt;
        InputBox.Text    = initial;
        if (maxLength is { } max) InputBox.MaxLength = max;
        Loaded += (_, _) => { InputBox.SelectAll(); InputBox.Focus(); };
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        Result       = InputBox.Text.Trim();
        DialogResult = true;
    }

    void OnCancel(object s, RoutedEventArgs e) => DialogResult = false;
}
