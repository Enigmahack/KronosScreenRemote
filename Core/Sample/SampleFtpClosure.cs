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

    public static async Task<(string localPath, Dictionary<string, string> remoteMap)> PullAsync(
        AsyncFtpClient client, string remoteEntryPath, string localRoot, Action<string>? onProgress = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    continue;
                }
                await PullZonesAsync(client, kmpBytes, kmpRemotePath, localRoot, map, onProgress);
            }
        }
        else if (name.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase))
        {
            await PullZonesAsync(client, bytes, remoteEntryPath, localRoot, map, onProgress);
        }

        return (LocalPathFor(localRoot, remoteEntryPath), map);
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
        Dictionary<string, string> map, Action<string>? onProgress)
    {
        var m = KmpMultisample.Open(kmpBytes);
        if (m == null) return;
        var kmpBaseRemoteDir = kmpRemotePath[..^4];
        foreach (var zone in m.Zones)
        {
            if (zone.IsSkipped) continue;
            var ksfRemotePath = $"{kmpBaseRemoteDir}/{zone.Filename}";
            try { await DownloadOneAsync(client, ksfRemotePath, localRoot, map, onProgress); }
            catch (Exception ex) { AppLog.Warn($"Sample pull: skipping unreachable sample '{ksfRemotePath}': {ex.Message}"); }
        }
    }
}
