using System.IO;
using System.Windows;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

// Production IRemotePcgSource: the real "Load PCG… From Kronos" flow — FTP login prompt, then
// browse/download over one connection. It owns BOTH untestable halves (KronosFtpSession.
// EnsureLoginAsync and RemoteFilePickerDialog) so a self-test can replace the whole pick with
// an in-memory fake, symmetric with the ISysExService seam behind the pull pipeline.
//
// `owner` is captured here rather than passed into PickAsync so the interface stays WPF-free:
// it's known at call time (the RelayCommand's parameter), and LibrarianShellViewModel builds
// one of these per invocation with the current owner/settings/host.
sealed class KronosRemotePcgSource : IRemotePcgSource
{
    readonly Window _owner;
    readonly AppSettings _settings;
    readonly string _host;
    readonly string _extension;

    public KronosRemotePcgSource(Window owner, AppSettings settings, string host, string extension = ".pcg")
    {
        _owner = owner;
        _settings = settings;
        _host = host;
        _extension = extension;
    }

    public async Task<RemotePcgPick> PickAsync()
    {
        if (!await KronosFtpSession.EnsureLoginAsync(_owner, _settings, _host))
            return RemotePcgPick.Failed("FTP login failed or was cancelled.");

        // The picker downloads the selected file itself, over the one connection it opened to
        // browse — no second connection here. Opening a second one right after the first closes
        // risked hanging: the Kronos's FTP server appears to hold a session open until its own
        // timeout unless sent a clean QUIT (see RemoteFilePickerDialog's own comment), so a
        // second connect could be left waiting for a session slot.
        var picker = new RemoteFilePickerDialog(_host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword, _extension)
        {
            Owner = _owner,
        };
        if (picker.ShowDialog() != true || picker.DownloadedTempPath == null)
            return RemotePcgPick.Failed("Load from Kronos cancelled — the previously loaded file (if any) is unchanged.");

        try
        {
            var bytes = await File.ReadAllBytesAsync(picker.DownloadedTempPath);
            return RemotePcgPick.Ok(bytes, Path.GetFileName(picker.DownloadedTempPath));
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from Kronos failed: {ex}");
            return RemotePcgPick.Failed($"Load failed: {ex.Message}");
        }
    }
}
