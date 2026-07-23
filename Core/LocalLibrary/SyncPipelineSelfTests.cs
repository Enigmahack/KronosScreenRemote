namespace KronosScreenRemote;

using System.IO;

// Off-hardware self-test for Phase 3: ChangesetBuilder/SyncPipeline (Push/Commit/Sync).
// Async, against FakeMoveExecutor — same convention as Phase 1/2. Each case gets its own
// scratch subdirectory so they can't interfere with each other.
static class SyncPipelineSelfTests
{
    static string ScratchRoot => Path.Combine(Path.GetTempPath(), "kronos_selftest_sync_pipeline");

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── A: clean push writes exactly the dirty set, records one permanent PushCommit ──
        {
            string root = ScratchRoot + "_a";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);
                exec.Seed(LibObj.Program, 0x00, 1, 1, new byte[3706]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                var loc2 = new ObjLoc(LibObj.Program, 0x00, 1);
                LocalEditOps.Rename(cache, loc, "PUSHTEST", DateTime.UtcNow);
                LocalEditOps.Rename(cache, loc2, "PUSHTEST2", DateTime.UtcNow);
                Check("a-dirty-before-push", cache.IsDirty(loc.ObjType, loc.Bank, loc.Number) &&
                                              cache.IsDirty(loc2.ObjType, loc2.Bank, loc2.Number));

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("a-push-ok", result.Ok && result.Written == 2);
                Check("a-clean-after-push", !cache.IsDirty(loc.ObjType, loc.Bank, loc.Number) &&
                                             !cache.IsDirty(loc2.ObjType, loc2.Bank, loc2.Number));

                var hwDump = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("a-hardware-updated", hwDump != null && ProgramBody.ReadName(hwDump.Body) == "PUSHTEST");

                // Regression: pushing MULTIPLE objects in one Commit/Sync must append
                // exactly ONE "PushCommit" op-log entry (covering both via multiple Targets),
                // never one per object — same batching bug/fix as the Pull side above.
                var log = OpLog.ReadAll(root);
                Check("a-one-pushcommit-entry", log.Count(e => e.OpKind == "PushCommit") == 1);
                Check("a-pushcommit-covers-both-objects", log.First(e => e.OpKind == "PushCommit").Targets.Count == 2);
                Check("a-pushcommit-has-syncbatch", log.First(e => e.OpKind == "PushCommit").SyncBatchId != null);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── B: a hardware change in ONE bank excludes only that bank's dirty items ──
        {
            string root = ScratchRoot + "_b";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);   // bank 0x00
                exec.Seed(LibObj.Program, 0x40, 0, 1, new byte[3706]);   // bank 0x40 (different bank)
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var locA = new ObjLoc(LibObj.Program, 0x00, 0);
                var locB = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.Rename(cache, locA, "BANK-A-EDIT", DateTime.UtcNow);
                LocalEditOps.Rename(cache, locB, "BANK-B-EDIT", DateTime.UtcNow);

                // Simulate a front-panel edit landing in bank 0x00 (a different slot),
                // changing that bank's digest between our last pull and this push.
                var hwEdited = new byte[3706]; hwEdited[50] = 0x77;
                exec.Seed(LibObj.Program, 0x00, 1, 1, hwEdited);

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("b-push-ok-overall", result.Ok);
                Check("b-bank-a-conflicted", result.Conflicted.Any(l => l.Equals(locA)));
                Check("b-bank-a-still-dirty", cache.IsDirty(locA.ObjType, locA.Bank, locA.Number));
                Check("b-bank-a-marked-conflicted", cache.IsConflicted(locA.ObjType, locA.Bank, locA.Number));

                Check("b-bank-b-not-conflicted", !result.Conflicted.Any(l => l.Equals(locB)));
                Check("b-bank-b-clean-after-push", !cache.IsDirty(locB.ObjType, locB.Bank, locB.Number));
                var hwB = await exec.DumpObjectAsync(locB.ObjType, locB.Bank, locB.Number);
                Check("b-bank-b-pushed-to-hardware", hwB != null && ProgramBody.ReadName(hwB.Body) == "BANK-B-EDIT");

                var hwA = await exec.DumpObjectAsync(locA.ObjType, locA.Bank, locA.Number);
                Check("b-bank-a-not-overwritten", hwA != null && ProgramBody.ReadName(hwA.Body) != "BANK-A-EDIT");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── C: non-empty session clipboard REFUSEs the whole push ──
        {
            string root = ScratchRoot + "_c";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "SHOULD-NOT-PUSH", DateTime.UtcNow);

                var sessionClip = new SessionDependencyClipboard();
                sessionClip.Add(new SessionDependencyEntry(new ObjLoc(LibObj.Program, 0x41, 5), "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null));

                var result = await SyncPipeline.PushAsync(exec, cache, sessionClip);
                Check("c-refused", !result.Ok);
                Check("c-error-mentions-clipboard", result.Error != null && result.Error.Contains("session clipboard"));
                Check("c-still-dirty", cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));
                var hw = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("c-hardware-untouched", hw != null && ProgramBody.ReadName(hw.Body) != "SHOULD-NOT-PUSH");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── D: SyncLibraryAsync pulls before it pushes ──
        {
            string root = ScratchRoot + "_d";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "SYNC-TEST", DateTime.UtcNow);

                exec.CallLog.Clear();
                var (pull, push) = await SyncPipeline.SyncLibraryAsync(exec, cache, new SessionDependencyClipboard(), fullPull: true);
                Check("d-pull-ran", exec.CallLog.Any(c => c.StartsWith("Dump:") || c.StartsWith("BulkDump:")));
                Check("d-push-ran", push.Ok && push.Written == 1);
                int firstDump = exec.CallLog.FindIndex(c => c.StartsWith("Dump:") || c.StartsWith("BulkDump:"));
                int firstWrite = exec.CallLog.IndexOf("Write");
                Check("d-pull-before-push", firstDump >= 0 && firstWrite >= 0 && firstDump < firstWrite);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── F: a stale/placeholder stored version (e.g. 0, from the old PCG-import bug)
        //      is corrected to the real object version at the moment of the actual write —
        //      never trusted from whatever's stored locally. Regression test for the func-0x24
        //      Reply Code 3 ("short or otherwise mangled message") bug: PCG-sourced Programs
        //      used to carry version 0 instead of the documented 5, and Set List's own version
        //      really is 0 so that bug never surfaced there — see LibObj.CurrentObjectVersion.
        {
            string root = ScratchRoot + "_f";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x40, 0, 0, new byte[ProgramFormatConverter.WireSizeHd1]);   // stale placeholder version
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x40, 0);
                Check("f-cache-still-has-stale-version-before-push", cache.GetVersion(loc.ObjType, loc.Bank, loc.Number) == 0);
                LocalEditOps.Rename(cache, loc, "VERFIX-TEST", DateTime.UtcNow);

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("f-push-ok", result.Ok && result.Written == 1);

                var hw = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("f-hardware-got-correct-version", hw != null && hw.Version == 5);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── E: Program bank-type mismatch REFUSEs the push before any hardware write ──
        {
            string root = ScratchRoot + "_e1";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x40, 0, 1, new byte[ProgramFormatConverter.WireSizeHd1]);   // U-A, HD-1-sized body
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.Rename(cache, loc, "MISMATCH", DateTime.UtcNow);

                // U-A is bit 7 (bit 0 = edit buffer, 1-6 = I-A..I-F, 7-13 = U-A..U-G).
                var bits = new bool[21];
                bits[7] = true;   // hardware says U-A is actually EXi — mismatches the HD-1-sized body above
                exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("e1-refused", !result.Ok);
                Check("e1-error-mentions-format", result.Error != null && result.Error.Contains("wrong format"));
                Check("e1-still-dirty", cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));
                Check("e1-no-hardware-write", !exec.CallLog.Contains("Write"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── E2: matching bank type pushes normally ──
        {
            string root = ScratchRoot + "_e2";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x40, 0, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.Rename(cache, loc, "MATCHTEST", DateTime.UtcNow);

                var bits = new bool[21];
                bits[7] = false;   // U-A correctly HD-1, matching the body's own size
                exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("e2-push-ok", result.Ok && result.Written == 1);
                var hw = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("e2-hardware-updated", hw != null && ProgramBody.ReadName(hw.Body) == "MATCHTEST");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── E3: unqueried/unknown bank type never blocks (offline-friendly by design) ──
        {
            string root = ScratchRoot + "_e3";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x40, 0, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.Rename(cache, loc, "NOQUERY", DateTime.UtcNow);
                // exec.ProgramBankTypesToReturn left null (default) — simulates func 0x61 unavailable.

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("e3-push-ok-when-unverifiable", result.Ok && result.Written == 1);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        return fails;
    }
}
