namespace KronosScreenRemote;

using System.IO;

// Local root for Sample Editor content pulled from the Kronos over FTP. Deliberately
// NOT LocalObjectStore's blob model - pulled .KSC/.KMP/.KSF content mirrors the remote
// directory structure verbatim (same folder convention both sides, see KmpZone.KsfPath),
// since that naming/nesting is load-bearing for the format itself, not an implementation
// detail a content-addressed store could hide.
//
// Defaults under the OS temp dir, not DataDir - DataDir is routinely an SMB-mounted
// share (see Storage.DataDir), and pulled sample audio is disposable working content,
// not something that belongs on a slow network mount or needs to survive a reinstall.
static class SampleWorkspace
{
    public static string ResolveRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SampleWorkspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "kronos_sample_workspace")
            : settings.SampleWorkspaceRoot;
}
