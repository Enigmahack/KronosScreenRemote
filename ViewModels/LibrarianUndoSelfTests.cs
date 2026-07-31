namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware coverage for the Librarian's linear undo (Core/LocalLibrary/LibrarianUndo.cs),
// which exists for a specific reported problem: dragging a whole bank out of the Merge Window by
// accident emptied the staging area AND wrote the destination bank, with no way back - the user
// had to re-pull and re-stage everything by hand.
//
// The load-bearing property these cases pin down is that ONE gesture is ONE step and rolls back
// BOTH sides of it (staged merge entries and every local slot the action wrote, including an
// occupant it overwrote), never just one of the two - a half-applied undo is worse than none.
// Wired into App.xaml.cs's --librarian-selftest.
static class LibrarianUndoSelfTests
{
    const int CombiWireSize = 7810;

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── Case A: whole-bank Merge -> Local drop, undone (the reported gesture) ─────────────
        //    Also covers: an empty stack is inert, and LIFO order across two steps.
        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_undo_group");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            const int srcBank = 0x00, destBank = 0x40;   // Combi I-A -> U-A
            var exec = new FakeMoveExecutor();
            var bodies = new byte[3][];
            for (int n = 0; n < 3; n++)
            {
                bodies[n] = MakeCombi($"UNDO {n}");
                exec.Seed(LibObj.Combi, srcBank, n, 1, bodies[n]);
            }
            // An occupant in the destination bank the sequential fill must skip (and undo must
            // leave alone) - the fill starts at the first FREE slot, so this stays at slot 0.
            exec.Seed(LibObj.Combi, destBank, 0, 1, MakeCombi("OCCUPANT", real: true));

            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            // A UNIQUE host, never "" - the ctor's bank-type warm-up persists to a host-keyed
            // global cache, and sharing the empty host pollutes every other VM-based self-test.
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-undo-host");

            Check("A-empty-stack-cannot-undo", !vm.CanUndo && !vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Check("A-empty-stack-undo-is-inert",
                vm.StatusText == AppMessages.Librarian.Shell.NothingToUndo &&
                cache.GetDisplayName(LibObj.Combi, destBank, 0) == "OCCUPANT");

            var hashes = bodies.Select(LocalObjectStore.ComputeHash).ToList();
            vm.PullLocalIntoMerge(Enumerable.Range(0, 3).Select(n => new ObjLoc(LibObj.Combi, srcBank, n)).ToList());
            Check("A-staged", hashes.All(h => vm.MergePane.TryGet(h) != null));
            Check("A-pull-is-undoable", vm.CanUndo);

            var (ok, _) = vm.PlaceMergeGroupSequentially(LibObj.Combi, destBank, hashes);
            Check("A-group-placed", ok);
            Check("A-landed-after-occupant",
                cache.GetDisplayName(LibObj.Combi, destBank, 1) == "UNDO 0" &&
                cache.GetDisplayName(LibObj.Combi, destBank, 2) == "UNDO 1" &&
                cache.GetDisplayName(LibObj.Combi, destBank, 3) == "UNDO 2");
            Check("A-merge-emptied-by-placement", hashes.All(h => vm.MergePane.TryGet(h) == null));

            vm.UndoCommand.Execute(null);
            Check("A-local-slots-rolled-back",
                !cache.Exists(LibObj.Combi, destBank, 1) &&
                !cache.Exists(LibObj.Combi, destBank, 2) &&
                !cache.Exists(LibObj.Combi, destBank, 3));
            Check("A-merge-restaged", hashes.All(h => vm.MergePane.TryGet(h) != null));
            Check("A-occupant-untouched", cache.GetDisplayName(LibObj.Combi, destBank, 0) == "OCCUPANT");
            Check("A-source-untouched", cache.GetDisplayName(LibObj.Combi, srcBank, 0) == "UNDO 0");
            Check("A-status-names-the-step", vm.StatusText.StartsWith("Undone: ", StringComparison.Ordinal));

            // LIFO: the pull is still on the stack under the placement that was just undone.
            Check("A-pull-still-undoable", vm.CanUndo);
            vm.UndoCommand.Execute(null);
            Check("A-pull-rolled-back", hashes.All(h => vm.MergePane.TryGet(h) == null));
            Check("A-stack-now-empty", !vm.CanUndo);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        // ── Case B: exact-slot placement OVER an occupant - undo must bring the occupant back,
        //    not merely empty the slot (the occupant's own body, not just its presence). ────────
        string overwriteRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_undo_overwrite");
        if (Directory.Exists(overwriteRoot)) Directory.Delete(overwriteRoot, recursive: true);
        try
        {
            const int srcBank = 0x00, destBank = 0x40;
            var exec = new FakeMoveExecutor();
            var incoming = MakeCombi("INCOMING");
            var occupant = MakeCombi("OCCUPANT");
            exec.Seed(LibObj.Combi, srcBank, 0, 1, incoming);
            exec.Seed(LibObj.Combi, destBank, 5, 1, occupant);

            var cache = new LocalLibraryCache(overwriteRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-undo-host");

            string occupantHash = LocalObjectStore.ComputeHash(occupant);
            string incomingHash = LocalObjectStore.ComputeHash(incoming);
            var dest = new ObjLoc(LibObj.Combi, destBank, 5);

            vm.PullLocalIntoMerge(new[] { new ObjLoc(LibObj.Combi, srcBank, 0) });
            var (ok, _) = vm.PlaceFromMerge(incomingHash, dest);
            Check("B-placed-over-occupant", ok && cache.GetDisplayName(LibObj.Combi, destBank, 5) == "INCOMING");

            vm.UndoCommand.Execute(null);
            var restored = cache.GetCurrentBody(LibObj.Combi, destBank, 5);
            Check("B-occupant-body-restored", restored != null && LocalObjectStore.ComputeHash(restored) == occupantHash);
            Check("B-occupant-name-restored", cache.GetDisplayName(LibObj.Combi, destBank, 5) == "OCCUPANT");
            // The occupant was clean (just pulled) before the placement dirtied its slot - undo
            // restores the whole index entry, so it must be clean again, not left flagged dirty.
            Check("B-occupant-clean-again", !cache.IsDirty(LibObj.Combi, destBank, 5));
            Check("B-item-restaged", vm.MergePane.TryGet(incomingHash) != null);
        }
        finally { if (Directory.Exists(overwriteRoot)) Directory.Delete(overwriteRoot, recursive: true); }

        // ── Case C: whole Program bank copy WITH an HD-1/EXi type change - the most destructive
        //    placement (it drops every local Program in the destination first) plus a pending
        //    bank-type intent that isn't a slot write and so is captured explicitly. ────────────
        string tcRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_undo_typechange");
        if (Directory.Exists(tcRoot)) Directory.Delete(tcRoot, recursive: true);
        try
        {
            const int srcBank = 0x00, destBank = 0x40;   // Program I-A (EXi) -> U-A (HD-1)
            var exec = new FakeMoveExecutor();
            var exiBodies = new byte[3][];
            for (int n = 0; n < 3; n++)
            {
                exiBodies[n] = new byte[ProgramFormatConverter.WireSizeExi];
                Encoding.ASCII.GetBytes($"EXI {n}").CopyTo(exiBodies[n], 0);
                exec.Seed(LibObj.Program, srcBank, n, 5, exiBodies[n]);
            }
            var hd1Bodies = new byte[2][];
            for (int n = 0; n < 2; n++)
            {
                hd1Bodies[n] = new byte[ProgramFormatConverter.WireSizeHd1];
                Encoding.ASCII.GetBytes($"HD1 {n}").CopyTo(hd1Bodies[n], 0);
                exec.Seed(LibObj.Program, destBank, n, 5, hd1Bodies[n]);
            }
            var bits = new bool[21];
            bits[1] = true;    // I-A = EXi
            bits[7] = false;   // U-A = HD-1 (the bank being reformatted)
            exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);

            var cache = new LocalLibraryCache(tcRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-undo-typechange-host");
            await vm.WarmProgramBankTypesForTestingAsync();

            var hashes = exiBodies.Select(LocalObjectStore.ComputeHash).ToList();
            vm.PullLocalIntoMerge(Enumerable.Range(0, 3).Select(n => new ObjLoc(LibObj.Program, srcBank, n)).ToList());
            Check("C-type-change-detected", vm.BankTypeChangeNeeded(LibObj.Program, destBank, hashes) == true);

            var (ok, _) = vm.PlaceMergeBankWithTypeChange(destBank, hashes, targetIsExi: true);
            Check("C-placed", ok && cache.PendingBankTypeChange(destBank) == true);
            Check("C-dest-replaced", Enumerable.Range(0, 128).Count(n => cache.Exists(LibObj.Program, destBank, n)) == 3 &&
                cache.GetDisplayName(LibObj.Program, destBank, 0) == "EXI 0");

            vm.UndoCommand.Execute(null);
            Check("C-type-change-intent-cleared", cache.PendingBankTypeChange(destBank) == null);
            Check("C-wiped-hd1-programs-restored",
                cache.GetDisplayName(LibObj.Program, destBank, 0) == "HD1 0" &&
                cache.GetDisplayName(LibObj.Program, destBank, 1) == "HD1 1" &&
                !cache.IsExi(LibObj.Program, destBank, 0));
            Check("C-extra-slot-removed", Enumerable.Range(0, 128).Count(n => cache.Exists(LibObj.Program, destBank, n)) == 2);
            Check("C-merge-restaged", hashes.All(h => vm.MergePane.TryGet(h) != null));
        }
        finally { if (Directory.Exists(tcRoot)) Directory.Delete(tcRoot, recursive: true); }

        // ── Case D: a Local-pane edit (rename) is one step too, and undo restores the prior body
        //    AND the prior dirty state - undo isn't Merge-Window-only. ─────────────────────────
        string localRoot = Path.Combine(Path.GetTempPath(), "kronos_selftest_undo_local");
        if (Directory.Exists(localRoot)) Directory.Delete(localRoot, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            exec.Seed(LibObj.Combi, 0x00, 0, 1, MakeCombi("BEFORE"));
            var cache = new LocalLibraryCache(localRoot);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-undo-host");
            var loc = new ObjLoc(LibObj.Combi, 0x00, 0);

            Check("D-clean-before-rename", !cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));
            vm.LocalPane.Rename(loc, "AFTER");
            Check("D-renamed", cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number) == "AFTER" &&
                cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));

            vm.UndoCommand.Execute(null);
            Check("D-name-restored", cache.GetDisplayName(loc.ObjType, loc.Bank, loc.Number) == "BEFORE");
            Check("D-clean-again", !cache.IsDirty(loc.ObjType, loc.Bank, loc.Number));
            Check("D-stack-empty", !vm.CanUndo);
        }
        finally { if (Directory.Exists(localRoot)) Directory.Delete(localRoot, recursive: true); }

        return fails;
    }

    // real: give one timbre a non-default reference so the Combi reads as genuine content rather
    // than an INIT placeholder. A body that is merely named still has all 16 timbres pointing at
    // the zero default, which IS the defining shape of an init Combi (CombiBody.AllTimbresAtDefault)
    // - and init slots now count as free space for auto-fill, so an occupant meant to be SKIPPED
    // has to be real. See InitObjects.
    static byte[] MakeCombi(string name, bool real = false)
    {
        var body = new byte[CombiWireSize];
        Encoding.ASCII.GetBytes(name).CopyTo(body, 0);
        if (real) LibRefs.SetCombiTimbreRef(body, 0, KronosBanks.ObjBankToFunc33(1, 0x40), 7);
        return body;
    }
}
