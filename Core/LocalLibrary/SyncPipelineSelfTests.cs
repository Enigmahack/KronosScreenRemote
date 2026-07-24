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

        // ── G: committed deletion (requirement 2) erases the slot on hardware and drops the
        //      local entry. A Set List becomes an empty Set List; a Program becomes INIT. ──
        {
            string root = ScratchRoot + "_g";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                var slBody = new byte[69416];
                System.Text.Encoding.ASCII.GetBytes("DELETE ME").CopyTo(slBody, 0);
                slBody = SetListBody.WriteSlotName(slBody, 0, "SLOT ONE");   // populated -> not IsEmpty
                exec.Seed(LibObj.SetList, 0, 5, 0, slBody);
                // A blank donor at Set List 127 (all slots empty, name "Set List 127") so the erase
                // path captures + reuses it as the blank template — replicating the exact scenario
                // where an erased slot could inherit the donor's "Set List 127" name.
                var donor127 = SetListBody.WriteName(new byte[69416], SetListData.DefaultName(127));
                exec.Seed(LibObj.SetList, 0, 127, 0, donor127);
                exec.Seed(LibObj.Program, 0x40, 0, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var slLoc = new ObjLoc(LibObj.SetList, 0, 5);
                var progLoc = new ObjLoc(LibObj.Program, 0x40, 0);
                Check("g-setlist-present-before", cache.Exists(slLoc.ObjType, slLoc.Bank, slLoc.Number));

                // Mimics LocalLibraryPaneViewModel.ToggleDelete (Discard is a no-op on a clean
                // pulled object; SetPendingDelete flags it).
                LocalEditOps.SetPendingDelete(cache, slLoc, true, DateTime.UtcNow);
                LocalEditOps.SetPendingDelete(cache, progLoc, true, DateTime.UtcNow);

                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("g-commit-ok", result.Ok);
                Check("g-deleted-count", result.Deleted == 2);

                var hwSl = await exec.DumpObjectAsync(slLoc.ObjType, slLoc.Bank, slLoc.Number);
                Check("g-setlist-erased-on-hardware", hwSl != null && (SetListBody.FromRawBody(5, hwSl.Body)?.IsEmpty ?? false));
                // The erased Set List must carry THIS slot's own default name ("Set List 005"), not
                // the shared blank template's donor name ("Set List 127") — both on the body written
                // to hardware and in the local revert-to-blank. Regression guard for the bug where a
                // deleted slot inherited "Set List 127" from the captured donor template.
                Check("g-setlist-erased-name-is-slot-default",
                    hwSl != null && SetListBody.FromRawBody(5, hwSl.Body)?.Name == SetListData.DefaultName(5));
                var hwProg = await exec.DumpObjectAsync(progLoc.ObjType, progLoc.Bank, progLoc.Number);
                Check("g-program-erased-on-hardware", hwProg != null && ProgramBody.ReadName(hwProg.Body) == "INIT PROGRAM");

                // The slots STAY in the local library, reverted to the init/blank object at their
                // address (requirement 2 — a bank slot never vanishes), clean + no longer pending.
                Check("g-setlist-kept-locally", cache.Exists(slLoc.ObjType, slLoc.Bank, slLoc.Number));
                Check("g-setlist-reverted-blank",
                    SetListBody.FromRawBody(5, cache.GetCurrentBody(slLoc.ObjType, slLoc.Bank, slLoc.Number)!)?.IsEmpty ?? false);
                Check("g-setlist-reverted-name-is-slot-default",
                    cache.GetDisplayName(slLoc.ObjType, slLoc.Bank, slLoc.Number) == SetListData.DefaultName(5));
                Check("g-program-kept-locally", cache.Exists(progLoc.ObjType, progLoc.Bank, progLoc.Number));
                Check("g-program-reverted-init", cache.GetDisplayName(progLoc.ObjType, progLoc.Bank, progLoc.Number) == "INIT PROGRAM");
                Check("g-no-longer-pending-delete",
                    !cache.IsPendingDelete(slLoc.ObjType, slLoc.Bank, slLoc.Number) &&
                    !cache.IsPendingDelete(progLoc.ObjType, progLoc.Bank, progLoc.Number));
                Check("g-slots-clean-after-commit",
                    !cache.IsDirty(slLoc.ObjType, slLoc.Bank, slLoc.Number) &&
                    !cache.IsDirty(progLoc.ObjType, progLoc.Bank, progLoc.Number));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── H: a local-only object (placed, never pushed) marked for deletion is dropped
        //      locally with NO hardware write — nothing to erase on the instrument. ──
        {
            string root = ScratchRoot + "_h";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);   // a real object, so a pull baseline exists
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var newLoc = new ObjLoc(LibObj.Program, 0x00, 5);   // empty on hardware
                var body = new byte[3706];
                System.Text.Encoding.ASCII.GetBytes("LOCAL ONLY").CopyTo(body, 0);
                var (placeOk, _, _) = LocalEditOps.PlaceObject(cache, newLoc, LibObj.Program, 5, body, "LOCAL ONLY",
                    divertDisplacedToClipboard: true, DateTime.UtcNow);
                Check("h-placed", placeOk && cache.Exists(newLoc.ObjType, newLoc.Bank, newLoc.Number));

                LocalEditOps.SetPendingDelete(cache, newLoc, true, DateTime.UtcNow);

                exec.CallLog.Clear();
                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("h-commit-ok", result.Ok && result.Deleted == 1);
                Check("h-no-hardware-write", !exec.CallLog.Contains("Write"));
                Check("h-removed-locally", !cache.Exists(newLoc.ObjType, newLoc.Bank, newLoc.Number));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── I: whole-bank HD-1->EXi type change (requirement 4) issues func 0x7C BEFORE the
        //      writes, then writes the whole EXi bank; the intent is cleared on success. ──
        {
            string root = ScratchRoot + "_i";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                const int destBank = 0x40;   // U-A
                for (int n = 0; n < 3; n++) exec.Seed(LibObj.Program, destBank, n, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var bits = new bool[21]; bits[7] = false;   // hardware: U-A is HD-1
                exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

                // Simulate the whole-bank type-change placement: clear the old HD-1 programs,
                // place EXi programs, record the intent (what PlaceMergeBankWithTypeChange does).
                for (int n = 0; n < 3; n++) cache.RemoveObject(LibObj.Program, destBank, n, DateTime.UtcNow);
                for (int n = 0; n < 3; n++)
                {
                    var exiBody = new byte[ProgramFormatConverter.WireSizeExi];
                    System.Text.Encoding.ASCII.GetBytes($"EXI {n}").CopyTo(exiBody, 0);
                    LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, destBank, n), LibObj.Program, 5, exiBody,
                        $"EXI {n}", divertDisplacedToClipboard: true, DateTime.UtcNow);
                }
                cache.SetPendingBankTypeChange(destBank, isExi: true);

                exec.CallLog.Clear();
                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("i-commit-ok", result.Ok && result.Written == 3);
                Check("i-bank-type-changed", exec.BankTypeChanges.TryGetValue(destBank, out var toExi) && toExi);
                int idxType = exec.CallLog.FindIndex(c => c.StartsWith("ChangeBankType:"));
                int idxWrite = exec.CallLog.IndexOf("Write");
                Check("i-typechange-before-write", idxType >= 0 && idxWrite >= 0 && idxType < idxWrite);
                var hw = await exec.DumpObjectAsync(LibObj.Program, destBank, 0);
                Check("i-exi-program-written", hw != null && hw.Body.Length == ProgramFormatConverter.WireSizeExi &&
                    ProgramBody.ReadName(hw.Body) == "EXI 0");
                Check("i-intent-cleared", cache.PendingBankTypeChange(destBank) == null);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── J: the same mismatched placement WITHOUT a staged type change still REFUSEs — an
        //      accidental EXi-into-HD-1 crossing must never silently erase a bank. ──
        {
            string root = ScratchRoot + "_j";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                const int destBank = 0x40;
                exec.Seed(LibObj.Program, destBank, 0, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                exec.Seed(LibObj.Program, destBank, 1, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var bits = new bool[21]; bits[7] = false;   // hardware: U-A is HD-1
                exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

                // TWO stray EXi programs in the same bank — proves the REFUSE is deduped to one
                // line PER BANK (issue 3b), not one per program.
                foreach (int n in new[] { 0, 1 })
                {
                    var exiBody = new byte[ProgramFormatConverter.WireSizeExi];
                    System.Text.Encoding.ASCII.GetBytes($"STRAY EXI {n}").CopyTo(exiBody, 0);
                    LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, destBank, n), LibObj.Program, 5, exiBody,
                        $"STRAY EXI {n}", divertDisplacedToClipboard: true, DateTime.UtcNow);
                }
                // No SetPendingBankTypeChange — this is an accidental crossing.

                exec.CallLog.Clear();
                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("j-refused", !result.Ok);
                Check("j-error-format", result.Error != null && result.Error.Contains("wrong format"));
                Check("j-refuse-deduped-per-bank", result.Error != null &&
                    System.Text.RegularExpressions.Regex.Matches(result.Error, "REFUSE:").Count == 1);
                Check("j-no-typechange", !exec.CallLog.Any(c => c.StartsWith("ChangeBankType:")));
                Check("j-no-write", !exec.CallLog.Contains("Write"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── K: a committed deletion uses the REAL captured blank template (requirement 2) when a
        //      blank source slot is available, not the derived name-blank fallback. ──
        {
            string root = ScratchRoot + "_k";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                // A recognizable blank HD-1 program at the HD-1 capture source (U-GG000 = 0x4D:0).
                // Marker byte proves THIS body was written back, not a renamed copy of the deleted one.
                var blankHd1 = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "InitProgram");
                blankHd1[100] = 0x5A;
                exec.Seed(LibObj.Program, 0x4D, 0, 5, blankHd1);
                exec.Seed(LibObj.Program, 0x40, 0, 5, ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "MY PATCH"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var delLoc = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.SetPendingDelete(cache, delLoc, true, DateTime.UtcNow);

                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("k-commit-ok", result.Ok && result.Deleted == 1);

                var hw = await exec.DumpObjectAsync(delLoc.ObjType, delLoc.Bank, delLoc.Number);
                Check("k-used-captured-template", hw != null && hw.Body.Length == blankHd1.Length &&
                    hw.Body[100] == 0x5A && ProgramBody.ReadName(hw.Body) == "InitProgram");
                // The slot stays locally, now byte-identical to the blank template, clean.
                Check("k-slot-kept-locally", cache.Exists(delLoc.ObjType, delLoc.Bank, delLoc.Number));
                Check("k-local-reverted-to-blank",
                    cache.GetDisplayName(delLoc.ObjType, delLoc.Bank, delLoc.Number) == "InitProgram" &&
                    !cache.IsDirty(delLoc.ObjType, delLoc.Bank, delLoc.Number));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        return fails;
    }
}
