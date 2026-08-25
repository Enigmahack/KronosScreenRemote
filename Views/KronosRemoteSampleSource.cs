using System.IO;
using System.Windows;
using FluentFTP;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

// Production IRemoteSampleSource: owns both untestable halves (FTP login, then
// browse + dependency-closure download via SampleRemoteBrowserDialog) so a self-test
// can replace the whole thing with an in-memory fake - symmetric with
// KronosRemotePcgSource, just closure-shaped instead of single-file.
sealed class KronosRemoteSampleSource : IRemoteSampleSource
{
    readonly Window _owner;
    readonly AppSettings _settings;
    readonly string _host;

    public KronosRemoteSampleSource(Window owner, AppSettings settings, string host)
    {
        _owner = owner;
        _settings = settings;
        _host = host;
    }

    public async Task<RemoteSamplePullResult> PickAndPullAsync(string extensionFilter, string localRoot)
    {
        if (!await KronosFtpSession.EnsureLoginAsync(_owner, _settings, _host))
            return RemoteSamplePullResult.Failed(AppMessages.Librarian.Pcg.FtpLoginFailedOrCancelled);

        var dlg = new SampleRemoteBrowserDialog(_host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword, extensionFilter, localRoot)
        {
            Owner = _owner,
        };
        if (dlg.ShowDialog() != true || dlg.PickedLocalPath == null || dlg.RemoteMap == null)
            return RemoteSamplePullResult.Failed(AppMessages.Librarian.Pcg.LoadFromKronosCancelled);

        return RemoteSamplePullResult.Ok(dlg.PickedLocalPath, new Dictionary<string, string>(dlg.RemoteMap));
    }

    // A dedicated connection for the push - the pull's own connection (opened inside
    // SampleRemoteBrowserDialog) is long closed by the time an edited file gets saved
    // and pushed back, so there's no "same connection" to reuse here.
    public async Task<RemoteSamplePushResult> PushAsync(string localPath, string remotePath)
    {
        if (!await KronosFtpSession.EnsureLoginAsync(_owner, _settings, _host))
            return RemoteSamplePushResult.Failed(AppMessages.Librarian.Pcg.FtpLoginFailedOrCancelled);

        using var client = KronosFtpSession.CreateClient(_host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword);
        try
        {
            await client.Connect();
            var status = await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
            try { await client.Disconnect(); } catch { }
            return status == FtpStatus.Success
                ? RemoteSamplePushResult.Success($"Pushed '{Path.GetFileName(localPath)}' to the Kronos.")
                : RemoteSamplePushResult.Failed($"Push of '{Path.GetFileName(localPath)}' did not complete ({status}).");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample push '{localPath}' -> '{remotePath}' failed: {ex}");
            return RemoteSamplePushResult.Failed($"Push failed: {ex.Message}");
        }
    }

    // The folder-pick dialog does BOTH the browse AND the upload over its own single
    // connection before closing (SampleRemoteBrowserDialog.SelectFolderAndPushAsync) -
    // same one-connection discipline PickAndPullAsync above relies on, same reason (that
    // class's own header comment: a second connection right after the first closes
    // risked hanging on the Kronos's FTP server). Nothing left to do here once it
    // returns except report the result.
    public async Task<RemoteCollectionPushResult> PickFolderAndPushCollectionAsync(string localKscPath, KscCollection collection)
    {
        if (!await KronosFtpSession.EnsureLoginAsync(_owner, _settings, _host))
            return RemoteCollectionPushResult.Failed(AppMessages.Librarian.Pcg.FtpLoginFailedOrCancelled);

        var dlg = new SampleRemoteBrowserDialog(_host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword, localKscPath, collection)
        {
            Owner = _owner,
        };
        if (dlg.ShowDialog() != true || dlg.SelectedRemoteDir == null)
            return RemoteCollectionPushResult.Failed("Push to Kronos cancelled.");

        return RemoteCollectionPushResult.Success(
            $"Pushed '{Path.GetFileName(localKscPath)}' and its content to '{dlg.SelectedRemoteDir}' on the Kronos.");
    }
}
