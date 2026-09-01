namespace KronosScreenRemote;

using System.IO;

// Every local path in the sample editor is derived from a NAME that came out of a file: a
// .KSC's plain entry lines, a .KMP zone's 12-byte filename field, an FTP listing. None of
// those are validated by the formats themselves, and both derivations are Path.Combine-based,
// which silently discards everything to its left when handed a rooted name ("C:\x.KSF") and
// happily walks upward on "..". A corrupt or hand-edited manifest could therefore steer a
// pull, or a save, outside the workspace it was supposed to stay inside.
//
// This is defense in depth, not an exploit fix - the input is the user's own Kronos - so it
// throws IOException rather than introducing a new failure category. Both pull call sites
// already catch and route into their user-visible `failures` list, so a bad entry surfaces in
// the same "Some Files Didn't Download" dialog as an unreachable one.
static class SamplePathGuard
{
    public static string EnsureUnder(string root, string candidate, string forDisplay)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        // TrimEnd then re-append: GetFullPath preserves a trailing separator on some inputs and
        // strips it on others, and without normalising, a root of "C:\ws\" compares unequal to
        // the prefix of its own children.
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"'{forDisplay}' resolves outside the collection folder");
        return fullCandidate;
    }
}
