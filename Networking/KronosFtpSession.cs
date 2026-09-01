using FluentFTP;
using System.Windows;

namespace KronosScreenRemote;

static class KronosFtpSession
{
    // Shared across every caller (MainWindow's own connect flow, the new Librarian's PCG
    // pane, ...) - this is genuinely one FTP session concept for whichever Kronos is
    // currently connected, not something each feature should track separately.
    // The full identity a verification actually proved, not just the host. Keying on the host
    // alone meant changing the port or the username in Settings still hit the cached "yes" and
    // skipped verification entirely, so credentials that had never been checked were treated as
    // known-good. Comparing the whole tuple makes any settings change invalidate the cache by
    // construction, with no reset call site to remember.
    static (string Host, int Port, string User)? _authIdentity;

    // Call when an existing FTP session is known to be invalid (e.g. the daemon connection
    // itself rejected auth) so the next EnsureLoginAsync re-verifies instead of trusting a
    // stale "already authenticated" flag.
    public static void ResetAuthentication() => _authIdentity = null;

    // Mutates `settings` in place (Username/Password) and saves it if the user checked
    // "save password".
    public static async Task<bool> EnsureLoginAsync(Window owner, AppSettings settings, string host)
    {
        if (_authIdentity == (host, settings.FtpPort, settings.FtpUsername)) return true;
        _authIdentity = null;

        // Silent verify with cached credentials - if they work, skip the dialog entirely.
        if (!string.IsNullOrEmpty(settings.FtpUsername))
        {
            var (silentOk, _) = await VerifyAsync(host, settings.FtpPort, settings.FtpUsername, settings.FtpPassword)
                .ConfigureAwait(false);
            if (silentOk)
            {
                _authIdentity = (host, settings.FtpPort, settings.FtpUsername);
                return true;
            }
        }

        // Prompt - up to 3 interactive attempts regardless of silent verify outcome.
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
            _authIdentity = (host, settings.FtpPort, settings.FtpUsername);
            return true;
        }

        if (exhausted)
        {
            await owner.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(owner,
                    AppMessages.Ftp.AuthFailedAfterAttempts,
                    AppMessages.Titles.AuthenticationFailed, MessageBoxButton.OK, MessageBoxImage.Error))
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
