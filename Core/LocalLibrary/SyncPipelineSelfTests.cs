namespace KronosScreenRemote;

using System.IO;

// Off-hardware self-test for Phase 3: ChangesetBuilder/SyncPipeline (Push/Commit/Sync).
// Async, against FakeMoveExecutor - same convention as Phase 1/2. Each case gets its own
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
                // never one per object - same batching bug/fix as the Pull side above.
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
        //      is corrected to the real object version at the moment of the actual write -
        //      never trusted from whatever's stored locally. Regression test for the func-0x24
        //      Reply Code 3 ("short or otherwise mangled message") bug: PCG-sourced Programs
        //      used to carry version 0 instead of the documented 5, and Set List's own version
        //      really is 0 so that bug never surfaced there - see LibObj.CurrentObjectVersion.
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
                bits[7] = true;   // hardware says U-A is actually EXi - mismatches the HD-1-sized body above
                exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

                var result = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("e1-refused", !result.Ok);
                // Assert against the message SOURCE, not a hand-copied substring: the exact
                // wording is UI copy that gets revised (it already lost the literal "wrong
                // format" this line used to look for), while the identity of the refusal - "the
                // bank-type mismatch one, not some other REFUSE" - is what the test cares about.
                Check("e1-error-mentions-format", result.Error != null &&
                    result.Error.Contains(
                        AppMessages.Librarian.Sync.RefuseBankTypeMismatch(KronosBanks.ProgramLabel(0x40), "EXi"),
                        StringComparison.Ordinal));
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
                // exec.ProgramBankTypesToReturn left null (default) - simulates func 0x61 unavailable.

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
                // path captures + reuses it as the blank template - replicating the exact scenario
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
                // the shared blank template's donor name ("Set List 127") - both on the body written
                // to hardware and in the local revert-to-blank. Regression guard for the bug where a
                // deleted slot inherited "Set List 127" from the captured donor template.
                Check("g-setlist-erased-name-is-slot-default",
                    hwSl != null && SetListBody.FromRawBody(5, hwSl.Body)?.Name == SetListData.DefaultName(5));
                var hwProg = await exec.DumpObjectAsync(progLoc.ObjType, progLoc.Bank, progLoc.Number);
                // "Init Program" (mixed case) is the instrument's OWN name, from the factory body
                // shipped in Resources/InitBodies. It used to read "INIT PROGRAM" here - EraseBody's
                // derived fallback - because nothing better was available; the case difference is
                // precisely what distinguishes the two, so don't "fix" this to match EraseBody.
                Check("g-program-erased-on-hardware", hwProg != null && ProgramBody.ReadName(hwProg.Body) == "Init Program");

                // The slots STAY in the local library, reverted to the init/blank object at their
                // address (requirement 2 - a bank slot never vanishes), clean + no longer pending.
                Check("g-setlist-kept-locally", cache.Exists(slLoc.ObjType, slLoc.Bank, slLoc.Number));
                Check("g-setlist-reverted-blank",
                    SetListBody.FromRawBody(5, cache.GetCurrentBody(slLoc.ObjType, slLoc.Bank, slLoc.Number)!)?.IsEmpty ?? false);
                Check("g-setlist-reverted-name-is-slot-default",
                    cache.GetDisplayName(slLoc.ObjType, slLoc.Bank, slLoc.Number) == SetListData.DefaultName(5));
                Check("g-program-kept-locally", cache.Exists(progLoc.ObjType, progLoc.Bank, progLoc.Number));
                Check("g-program-reverted-init", cache.GetDisplayName(progLoc.ObjType, progLoc.Bank, progLoc.Number) == "Init Program");
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
        //      locally with NO hardware write - nothing to erase on the instrument. ──
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

        // ── J: the same mismatched placement WITHOUT a staged type change still REFUSEs - an
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

                // TWO stray EXi programs in the same bank - proves the REFUSE is deduped to one
                // line PER BANK (issue 3b), not one per program.
                foreach (int n in new[] { 0, 1 })
                {
                    var exiBody = new byte[ProgramFormatConverter.WireSizeExi];
                    System.Text.Encoding.ASCII.GetBytes($"STRAY EXI {n}").CopyTo(exiBody, 0);
                    LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, destBank, n), LibObj.Program, 5, exiBody,
                        $"STRAY EXI {n}", divertDisplacedToClipboard: true, DateTime.UtcNow);
                }
                // No SetPendingBankTypeChange - this is an accidental crossing.

                exec.CallLog.Clear();
                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("j-refused", !result.Ok);
                // Same reasoning as e1-error-mentions-format: match the message SOURCE, not a
                // hand-copied substring. Here the bank is HD-1 and the pending Programs are EXi.
                Check("j-error-format", result.Error != null &&
                    result.Error.Contains(
                        AppMessages.Librarian.Sync.RefuseBankTypeMismatch(KronosBanks.ProgramLabel(destBank), "HD-1"),
                        StringComparison.Ordinal));
                Check("j-refuse-deduped-per-bank", result.Error != null &&
                    System.Text.RegularExpressions.Regex.Matches(result.Error, "REFUSE:").Count == 1);
                Check("j-no-typechange", !exec.CallLog.Any(c => c.StartsWith("ChangeBankType:")));
                Check("j-no-write", !exec.CallLog.Contains("Write"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── K: a committed deletion uses the REAL blank body shipped with the app (requirement 2),
        //      not the derived name-blank fallback - and needs no blank slot on the instrument to
        //      do it. Programs, HD-1 format. ──
        {
            string root = ScratchRoot + "_k";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                // NO blank donor slot is seeded anywhere: the HD-1 blank ships with the assembly
                // (Resources/InitBodies/program_hd1_init.bin), so the erase must work on an
                // instrument where nothing happens to be blank. That independence is the entire
                // reason the bodies are baked in rather than captured.
                exec.Seed(LibObj.Program, 0x40, 0, 5, ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "MY PATCH"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                // Pre-poison the store with a real patch, the way the old length-only LooksBlank
                // did on a real install. The shipped body is consulted first, so this file can no
                // longer decide the outcome however it got there.
                new BlankTemplateStore(cache.Root).Set(LibObj.Program, false,
                    ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "MY PATCH"));

                var delLoc = new ObjLoc(LibObj.Program, 0x40, 0);
                LocalEditOps.SetPendingDelete(cache, delLoc, true, DateTime.UtcNow);

                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("k-commit-ok", result.Ok && result.Deleted == 1);

                var hw = await exec.DumpObjectAsync(delLoc.ObjType, delLoc.Bank, delLoc.Number);
                // "Init Program" is the instrument's own spelling, from U-GG:000. EraseBody's
                // derived fallback writes "INIT PROGRAM" (upper case), so this assertion also
                // distinguishes "used the real factory body" from "gave up and blanked the name".
                Check("k-used-baked-template", hw != null && hw.Body.Length == ProgramFormatConverter.WireSizeHd1 &&
                    ProgramBody.ReadName(hw.Body) == "Init Program");
                Check("k-poisoned-store-not-used", hw != null && ProgramBody.ReadName(hw.Body) != "MY PATCH");
                // The slot stays locally, now byte-identical to the blank template, clean.
                Check("k-slot-kept-locally", cache.Exists(delLoc.ObjType, delLoc.Bank, delLoc.Number));
                Check("k-local-reverted-to-blank",
                    cache.GetDisplayName(delLoc.ObjType, delLoc.Bank, delLoc.Number) == "Init Program" &&
                    !cache.IsDirty(delLoc.ObjType, delLoc.Bank, delLoc.Number));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── K1b: the other two shipped bodies - EXi Programs and Set Lists. Same contract as K,
        //      and the Set List adds one wrinkle of its own: its default name encodes its OWN slot
        //      number, so the donor's "Set List 127" must be re-stamped to the erased slot's name
        //      (ChangesetBuilder) rather than written through verbatim. That was a real bug once -
        //      it renamed live hardware set-list slots to "Set List 127". ──
        {
            string root = ScratchRoot + "_k1b";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                // Again no blank donor slots anywhere - only the two objects being deleted.
                exec.Seed(LibObj.Program, 0x40, 1, 5,
                    ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeExi], "MY EXI PATCH"));
                var realSetList = SetListBody.WriteSlotName(
                    SetListBody.WriteName(new byte[69416], "MY SET LIST"), 0, "A REAL SLOT");
                exec.Seed(LibObj.SetList, 0, 6, 0, realSetList);

                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var exiLoc = new ObjLoc(LibObj.Program, 0x40, 1);
                var slLoc = new ObjLoc(LibObj.SetList, 0, 6);
                LocalEditOps.SetPendingDelete(cache, exiLoc, true, DateTime.UtcNow);
                LocalEditOps.SetPendingDelete(cache, slLoc, true, DateTime.UtcNow);

                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("k1b-commit-ok", result.Ok && result.Deleted == 2);

                var exiHw = await exec.DumpObjectAsync(exiLoc.ObjType, exiLoc.Bank, exiLoc.Number);
                Check("k1b-exi-used-baked-template", exiHw != null &&
                    exiHw.Body.Length == ProgramFormatConverter.WireSizeExi &&
                    ProgramBody.ReadName(exiHw.Body) == "Init EXi Program");

                var slHw = await exec.DumpObjectAsync(slLoc.ObjType, slLoc.Bank, slLoc.Number);
                Check("k1b-setlist-used-baked-template", slHw != null && slHw.Body.Length == 69416);
                Check("k1b-setlist-reads-as-init", slHw != null && InitObjects.IsInit(LibObj.SetList, slHw.Body));
                // Named for the slot it now occupies, NOT for the donor slot it was captured from.
                Check("k1b-setlist-renamed-to-its-own-slot", slHw != null &&
                    Librarian.ReadName(slHw.Body) == SetListData.DefaultName(slLoc.Number));
                Check("k1b-setlist-not-donor-name", slHw != null && Librarian.ReadName(slHw.Body) != "Set List 127");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── K2: the same capture path for COMBIS, which is where it went wrong in the field. ──
        //      A deleted Combi came back named "SCREAMING HEAD Gmin RIFF": LooksBlank's Combi arm
        //      only checked the body LENGTH, and every valid Combi is >= 7810 bytes, so the real
        //      patch sitting in the old capture source slot was enshrined as "the blank Combi" and
        //      stamped onto every subsequent erase. Both halves are covered here - the source slot
        //      must actually be init, and a template that ISN'T must be discarded rather than
        //      trusted (BlankTemplates.EnsureAsync re-validates before returning a stored one).
        {
            string root = ScratchRoot + "_k2";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                // NOTHING is seeded as a blank source slot, deliberately: the Combi blank comes
                // from the body shipped with the assembly (Resources/InitBodies/combi_init.bin),
                // so this must work with no donor slot anywhere on the "instrument" - which is the
                // entire point of baking it in rather than capturing it.

                // The Combi to delete: REAL content (non-default timbres), so it can't be confused
                // with an init placeholder at any point in the pipeline.
                var realCombi = new byte[7810];
                System.Text.Encoding.ASCII.GetBytes("MY COMBI").CopyTo(realCombi, 0);
                for (int t = 0; t < LibRefs.TimbreCount; t++)
                    LibRefs.SetCombiTimbreRef(realCombi, t, KronosBanks.ObjBankToFunc33(1, 0x40), t + 1);
                exec.Seed(LibObj.Combi, 0x40, 0, 3, realCombi);

                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var delLoc = new ObjLoc(LibObj.Combi, 0x40, 0);
                LocalEditOps.SetPendingDelete(cache, delLoc, true, DateTime.UtcNow);

                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("k2-commit-ok", result.Ok && result.Deleted == 1);

                var hw = await exec.DumpObjectAsync(delLoc.ObjType, delLoc.Bank, delLoc.Number);
                // The shipped body's own identity: 7810 bytes, named "Init Combi" (the
                // instrument's own spelling, NOT EraseBody's derived "INIT COMBI" fallback - the
                // difference is exactly what distinguishes "used the real factory body" from
                // "gave up and blanked the name"), and init by shape.
                Check("k2-used-baked-init-combi", hw != null && hw.Body.Length == 7810 &&
                    CombiBody.ReadName(hw.Body) == "Init Combi");
                Check("k2-erased-slot-reads-as-init", hw != null && CombiBody.IsInit(hw.Body));
                Check("k2-not-the-deleted-patch", hw != null && CombiBody.ReadName(hw.Body) != "MY COMBI");
                // No capture happened, so nothing was persisted to the per-library store either.
                Check("k2-no-capture-persisted", new BlankTemplateStore(cache.Root).Get(LibObj.Combi, false) == null &&
                    new BlankTemplateStore(cache.Root).Get(LibObj.Combi, true) == null);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── K3: an install that ALREADY has a poisoned blank_templates/combi.bin - the exact state
        //      the field bug leaves behind - erases correctly anyway. This is the regression test
        //      for the reported symptom itself: the shipped body is consulted before any stored
        //      capture, so the bad file can't decide the outcome no matter how it got there. ──
        {
            string root = ScratchRoot + "_k3";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                var realCombi = new byte[7810];
                System.Text.Encoding.ASCII.GetBytes("MY COMBI").CopyTo(realCombi, 0);
                for (int t = 0; t < LibRefs.TimbreCount; t++)
                    LibRefs.SetCombiTimbreRef(realCombi, t, KronosBanks.ObjBankToFunc33(1, 0x40), t + 1);
                exec.Seed(LibObj.Combi, 0x40, 0, 3, realCombi);

                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                // Pre-poison the store exactly as the old weak LooksBlank did: a real patch saved
                // as "the blank Combi". The isExi argument is irrelevant here by design -
                // BlankTemplateStore.Key maps every Combi to one "combi" file, since the HD-1/EXi
                // split is a Program-only concept - so this lands on the same file the erase path
                // reads back whatever cache.IsExi happens to report for a Combi slot.
                var poison = new byte[7810];
                System.Text.Encoding.ASCII.GetBytes("SCREAMING HEAD Gmin RIFF").CopyTo(poison, 0);
                for (int t = 0; t < LibRefs.TimbreCount; t++)
                    LibRefs.SetCombiTimbreRef(poison, t, KronosBanks.ObjBankToFunc33(1, 0x41), t + 1);
                new BlankTemplateStore(cache.Root).Set(LibObj.Combi, false, poison);

                var delLoc = new ObjLoc(LibObj.Combi, 0x40, 0);
                LocalEditOps.SetPendingDelete(cache, delLoc, true, DateTime.UtcNow);
                var result = await SyncPipeline.CommitChangesAsync(exec, cache, new SessionDependencyClipboard());
                Check("k3-commit-ok", result.Ok && result.Deleted == 1);

                var hw = await exec.DumpObjectAsync(delLoc.ObjType, delLoc.Bank, delLoc.Number);
                Check("k3-poisoned-template-not-used", hw != null && CombiBody.ReadName(hw.Body) != "SCREAMING HEAD Gmin RIFF");
                Check("k3-erased-slot-reads-as-init", hw != null && CombiBody.IsInit(hw.Body));
                Check("k3-baked-body-used-instead", hw != null && CombiBody.ReadName(hw.Body) == "Init Combi");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // ── L: a bank that answers NO digest is swept ONCE, not on every lazy pull ──
        //      Regression test for "Sync Library re-syncs the whole I-G bank every time": with no
        //      digest there was nothing to persist as a baseline, so PlanPull saw the bank as
        //      changed forever. It must now pin a NoDigest baseline (first pull sweeps it, later
        //      lazy pulls skip it, an explicit full pull still sweeps it) - while a pull where NO
        //      bank answered (instrument unreachable) must leave every baseline alone.
        {
            string root = ScratchRoot + "_l";
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                var quiet = (LibObj.Program, 0x05);   // I-F: any bank the fake keeps silent
                exec.NoDigestBanks.Add(quiet);
                exec.Seed(LibObj.Program, 0x00, 0, 5, new byte[ProgramFormatConverter.WireSizeHd1]);
                var cache = new LocalLibraryCache(root);

                // First (lazy) pull: no baselines at all yet, so every bank is swept.
                var first = await LibraryPullPipeline.PullAsync(exec, cache, full: false);
                Check("l-first-pull-covers-quiet-bank", first.BanksChecked == LibraryPullPlanner.AllBanks().Count());
                Check("l-quiet-bank-baseline-pinned",
                    cache.BankDigestBaselineHex().TryGetValue(quiet, out var pinned) &&
                    pinned == LibraryPullPipeline.NoDigest);

                // Second lazy pull: nothing changed on "hardware", so NOTHING is swept - the
                // quiet bank included. That is the whole bug.
                exec.CallLog.Clear();
                var second = await LibraryPullPipeline.PullAsync(exec, cache, full: false);
                Check("l-second-pull-skips-everything", second.BanksChecked == 0);
                Check("l-second-pull-no-quiet-bank-dumps",
                    !exec.CallLog.Any(c => c.StartsWith($"Dump:{LibObj.Program}:5:") ||
                                           c == $"BulkDump:{LibObj.Program}:5"));

                // Force Pull-All still sweeps it - the documented escape hatch.
                exec.CallLog.Clear();
                var forced = await LibraryPullPipeline.PullAsync(exec, cache, full: true);
                Check("l-full-pull-still-sweeps-quiet-bank",
                    forced.BanksChecked == LibraryPullPlanner.AllBanks().Count() &&
                    exec.CallLog.Contains($"BulkDump:{LibObj.Program}:5"));

                // A pull where NOTHING answered (unreachable instrument) must not overwrite the
                // good baselines with sentinels, or the next connected sync would see a library
                // that is silently, wrongly "up to date".
                var goodBaseline = cache.BankDigestBaselineHex()[(LibObj.Program, 0x00)];
                foreach (var b in LibraryPullPlanner.AllBanks()) exec.NoDigestBanks.Add((b.ObjType, b.Bank));
                await LibraryPullPipeline.PullAsync(exec, cache, full: false);
                Check("l-offline-pull-keeps-baselines",
                    cache.BankDigestBaselineHex()[(LibObj.Program, 0x00)] == goodBaseline);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        return fails;
    }
}
