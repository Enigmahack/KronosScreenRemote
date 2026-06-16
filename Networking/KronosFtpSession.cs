using FluentFTP;

namespace KronosScreenRemote;

static class KronosFtpSession
{
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
