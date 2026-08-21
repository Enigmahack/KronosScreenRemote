namespace KronosScreenRemote;

using System.IO;
using FluentFTP;

// Shared "pull a .KSC/.KMP plus its whole dependency closure over an already-connected
// FTP client" logic - the .KSC + every listed .KMP + every non-skipped zone's .KSF,
// mirroring the exact folder convention the format itself uses (see KmpZone.KsfPath).
// Used by both SampleRemoteBrowserDialog (interactive, one connection per browse+pull)
// and Tools/SampleFtpPullCheck.cs (headless, real-hardware verification with no Window).
static class SampleFtpClosure
{
    // Every pulled file's local path mirrors its full remote path under localRoot -
    // this is what lets OpenCollection/KmpZone.KsfPath (which independently recompute
    // local paths from the picked .KSC's own path, with no knowledge of the returned
    // map) find exactly what was pulled here, without duplicating their path logic.
    public static string LocalPathFor(string localRoot, string remoteFullPath) =>
        Path.Combine(localRoot, remoteFullPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public static async Task<(string localPath, Dictionary<string, string> remoteMap, List<string> failures)> PullAsync(
        AsyncFtpClient client, string remoteEntryPath, string localRoot, Action<string>? onProgress = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Every KMP/KSF that couldn't be downloaded, with why - previously only logged
        // (AppLog.Warn), so a wrong path or a missing file on the Kronos produced a
        // collection that "loaded" (status text reporting the .KSC's own raw entry
        // count) with an EMPTY tree and no visible explanation why. Returned so the
        // caller can put this in front of the user instead of just the log file.
        var failures = new List<string>();
        var name = Path.GetFileName(remoteEntryPath);
        var slash = remoteEntryPath.LastIndexOf('/');
        var remoteDir = slash <= 0 ? "/" : remoteEntryPath[..slash];

        var bytes = await DownloadOneAsync(client, remoteEntryPath, localRoot, map, onProgress);

        if (name.EndsWith(".KSC", StringComparison.OrdinalIgnoreCase))
        {
            var collection = KscCollection.Open(bytes);
            var kscBaseRemoteDir = $"{remoteDir.TrimEnd('/')}/{Path.GetFileNameWithoutExtension(name)}";
            foreach (var kmpName in collection.Entries)
            {
                if (!kmpName.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
                var kmpRemotePath = $"{kscBaseRemoteDir}/{kmpName}";
                byte[] kmpBytes;
                try { kmpBytes = await DownloadOneAsync(client, kmpRemotePath, localRoot, map, onProgress); }
                catch (Exception ex)
                {
                    AppLog.Warn($"Sample pull: skipping unreachable multisample '{kmpRemotePath}': {ex.Message}");
                    // NEWMS000.KMP/NEWMS001.KMP are the Kronos's own default placeholder
                    // multisample names, always present (and often empty/unpopulated) on
                    // a brand-new library - not finding one on the Kronos is the normal
                    // state, not a real pull failure worth surfacing to the user.
                    if (!KronosScreenRemote.ViewModels.SampleEditorViewModel.IsIgnorablePlaceholderKmp(kmpName))
                        failures.Add($"{kmpName}: {ex.Message}");
                    continue;
                }
                await PullZonesAsync(client, kmpBytes, kmpRemotePath, localRoot, map, onProgress, failures);
            }
        }
        else if (name.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase))
        {
            await PullZonesAsync(client, bytes, remoteEntryPath, localRoot, map, onProgress, failures);
        }

        return (LocalPathFor(localRoot, remoteEntryPath), map, failures);
    }

    static async Task<byte[]> DownloadOneAsync(AsyncFtpClient client, string remotePath, string localRoot,
        Dictionary<string, string> map, Action<string>? onProgress)
    {
        var localPath = LocalPathFor(localRoot, remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        onProgress?.Invoke($"Downloading {Path.GetFileName(remotePath)}...");
        await client.DownloadFile(localPath, remotePath);
        map[localPath] = remotePath;
        return await File.ReadAllBytesAsync(localPath);
    }

    // KmpZone.KsfPath's convention is <kmp-dir>/<kmp-basename>/<filename> - stripping
    // the trailing ".KMP" (4 chars, guaranteed by the caller's own extension check)
    // from the remote .KMP path gives exactly "<kmp-dir>/<kmp-basename>" in one step.
    static async Task PullZonesAsync(AsyncFtpClient client, byte[] kmpBytes, string kmpRemotePath, string localRoot,
        Dictionary<string, string> map, Action<string>? onProgress, List<string> failures)
    {
        var m = KmpMultisample.Open(kmpBytes);
        if (m == null) return;
        var kmpBaseRemoteDir = kmpRemotePath[..^4];
        foreach (var zone in m.Zones)
        {
            if (zone.IsSkipped) continue;
            var ksfRemotePath = $"{kmpBaseRemoteDir}/{zone.Filename}";
            try { await DownloadOneAsync(client, ksfRemotePath, localRoot, map, onProgress); }
            catch (Exception ex)
            {
                AppLog.Warn($"Sample pull: skipping unreachable sample '{ksfRemotePath}': {ex.Message}");
                failures.Add($"{zone.Filename}: {ex.Message}");
            }
        }
    }
}
