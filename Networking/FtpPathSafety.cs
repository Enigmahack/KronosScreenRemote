using FluentFTP;

namespace KronosScreenRemote;

// Guards against renaming a Kronos's top-level FTP storage volume (SSD1/SSD2/SSD3/...,
// whatever the mounted BusyBox ftpd exposes at the root). FileManagerWindow's own Rename
// command was found capable of doing exactly this - the Kronos's OS/firmware expects a
// fixed name at each of those mount points, so renaming one is a real risk of corrupting
// or bricking the unit's storage naming, not just a cosmetic mistake the user can undo by
// renaming it back.
//
// RenameGuardedAsync is the ONE choke point every FTP rename in the app is expected to go
// through (FileManagerWindow's explicit Rename command, its Cut/Paste-as-move path, and
// the atomic upload-then-rename-into-place pattern SampleFtpPush/KronosRemoteSampleSource
// both use) - calling AsyncFtpClient.Rename directly anywhere bypasses this, so it's the
// one to reach for instead of the raw client method whenever a `.Rename(` shows up in a
// diff. Deliberately checks BOTH paths, not just the source: this app's own rename call
// sites never actually change which parent directory a path lives under (a real MOVE
// across directories still keeps the same depth), but the guard shouldn't rely on that
// staying true forever.
static class FtpPathSafety
{
    // A path is "top-level" when it names a direct child of the FTP root - no other
    // slash in it besides (at most) the leading one.
    public static bool IsTopLevelPath(string ftpPath)
    {
        var clean = ftpPath.TrimEnd('/');
        var slash = clean.LastIndexOf('/');
        return slash <= 0;
    }

    public static async Task RenameGuardedAsync(this AsyncFtpClient client, string oldPath, string newPath)
    {
        if (IsTopLevelPath(oldPath) || IsTopLevelPath(newPath))
            throw new InvalidOperationException(
                $"Refusing to rename '{oldPath}' - top-level Kronos storage volumes (SSD1/SSD2/SSD3/...) can never be renamed or moved.");
        await client.Rename(oldPath, newPath);
    }

    // Hardware-verified 2026-09-04: the Kronos's own filesystem refuses a path once its
    // TOTAL length from the FTP root (every "/", "SSD1"/etc., and folder/file name along
    // the way) passes 245 characters - found by creating nested folders on a real unit
    // until folder creation itself started failing. Character count, not byte count -
    // matches how the Sample/MS 22-character name limit is enforced (WPF's
    // TextBox.MaxLength, itself a character count), and both limits came from the same
    // round of hardware testing.
    //
    // Checked proactively wherever a user (or this app) is about to EXTEND a remote
    // path's length - New Folder and Rename in both FileManagerWindow and
    // SampleRemoteBrowserDialog, and Push's own destination-path construction - so the
    // failure shows up as a clear, actionable message before the fact (or, for Push,
    // before uploading most of a collection only to fail on one deep file) rather than a
    // bare FTP error after it.
    public const int MaxRemotePathLength = 245;

    public static bool FitsMaxRemotePathLength(string ftpPath) => ftpPath.Length <= MaxRemotePathLength;

    public static string TooLongMessage(string ftpPath) =>
        $"That would make the remote path {ftpPath.Length} characters long - the Kronos's own filesystem "
        + $"refuses anything over {MaxRemotePathLength}. Try a shorter name, or a shallower destination folder.";
}
