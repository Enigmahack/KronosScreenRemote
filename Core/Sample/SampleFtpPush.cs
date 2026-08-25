namespace KronosScreenRemote;

using System.IO;
using FluentFTP;

// Upload-direction mirror of SampleFtpClosure (Core/Sample/SampleFtpClosure.cs) - pushes
// a WHOLE local collection (its .KSC + every listed .KMP + every non-skipped zone's
// .KSF) to an arbitrary remote destination folder over an already-connected FTP client,
// replaying the exact same folder convention SampleFtpClosure's pull side already relies
// on (<dest>/<ksc-basename>/<kmp-name>, <dest>/<ksc-basename>/<kmp-basename>/<ksf-name>).
// Used by the tree's right-click "Push to Kronos..." (2026-08-24) - a genuinely new
// upload-to-a-chosen-folder action, distinct from IRemoteSampleSource.PushAsync's
// existing single-file "push back to wherever this was pulled from" contract.
static class SampleFtpPush
{
    public static async Task<List<string>> PushClosureAsync(
        AsyncFtpClient client, string localKscPath, KscCollection collection, string remoteDestDir, Action<string>? onProgress = null)
    {
        var failures = new List<string>();
        var kscName = Path.GetFileName(localKscPath);
        var remoteDestDirTrimmed = remoteDestDir.TrimEnd('/');
        var kscRemotePath = $"{remoteDestDirTrimmed}/{kscName}";

        await UploadOneAsync(client, localKscPath, kscRemotePath, onProgress, failures);

        // _UserBank.KSC (2026-08-25) - SaveCollectionWithUserBank already wrote this
        // sibling locally as part of any local save, so pushing just uploads whatever's
        // already there rather than regenerating it again. Derived from `localKscPath`
        // directly (mirrors KscCollection.UserBankPath's own logic) rather than reading
        // `collection.Path` - that field is only populated by KscCollection.Save's own
        // side effect, so it can still be null here for a collection that was loaded
        // and pushed without ever going through a local Save in THIS instance's
        // lifetime. Not a push failure if the file is missing (an older collection
        // saved before this existed) - it's a derived convenience file, not owned data.
        var userBankLocalPath = Path.Combine(
            Path.GetDirectoryName(localKscPath) ?? "",
            Path.GetFileNameWithoutExtension(localKscPath) + "_UserBank.KSC");
        if (File.Exists(userBankLocalPath))
        {
            var userBankRemotePath = $"{remoteDestDirTrimmed}/{Path.GetFileName(userBankLocalPath)}";
            await UploadOneAsync(client, userBankLocalPath, userBankRemotePath, onProgress, failures);
        }

        var contentDir = KscCollection.ContentDirFor(localKscPath);
        var kscBaseRemoteDir = $"{remoteDestDirTrimmed}/{Path.GetFileNameWithoutExtension(kscName)}";
        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var kmpLocalPath = Path.Combine(contentDir, entry);
            if (!File.Exists(kmpLocalPath))
            {
                // Same "missing NEWMS000/NEWMS001 is normal" exemption the pull side and
                // RebuildTreeFromCollection already apply - not a real push failure.
                if (!KronosScreenRemote.ViewModels.SampleEditorViewModel.IsIgnorablePlaceholderKmp(entry))
                    failures.Add($"{entry}: not found locally");
                continue;
            }
            var kmpRemotePath = $"{kscBaseRemoteDir}/{entry}";
            await UploadOneAsync(client, kmpLocalPath, kmpRemotePath, onProgress, failures);

            KmpMultisample? m;
            try { m = KmpMultisample.Open(File.ReadAllBytes(kmpLocalPath)); }
            catch (Exception ex) { failures.Add($"{entry}: couldn't read to find its zones ({ex.Message})"); continue; }
            if (m == null) { failures.Add($"{entry}: not a recognizable .KMP"); continue; }

            var kmpBaseRemoteDir = kmpRemotePath[..^4]; // strip ".KMP" (4 chars, guaranteed by the check above)
            foreach (var zone in m.Zones)
            {
                if (zone.IsSkipped) continue;
                var ksfLocalPath = zone.KsfPath(kmpLocalPath);
                if (!File.Exists(ksfLocalPath)) { failures.Add($"{zone.Filename}: not found locally"); continue; }
                var ksfRemotePath = $"{kmpBaseRemoteDir}/{zone.Filename}";
                await UploadOneAsync(client, ksfLocalPath, ksfRemotePath, onProgress, failures);
            }
        }

        return failures;
    }

    static async Task UploadOneAsync(AsyncFtpClient client, string localPath, string remotePath, Action<string>? onProgress, List<string> failures)
    {
        onProgress?.Invoke($"Uploading {Path.GetFileName(localPath)}...");
        try
        {
            var status = await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
            if (status != FtpStatus.Success) failures.Add($"{Path.GetFileName(localPath)}: upload did not complete ({status})");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Sample push: '{localPath}' -> '{remotePath}' failed: {ex.Message}");
            failures.Add($"{Path.GetFileName(localPath)}: {ex.Message}");
        }
    }
}
