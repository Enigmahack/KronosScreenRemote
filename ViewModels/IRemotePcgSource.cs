namespace KronosScreenRemote.ViewModels;

// The librarian's one missing seam: "pick a .pcg from the Kronos and hand back its bytes."
//
// PcgPaneViewModel.LoadFromKronosAsync used to do the login prompt, browse dialog, and
// download inline — constructing a Window and talking to the Kronos's FTP server — which is
// the single librarian branch the off-hardware self-tests can't reach. This interface pulls
// those two untestable halves (login + browse/download) behind one boundary, exactly the way
// ISysExService seams the hardware off the pull pipeline. The production implementation
// (KronosRemotePcgSource) owns BOTH halves; a self-test injects an in-memory fake.
interface IRemotePcgSource
{
    // Log in (if needed), let the user browse and pick one file, download it, and return its
    // bytes. On cancel or failure, File is null and StatusMessage explains why (login failed
    // vs. cancelled vs. download error) so the pane can show the same specific text the old
    // inline flow did. On success the pane's own Load() sets the status, so StatusMessage is
    // unused there.
    Task<RemotePcgPick> PickAsync();
}

// Result of a pick. File is non-null exactly when a file was downloaded successfully.
readonly record struct RemotePcgPick(RemotePcgFile? File, string StatusMessage)
{
    public static RemotePcgPick Ok(byte[] bytes, string fileName) =>
        new(new RemotePcgFile(bytes, fileName), "");

    public static RemotePcgPick Failed(string statusMessage) => new(null, statusMessage);
}

// A downloaded remote file: its raw bytes plus a leaf name for display.
readonly record struct RemotePcgFile(byte[] Bytes, string FileName);
