namespace KronosScreenRemote.ViewModels;

// The Sample Editor's FTP seam, symmetric with IRemotePcgSource: production code owns
// BOTH untestable halves (login + browse/download), a self-test injects an in-memory
// fake. Unlike IRemotePcgSource's single-file pick, pulling a .KSC/.KMP means pulling
// its whole dependency closure (the .KSC + every listed .KMP + every non-skipped zone's
// .KSF) - the real folder convention (a .KMP's zones live in <kmp-basename>/ beside it,
// a .KSC's multisamples live in <ksc-basename>/ beside it, see KmpZone.KsfPath and
// SampleEditorViewModel.RebuildTreeFromCollection) is identical on the Kronos and on
// disk, so a pull just replays that same relative layout under localRoot.
interface IRemoteSampleSource
{
    // Let the user browse and pick one remote file matching extensionFilter (".KSC" or
    // ".KMP"), download it plus its full dependency closure, and return the local path
    // of what was picked plus a local-path -> remote-path map for every file pulled
    // (used later to resolve where PushAsync should send an edited file back to). Each
    // pulled file's local path mirrors its full remote path under localRoot (leading
    // '/' stripped), so pulls from different remote directories into the same
    // localRoot never collide. LocalPath is null on cancel/failure, with StatusMessage
    // explaining why.
    Task<RemoteSamplePullResult> PickAndPullAsync(string extensionFilter, string localRoot);

    // Upload one local file back to the given remote path (overwriting it) - the
    // inverse of PickAndPullAsync for a single file, used once a pulled file has been
    // edited and saved locally.
    Task<RemoteSamplePushResult> PushAsync(string localPath, string remotePath);
}

readonly record struct RemoteSamplePullResult(
    string? LocalPath, IReadOnlyDictionary<string, string>? RemoteMap, string StatusMessage)
{
    public static RemoteSamplePullResult Ok(string localPath, Dictionary<string, string> remoteMap) =>
        new(localPath, remoteMap, "");
    public static RemoteSamplePullResult Failed(string statusMessage) => new(null, null, statusMessage);
}

readonly record struct RemoteSamplePushResult(bool Ok, string StatusMessage)
{
    public static RemoteSamplePushResult Success(string statusMessage) => new(true, statusMessage);
    public static RemoteSamplePushResult Failed(string statusMessage) => new(false, statusMessage);
}
