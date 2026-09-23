using System.Windows;

namespace KronosScreenRemote;

// Default "can't reach the device" popup for OnSessionConnectionFailed - replaces the old
// per-error-type MessageBoxes (and the FTP-login-as-failure-response flow) with one consistent
// message and a way straight to the Connection settings tab.
public partial class ConnectionFailedDialog : ThemedWindow
{
    public bool OpenSettings { get; private set; }

    public ConnectionFailedDialog(string message)
    {
        InitializeComponent();
        MessageLabel.Text = message;
    }

    void OnSettings(object s, RoutedEventArgs e)
    {
        OpenSettings = true;
        DialogResult = true;
    }

    void OnOk(object s, RoutedEventArgs e) => DialogResult = true;
}
