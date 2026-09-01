namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for the CROSS-CUTTING case the auto-heal placement pipeline exists
// for: a Combi placed via the Merge Window whose dependency isn't local YET gets tracked
// (LibrarianShellViewModel.TrackMergeDependencies); placing that dependency LATER, at a
// DIFFERENT address than originally expected, must NOT immediately fix the already-placed
// Combi (step 3 resolves LAZILY, only at the next Sync/Commit - see
// LibrarianShellViewModel.ResolvePendingDependencies); and once Commit runs, the Combi must
// come back repatched via a REAL edit (re-dirtied, in History, present in the push changeset)
// - never a silent byte mutation. This is the one test that actually guards the RecordEdit
// requirement (see LocalEditOps.RepatchReference's own comment). Also covers step 4 - the
// ConfirmContinueWithPendingDependencies gate for whatever's STILL unresolved.
static class DependencyResolutionSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_dependency_resolution");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // nothing seeded - empty local library

            // A UNIQUE host key, never the empty one: LibrarianShellViewModel's constructor seeds
            // its Program bank types from the REAL, global, host-keyed cache file next to the exe,
            // so sharing a key with another self-test means loading whatever that test persisted -
            // an all-HD-1 answer here would REFUSE every EXi placement below.
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-depresolution-host");

            var pcgBuffer = BuildSyntheticPcg(out var progABody, out var combiXBody, out var combiZBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("pcg-opens", file != null);
            if (file == null) return fails;

            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));
            var combiXLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            var combiZLoc = new ObjLoc(LibObj.Combi, 0x00, 1);

            // ── Lazy repatch across Sync/Commit ─────────────────────────────────────────
            // Pull Combi X into the Merge Window - fully transitive, so Program A (its own
            // dependency) comes along too. Computed directly from the fixture's own bytes
            // (MergePaneViewModel exposes no "find by name" - this is the same content the
            // pull will hash internally).
            vm.PullIntoMerge(combiXLoc);
            string progAHash = LocalObjectStore.ComputeHash(progABody);
            string combiXHash = LocalObjectStore.ComputeHash(combiXBody);
            Check("progA-staged-in-merge", vm.MergePane.TryGet(progAHash) != null);
            Check("combiX-staged-in-merge", vm.MergePane.TryGet(combiXHash) != null);

            // Place Combi X FIRST, while Program A is still only staged (not placed anywhere)
            // - its reference can't resolve yet, so it must be tracked, not silently left
            // pointing at the raw (empty) PCG address.
            var combiDestLoc = new ObjLoc(LibObj.Combi, 0x40, 0);
            var (combiOk, combiErr) = vm.PlaceFromMerge(combiXHash, combiDestLoc);
            Check("place-combiX-ok", combiOk && combiErr == null);

            // "Unresolved" means the timbre still holds the address the PCG itself encoded
            // (Program A's own I-B:000), untouched - NOT some sentinel. Program A isn't local
            // anywhere yet, so there is nothing to repoint it to.
            var pcgProgALoc = new ObjLoc(LibObj.Program, 0x01, 0);
            int fbProgASource = KronosBanks.ObjBankToFunc33(1, pcgProgALoc.Bank);
            var combiBodyRightAfterPlacement = cache.GetCurrentBody(combiDestLoc.ObjType, combiDestLoc.Bank, combiDestLoc.Number);
            Check("combiX-reference-unresolved-right-after-placement",
                combiBodyRightAfterPlacement != null &&
                LibRefs.CombiTimbreRef(combiBodyRightAfterPlacement, 0) == (fbProgASource, pcgProgALoc.Number));
            Check("combiX-tracked-pending", vm.SessionClipboardRows.Count > 0);

            // Now place Program A - deliberately at a DIFFERENT address than its own natural
            // PCG address (which stays empty), proving the eventual fix-up can't just be
            // "re-check the same address the reference already encodes."
            var progADestLoc = new ObjLoc(LibObj.Program, 0x41, 9);
            var (progOk, progErr) = vm.PlaceFromMerge(progAHash, progADestLoc);
            Check("place-progA-ok", progOk && progErr == null);

            // LAZY, not eager - Combi X's already-placed body must be UNCHANGED right after
            // Program A lands; nothing repatches it until the next Sync/Commit.
            var combiBodyStillUnresolved = cache.GetCurrentBody(combiDestLoc.ObjType, combiDestLoc.Bank, combiDestLoc.Number);
            Check("combiX-not-repatched-before-commit", combiBodyStillUnresolved != null &&
                LibRefs.CombiTimbreRef(combiBodyStillUnresolved, 0) == (fbProgASource, pcgProgALoc.Number));

            await vm.PushOnlyAsync();

            var finalCombiBody = cache.GetCurrentBody(combiDestLoc.ObjType, combiDestLoc.Bank, combiDestLoc.Number);
            int fbProgADest = KronosBanks.ObjBankToFunc33(1, progADestLoc.Bank);
            Check("combiX-repatched-after-commit", finalCombiBody != null &&
                LibRefs.CombiTimbreRef(finalCombiBody, 0) == (fbProgADest, progADestLoc.Number));
            Check("progA-pending-cleared-after-commit", vm.SessionClipboardRows.Count == 0);
            Check("history-shows-real-repatch-edit", vm.History.Any(h => h.Description.Contains("Repointed a reference")));
            Check("combiX-clean-after-successful-push", !cache.IsDirty(combiDestLoc.ObjType, combiDestLoc.Bank, combiDestLoc.Number));

            // ── Step 4: whatever's STILL unresolved after the repatch pass consults
            // ConfirmContinueWithPendingDependencies, and respects its answer either way.
            // Combi Z references a Program not present in this PCG at ALL - a true,
            // unrepairable gap (ExpectedContentHash stays null; ResolvePendingDependencies
            // can never search for it).
            var combiZDestLoc = new ObjLoc(LibObj.Combi, 0x40, 1);
            vm.PlaceFromPcg(combiZLoc, combiZDestLoc);
            Check("combiZ-tracked-pending", vm.SessionClipboardRows.Count > 0);

            bool confirmCalled = false;
            vm.ConfirmContinueWithPendingDependencies = _ => { confirmCalled = true; return Task.FromResult(false); };   // user cancels
            await vm.PushOnlyAsync();
            Check("confirm-delegate-invoked-when-unresolved", confirmCalled);
            Check("cancel-leaves-pending-tracked", vm.SessionClipboardRows.Count > 0);
            Check("cancel-status-reflects-it", vm.StatusText.Contains("Cancelled", StringComparison.Ordinal));

            vm.ConfirmContinueWithPendingDependencies = _ => Task.FromResult(true);   // user accepts the risk
            await vm.PushOnlyAsync();
            Check("continue-clears-pending-regardless-of-eventual-push-outcome", vm.SessionClipboardRows.Count == 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }

    static void SetAllTimbres(byte[] combiBody, int func33Bank, int number)
    {
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combiBody, t, func33Bank, number);
    }

    // Minimal fixture: one Program (A), one Combi (X) referencing it, and one Combi (Z)
    // referencing something this PCG doesn't contain at all (a true gap).
    static byte[] BuildSyntheticPcg(out byte[] progABody, out byte[] combiXBody, out byte[] combiZBody)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810;

        progABody = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG A").CopyTo(progABody, 0);

        // Program A lives in I-B, NOT I-A. Deliberate: func-33 bank 0 / number 0 is the zero
        // default every timbre of an INIT Combi already holds, so a Combi whose only reference
        // is (0, 0) satisfies CombiBody.AllTimbresAtDefault and reads as an init placeholder -
        // InitObjects then correctly reports it as having NO dependencies at all, and Program A
        // would never be pulled, tracked, or repatched. Pointing at I-B:000 makes the timbre
        // write a real, non-default reference, which is what this whole test is about.
        // ...and EVERY timbre is pointed at it, never just timbre 0: a timbre left at (0, 0) is
        // not "unset", it is a live reference to Program I-A:000, so 15 untouched timbres would
        // manufacture 15 phantom gaps on top of the one dependency under test. All defaults, or
        // none - there is no useful middle ground.
        int fbProgA = KronosBanks.ObjBankToFunc33(1, 0x01);
        combiXBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI X").CopyTo(combiXBody, 0);
        SetAllTimbres(combiXBody, fbProgA, 0);   // -> Program A

        combiZBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI Z").CopyTo(combiZBody, 0);
        SetAllTimbres(combiZBody, 5, 42);   // -> a Program this PCG does NOT contain

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

        WriteBank("MBK1", 1, programSize, 0x01, progABody);   // bank 0x01 (I-B) -> EXi; see fbProgA

        using var combis = new MemoryStream();
        combis.Write(combiXBody); combis.Write(combiZBody);
        WriteBank("CBK1", 2, combiSize, 0, combis.ToArray());

        return ms.ToArray();
    }
}
