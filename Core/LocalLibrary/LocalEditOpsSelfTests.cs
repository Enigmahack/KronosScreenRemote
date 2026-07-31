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

            // ── ROM (GM/g) references are never a dependency gap ──
            // The read-only GM/g Program banks are factory content on the instrument and are
            // deliberately never pulled (LibraryPullPlanner/ObjectTypeRegistry.EditableBanks), so a
            // timbre pointing at one used to read as a PERMANENTLY unresolvable dependency - a red
            // dot that never cleared, a session-clipboard entry no retry could clear, and a hard
            // REFUSE of the whole push from ChangesetBuilder's referential check. See
            // ObjectReferenceWalker.IsAlwaysAvailable.
            int fbGm = KronosBanks.ObjBankToFunc33(1, 0x10);          // GM
            int fbGd = KronosBanks.ObjBankToFunc33(1, 0x1A);          // g(d) - the far end of the ROM range
            // EVERY timbre is set, not just the two of interest: a Combi body always carries all 16
            // timbre references, and a zero-filled one reads as 16 references to I-A:000 - which
            // would drown out what these checks are actually about.
            var romCombiBody = new byte[7810];
            for (int t = 0; t < LibRefs.TimbreCount; t++)
                LibRefs.SetCombiTimbreRef(romCombiBody, t, t % 2 == 0 ? fbGm : fbGd, t);
            Check("rom-ref-not-scanned-as-missing", DependencyScanner.Scan(cache, LibObj.Combi, romCombiBody).ToList().Count == 0);
            Check("rom-ref-has-all-dependencies", DependencyScanner.HasAllDependencies(cache, LibObj.Combi, romCombiBody));
            Check("rom-classifier-gm", ObjectReferenceWalker.IsAlwaysAvailable(new ObjLoc(LibObj.Program, 0x10, 0)));
            Check("rom-classifier-gd", ObjectReferenceWalker.IsAlwaysAvailable(new ObjLoc(LibObj.Program, 0x1A, 127)));
            // The classifier must stay narrow: a writable USER/INT Program bank, and a Combi in any
            // bank, are still real dependencies that must resolve locally.
            Check("rom-classifier-not-user-prog", !ObjectReferenceWalker.IsAlwaysAvailable(new ObjLoc(LibObj.Program, 0x40, 0)));
            Check("rom-classifier-not-int-prog", !ObjectReferenceWalker.IsAlwaysAvailable(new ObjLoc(LibObj.Program, 0x00, 0)));
            Check("rom-classifier-not-combi", !ObjectReferenceWalker.IsAlwaysAvailable(new ObjLoc(LibObj.Combi, 0x10, 0)));
            // A body mixing a ROM ref with a genuinely-missing one still reports exactly the
            // missing one - the fix must not blanket-suppress real gaps.
            var mixedCombiBody = new byte[7810];
            for (int t = 0; t < LibRefs.TimbreCount; t++) LibRefs.SetCombiTimbreRef(mixedCombiBody, t, fbGm, 5);
            LibRefs.SetCombiTimbreRef(mixedCombiBody, 1, fbUnseen, 77);   // -> U-B:077, not present locally
            var mixedMissing = DependencyScanner.Scan(cache, LibObj.Combi, mixedCombiBody).ToList();
            Check("rom-mix-reports-only-real-gap", mixedMissing.Count == 1 &&
                mixedMissing[0].MissingRef.Equals(new ObjLoc(LibObj.Program, 0x41, 77)));

            // ── Type-root drop target: "first bank with room" (requirement 6) ──
            // Dropping on the "Programs"/"Combis" header names a type but no bank. Banks are walked
            // in plain registry order - I-A onward, then U-A onward - and for Programs the chosen
            // bank must match the incoming wire format, otherwise this would just trade "drop onto a
            // specific bank" for PlanBatchMove's wrong-format REFUSE. This fixture holds only HD-1
            // Programs (3706-byte bodies) in I-A/U-A/U-B, so an EXi drop must skip past all three to
            // an unformatted (empty) bank.
            //
            // The reported bug, exactly: BOTH I-A and U-A have room here, and the internal one must
            // win. An earlier version preferred USER banks outright, which sent a Combi dropped on
            // the header to U-A while seven internal banks still had free slots.
            Check("typeroot-combi-takes-first-internal-bank-with-room",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Combi) == 0x00);
            Check("typeroot-program-hd1-lands-in-hd1-bank",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: false) == 0x00);
            // I-A is HD-1 (slot 7), so an EXi drop skips it and settles on the first bank whose
            // format nothing can rule out - I-B, not the first empty USER bank.
            Check("typeroot-program-exi-skips-hd1-banks",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: true) is int exiBank &&
                exiBank == 0x01 && LocalEditOps.LocalProgramBankFormat(cache, exiBank) == null);
            Check("typeroot-program-unknown-format-takes-first-bank-with-room",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program) == 0x00);
            Check("typeroot-setlist-single-pseudo-bank",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.SetList) == 0);
            // The live bank-type lookup wins over the locally-inferred one when it's available.
            Check("typeroot-program-honours-live-bank-types",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: true,
                    bankTypeOf: b => b == 0x40 ? true : false) == 0x40);
            // The format filter is HARD: with every bank reporting EXi, an HD-1 group has nowhere
            // to go and must come back null. Answering "some EXi bank with room" instead is how an
            // HD-1 drop on the Programs header used to end up aimed at an EXi bank - where a
            // single-item drop hits PlanBatchMove's wrong-format REFUSE and a multi-item drop
            // escalates to the whole-bank "Change Program Bank Type" prompt (a func 0x7C that
            // ERASES that bank). The caller reports the null, naming the format.
            Check("typeroot-program-no-matching-format-refuses",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: false,
                    bankTypeOf: _ => true) == null);
            Check("typeroot-program-no-matching-format-refuses-other-way",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: true,
                    bankTypeOf: _ => false) == null);
            // A bank of UNVERIFIABLE type (no live answer AND empty locally) is a fallback, never a
            // first choice: I-B onward are unverifiable and come first in bank order, but U-E is a
            // CONFIRMED EXi match, so it wins. Taking I-B would have deferred a real format error to
            // the hardware write, since an unverifiable destination only earns PlanBatchMove's
            // advisory CHECK.
            Check("typeroot-program-prefers-known-match-over-unverifiable",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: true,
                    bankTypeOf: b => b == 0x44 ? true : (bool?)null) == 0x44);
            // ...but an unverifiable bank IS still used when nothing confirmed matches, rather
            // than refusing a drop that a fresh/unsynced library can perfectly well accept.
            Check("typeroot-program-falls-back-to-unverifiable-bank",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: true,
                    bankTypeOf: _ => null) == 0x01);

            // The reported bug, in its exact shape. I-G has no bit in func 0x61's Program Bank
            // Types bitmap, so the LIVE lookup can only ever answer null for it - but once any
            // Program lands there its format is perfectly well known locally. That's how an HD-1
            // group dropped on the Programs header came to be aimed at a bank holding EXi
            // programs: nothing but the local inference can rule it out. Seed one EXi Program into
            // U-F to reproduce "has room + no live type + locally the WRONG format", and require
            // the search to walk past it to the genuinely-blank U-G. This is the only assertion
            // that fails if the `?? LocalProgramBankFormat(...)` fallback is ever dropped.
            var exiSeedBody = new byte[ProgramFormatConverter.WireSizeExi];
            var (exiSeedOk, _, _) = LocalEditOps.PlaceObject(
                cache, new ObjLoc(LibObj.Program, 0x45, 0), LibObj.Program, 1, exiSeedBody, "exi-seed",
                divertDisplacedToClipboard: false, utcNow);
            Check("typeroot-exi-seed-placed", exiSeedOk);
            Check("typeroot-program-skips-locally-inferred-wrong-format",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: false,
                    bankTypeOf: b => b <= 0x44 ? true : (bool?)null) == 0x46);
            // A read-only GM/g bank is never a destination, however much room it appears to have.
            Check("typeroot-never-picks-readonly-bank",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program) is int anyBank &&
                !KronosBanks.IsReadOnlyProgramBank(anyBank));

            // ── The reported Combi shape, end to end ────────────────────────────────────────
            // A real Kronos: internal Combi banks filled with factory content up to some bank, then
            // a partly-used one - I-E holding one real Combi at 000 and init placeholders after it.
            // A drop on the "Combis" header must land at I-E:001, not skip seven internal banks to
            // U-A. Fill I-A entirely so the search has to WALK, not just answer with the first bank.
            var realCombi = new byte[7810];
            for (int t = 0; t < LibRefs.TimbreCount; t++) LibRefs.SetCombiTimbreRef(realCombi, t, fbDst, 5);
            var initCombi = new byte[7810];   // every timbre at the zero default -> CombiBody.IsInit
            for (int n = 0; n < 128; n++)
                LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x00, n), LibObj.Combi, 3,
                    realCombi, "fill-IA", divertDisplacedToClipboard: false, utcNow);
            for (int n = 0; n < 128; n++)
                LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x01, n), LibObj.Combi, 3,
                    n == 0 ? realCombi : initCombi, "fill-IB", divertDisplacedToClipboard: false, utcNow);
            Check("combi-init-body-reads-as-free", !cache.HasContent(LibObj.Combi, 0x01, 1));
            Check("combi-real-body-reads-as-occupied", cache.HasContent(LibObj.Combi, 0x01, 0));
            Check("typeroot-combi-walks-past-full-internal-bank",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Combi) == 0x01);
            Check("typeroot-combi-lands-after-the-real-occupant",
                LocalEditOps.FindNextFreeSlot(cache, LibObj.Combi, 0x01) == 1);

            // ── Read-only factory banks: browsable, never writable ──────────────────────────
            // GM/g Program banks are shown in Local Library so their content can be looked up, and
            // that is the whole risk: a bank the user can SEE is a bank a drop can land on. BatchPlace
            // is the single choke point every drop/paste/auto-fill goes through, so the refusal has to
            // hold there, not only in the handlers that remember to ask.
            var romDest = new ObjLoc(LibObj.Program, 0x10, 0);   // GM:000
            var (romOk, romErr, _) = LocalEditOps.PlaceObject(
                cache, romDest, LibObj.Program, 5, new byte[ProgramFormatConverter.WireSizeHd1], "rom-write",
                divertDisplacedToClipboard: false, utcNow);
            Check("readonly-bank-write-refused", !romOk);
            Check("readonly-bank-refusal-names-the-bank", romErr != null && romErr.Contains("GM"));
            Check("readonly-bank-nothing-landed", !cache.Exists(romDest.ObjType, romDest.Bank, romDest.Number));

            // The registry split itself: read-only banks are browsable but must never appear in the
            // WRITE scope, which is what every auto-fill and BackfillInitFlags iterates.
            var progDescriptor = ObjectTypeRegistry.Get(LibObj.Program);
            Check("readonly-banks-not-editable",
                !progDescriptor.EditableBanks().Any(b => progDescriptor.IsReadOnlyBank(b)));
            Check("readonly-banks-are-browsable",
                progDescriptor.ReadOnlyBanks().All(b => progDescriptor.BrowsableBanks().Contains(b)) &&
                progDescriptor.ReadOnlyBanks().SequenceEqual(Enumerable.Range(0x10, 11)));
            // Program object-dump bank 0x06 ("I-G") is not a real Program bank - it answers no digest
            // and no dump, and while it was listed it stayed a legal write destination.
            Check("program-has-six-internal-banks",
                progDescriptor.EditableBanks().Where(b => b < 0x40).SequenceEqual(Enumerable.Range(0x00, 6)));
            // Combi I-G is real and must NOT have been caught by that removal.
            Check("combi-keeps-seven-internal-banks",
                ObjectTypeRegistry.Get(LibObj.Combi).EditableBanks().Where(b => b < 0x40)
                    .SequenceEqual(Enumerable.Range(0x00, 7)));
            // ── INIT placeholders count as free space ──────────────────────────────────────
            // The Kronos protocol has no empty slot, so a synced library indexes all 128 slots of
            // every bank and an Exists-based scan calls a bank of 128 "Init Program"s FULL - the
            // reported bug ("every Combi bank is full" against a library with hundreds of free
            // init slots). U-C is untouched here, so fill it: real content at 0-1, init at 2, real
            // at 3, then init to the end. Note the INTERLEAVING - that is the shape init-awareness
            // creates and the reason an auto-fill can no longer walk startSlot+i blindly.
            var realProg = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "REAL PATCH");
            var initProg = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "Init Program");
            for (int n = 0; n < 128; n++)
            {
                bool real = n is 0 or 1 or 3;
                LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, 0x42, n), LibObj.Program, 1,
                    real ? realProg : initProg, "fill", divertDisplacedToClipboard: false, utcNow);
            }
            Check("init-slot-detected", cache.IsInitSlot(LibObj.Program, 0x42, 2));
            Check("real-slot-not-init", !cache.IsInitSlot(LibObj.Program, 0x42, 0));
            Check("init-slot-indexed-but-has-no-content",
                cache.Exists(LibObj.Program, 0x42, 2) && !cache.HasContent(LibObj.Program, 0x42, 2));
            // Every slot is occupied, so the OLD Exists-based scan called this bank full.
            Check("init-bank-not-reported-full", LocalEditOps.TryFindNextFreeSlot(cache, LibObj.Program, 0x42) == 2);
            Check("init-next-free-slot-skips-real", LocalEditOps.FindNextFreeSlot(cache, LibObj.Program, 0x42) == 2);
            // The data-loss guard: slot 3 holds a REAL patch between two init slots, so a 3-item
            // auto-fill starting at 2 must land on 2,4,5 - never on 3. A startSlot+i walk would
            // have overwritten it, which is exactly what init-awareness would otherwise introduce.
            var avail = LocalEditOps.AvailableSlotsFrom(cache, LibObj.Program, 0x42, 2, 3);
            Check("available-slots-skip-real-content", avail.SequenceEqual(new[] { 2, 4, 5 }));
            // ...and the bank-level search sees the room too (U-C is HD-1 by the content above).
            Check("init-bank-offered-to-typeroot-drop",
                LocalEditOps.FindBankWithFreeSlot(cache, LibObj.Program, incomingIsExi: false,
                    bankTypeOf: b => b == 0x42 ? false : true) == 0x42);
            // ...and real content is still real: init-awareness must not turn every occupied slot
            // into a free one, or an auto-fill would happily overwrite the user's whole library.
            Check("real-content-still-counts-as-occupied",
                cache.HasContent(LibObj.Program, 0x42, 0) && cache.HasContent(LibObj.Program, 0x42, 1) &&
                cache.HasContent(LibObj.Program, 0x42, 3));

            // ── Set Lists: emptiness is the aggregate of the slots, and needs the backfill ──
            // The fixture's Set List 0 has one filled slot (slot 2 was seeded + edited above), so
            // it is real content. A Set List whose slots are ALL blank is an empty placeholder -
            // but only the BODY can say so (no "Init Set List" naming convention exists), which is
            // why BackfillInitFlags has to read them once for a library synced by an older build.
            var blankSetList = new byte[69416];
            LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.SetList, 0, 5), LibObj.SetList, 0,
                blankSetList, "blank-sl", divertDisplacedToClipboard: false, utcNow);
            Check("blank-setlist-is-init", cache.IsInitSlot(LibObj.SetList, 0, 5));
            // The case blank-slot-names alone misses: every slot still points at the zero default
            // (Program I-A:000 = "nothing assigned"), but the object and its slots ARE named - the
            // instrument ships them named "Set List 000".., and a user can rename one without ever
            // assigning anything. Names must not make an unassigned Set List look like content.
            var namedButUnassigned = SetListBody.WriteName(new byte[69416], "MY EMPTY LIST");
            for (int s = 0; s < SetListData.SlotCount; s++)
            {
                namedButUnassigned = SetListBody.WriteSlotName(namedButUnassigned, s, $"Slot {s:D3}");
                // Written explicitly rather than left as zero bytes: an all-zero slot decodes as
                // type 0 = COMBI 000, not the PRG I-A:000 a real untouched Set List carries.
                LibRefs.SetSetListSlotRef(namedButUnassigned, s, KronosBanks.ObjBankToFunc33(1, 0x00), 0, type: 1);
            }
            LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.SetList, 0, 7), LibObj.SetList, 0,
                namedButUnassigned, "named-empty-sl", divertDisplacedToClipboard: false, utcNow);
            Check("named-but-unassigned-setlist-is-init", cache.IsInitSlot(LibObj.SetList, 0, 7));
            Check("named-but-unassigned-setlist-has-no-content", !cache.HasContent(LibObj.SetList, 0, 7));
            // ...and one real assignment (Program U-A:007 in slot 4) is enough to make it content
            // again, however many of the other 127 slots are still at the default.
            var oneAssigned = SetListBody.WriteSlotName(new byte[69416], 4, "REAL SLOT");
            LibRefs.SetSetListSlotRef(oneAssigned, 4, KronosBanks.ObjBankToFunc33(1, 0x40), 7, type: 1);
            LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.SetList, 0, 8), LibObj.SetList, 0,
                oneAssigned, "one-assigned-sl", divertDisplacedToClipboard: false, utcNow);
            Check("one-assigned-slot-setlist-is-not-init", !cache.IsInitSlot(LibObj.SetList, 0, 8));
            Check("blank-setlist-has-no-content", !cache.HasContent(LibObj.SetList, 0, 5));
            // A NAMED slot is what makes a Set List real content - SetListSlot.IsEmpty keys on the
            // slot's name, not its reference (an unused slot still carries a default prog/combi
            // pointer, so the refs can't distinguish the two). The fixture's own Set List 0 has a
            // slot REFERENCE but no slot name, so it correctly reads as an empty placeholder.
            var namedSetList = SetListBody.WriteSlotName(new byte[69416], 0, "MY SET");
            LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.SetList, 0, 6), LibObj.SetList, 0,
                namedSetList, "named-sl", divertDisplacedToClipboard: false, utcNow);
            Check("populated-setlist-still-real", cache.HasContent(LibObj.SetList, 0, 6));
            // Backfill is idempotent - everything above was written with IsInit already computed,
            // so a second pass has nothing left to fill in.
            Check("setlist-backfill-idempotent", cache.BackfillInitFlags(LibObj.SetList) == 0);

            // TryFindNextFreeSlot says "full" rather than silently answering slot 0.
            Check("try-next-free-slot-finds-gap", LocalEditOps.TryFindNextFreeSlot(cache, LibObj.Program, 0x41) == 1);
            Check("try-next-free-slot-empty-bank", LocalEditOps.TryFindNextFreeSlot(cache, LibObj.Combi, 0x46) == 0);

            // ── SessionDependencyClipboard: add/resolve ──
            var sessionClip = new SessionDependencyClipboard();
            var otherMissing = new ObjLoc(LibObj.Program, 0x41, 55);
            sessionClip.Add(new SessionDependencyEntry(otherMissing, "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null));
            sessionClip.Add(new SessionDependencyEntry(otherMissing, "timbre 1", 0, new ObjLoc(LibObj.Combi, 0x00, 0), null));   // duplicate - must not double-add
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
