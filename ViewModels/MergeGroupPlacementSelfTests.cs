namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware self-test for a real bug: PlaceMergeGroupSequentially's predecessor (
// PlaceMergeGroupIntoEmptyBank) refused a multi-item Merge Window drag outright unless the
// ENTIRE destination bank was completely empty — reported by a user dragging 7 Combis onto a
// bank that had plenty of free slots, just not from slot 0. Fixed to auto-fill sequentially
// starting at the bank's own first free slot (LocalEditOps.FindNextFreeSlot), exactly like
// BatchPlaceFromPcg's own long-standing multi-item behavior, instead of requiring emptiness.
static class MergeGroupPlacementSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_group_placement");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // nothing seeded — empty local library

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "");

            var pcgBuffer = BuildSyntheticPcg(out var combiABody, out var combiBBody, out var combiCBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("pcg-opens", file != null);
            if (file == null) return fails;

            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 0));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 1));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 2));

            string hashA = LocalObjectStore.ComputeHash(combiABody);
            string hashB = LocalObjectStore.ComputeHash(combiBBody);
            string hashC = LocalObjectStore.ComputeHash(combiCBody);
            Check("all-three-staged", vm.MergePane.TryGet(hashA) != null && vm.MergePane.TryGet(hashB) != null && vm.MergePane.TryGet(hashC) != null);

            // Occupy slots 0 and 1 of the destination bank BEFORE the group drop — the exact
            // shape of the user's report: the bank isn't empty, but there's plenty of free
            // room from slot 2 onward.
            var seed0Body = new byte[7810];
            Encoding.ASCII.GetBytes("SEED 0").CopyTo(seed0Body, 0);
            var seed1Body = new byte[7810];
            Encoding.ASCII.GetBytes("SEED 1").CopyTo(seed1Body, 0);
            var (seedOk1, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x40, 0), LibObj.Combi, 1, seed0Body, "seed0", true, DateTime.UtcNow);
            var (seedOk2, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x40, 1), LibObj.Combi, 1, seed1Body, "seed1", true, DateTime.UtcNow);
            Check("seed-slots-ok", seedOk1 && seedOk2);

            var (ok, msg) = vm.PlaceMergeGroupSequentially(LibObj.Combi, 0x40, new[] { hashA, hashB, hashC });
            Check("group-drop-not-refused-for-nonempty-bank", ok);

            // Lands starting at the first free slot (2), leaving the pre-occupied 0/1 untouched.
            Check("combiA-at-slot-2", cache.GetDisplayName(LibObj.Combi, 0x40, 2) == "COMBI A");
            Check("combiB-at-slot-3", cache.GetDisplayName(LibObj.Combi, 0x40, 3) == "COMBI B");
            Check("combiC-at-slot-4", cache.GetDisplayName(LibObj.Combi, 0x40, 4) == "COMBI C");
            Check("seed-slot-0-untouched", cache.GetDisplayName(LibObj.Combi, 0x40, 0) == "SEED 0");
            Check("seed-slot-1-untouched", cache.GetDisplayName(LibObj.Combi, 0x40, 1) == "SEED 1");

            // All three are now placed (moved), so the Merge Window shouldn't still have them.
            Check("all-three-removed-from-merge", vm.MergePane.TryGet(hashA) == null &&
                vm.MergePane.TryGet(hashB) == null && vm.MergePane.TryGet(hashC) == null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        // ── Dropped on a specific slot -> fill starts EXACTLY there, not at the bank's first
        //    free slot (the bug this comment's sibling test above doesn't cover): a user
        //    dragging Combis onto slot U-E003 expects the paste to begin at 003, even though an
        //    earlier free slot (e.g. 000) exists elsewhere in the bank. ────────────────────────
        string slotRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_group_placement_slot");
        if (Directory.Exists(slotRoot)) Directory.Delete(slotRoot, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(slotRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "");

            var pcgBuffer = BuildSyntheticPcg(out var combiABody, out var combiBBody, out var combiCBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("slot-pcg-opens", file != null);
            if (file == null) return fails;

            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 0));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 1));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 2));

            string hashA = LocalObjectStore.ComputeHash(combiABody);
            string hashB = LocalObjectStore.ComputeHash(combiBBody);
            string hashC = LocalObjectStore.ComputeHash(combiCBody);

            // Slot 0 is left free on purpose — if startSlot fell back to FindNextFreeSlot instead
            // of honoring destSlot, the group would land at 0/1/2 instead of 3/4/5.
            var (ok, _) = vm.PlaceMergeGroupSequentially(LibObj.Combi, 0x40, new[] { hashA, hashB, hashC }, destSlot: 3);
            Check("slot-group-drop-ok", ok);

            Check("slot-combiA-at-dropped-slot-3", cache.GetDisplayName(LibObj.Combi, 0x40, 3) == "COMBI A");
            Check("slot-combiB-at-slot-4", cache.GetDisplayName(LibObj.Combi, 0x40, 4) == "COMBI B");
            Check("slot-combiC-at-slot-5", cache.GetDisplayName(LibObj.Combi, 0x40, 5) == "COMBI C");
            Check("slot-0-left-empty", !cache.Exists(LibObj.Combi, 0x40, 0));
        }
        finally { if (Directory.Exists(slotRoot)) Directory.Delete(slotRoot, recursive: true); }

        // ── Whole-bank copy with EXi/HD-1 type change (requirement 4), via PullFromLocal so no
        //    synthetic-PCG program encoding is needed: seed EXi programs in source bank I-A and
        //    HD-1 programs in destination bank U-A, pull the EXi bank into the Merge Window, then
        //    place it into U-A with a type change. The destination bank is replaced and the
        //    type-change intent is recorded for Commit (the 0x7C path is covered by
        //    SyncPipelineSelfTests case I). ─────────────────────────────────────────────────────
        string tcRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_typechange");
        if (Directory.Exists(tcRoot)) Directory.Delete(tcRoot, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            const int srcBank = 0x00, destBank = 0x40;   // I-A (source), U-A (destination)
            var exiBodies = new byte[3][];
            for (int n = 0; n < 3; n++)
            {
                exiBodies[n] = new byte[ProgramFormatConverter.WireSizeExi];
                Encoding.ASCII.GetBytes($"EXI {n}").CopyTo(exiBodies[n], 0);
                exec.Seed(LibObj.Program, srcBank, n, 5, exiBodies[n]);
            }
            for (int n = 0; n < 2; n++) exec.Seed(LibObj.Program, destBank, n, 5, new byte[ProgramFormatConverter.WireSizeHd1]);

            var bits = new bool[21];
            bits[1] = true;    // I-A = EXi (matches the seeded EXi bodies)
            bits[7] = false;   // U-A = HD-1 (the destination we're changing)
            exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

            var cache = new LocalLibraryCache(tcRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            // A UNIQUE host, not "" — WarmProgramBankTypesForTestingAsync persists the queried
            // types to the host-keyed global cache (program_bank_types_cache.json), so using the
            // empty host every other VM-based self-test uses would pollute their BankTypeOf and
            // make their EXi placements REFUSE. This host is read by nothing else.
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-typechange-host");
            await vm.WarmProgramBankTypesForTestingAsync();   // so BankTypeOf(destBank) resolves

            for (int n = 0; n < 3; n++) vm.PullLocalIntoMerge(new ObjLoc(LibObj.Program, srcBank, n));
            var hashes = exiBodies.Select(LocalObjectStore.ComputeHash).ToList();
            Check("tc-all-exi-staged", hashes.All(h => vm.MergePane.TryGet(h) != null));

            var needed = vm.BankTypeChangeNeeded(LibObj.Program, destBank, hashes);
            Check("tc-type-change-detected", needed == true);

            var (ok, _) = vm.PlaceMergeBankWithTypeChange(destBank, hashes, targetIsExi: true);
            Check("tc-place-ok", ok);
            Check("tc-intent-recorded", cache.PendingBankTypeChange(destBank) == true);
            Check("tc-exi-programs-in-dest", cache.Exists(LibObj.Program, destBank, 0) &&
                cache.IsExi(LibObj.Program, destBank, 0) && cache.GetDisplayName(LibObj.Program, destBank, 0) == "EXI 0");
            Check("tc-dest-bank-replaced-count", Enumerable.Range(0, 128).Count(n => cache.Exists(LibObj.Program, destBank, n)) == 3);
            Check("tc-removed-from-merge", hashes.All(h => vm.MergePane.TryGet(h) == null));
            Check("tc-source-untouched", cache.Exists(LibObj.Program, srcBank, 0));
        }
        finally { if (Directory.Exists(tcRoot)) Directory.Delete(tcRoot, recursive: true); }

        // ── Duplicate-content guard: placing a Merge-staged item whose content is byte-
        //    identical to something ALREADY elsewhere in Local Library reuses that location
        //    instead of writing a second copy — single-item (PlaceFromMerge) and group
        //    (PlaceMergeGroupSequentially) paths both covered. ──────────────────────────────────
        string dedupRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_dedup");
        if (Directory.Exists(dedupRoot)) Directory.Delete(dedupRoot, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(dedupRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // empty local library

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "");

            var pcgBuffer = BuildSyntheticPcg(out var combiABody, out var combiBBody, out var combiCBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("dedup-pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));

            // Seed A's exact content already in the library, at a location distinct from
            // anything the placements below target.
            var existingLoc = new ObjLoc(LibObj.Combi, 0x40, 50);
            var (seedOk, _, _) = LocalEditOps.PlaceObject(cache, existingLoc, LibObj.Combi, 1, combiABody, "PRE-EXISTING A", true, DateTime.UtcNow);
            Check("dedup-seed-ok", seedOk);
            // Display name is decoded from the wire body itself (matches "COMBI A" baked into
            // combiABody by BuildSyntheticPcg), not the friendly-name argument above — same as
            // every other placement test in this file (e.g. combiA-at-slot-2 below).

            string hashA = LocalObjectStore.ComputeHash(combiABody);
            string hashB = LocalObjectStore.ComputeHash(combiBBody);
            string hashC = LocalObjectStore.ComputeHash(combiCBody);

            // Single-item path: dragging A onto a fresh slot must NOT write a second copy —
            // it reuses the pre-existing location and reports it in the returned message.
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 0));
            var destForA = new ObjLoc(LibObj.Combi, 0x40, 60);
            var (aOk, aNote) = vm.PlaceFromMerge(hashA, destForA);
            Check("dedup-single-ok", aOk);
            Check("dedup-single-not-written-at-requested-dest", !cache.Exists(LibObj.Combi, 0x40, 60));
            Check("dedup-single-existing-untouched", cache.GetDisplayName(LibObj.Combi, 0x40, 50) == "COMBI A");
            Check("dedup-single-note-names-existing-loc", aNote != null && aNote.Contains(existingLoc.Label()));
            Check("dedup-single-removed-from-merge", vm.MergePane.TryGet(hashA) == null);

            // Group path: B and C are genuinely new; re-stage A alongside them (it's fine for
            // Merge to hold a duplicate entry again) and drop all three as a group — only B/C
            // should consume destination slots, A should dedup without taking one.
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 0));   // A, restaged
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 1));   // B
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 2));   // C
            var (groupOk, groupMsg) = vm.PlaceMergeGroupSequentially(LibObj.Combi, 0x40, new[] { hashA, hashB, hashC });
            Check("dedup-group-ok", groupOk);
            Check("dedup-group-b-at-first-free-slot", cache.GetDisplayName(LibObj.Combi, 0x40, 0) == "COMBI B");
            Check("dedup-group-c-at-next-slot", cache.GetDisplayName(LibObj.Combi, 0x40, 1) == "COMBI C");
            Check("dedup-group-a-not-duplicated", !cache.Exists(LibObj.Combi, 0x40, 2));
            Check("dedup-group-message-mentions-reuse", groupMsg != null && groupMsg.Contains("already existed elsewhere"));
            Check("dedup-group-all-removed-from-merge", vm.MergePane.TryGet(hashA) == null &&
                vm.MergePane.TryGet(hashB) == null && vm.MergePane.TryGet(hashC) == null);
        }
        finally { if (Directory.Exists(dedupRoot)) Directory.Delete(dedupRoot, recursive: true); }

        return fails;
    }

    static byte[] BuildSyntheticPcg(out byte[] combiABody, out byte[] combiBBody, out byte[] combiCBody)
    {
        const int combiSize = 7810;
        combiABody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI A").CopyTo(combiABody, 0);
        combiBBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI B").CopyTo(combiBBody, 0);
        combiCBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI C").CopyTo(combiCBody, 0);

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void WriteBank(string tag, int count, int itemSize, int bankId, byte[] record)
        {
            WriteAscii(tag); WriteBE32(0); WriteBE32(0); WriteBE32(count); WriteBE32(itemSize); WriteBE32(bankId);
            ms.Write(record);
        }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        using var combis = new MemoryStream();
        combis.Write(combiABody); combis.Write(combiBBody); combis.Write(combiCBody);
        WriteBank("CBK1", 3, combiSize, 0, combis.ToArray());

        return ms.ToArray();
    }
}
