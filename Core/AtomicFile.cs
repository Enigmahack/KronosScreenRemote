namespace KronosScreenRemote;

using System.IO;

// Crash-safe whole-file replacement. Every persisted file this app owns (the Local Library
// index, the merge-cache snapshot, .KSF/.KMP/.KSC) is rewritten whole rather than patched,
// and DataDir is routinely an SMB-mounted share where that rewrite is a multi-second window -
// long enough that an interrupted in-place write is a realistic outcome, not a theoretical
// one. A truncated file is indistinguishable from "no file yet" to a reader that only checks
// File.Exists, which is how a corrupt index turned into a silently empty library.
//
// File.Replace is deliberately NOT used: it is unreliable over SMB, which is exactly where
// this matters most. Two same-directory renames are cheap everywhere and give the same
// indivisibility.
static class AtomicFile
{
    // Writes via `writeTo` to a sibling temp file, then swaps it in by rename. At no instant
    // is content unrecoverable: crash before the swap and the real path still holds the old
    // version; crash mid-swap and .bak holds it while the complete new version sits in .tmp.
    // CandidatesForRead below is the other half of that guarantee.
    //
    // keepBackup retains the outgoing content as a sibling .bak. Worth it for the small JSON
    // stores, whose loss is unrecoverable and whose size is trivial; NOT worth it for .KSF
    // audio, where it would silently double a workspace already living on a network share and
    // where the remote copy is the real backup. Without it the swap is a single rename, which
    // is still indivisible - only the previous version is not kept.
    public static void Write(string path, Action<string> writeTo, bool keepBackup = true)
    {
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        writeTo(tmp);

        bool movedAside = false;
        if (keepBackup && File.Exists(path))
        {
            File.Move(path, bak, overwrite: true);
            movedAside = true;
        }

        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The swap failed (a share hiccup, a transient lock). Put back exactly what was
            // there and take the temp with us, so a failed write is a no-op rather than a
            // deletion - the one outcome that would be WORSE than the truncation this class
            // exists to prevent. Without keepBackup nothing was moved aside in the first
            // place, so `path` still holds the old content and only the temp needs clearing.
            if (movedAside) { try { File.Move(bak, path, overwrite: true); } catch { } }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes, bool keepBackup = true) =>
        Write(path, tmp => File.WriteAllBytes(tmp, bytes), keepBackup);

    public static void WriteAllText(string path, string text, bool keepBackup = true) =>
        Write(path, tmp => File.WriteAllText(tmp, text), keepBackup);

    // The paths a reader should try, best first. A .tmp is only ever preferred over a missing
    // real path, which can only happen after Write's first rename - so reaching it means the
    // write had already completed and only the second rename was lost. Callers that can
    // validate content (a JSON parse) should walk the whole list; callers that cannot should
    // take the first entry.
    public static IEnumerable<string> CandidatesForRead(string path)
    {
        if (File.Exists(path)) yield return path;
        if (File.Exists(path + ".tmp")) yield return path + ".tmp";
        if (File.Exists(path + ".bak")) yield return path + ".bak";
    }
}
