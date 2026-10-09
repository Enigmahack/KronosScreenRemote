using System.Windows;

namespace KronosScreenRemote;

public partial class SavedConnectionDialog : ThemedWindow
{
    public SavedConnection Result { get; private set; }

    public SavedConnectionDialog(SavedConnection seed, bool isNew)
    {
        InitializeComponent();
        Title         = isNew ? "New Saved Connection" : "Edit Saved Connection";
        Result        = seed;
        TxtName.Text  = seed.Name;
        TxtHost.Text  = seed.Host;
        TxtUser.Text  = seed.Username;
        TxtPort.Text  = seed.FtpPort.ToString();
        if (!string.IsNullOrEmpty(seed.Password)) PwdBox.Password = seed.Password;
        Loaded += (_, _) => TxtName.Focus();
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        var host = TxtHost.Text.Trim();
        if (host.Length == 0) { ShowError("Enter the instrument IP address."); return; }
        if (!int.TryParse(TxtPort.Text, out int port) || port is < 1 or > 65535)
        {
            ShowError("FTP port must be a number from 1 to 65535.");
            return;
        }
        var name = TxtName.Text.Trim();
        Result = new SavedConnection(name.Length > 0 ? name : host, host, TxtUser.Text.Trim(), PwdBox.Password, port);
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void ShowError(string msg)
    {
        ErrorText.Text       = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
