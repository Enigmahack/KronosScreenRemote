namespace KronosScreenRemote;

using System.IO;

// Off-hardware self-test for Phase 1 of the Librarian rebuild: the content-addressed blob
// store, op-log, index, pull planner, and pull pipeline (against FakeMoveExecutor). Same
// convention as Librarian.SelfTest/LibraryRepository.SelfTest for the pure/sync half;
// SelfTestAsync() is this codebase's first ASYNC self-test, needed because Pull is
// inherently async against ISysExService (unlike every existing pure SelfTest()).
// App.xaml.cs awaits it via .GetAwaiter().GetResult() from its already-synchronous
// OnStartup, same "one-shot diagnostic, then Environment.Exit" shape as everything else.
static class LocalLibrarySelfTests
{
    static string ScratchRoot => Path.Combine(Path.GetTempPath(), "kronos_selftest_local_library");

    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = ScratchRoot;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            // ── LocalObjectStore: put/get round-trip, content-addressed dedup ──
            var bodyA = new byte[] { 1, 2, 3, 4 };
            var bodyB = new byte[] { 1, 2, 3, 4 };   // same content as A
            var bodyC = new byte[] { 9, 9, 9 };
            string hashA = LocalObjectStore.Put(root, bodyA);
            string hashB = LocalObjectStore.Put(root, bodyB);
            string hashC = LocalObjectStore.Put(root, bodyC);
            Check("cas-dedup", hashA == hashB);
            Check("cas-distinct", hashA != hashC);
            Check("cas-roundtrip", LocalObjectStore.TryGet(root, hashA) is { } got && got.SequenceEqual(bodyA));
            Check("cas-missing-null", LocalObjectStore.TryGet(root, new string('0', 40)) == null);

            // ── OpLog: append + read-all round-trip ──
            var e1 = new OpLogEntry(Guid.NewGuid(), DateTime.UtcNow, "Rename",
                new[] { new OpLogTarget(LibObj.Program, 0x00, 3, hashA) }, "Renamed Program I-A:003", null, null);
            OpLog.Append(root, e1);
            var read = OpLog.ReadAll(root);
            Check("oplog-roundtrip-count", read.Count == 1);
            Check("oplog-roundtrip-fields", read.Count == 1 && read[0].OpKind == "Rename" &&
                read[0].Targets.Count == 1 && read[0].Targets[0].ResultHash == hashA);

            // ── LocalLibraryIndex: save/load round-trip + fold-from-oplog ──
            var idx = new LocalLibraryIndex();
            idx.Entries[LocalLibraryIndex.Key(LibObj.Program, 0x00, 3)] =
                new LocalIndexEntry(5, hashA, hashA, "Test Name", DateTime.UtcNow, null, false);
            idx.BankDigestBaseline[LocalLibraryIndex.BankKey(LibObj.Program, 0x00)] = "deadbeef";
            idx.Save(root);
            var reloaded = LocalLibraryIndex.Load(root);
            Check("index-roundtrip-entry",
                reloaded.Entries.TryGetValue(LocalLibraryIndex.Key(LibObj.Program, 0x00, 3), out var re) && re.CurrentHash == hashA);
            Check("index-roundtrip-digest",
                reloaded.BankDigestBaseline[LocalLibraryIndex.BankKey(LibObj.Program, 0x00)] == "deadbeef");

            // The per-path JsonFileCache serializes whole-file index reads and writes.
            // Any read racing these writes must still observe a complete index.
            bool concurrentReadFailed = false;
            Parallel.Invoke(
                () =>
                {
                    for (int i = 0; i < 100; i++) idx.Save(root);
                },
                () =>
                {
                    for (int i = 0; i < 100; i++)
                        if (!LocalLibraryIndex.Load(root).Entries.ContainsKey(LocalLibraryIndex.Key(LibObj.Program, 0x00, 3)))
                            concurrentReadFailed = true;
                });
            Check("index-concurrent-read-write", !concurrentReadFailed);

            var folded = LocalLibraryIndex.RebuildCurrentFromOpLog(read);
            Check("index-fold-matches", folded[LocalLibraryIndex.Key(LibObj.Program, 0x00, 3)] == hashA);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // ── LibraryPullPlanner: generalized PlanScan, mirrors LibraryRepository.SelfTest's
        //    five cases, generalized from "combi banks + 1 setlist" to "every registry bank" ──
        var allBanks = LibraryPullPlanner.AllBanks().ToList();
        Check("registry-bank-count", allBanks.Count == 21 + 14 + 1);   // 21 program + 14 combi + 1 setlist pseudo-bank

        var empty = new Dictionary<(int, int), string>();
        var freshAllPresent = allBanks.ToDictionary(b => (b.ObjType, b.Bank), b => $"{b.ObjType}-{b.Bank}");
        var p1 = LibraryPullPlanner.PlanPull(empty, freshAllPresent, full: false);
        Check("pull-firstrun-is-full", p1.FirstRun && p1.BanksToFetch.Count == allBanks.Count);

        var p2 = LibraryPullPlanner.PlanPull(freshAllPresent, freshAllPresent, full: false);
        Check("pull-no-change-fetches-nothing", !p2.FirstRun && p2.BanksToFetch.Count == 0);

        var oneBank = allBanks[0];
        var freshOneChanged = new Dictionary<(int, int), string>(freshAllPresent) { [(oneBank.ObjType, oneBank.Bank)] = "CHANGED" };
        var p3 = LibraryPullPlanner.PlanPull(freshAllPresent, freshOneChanged, full: false);
        Check("pull-single-bank-change-detected", p3.BanksToFetch.Count == 1 &&
            p3.BanksToFetch[0].ObjType == oneBank.ObjType && p3.BanksToFetch[0].Bank == oneBank.Bank);

        var setListBank = allBanks.First(b => b.ObjType == LibObj.SetList);
        var freshSlChanged = new Dictionary<(int, int), string>(freshAllPresent) { [(setListBank.ObjType, setListBank.Bank)] = "CHANGED" };
        var p4 = LibraryPullPlanner.PlanPull(freshAllPresent, freshSlChanged, full: false);
        Check("pull-setlist-change-detected", p4.BanksToFetch.Count == 1 && p4.BanksToFetch[0].ObjType == LibObj.SetList);

        var p5 = LibraryPullPlanner.PlanPull(freshAllPresent, freshAllPresent, full: true);
        Check("pull-full-ignores-digests", p5.BanksToFetch.Count == allBanks.Count);

        return fails;
    }

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = ScratchRoot + "_async";
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var namedProgram = ProgramBody.WriteName(new byte[3706], "SEEDED PROG");
            exec.Seed(LibObj.Program, 0x00, 0, 1, namedProgram);
            exec.Seed(LibObj.Combi, 0x00, 0, 1, new byte[7810]);
            exec.Seed(LibObj.SetList, 0, 0, 1, new byte[69416]);

            var cache = new LocalLibraryCache(root);
            var result1 = await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            Check("pull-first-fetched-seeded", result1.ObjectsFetched == 3);
            Check("pull-first-no-conflicts", result1.Conflicts == 0);
            Check("pull-populates-cache", cache.GetCurrentBody(LibObj.Program, 0x00, 0) != null);
            Check("pull-not-dirty-after-pull", !cache.IsDirty(LibObj.Program, 0x00, 0));

            // Regression: a pull touching multiple objects must append exactly ONE
            // "PullBaseline" op-log entry (covering all of them via multiple Targets), never
            // one entry per object — that was a real bug (thousands of individual
            // File.AppendAllText calls against the same oplog.jsonl during a full sync,
            // catastrophically slow over an SMB-mounted DataDir).
            var oplogAfterPull = OpLog.ReadAll(root);
            Check("pull-writes-one-batched-oplog-entry",
                oplogAfterPull.Count(e => e.OpKind == "PullBaseline") == 1);
            Check("pull-oplog-entry-covers-all-objects",
                oplogAfterPull.First(e => e.OpKind == "PullBaseline").Targets.Count == 3);

            // DisplayName is cached at pull time (never re-derived from a body read later —
            // see LocalIndexEntry's doc comment for why that matters) and Exists() answers
            // "is anything here" from the index alone.
            Check("pull-caches-display-name", cache.GetDisplayName(LibObj.Program, 0x00, 0) == "SEEDED PROG");
            Check("exists-true-for-populated-slot", cache.Exists(LibObj.Program, 0x00, 0));
            Check("exists-false-for-empty-slot", !cache.Exists(LibObj.Program, 0x00, 99));
            Check("display-name-blank-for-empty-slot", cache.GetDisplayName(LibObj.Program, 0x00, 99) == "");

            // Local edit to Program (0x00,0) — dirty, but hardware for that bank hasn't
            // changed yet, so an intervening lazy pull must leave it alone.
            var editedBody = ProgramBody.WriteName(new byte[3706], "EDITED PROG"); editedBody[100] = 0x41;   // marker outside the 24-byte name field
            cache.RecordEdit(LibObj.Program, 0x00, 0, 1, editedBody, "Rename", "Renamed test", DateTime.UtcNow);
            Check("edit-marks-dirty", cache.IsDirty(LibObj.Program, 0x00, 0));
            Check("edit-updates-display-name", cache.GetDisplayName(LibObj.Program, 0x00, 0) == "EDITED PROG");

            var result2 = await LibraryPullPipeline.PullAsync(exec, cache, full: false);
            Check("unrelated-pull-preserves-edit",
                cache.IsDirty(LibObj.Program, 0x00, 0) && !cache.IsConflicted(LibObj.Program, 0x00, 0));
            Check("unrelated-pull-fetches-nothing", result2.BanksChecked == 0);

            // Regression: a FORCE FULL pull re-sweeps every bank regardless of digest, so
            // it must NOT treat "bank unchanged" as "safe to overwrite" — a dirty object
            // survives even when its bank IS re-swept (unlike the lazy pull above, which
            // never even looks at this bank because nothing flagged it as changed).
            var resultFull = await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            Check("full-pull-preserves-unconflicted-edit",
                cache.IsDirty(LibObj.Program, 0x00, 0) && !cache.IsConflicted(LibObj.Program, 0x00, 0) &&
                cache.GetCurrentBody(LibObj.Program, 0x00, 0) is { } stillEdited && stillEdited.SequenceEqual(editedBody));

            // Mutate "hardware" in the SAME bank, a DIFFERENT slot — the bank digest
            // changes, so a lazy pull must re-sweep the bank: flag (0x00,0) Conflicted
            // (preserving its edit), while still refreshing (0x00,1), which isn't dirty.
            var hwEdited = new byte[3706]; hwEdited[1] = 0x42;
            exec.Seed(LibObj.Program, 0x00, 1, 1, hwEdited);
            var result3 = await LibraryPullPipeline.PullAsync(exec, cache, full: false);
            Check("conflict-detected", cache.IsConflicted(LibObj.Program, 0x00, 0));
            Check("conflict-preserves-edit", cache.IsDirty(LibObj.Program, 0x00, 0) &&
                cache.GetCurrentBody(LibObj.Program, 0x00, 0) is { } cur && cur.SequenceEqual(editedBody));
            Check("conflict-refreshes-sibling",
                cache.GetCurrentBody(LibObj.Program, 0x00, 1) is { } sib && sib.SequenceEqual(hwEdited));
            Check("conflict-count-reported", result3.Conflicts == 1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // ── Bulk-bank dump: succeeds -> zero individual Dump calls for that bank ──
        {
            string bulkRoot = ScratchRoot + "_bulk_ok";
            if (Directory.Exists(bulkRoot)) Directory.Delete(bulkRoot, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor();
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);
                exec.Seed(LibObj.Program, 0x00, 5, 1, new byte[3706]);
                var cache = new LocalLibraryCache(bulkRoot);

                var result = await LibraryPullPipeline.PullAsync(exec, cache, full: true);
                Check("bulk-ok-fetched-both", result.ObjectsFetched >= 2);
                Check("bulk-ok-populates-slot0", cache.GetCurrentBody(LibObj.Program, 0x00, 0) != null);
                Check("bulk-ok-populates-slot5", cache.GetCurrentBody(LibObj.Program, 0x00, 5) != null);
                Check("bulk-ok-calls-bulkdump", exec.CallLog.Any(c => c.StartsWith($"BulkDump:{LibObj.Program}:{0x00}")));
                // Other (unrelated, unseeded) banks still legitimately fall back per-slot —
                // their bulk result is ambiguously empty too. Only THIS bank, which bulk
                // demonstrably covered (2 real objects came back), must see zero individual
                // Dump calls for any of its own slots.
                Check("bulk-ok-skips-individual-dump-for-covered-bank",
                    !exec.CallLog.Any(c => c.StartsWith($"Dump:{LibObj.Program}:{0x00}:")));
            }
            finally { if (Directory.Exists(bulkRoot)) Directory.Delete(bulkRoot, recursive: true); }
        }

        // ── Bulk-bank dump: rejected/unsupported -> falls back to per-object dump,
        //    same end result as if bulk never existed ──
        {
            string fallbackRoot = ScratchRoot + "_bulk_fallback";
            if (Directory.Exists(fallbackRoot)) Directory.Delete(fallbackRoot, recursive: true);
            try
            {
                var exec = new FakeMoveExecutor { SimulateBulkDumpUnsupported = true };
                exec.Seed(LibObj.Program, 0x00, 0, 1, new byte[3706]);
                var cache = new LocalLibraryCache(fallbackRoot);

                var result = await LibraryPullPipeline.PullAsync(exec, cache, full: true);
                Check("bulk-fallback-still-fetches", result.ObjectsFetched >= 1);
                Check("bulk-fallback-populates-cache", cache.GetCurrentBody(LibObj.Program, 0x00, 0) != null);
                Check("bulk-fallback-attempted-bulk-first", exec.CallLog.Any(c => c.StartsWith("BulkDump:")));
                Check("bulk-fallback-used-individual-dump",
                    exec.CallLog.Any(c => c.StartsWith($"Dump:{LibObj.Program}:{0x00}:")));
            }
            finally { if (Directory.Exists(fallbackRoot)) Directory.Delete(fallbackRoot, recursive: true); }
        }

        return fails;
    }
}
