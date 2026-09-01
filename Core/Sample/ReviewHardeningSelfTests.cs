namespace KronosScreenRemote;

using System.IO;

// Regression checks for the defects an external code review turned up, each pinned at the
// exact input that used to fail. All off-hardware.
//
//  1. KorgRiffChunk.ReadChunks advanced by the RAW chunk length. 0xFFFFFFF8 casts to -8, so
//     the step was exactly zero: pos never moved and the loop appended chunks until it ran
//     out of memory. Other oversized lengths stepped pos negative and threw, breaking the
//     null-not-throw contract KsfSample.Open/KmpMultisample.Open advertise. Both directions
//     are checked, plus a well-formed file to prove the fix changed nothing there.
//  2. KsfSample.ReadSno1 must agree with a full Open() - it is the header-only shortcut
//     NextFreeSno1 now depends on, so a divergence would silently hand out colliding ids.
//  3. Sno1Allocator must hand out distinct consecutive ids seeded past what is on disk;
//     the stereo import path relies on -L and -R never colliding.
//  4. SamplePathGuard must reject the rooted and ".." names a corrupt manifest can carry,
//     while leaving ordinary leaf and subfolder names alone.
//
// The SaveAllChanges/orphan-sweep fix (a failed save must not let the sweep delete .KSF
// files the on-disk .KMP still references) is NOT covered here: it needs a
// SampleEditorViewModel with a loaded collection and an injectable write failure, which this
// suite has no harness for. Owed - see Commit Notes.md.
//
// Wired into App.xaml.cs's --librarian-selftest.
static class ReviewHardeningSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── 1. Oversized chunk lengths terminate ───────────────────────────────────
        // The exact value that produced a zero step. A plain call is the whole assertion:
        // before the fix this never returned.
        //
        // VERIFIED by reverting `pos += 8 + payloadLen` to `pos += 8 + (int)length` and re-running:
        // the suite HANGS. So a regression here shows up as --librarian-selftest never finishing,
        // NOT as a FAIL line - if this suite ever stops terminating, start looking here.
        {
            var data = new byte[16];
            "SMP1"u8.CopyTo(data);
            KorgRiffChunk.WriteU32BE(data, 4, 0xFFFFFFF8);
            var chunks = KorgRiffChunk.ReadChunks(data);
            Check("riff-zero-step-terminates", chunks.Count >= 1);
            // Clamped to what is actually there, so the payload can never over-read either.
            Check("riff-zero-step-payload-clamped", chunks[0].Payload.Length <= 8);
        }
        {
            var data = new byte[16];
            "SMP1"u8.CopyTo(data);
            KorgRiffChunk.WriteU32BE(data, 4, 0x80000000);
            bool threw = false;
            try { KorgRiffChunk.ReadChunks(data); } catch { threw = true; }
            Check("riff-huge-length-does-not-throw", !threw);
        }
        {
            // Well-formed input must parse exactly as before - the fix is only meant to
            // change behaviour on lengths that were already impossible.
            var payload = new byte[] { 1, 2, 3, 4 };
            var built = KorgRiffChunk.Concat(
                KorgRiffChunk.BuildChunk("AAAA", payload),
                KorgRiffChunk.BuildChunk("BBBB", payload));
            var chunks = KorgRiffChunk.ReadChunks(built);
            Check("riff-wellformed-unchanged",
                chunks.Count == 2 && chunks[0].Tag == "AAAA" && chunks[1].Tag == "BBBB"
                && chunks[1].Payload.SequenceEqual(payload));
        }

        // ── 2/3. Header-only SNO1 read, and the batch allocator ────────────────────
        var dir = Path.Combine(Path.GetTempPath(), "kronos_review_hardening_selftest");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            var ksf = new KsfSample { Name = "HARDEN", SampleRate = 44100, Flags = 0x81, Sno1 = 41 };
            ksf.SetSamples([1, 2, 3, 4, 5, 6, 7, 8]);
            var path = Path.Combine(dir, "HARDEN.KSF");
            ksf.Save(path);

            Check("readsno1-matches-full-open", KsfSample.ReadSno1(path) == 41);
            Check("readsno1-matches-reopen", KsfSample.Open(File.ReadAllBytes(path))?.Sno1 == 41);
            Check("readsno1-null-for-non-ksf", KsfSample.ReadSno1(Path.Combine(dir, "nope.KSF")) == null);

            // Seeded past the highest id on disk, then strictly increasing - which is what the
            // old scan-per-import achieved, at O(imports x files).
            Check("nextfree-seeds-past-disk", KscCollection.NextFreeSno1(dir) == 42);
            var alloc = new KscCollection.Sno1Allocator(dir);
            uint a = alloc.Next(), b = alloc.Next(), c = alloc.Next();
            Check("allocator-seeded-past-disk", a == 42);
            Check("allocator-distinct-consecutive", b == 43 && c == 44 && a != b && b != c);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        // ── 4. Path guard ─────────────────────────────────────────────────────────
        {
            var root = Path.Combine(Path.GetTempPath(), "kronos_guard_root");
            bool Rejects(string name)
            {
                try
                {
                    SamplePathGuard.EnsureUnder(root, Path.Combine(root, name), name);
                    return false;
                }
                catch (IOException) { return true; }
            }
            bool Accepts(string name)
            {
                try { return SamplePathGuard.EnsureUnder(root, Path.Combine(root, name), name).Length > 0; }
                catch (IOException) { return false; }
            }

            Check("guard-rejects-parent-escape", Rejects(Path.Combine("..", "escaped.KSF")));
            Check("guard-rejects-deep-escape", Rejects(Path.Combine("..", "..", "escaped.KSF")));
            Check("guard-rejects-rooted", Rejects(@"C:\Windows\escaped.KSF"));
            Check("guard-accepts-leaf", Accepts("NORMAL.KSF"));
            Check("guard-accepts-subfolder", Accepts(Path.Combine("MS000", "NORMAL.KSF")));
            // A traversal that lands back inside is not an escape - the guard tests the
            // RESOLVED path, not the presence of "..".
            Check("guard-accepts-roundtrip", Accepts(Path.Combine("MS000", "..", "NORMAL.KSF")));
        }

        return fails;
    }
}
