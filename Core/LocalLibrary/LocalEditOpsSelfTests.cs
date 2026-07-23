namespace KronosScreenRemote;

using System.IO;

// Off-hardware self-test for Phase 2: LocalEditOps (Rename/Move/PlaceObject/EditProperties/
// EditSetListSlot/Discard), DependencyScanner, and SessionDependencyClipboard. Async (like
// Phase 1's LocalLibrarySelfTests.SelfTestAsync) because setting up a populated cache goes
// through the Pull pipeline. App.xaml.cs awaits it via .GetAwaiter().GetResult().
static class LocalEditOpsSelfTests
{
    static string ScratchRoot => Path.Combine(Path.GetTempPath(), "kronos_selftest_local_edit_ops");

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = ScratchRoot;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();

            // Program I-A:007 <-> U-A:005 (mirrors Librarian.SelfTest's own move scenario),
            // a Combi timbre + a Set List slot both referencing I-A:007.
            var progSrcBody = new byte[3706]; progSrcBody[100] = 0xAB;   // marker outside the 24-byte name field
            var progDstBody = new byte[3706]; progDstBody[100] = 0xCD;
            exec.Seed(LibObj.Program, 0x00, 7, 5, progSrcBody);
            exec.Seed(LibObj.Program, 0x40, 5, 5, progDstBody);

            int fbSrc = KronosBanks.ObjBankToFunc33(1, 0x00);
            int fbDst = KronosBanks.ObjBankToFunc33(1, 0x40);
            var combiBody = new byte[7810];
            LibRefs.SetCombiTimbreRef(combiBody, 3, fbSrc, 7);
            exec.Seed(LibObj.Combi, 0x00, 0, 3, combiBody);

            var slBody = new byte[69416];
            LibRefs.SetSetListSlotRef(slBody, 2, fbSrc, 7, type: 1);
            exec.Seed(LibObj.SetList, 0, 0, 0, slBody);

            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var utcNow = DateTime.UtcNow;

            // ── Rename ──
            var progLoc = new ObjLoc(LibObj.Program, 0x00, 7);
            var (renOk, renErr) = LocalEditOps.Rename(cache, progLoc, "RENAMED-TEST", utcNow);
            Check("rename-ok", renOk && renErr == null);
            var renamedBody = cache.GetCurrentBody(progLoc.ObjType, progLoc.Bank, progLoc.Number);
            Check("rename-name", renamedBody != null && ProgramBody.ReadName(renamedBody) == "RENAMED-TEST");
            Check("rename-preserves-tail", renamedBody != null && renamedBody[100] == 0xAB);
            Check("rename-marks-dirty", cache.IsDirty(progLoc.ObjType, progLoc.Bank, progLoc.Number));

            // ── Discard ──
            var (discOk, _) = LocalEditOps.Discard(cache, progLoc, utcNow);
            Check("discard-ok", discOk);
            Check("discard-clears-dirty", !cache.IsDirty(progLoc.ObjType, progLoc.Bank, progLoc.Number));
            var afterDiscard = cache.GetCurrentBody(progLoc.ObjType, progLoc.Bank, progLoc.Number);
            Check("discard-exact-bytes", afterDiscard != null && afterDiscard.SequenceEqual(progSrcBody));

            // ── Move: swap Program I-A:007 <-> U-A:005; combi + set-list referrers retarget ──
            var dstLoc = new ObjLoc(LibObj.Program, 0x40, 5);
            var (moveOk, moveErr) = LocalEditOps.Move(cache, progLoc, dstLoc, utcNow);
            Check("move-ok", moveOk && moveErr == null);
            var movedToDstBody = cache.GetCurrentBody(dstLoc.ObjType, dstLoc.Bank, dstLoc.Number);
            Check("move-src-body-landed-at-dst", movedToDstBody != null && movedToDstBody[100] == 0xAB);
            var combiAfter = cache.GetCurrentBody(LibObj.Combi, 0x00, 0);
            var (t3bank, t3num) = combiAfter != null ? LibRefs.CombiTimbreRef(combiAfter, 3) : (-1, -1);
            Check("move-combi-retargeted", t3bank == fbDst && t3num == 5);
            var slAfterMove = cache.GetCurrentBody(LibObj.SetList, 0, 0);
            var (slType, slBank, slIdx) = slAfterMove != null ? LibRefs.SetListSlotRef(slAfterMove, 2) : (-1, -1, -1);
            Check("move-setlist-retargeted", slType == 1 && slBank == fbDst && slIdx == 5);
            Check("move-marks-combi-dirty", cache.IsDirty(LibObj.Combi, 0x00, 0));
            Check("move-marks-setlist-dirty", cache.IsDirty(LibObj.SetList, 0, 0));

            // ── EditProperties: Program category, preserves name ──
            var (catOk, catErr) = LocalEditOps.EditProperties(cache, dstLoc, name: null, category: 4, subCategory: 2, utcNow);
            Check("category-ok", catOk && catErr == null);
            var withCat = cache.GetCurrentBody(dstLoc.ObjType, dstLoc.Bank, dstLoc.Number);
            var (readCat, readSub) = withCat != null ? ProgramBody.ReadCategory(withCat) : (-1, -1);
            Check("category-read-back", readCat == 4 && readSub == 2);
            Check("category-preserves-name", withCat != null && movedToDstBody != null &&
                ProgramBody.ReadName(withCat) == ProgramBody.ReadName(movedToDstBody));

            // ── EditSetListSlot: color + comments, preserves the just-retargeted ref ──
            var slLoc = new ObjLoc(LibObj.SetList, 0, 0);
            var (slotOk, slotErr) = LocalEditOps.EditSetListSlot(cache, slLoc, slot: 2, name: null, color: 9, comments: "test comment", utcNow);
            Check("slot-edit-ok", slotOk && slotErr == null);
            var slAfterEdit = cache.GetCurrentBody(slLoc.ObjType, slLoc.Bank, slLoc.Number);
            var decoded = slAfterEdit != null ? SetListBody.FromRawBody(0, slAfterEdit) : null;
            Check("slot-color-written", decoded != null && decoded.Slots[2].Color == 9);
            Check("slot-comments-written", decoded != null && decoded.Slots[2].Comments == "test comment");
            Check("slot-edit-preserves-refs", decoded != null &&
                decoded.Slots[2].Type == 1 && decoded.Slots[2].Bank == fbDst && decoded.Slots[2].Index == 5);

            // ── PlaceObject: sequential placement into a fresh program bank ──
            var newProgBody1 = new byte[3706]; newProgBody1[0] = 0x01;
            var (place1Ok, place1Err, _) = LocalEditOps.PlaceObject(
                cache, new ObjLoc(LibObj.Program, 0x41, 0), LibObj.Program, 1, newProgBody1, "seed1",
                divertDisplacedToClipboard: true, utcNow);
            Check("place1-ok", place1Ok && place1Err == null);
            Check("place1-landed", cache.GetCurrentBody(LibObj.Program, 0x41, 0) is { } p1 && p1[0] == 0x01);
            Check("place1-dirty", cache.IsDirty(LibObj.Program, 0x41, 0));

            // ── DependencyScanner: a fresh combi referencing a Program not yet local ──
            int fbUnseen = KronosBanks.ObjBankToFunc33(1, 0x41);
            var depCombiBody = new byte[7810];
            LibRefs.SetCombiTimbreRef(depCombiBody, 0, fbUnseen, 99);   // -> Program U-B:099, not present locally
            var missingLoc = new ObjLoc(LibObj.Program, 0x41, 99);
            var missingBefore = DependencyScanner.Scan(cache, LibObj.Combi, depCombiBody).ToList();
            Check("dependency-flags-missing", missingBefore.Any(m => m.MissingRef.Equals(missingLoc)));

            var newProg99 = new byte[3706];
            var (place99Ok, _, _) = LocalEditOps.PlaceObject(cache, missingLoc, LibObj.Program, 1, newProg99, "seed99", true, utcNow);
            Check("place99-ok", place99Ok);
            var missingAfter = DependencyScanner.Scan(cache, LibObj.Combi, depCombiBody).ToList();
            Check("dependency-clears-once-placed", !missingAfter.Any(m => m.MissingRef.Equals(missingLoc)));

            // ── SessionDependencyClipboard: add/resolve ──
            var sessionClip = new SessionDependencyClipboard();
            var otherMissing = new ObjLoc(LibObj.Program, 0x41, 55);
            sessionClip.Add(new SessionDependencyEntry(otherMissing, "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null));
            sessionClip.Add(new SessionDependencyEntry(otherMissing, "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null));   // duplicate — must not double-add
            Check("session-clipboard-add-dedup", sessionClip.Pending.Count == 1);
            sessionClip.Resolve(otherMissing);
            Check("session-clipboard-resolve", sessionClip.Pending.Count == 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return fails;
    }
}
