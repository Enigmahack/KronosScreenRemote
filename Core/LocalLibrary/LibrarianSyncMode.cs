namespace KronosScreenRemote;

// Which direction the Librarian's Sync Library button moves data. Each value maps 1:1 onto a
// SyncPipeline entry point that already existed - this enum is what the UI selects between, not
// a new transfer mechanism.
public enum LibrarianSyncMode
{
    // SyncPipeline.PullAsync. Instrument -> local, no push half.
    //
    // On its own a pull never destroys a local edit: LibraryPullPipeline records fresh baselines
    // and marks a locally-dirty object whose bank ALSO moved on hardware as Conflicted, leaving
    // both versions intact. "Pull Only" as a user-facing action means something stronger - make
    // the local library a mirror of the instrument - so the shell discards every pending local
    // change first, behind a confirm. See LibrarianShellViewModel.PullOnlyAsync.
    PullOnly,

    // SyncPipeline.CommitChangesAsync, which is a direct alias for PushAsync. Local -> instrument,
    // no pull half. This is exactly what the old "Commit Changes" button ran.
    PushOnly,

    // SyncPipeline.SyncLibraryAsync - pull, then push. What the Sync Library button has always
    // done, and the default for a fresh install.
    TwoWay,
}

public static class LibrarianSyncModeText
{
    // The button's own label. It always names the mode that a plain click will run, which is what
    // makes remembering the last-used mode safe: a persisted PushOnly can never masquerade as the
    // non-destructive default.
    public static string Label(this LibrarianSyncMode mode) => mode switch
    {
        LibrarianSyncMode.PullOnly => "Pull Only",
        LibrarianSyncMode.PushOnly => "Push Only",
        _                          => "2-Way Sync",
    };

    public static string Tooltip(this LibrarianSyncMode mode) => mode switch
    {
        LibrarianSyncMode.PullOnly =>
            "Replace the local library with what is on the Kronos. Pending local changes are discarded (you are asked first).",
        LibrarianSyncMode.PushOnly =>
            "Write every pending local change to the Kronos. If the Kronos has changed since the last sync you are asked before overwriting it.",
        _ =>
            "Pull the library, then push every pending local change.",
    };
}
