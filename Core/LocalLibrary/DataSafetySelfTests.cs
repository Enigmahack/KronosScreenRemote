namespace KronosScreenRemote;

using System.IO;

// Off-hardware self-test focused on the ONE thing that can lose a user's data: the Librarian's
// push safety net and its crash-durability contract. The existing Phase 1-3 self-tests already
// cover pull/push happy paths, conflict pre-scan, version-fix and bank-type gates; this file
// deliberately does NOT re-cover those. It fills three gaps nothing else asserts:
//
//   A. The pre-write BACKUP. ApplyMoveAsync backs up the pre-image of every object it is about
//      to overwrite BEFORE touching hardware (restore = replay that .syx). No other test opens
//      that backup file — so nothing catches it silently backing up the NEW body, an empty file,
//      or backing up AFTER the write. Those are the failure modes that turn "overwrote the wrong
//      thing" from recoverable into permanent.
//
//   B. The ABORT-BEFORE-STORE guards. If a bank changed under us since we armed the plan (a
//      front-panel edit landing in the arm->apply window) or a write is rejected, ApplyMoveAsync
//      must bail out BEFORE any Store — leaving hardware exactly as it was, never half-committed.
//
//   C. Crash DURABILITY. A bare local edit calls cache.Save() NOWHERE (only Pull/Push persist
//      index.json); its only durable record is the append-immediate op-log + CAS blob. index.json
//      documents itself as a rebuildable CACHE ("recoverable from oplog.jsonl alone"). These tests
//      prove that claim holds: an edit made and then "lost" to a crash before Save() is fully
//      reconstructable from the op-log, byte-for-byte.
//
// Same convention as the other *SelfTests: async, against FakeMoveExecutor, each case in its own
// scratch dir, returns the list of failing check names (empty == all pass). Wired into
// App.xaml.cs's --librarian-selftest.
static class DataSafetySelfTests
{
    static string ScratchRoot => Path.Combine(Path.GetTempPath(), "kronos_selftest_data_safety");

    static byte[] Prog(string name) => ProgramBody.WriteName(new byte[3706], name);

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── A1: the backup is written, holds the PRE-IMAGE (the about-to-be-overwritten original,
        //        not the new body), and lands BEFORE the hardware write. Driven through the exact
        //        Build -> Arm -> Apply path SyncPipeline.PushAsync uses, but with a scratch backup
        //        dir so we can read the file back and assert its contents. ──
        {
            string root = ScratchRoot + "_a_lib";
            string backupDir = ScratchRoot + "_a_bak";
            Reset(root); Reset(backupDir);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog("ORIGINALNAME"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "MODIFIEDNAME", DateTime.UtcNow);

                var (plan, _) = await ChangesetBuilder.BuildAsync(cache, exec, new SessionDependencyClipboard());
                Check("a-plan-one-write-one-preimage", plan.Writes.Count == 1 && plan.PreImages.Count == 1);

                await Librarian.ArmPlanAsync(plan, exec);
                exec.CallLog.Clear();
                var (ok, _, _) = await Librarian.ApplyMoveAsync(plan, exec, backupDir, "TESTSTAMP", null, doLive: false);
                Check("a-apply-ok", ok);

                var backups = SyxFiles(backupDir);
                Check("a-backup-file-created", backups.Length == 1);
                if (backups.Length == 1)
                {
                    var backupBody = File.ReadAllBytes(backups[0]);
                    // THE point: the backup captures the pre-edit original, so it is restorable.
                    Check("a-backup-holds-original-preimage", ProgramBody.ReadName(backupBody) == "ORIGINALNAME");
                    Check("a-backup-is-not-the-new-body", ProgramBody.ReadName(backupBody) != "MODIFIEDNAME");
                    Check("a-backup-nonempty", backupBody.Length > 0);
                }

                // Ordering guarantee: backup pre-image, THEN write, THEN Store. A backup that
                // fired after the write would be worthless (it'd capture what we just wrote).
                int iBackup = exec.CallLog.IndexOf("Backup");
                int iWrite  = exec.CallLog.IndexOf("Write");
                int iStore  = exec.CallLog.IndexOf("Store");
                Check("a-backup-before-write", iBackup >= 0 && iWrite >= 0 && iBackup < iWrite);
                Check("a-write-before-store", iWrite >= 0 && iStore >= 0 && iWrite < iStore);
            }
            finally { Reset(root); Reset(backupDir); }
        }

        // ── A2: the REAL user push path (SyncPipeline.PushAsync -> Storage.BackupDir()) actually
        //        emits the pre-image backup end-to-end, not just the ApplyMoveAsync unit in
        //        isolation. Identified by content, not filename: SyncPipeline stamps backups at
        //        one-second resolution, so several pushes in the same run/second reuse one
        //        "{stamp}_changeset.syx" (FileMode.Create overwrites) — a filename diff is
        //        unreliable. A unique seeded name pins down OUR backup regardless. ──
        {
            string root = ScratchRoot + "_a2_lib";
            const string uniqueOrig = "ORIG-REALPUSH-E2E";   // unique -> unambiguous in the shared backup dir
            Reset(root);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog(uniqueOrig));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "EDIT-REALPUSH", DateTime.UtcNow);

                var res = await SyncPipeline.PushAsync(exec, cache, new SessionDependencyClipboard());
                Check("a2-real-push-ok", res.Ok && res.Written == 1);
                Check("a2-real-push-backed-up-preimage-on-disk", BackupWithPreImageExists(uniqueOrig));
            }
            finally
            {
                DeleteBackupsWithPreImage(uniqueOrig);
                Reset(root);
            }
        }

        // ── B1: staleness gate. A bank changing between Arm and Apply (concurrent front-panel
        //        edit) must abort BEFORE any Store — hardware untouched — yet the backup is still
        //        written first, so the user can recover even from the aborted attempt. ──
        {
            string root = ScratchRoot + "_b1_lib";
            string backupDir = ScratchRoot + "_b1_bak";
            Reset(root); Reset(backupDir);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog("ORIG-STALE"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "EDIT-STALE", DateTime.UtcNow);

                var (plan, _) = await ChangesetBuilder.BuildAsync(cache, exec, new SessionDependencyClipboard());
                await Librarian.ArmPlanAsync(plan, exec);

                // Front-panel edit lands in the SAME bank (different slot) AFTER we armed — bank
                // 0x00's digest now differs from the baseline the arm captured.
                exec.Seed(LibObj.Program, 0x00, 7, 1, Prog("PANEL-EDIT"));

                exec.CallLog.Clear();
                var (ok, _, aborted) = await Librarian.ApplyMoveAsync(plan, exec, backupDir, "TESTSTAMP", null, doLive: false);
                Check("b1-aborted", !ok);
                Check("b1-error-explains-change", aborted != null && aborted.Contains("changed since preview"));
                Check("b1-no-store-fired", !exec.CallLog.Contains("Store"));
                Check("b1-no-write-fired", !exec.CallLog.Contains("Write"));

                var hw = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("b1-hardware-preserved", hw != null && ProgramBody.ReadName(hw.Body) == "ORIG-STALE");
                // Safety-first: backup is step 1, before the gate — so it exists even on abort.
                Check("b1-backup-still-written", SyxFiles(backupDir).Length == 1);
            }
            finally { Reset(root); Reset(backupDir); }
        }

        // ── B2: a rejected write (func-0x73 Reply != 0) must abort before any Store — no bank is
        //        half-committed, hardware stays at the original. ──
        {
            string root = ScratchRoot + "_b2_lib";
            string backupDir = ScratchRoot + "_b2_bak";
            Reset(root); Reset(backupDir);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog("ORIG-REJECT"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                LocalEditOps.Rename(cache, loc, "EDIT-REJECT", DateTime.UtcNow);

                var (plan, _) = await ChangesetBuilder.BuildAsync(cache, exec, new SessionDependencyClipboard());
                await Librarian.ArmPlanAsync(plan, exec);

                exec.WriteRejectCode = 3;   // hardware rejects the write
                exec.CallLog.Clear();
                var (ok, _, aborted) = await Librarian.ApplyMoveAsync(plan, exec, backupDir, "TESTSTAMP", null, doLive: false);
                Check("b2-aborted", !ok);
                Check("b2-error-mentions-reject", aborted != null && aborted.Contains("rejected"));
                Check("b2-error-suggests-replay", aborted != null && aborted.Contains("replay"));
                Check("b2-no-store-fired", !exec.CallLog.Contains("Store"));

                var hw = await exec.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
                Check("b2-hardware-preserved", hw != null && ProgramBody.ReadName(hw.Body) == "ORIG-REJECT");
            }
            finally { Reset(root); Reset(backupDir); }
        }

        // ── C1: op-log fold is the recovery primitive. It must be last-writer-wins per slot and
        //        must reflect a Discard (revert-to-baseline) — i.e. it reproduces exactly the
        //        cache's live current state, which is what makes index.json throw-away-able. ──
        {
            string root = ScratchRoot + "_c1_lib";
            Reset(root);
            try
            {
                var utc = DateTime.UtcNow;
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog("BASE-FOLD"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                string key = LocalLibraryIndex.Key(loc.ObjType, loc.Bank, loc.Number);
                LocalEditOps.Rename(cache, loc, "FOLD-EDIT-1", utc);
                LocalEditOps.Rename(cache, loc, "FOLD-EDIT-2", utc);   // the winner

                var folded = LocalLibraryIndex.RebuildCurrentFromOpLog(OpLog.ReadAll(root));
                Check("c1-fold-has-slot", folded.ContainsKey(key));
                var foldedBody = folded.TryGetValue(key, out var h1) ? LocalObjectStore.TryGet(root, h1) : null;
                var liveBody = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
                Check("c1-fold-last-writer-wins", foldedBody != null && ProgramBody.ReadName(foldedBody) == "FOLD-EDIT-2");
                Check("c1-fold-matches-live-bytes", foldedBody != null && liveBody != null && foldedBody.SequenceEqual(liveBody));

                // Discard reverts to baseline; the fold, replaying the whole log, must land there too.
                cache.Discard(loc.ObjType, loc.Bank, loc.Number, utc);
                var folded2 = LocalLibraryIndex.RebuildCurrentFromOpLog(OpLog.ReadAll(root));
                var foldedBody2 = folded2.TryGetValue(key, out var h2) ? LocalObjectStore.TryGet(root, h2) : null;
                Check("c1-fold-reflects-discard", foldedBody2 != null && ProgramBody.ReadName(foldedBody2) == "BASE-FOLD");
            }
            finally { Reset(root); }
        }

        // ── C2: crash durability. Pull (which Save()s index.json), then a bare edit (which does
        //        NOT). Simulate a crash before the next Save: the on-disk index.json is stale, yet
        //        the edit — body and all — is fully recoverable from the durable op-log + CAS store.
        //        This is index.json's "recoverable from oplog.jsonl alone" contract, exercised. ──
        {
            string root = ScratchRoot + "_c2_lib";
            Reset(root);
            try
            {
                var utc = DateTime.UtcNow;
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, Prog("BASE-RECOV"));
                var cache = new LocalLibraryCache(root);
                await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // persists index.json (current==baseline)

                var loc = new ObjLoc(LibObj.Program, 0x00, 0);
                string key = LocalLibraryIndex.Key(loc.ObjType, loc.Bank, loc.Number);
                LocalEditOps.Rename(cache, loc, "EDITED-RECOV", utc);            // op-log + CAS only; NO Save()
                var liveEdit = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
                Check("c2-precondition-dirty", cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));

                // The on-disk index still predates the edit (edits don't Save) — a fresh open that
                // trusted index.json alone would show the stale baseline. This is precisely why the
                // op-log has to be the durable source of truth.
                var reopenedFromDiskIndex = new LocalLibraryCache(root).GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
                Check("c2-disk-index-predates-edit",
                    reopenedFromDiskIndex != null && ProgramBody.ReadName(reopenedFromDiskIndex) == "BASE-RECOV");

                // Recover from the op-log: the edit's current hash is there, and its body blob
                // physically persisted to the CAS store — byte-identical to the live in-memory edit.
                var recovered = LocalLibraryIndex.RebuildCurrentFromOpLog(OpLog.ReadAll(root));
                Check("c2-oplog-carries-edit", recovered.ContainsKey(key));
                var recoveredBody = recovered.TryGetValue(key, out var rh) ? LocalObjectStore.TryGet(root, rh) : null;
                Check("c2-recovered-body-is-the-edit", recoveredBody != null && ProgramBody.ReadName(recoveredBody) == "EDITED-RECOV");
                Check("c2-recovered-bytes-intact", recoveredBody != null && liveEdit != null && recoveredBody.SequenceEqual(liveEdit));
            }
            finally { Reset(root); }
        }

        return fails;
    }

    static void Reset(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    // Missing-dir-safe: a skipped/failed backup leaves the dir absent — that must read as
    // "zero backups" (a clean red), never a DirectoryNotFoundException that aborts the suite.
    static string[] SyxFiles(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.syx") : Array.Empty<string>();

    // Does any .syx in the shared librarian-backup dir decode to a Program named `name`? Used to
    // find OUR backup by its unique pre-image, tolerant of non-Program backup files (ReadName on
    // a Combi/SetList/short body yields a non-match or throws — both mean "not ours").
    static bool BackupWithPreImageExists(string name)
    {
        foreach (var f in Directory.GetFiles(Storage.BackupDir(), "*.syx"))
            try { if (ProgramBody.ReadName(File.ReadAllBytes(f)) == name) return true; } catch { }
        return false;
    }

    static void DeleteBackupsWithPreImage(string name)
    {
        foreach (var f in Directory.GetFiles(Storage.BackupDir(), "*.syx"))
            try { if (ProgramBody.ReadName(File.ReadAllBytes(f)) == name) File.Delete(f); } catch { }
    }
}
