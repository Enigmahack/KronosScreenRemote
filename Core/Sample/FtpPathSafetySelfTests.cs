namespace KronosScreenRemote;

// Off-hardware checks for FtpPathSafety's pure logic (Networking/FtpPathSafety.cs) - the
// SSD1/SSD2/SSD3 top-level rename guard (2026-09-04) and the 245-character remote path
// limit (2026-09-04, both hardware-verified). Wired into App.xaml.cs's
// --librarian-selftest.
static class FtpPathSafetySelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── IsTopLevelPath ──
        Check("toplevel-ssd-bare",        FtpPathSafety.IsTopLevelPath("/SSD1"));
        Check("toplevel-ssd-trailing-slash", FtpPathSafety.IsTopLevelPath("/SSD1/"));
        Check("toplevel-root-itself",     FtpPathSafety.IsTopLevelPath("/"));
        Check("not-toplevel-one-deep",    !FtpPathSafety.IsTopLevelPath("/SSD1/Samples"));
        Check("not-toplevel-several-deep", !FtpPathSafety.IsTopLevelPath("/SSD1/Samples/Kit/MS001000.KSF"));

        // ── FitsMaxRemotePathLength ──
        var exactly245 = "/" + new string('a', 244);
        Check("fits-exactly-at-limit", exactly245.Length == 245 && FtpPathSafety.FitsMaxRemotePathLength(exactly245));
        var oneOver = exactly245 + "a";
        Check("refuses-one-over-limit", !FtpPathSafety.FitsMaxRemotePathLength(oneOver));
        Check("fits-short-path", FtpPathSafety.FitsMaxRemotePathLength("/SSD1/Samples/Kit/MS001000.KSF"));
        Check("toolong-message-names-the-length", FtpPathSafety.TooLongMessage(oneOver).Contains("246"));

        return fails;
    }
}
