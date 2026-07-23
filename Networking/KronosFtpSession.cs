using FluentFTP;
using System.Windows;

namespace KronosScreenRemote;

static class KronosFtpSession
{
    // Shared across every caller (MainWindow's own connect flow, the new Librarian's PCG
    // pane, ...) — this is genuinely one FTP session concept for whichever Kronos is
    // currently connected, not something each feature should track separately.
    static bool _authenticated;
    static string? _authenticatedHost;

    // Call when an existing FTP session is known to be invalid (e.g. the daemon connection
    // itself rejected auth) so the next EnsureLoginAsync re-verifies instead of trusting a
    // stale "already authenticated" flag.
    public static void ResetAuthentication() => _authenticated = false;

    // Promoted from MainWindow.Streaming.cs's private EnsureFtpLoginAsync (behavior-
    // preserving) so a second caller (PcgPaneViewModel.LoadFromKronosAsync) doesn't need
    // its own copy of the silent-verify-then-LoginDialog dance. Mutates `settings` in place
    // (Username/Password) and saves it if the user checked "save password", exactly as the
    // original did.
    public static async Task<bool> EnsureLoginAsync(Window owner, AppSettings settings, string host)
    {
        if (_authenticated && _authenticatedHost == host) return true;
        _authenticated = false;

        // Silent verify with cached credentials — if they work, skip the dialog entirely.
        if (!string.IsNullOrEmpty(settings.FtpUsername))
        {
            var (silentOk, _) = await VerifyAsync(host, settings.FtpPort, settings.FtpUsername, settings.FtpPassword)
                .ConfigureAwait(false);
            if (silentOk)
            {
                _authenticated = true;
                _authenticatedHost = host;
                return true;
            }
        }

        // Prompt — up to 3 interactive attempts regardless of silent verify outcome.
        bool dialogOk = false, exhausted = false;
        await owner.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new LoginDialog(host, settings.FtpPort, settings.FtpUsername, settings.FtpPassword, attemptsAllowed: 3)
                      { Owner = owner };
            dialogOk = dlg.ShowDialog() == true;
            exhausted = dlg.ExhaustedAttempts;
            if (dialogOk)
            {
                settings.FtpUsername = dlg.Username;
                settings.FtpPassword = dlg.Password;
                if (dlg.SavePassword) Storage.SaveSettings(settings);
            }
        }).Task.ConfigureAwait(false);

        if (dialogOk)
        {
            _authenticated = true;
            _authenticatedHost = host;
            return true;
        }

        if (exhausted)
        {
            await owner.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(owner,
                    "FTP authentication failed after 3 attempts.\nTry again.",
                    "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Error))
                .Task.ConfigureAwait(false);
        }

        return false;
    }

    public static async Task<(bool ok, string error)> VerifyAsync(
        string host, int port, string user, string pass)
    {
        using var c   = BuildClient(host, port, user, pass);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await c.Connect(cts.Token).ConfigureAwait(false);
            try { await c.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static AsyncFtpClient CreateClient(string host, int port, string user, string pass)
    {
        var c = BuildClient(host, port, user, pass);
        c.Config.ReadTimeout = 30_000;
        return c;
    }

    static AsyncFtpClient BuildClient(string host, int port, string user, string pass)
    {
        var c = new AsyncFtpClient(host, user, pass, port);
        c.Config.ConnectTimeout     = 6_000;
        c.Config.ReadTimeout        = 8_000;
        c.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        c.Config.EncryptionMode     = FtpEncryptionMode.None;
        return c;
    }
}
