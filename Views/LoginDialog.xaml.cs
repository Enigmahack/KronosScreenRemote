using System.Windows;

namespace KronosScreenRemote;

public partial class LoginDialog : ThemedWindow
{
    readonly string _host;
    readonly int    _port;
    readonly int    _attemptsAllowed;  // 0 = unlimited (Settings menu use)
    int             _attemptsFailed;

    public string Username          { get; private set; } = "";
    public string Password          { get; private set; } = "";
    public bool   SavePassword      { get; private set; } = true;
    public bool   ExhaustedAttempts { get; private set; }

    public LoginDialog(string host, int port, string existingUser = "", string existingPass = "", int attemptsAllowed = 0)
    {
        _host            = host;
        _port            = port;
        _attemptsAllowed = attemptsAllowed;
        InitializeComponent();
        SubtitleText.Text = AppMessages.Login.Subtitle(host, port);
        TxtUser.Text      = existingUser;
        if (!string.IsNullOrEmpty(existingPass))
            PwdBox.Password = existingPass;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(existingUser)) TxtUser.Focus();
            else PwdBox.Focus();
        };
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var user = TxtUser.Text.Trim();
        if (string.IsNullOrEmpty(user))
        {
            ShowError(AppMessages.Login.UsernameRequired);
            return;
        }

        BtnOk.IsEnabled      = false;
        BtnCancel.IsEnabled  = false;
        BtnOk.Content        = "Verifying…";
        ErrorText.Visibility = Visibility.Collapsed;

        var pass = PwdBox.Password;
        bool ok; string error;
        try
        {
            (ok, error) = await Task.Run(() => KronosFtpSession.VerifyAsync(_host, _port, user, pass));
        }
        catch (Exception ex)
        {
            // Never let an exception escape this async void handler (would crash the app) or
            // leave the dialog stuck on "Verifying…" with both buttons disabled.
            BtnOk.IsEnabled     = true;
            BtnCancel.IsEnabled = true;
            BtnOk.Content       = "OK";
            ShowError(AppMessages.Login.Failed(ex.Message));
            return;
        }

        if (ok)
        {
            Username     = user;
            Password     = PwdBox.Password;
            SavePassword = ChkSave.IsChecked == true;
            DialogResult = true;
        }
        else
        {
            BtnOk.IsEnabled     = true;
            BtnCancel.IsEnabled = true;
            BtnOk.Content       = "OK";

            _attemptsFailed++;
            if (_attemptsAllowed > 0 && _attemptsFailed >= _attemptsAllowed)
            {
                ExhaustedAttempts = true;
                DialogResult      = false;
                return;
            }

            string suffix = _attemptsAllowed > 0
                ? AppMessages.Login.AttemptsRemaining(_attemptsAllowed - _attemptsFailed)
                : "";
            ShowError(AppMessages.Login.Failed($"{error}{suffix}"));
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        TxtUser.Clear();
        PwdBox.Clear();
        TxtUser.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void ShowError(string msg)
    {
        ErrorText.Text       = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
