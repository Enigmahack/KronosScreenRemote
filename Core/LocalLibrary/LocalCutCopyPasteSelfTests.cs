namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for the Librarian redesign's file-manager-style Cut/Copy/Paste
// (LocalLibraryPaneViewModel), which replaced the old Set as Source/Destination + Swap flow.
// Constructs the ViewModel directly against a LocalLibraryCache seeded via FakeMoveExecutor +
// LibraryPullPipeline (the same pattern CrossPanePlacementSelfTests.cs already uses).
static class LocalCutCopyPasteSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_local_cutcopypaste");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            byte[] Named(int size, string name)
            {
                var b = new byte[size];
                Encoding.ASCII.GetBytes(name).CopyTo(b, 0);
                return b;
            }

            exec.Seed(LibObj.Program, 0x00, 0, 1, Named(3706, "PROG A"));    // Copy into empty
            exec.Seed(LibObj.Program, 0x00, 1, 1, Named(3706, "PROG B"));   // Cut onto occupied (swap)
            exec.Seed(LibObj.Program, 0x00, 2, 1, Named(3706, "PROG C"));   // swap partner
            exec.Seed(LibObj.Program, 0x00, 3, 1, Named(3706, "PROG D"));   // Cut onto empty, referenced
            exec.Seed(LibObj.Program, 0x00, 4, 1, Named(3706, "PROG E"));   // multi-cut #1
            exec.Seed(LibObj.Program, 0x00, 5, 1, Named(3706, "PROG F"));   // multi-cut #2
            exec.Seed(LibObj.Program, 0x00, 6, 1, Named(3706, "PROG G"));   // multi-copy #1
            exec.Seed(LibObj.Program, 0x00, 7, 1, Named(3706, "PROG H"));   // multi-copy #2
            exec.Seed(LibObj.Program, 0x40, 1, 1, Named(3706, "WAS HERE")); // occupies a Copy destination
            exec.Seed(LibObj.SetList, 0, 0, 1, Named(69416, "MY SETLIST"));
            exec.Seed(LibObj.SetList, 0, 1, 1, Named(69416, "SECOND SETLIST")); // Set-List swap partner

            var progD = new ObjLoc(LibObj.Program, 0x00, 3);
            int fbD = KronosBanks.ObjBankToFunc33(1, progD.Bank);
            var combiBody = Named(7810, "REFERENCING COMBI");
            LibRefs.SetCombiTimbreRef(combiBody, 0, fbD, progD.Number);
            exec.Seed(LibObj.Combi, 0x00, 0, 1, combiBody);

            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);

            var pane = new LocalLibraryPaneViewModel(cache);

            // 1. Copy into an EMPTY slot: source untouched, destination gets the content.
            var progA = new ObjLoc(LibObj.Program, 0x00, 0);
            var emptySlot = new ObjLoc(LibObj.Program, 0x41, 0);
            pane.Copy(new[] { progA });
            var (copyOk, _) = pane.PasteIntoSlot(emptySlot);
            Check("copy-into-empty-ok", copyOk);
            Check("copy-into-empty-source-untouched", cache.Exists(progA.ObjType, progA.Bank, progA.Number));
            Check("copy-into-empty-dest-has-copy", cache.GetDisplayName(emptySlot.ObjType, emptySlot.Bank, emptySlot.Number) == "PROG A");

            // 2. Copy into an OCCUPIED slot: existing displacement-to-clipboard path - the
            // occupant is preserved in the persisted clipboard, not lost.
            var occupiedForCopy = new ObjLoc(LibObj.Program, 0x40, 1);
            pane.Copy(new[] { progA });
            var (copyOccOk, _) = pane.PasteIntoSlot(occupiedForCopy);
            Check("copy-into-occupied-ok", copyOccOk);
            Check("copy-into-occupied-overwrites", cache.GetDisplayName(occupiedForCopy.ObjType, occupiedForCopy.Bank, occupiedForCopy.Number) == "PROG A");
            var displacedClip = BatchLibrarian.LoadClipboardGlobal();
            Check("copy-into-occupied-displaces-to-clipboard",
                displacedClip.Entries.Any(en => en.Provenance == ClipboardProvenance.DisplacedDestination && en.Origin.Equals(occupiedForCopy)));

            // 3. Cut + paste onto an OCCUPIED slot -> true swap (unchanged from before).
            var progB = new ObjLoc(LibObj.Program, 0x00, 1);
            var progC = new ObjLoc(LibObj.Program, 0x00, 2);
            pane.Cut(new[] { progB });
            var (swapOk, _) = pane.PasteIntoSlot(progC);
            Check("cut-onto-occupied-swaps",
                swapOk && cache.GetDisplayName(progC.ObjType, progC.Bank, progC.Number) == "PROG B"
                       && cache.GetDisplayName(progB.ObjType, progB.Bank, progB.Number) == "PROG C");

            // 4. Cut + paste onto an EMPTY slot - must REFUSE, not silently leave a duplicate.
            // This app has no primitive that vacates a clean source slot (Discard only
            // reverts a pending edit to baseline) and no way to push "now empty" to hardware,
            // so a move-to-empty can never be completed correctly; Cut is swap-onto-occupied
            // only (see LocalLibraryPaneViewModel.PasteSingle/Cut's own comments). Confirms
            // the refusal leaves everything exactly as it was - source intact, no phantom
            // copy at the destination, referrer untouched.
            var emptyForD = new ObjLoc(LibObj.Program, 0x42, 0);
            pane.Cut(new[] { progD });
            var (pasteToEmptyOk, _) = pane.PasteIntoSlot(emptyForD);
            Check("cut-onto-empty-refuses", !pasteToEmptyOk);
            Check("cut-onto-empty-source-untouched", cache.Exists(progD.ObjType, progD.Bank, progD.Number));
            Check("cut-onto-empty-dest-still-empty", !cache.Exists(emptyForD.ObjType, emptyForD.Bank, emptyForD.Number));
            var combiLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
            var combiBodyNow = cache.GetCurrentBody(combiLoc.ObjType, combiLoc.Bank, combiLoc.Number);
            var (refBank, refIndex) = LibRefs.CombiTimbreRef(combiBodyNow!, 0);
            Check("cut-onto-empty-referrer-unchanged", refBank == progD.Bank && refIndex == progD.Number);
            pane.ClearClipboard();

            // 5. Multi-select Cut is refused outright (no correct N-way move exists) -
            // nothing is armed, so a follow-up Paste can't do anything either.
            var progE = new ObjLoc(LibObj.Program, 0x00, 4);
            var progF = new ObjLoc(LibObj.Program, 0x00, 5);
            pane.Cut(new[] { progE, progF });
            Check("multi-cut-refused", !pane.HasClipboard);
            Check("multi-cut-sources-untouched", cache.Exists(progE.ObjType, progE.Bank, progE.Number) && cache.Exists(progF.ObjType, progF.Bank, progF.Number));

            // 5b. A single-item Cut dropped on a BANK (not a specific slot) is refused too -
            // a bank target has no specific occupied slot to swap onto, and PasteIntoBank
            // always auto-fills into the next free (i.e. empty) slot.
            pane.Cut(new[] { progE });
            var (cutIntoBankOk, _) = pane.PasteIntoBank(LibObj.Program, 0x43);
            Check("single-cut-into-bank-refuses", !cutIntoBankOk);
            Check("single-cut-into-bank-source-untouched", cache.Exists(progE.ObjType, progE.Bank, progE.Number));
            pane.ClearClipboard();

            // 6. Multi-select batch COPY into a bank: both copied, sources UNCHANGED.
            var progG = new ObjLoc(LibObj.Program, 0x00, 6);
            var progH = new ObjLoc(LibObj.Program, 0x00, 7);
            pane.Copy(new[] { progG, progH });
            var (batchCopyOk, _) = pane.PasteIntoBank(LibObj.Program, 0x44);
            Check("batch-copy-ok", batchCopyOk);
            Check("batch-copy-sources-unchanged", cache.Exists(progG.ObjType, progG.Bank, progG.Number) && cache.Exists(progH.ObjType, progH.Bank, progH.Number));
            Check("batch-copy-both-landed", cache.GetDisplayName(LibObj.Program, 0x44, 0) == "PROG G" && cache.GetDisplayName(LibObj.Program, 0x44, 1) == "PROG H");

            // 7. Set Lists: Copy allowed, and Cut now allowed too (requirement 1) - a Set-List
            // swap is a pure body-swap (nothing references a Set List), so Cut + paste onto
            // another occupied Set List swaps the two exactly like Programs/Combis.
            var slLoc = new ObjLoc(LibObj.SetList, 0, 0);
            var slLoc2 = new ObjLoc(LibObj.SetList, 0, 1);
            pane.Copy(new[] { slLoc });
            Check("setlist-copy-allowed", pane.HasClipboard);
            pane.Cut(new[] { slLoc });
            Check("setlist-cut-allowed", pane.HasClipboard);
            var (slSwapOk, _) = pane.PasteIntoSlot(slLoc2);
            Check("setlist-cut-onto-occupied-swaps",
                slSwapOk && cache.GetDisplayName(slLoc2.ObjType, slLoc2.Bank, slLoc2.Number) == "MY SETLIST"
                         && cache.GetDisplayName(slLoc.ObjType, slLoc.Bank, slLoc.Number) == "SECOND SETLIST");

            // 8. Issue 1: DescribeReferrers reports the Combi timbre that depends on progD (so a
            // delete can warn); a Set List (nothing ever references one) reports none.
            Check("referrers-detects-combi-dependent", pane.DescribeReferrers(progD).Count >= 1);
            Check("setlist-has-no-referrers", pane.DescribeReferrers(slLoc).Count == 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }
}
