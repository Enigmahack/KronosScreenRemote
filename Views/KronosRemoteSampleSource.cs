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
    static readonly TimeSpan PushConnectTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan PushUploadTimeout  = TimeSpan.FromMinutes(5);

    // Runs from a UI-thread click handler, so the two rules the rest of this codebase's FTP
    // code already follows are load-bearing here rather than stylistic:
    //   - every await is ConfigureAwait(false), and the client is disconnected/disposed on a
    //     background task rather than by a `using` that runs inline. FluentFTP's synchronous
    //     Dispose() can block on an async cleanup continuation that itself needs the UI
    //     thread - the deadlock SampleRemoteBrowserDialog.DisposeInBackground documents. With
    //     the continuation posted back to a UI thread already blocked inside Dispose, both
    //     Push menu items froze the whole application with no exit but force-quitting it.
    //   - Connect and the upload each carry their own CancellationToken deadline, because
    //     FluentFTP's async paths do not reliably honour Config.ConnectTimeout/ReadTimeout;
    //     without one, a Kronos FTP server that accepts the socket and then stalls hangs
    //     forever and reads to the user as the same freeze. The two timeout messages are
    //     deliberately distinct so a report from live hardware names which half stalled.
    public async Task<RemoteSamplePushResult> PushAsync(string localPath, string remotePath)
    {
        if (!await KronosFtpSession.EnsureLoginAsync(_owner, _settings, _host).ConfigureAwait(false))
            return RemoteSamplePushResult.Failed(AppMessages.Librarian.Pcg.FtpLoginFailedOrCancelled);

        var client = KronosFtpSession.CreateClient(_host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword);
        bool connected = false;
        try
        {
            using (var connectCts = new CancellationTokenSource(PushConnectTimeout))
                await client.Connect(connectCts.Token).ConfigureAwait(false);
            connected = true;

            // Upload to a unique sibling and promote only once verifiably complete - same
            // pattern as FileManagerWindow.UploadItemsAsync - so a disconnect/timeout mid-upload
            // can never truncate a previously-valid remote file.
            var part = $"{remotePath}.{Guid.NewGuid().ToString("N")[..8]}.part";
            FtpStatus status;
            using (var uploadCts = new CancellationTokenSource(PushUploadTimeout))
                status = await client.UploadFile(localPath, part, FtpRemoteExists.Overwrite,
                                                 createRemoteDir: true, token: uploadCts.Token).ConfigureAwait(false);

            if (status != FtpStatus.Success)
            {
                try { await client.DeleteFile(part).ConfigureAwait(false); } catch { }
                return RemoteSamplePushResult.Failed($"Push of '{Path.GetFileName(localPath)}' did not complete ({status}).");
            }

            if (await client.FileExists(remotePath).ConfigureAwait(false))
                await client.DeleteFile(remotePath).ConfigureAwait(false);
            await client.Rename(part, remotePath).ConfigureAwait(false);

            return RemoteSamplePushResult.Success($"Pushed '{Path.GetFileName(localPath)}' to the Kronos.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Error($"Sample push '{localPath}' -> '{remotePath}' timed out "
                + (connected ? "during the upload." : "connecting."));
            return RemoteSamplePushResult.Failed(connected
                ? $"Push timed out: the Kronos stopped responding partway through uploading '{Path.GetFileName(localPath)}' ({PushUploadTimeout.TotalMinutes:0} min). Nothing on the Kronos is guaranteed complete - check the file there before relying on it."
                : $"Push failed: could not connect to the Kronos at {_host} within {PushConnectTimeout.TotalSeconds:0} seconds. Nothing was uploaded.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sample push '{localPath}' -> '{remotePath}' failed: {ex}");
            return RemoteSamplePushResult.Failed($"Push failed: {ex.Message}");
        }
        finally
        {
            DisposeInBackground(client);
        }
    }

    // Same fire-and-forget background disconnect+dispose SampleRemoteBrowserDialog and
    // RemoteFilePickerDialog use, and for the same reason - see PushAsync's own comment.
    static void DisposeInBackground(AsyncFtpClient client) => Task.Run(async () =>
    {
        try { await client.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
        try { client.Dispose(); } catch { }
    });

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
