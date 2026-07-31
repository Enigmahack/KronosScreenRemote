namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware self-test for the Merge Window's "Auto-Fill" button
// (LibrarianShellViewModel.AutoFillFromMergeAsync): place EVERYTHING staged into Local Library's next
// free slots in one gesture, instead of one drag per type per bank.
//
// Three properties carry the feature, and each is the thing that would silently rot:
//
//   1. DEPENDENCY ORDER. Programs are placed before Combis before Set Lists, so each referrer is
//      placed only once its dependencies already live somewhere local and
//      MergeCache.ResolveReferencesForPlacement can repoint it at where they ACTUALLY landed.
//      The fixture deliberately pre-occupies each natural destination so every dependency lands
//      at a DIFFERENT address than the PCG encoded - a repoint that no-ops to the original
//      address would prove nothing.
//
//   2. PROGRAM FORMAT PARTITIONING. A mixed EXi+HD-1 staged set makes MergeGroupIsExi answer
//      null, and a null format switches FindBankWithFreeSlot's format filter OFF - so it returns
//      the first bank with room whatever its type, and BatchPlace refuses the placement outright
//      (Move.WrongFormatForBank), aborting the whole Program pass over a bank the user never
//      chose. Auto-Fill splits Programs by wire format and places each partition into a bank of
//      its own format, so that refusal is unreachable.
//
//   3. BANK OVERFLOW. More staged items than the first eligible bank can hold must spill into the
//      next bank with room, not stop at the first bank and not be lost.
//
// Everything here is LOCAL/staged - Auto-Fill never touches the instrument, so no push is
// exercised and none should be.
static class MergeAutoFillSelfTests
{
    // Never the empty host: LibrarianShellViewModel seeds its Program bank types from a REAL,
    // global, host-keyed cache next to the exe, so a shared key lets one self-test's persisted
    // answer decide another's placements. See CrossPanePlacementSelfTests' own comment.
    const string AutoFillHost = "selftest-mergeautofill-host";

    const int CombiSize = 7810, SetListSize = 700;

    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // The bank-types cache is a real file next to the exe (see AutoFillHost) - snapshot and
        // restore it verbatim so this test leaves nothing behind for the next one to inherit.
        string bankTypesCachePath = Path.Combine(Storage.DataDir, "program_bank_types_cache.json");
        string? bankTypesCacheBackup = File.Exists(bankTypesCachePath) ? File.ReadAllText(bankTypesCachePath) : null;

        // ── Full chain: Set List -> Combi -> Program, plus a stray Program of the OTHER format ──
        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_autofill");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // nothing seeded - empty local library

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), AutoFillHost);

            // "Hardware" bank types: I-B is EXi, every other bank in the bitmap is HD-1. That
            // makes the two Program partitions resolve to two DIFFERENT banks, which is the whole
            // point of property 2 - without a live answer every bank reads as unverifiable and
            // both partitions would land in the same first bank, proving nothing.
            var bits = new bool[21];
            bits[KronosBanks.ProgramBankTypeBitIndex(0x01)!.Value] = true;    // I-B -> EXi
            exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);
            await vm.WarmProgramBankTypesForTestingAsync();

            var pcgBuffer = BuildSyntheticPcg(out var exiProgBody, out var hd1ProgBody, out var combiBody, out var setListBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));

            // Pre-occupy each natural destination with REAL content, so every auto-filled object
            // is forced one slot along and a repoint has something to prove. A merely NAMED Combi
            // still has all 16 timbres at the zero default, which is the defining shape of an INIT
            // placeholder (CombiBody.AllTimbresAtDefault) - and init slots count as FREE - so a
            // slot meant to read as occupied needs a genuine non-default reference.
            var seedCombi = new byte[CombiSize];
            Encoding.ASCII.GetBytes("SEED COMBI").CopyTo(seedCombi, 0);
            LibRefs.SetCombiTimbreRef(seedCombi, 0, KronosBanks.ObjBankToFunc33(1, 0x40), 11);
            var seedExiProg = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeExi], "SEED EXI");
            var seedHd1Prog = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeHd1], "SEED HD1");
            var seedSetList = SetListBody.WriteSlotName(SetListBody.WriteName(new byte[SetListSize], "SEED SETLIST"), 0, "SEED SLOT");

            var (s1, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, 0x01, 0), LibObj.Program, 1, seedExiProg, "seedExi", true, DateTime.UtcNow);
            var (s2, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, 0x00, 0), LibObj.Program, 1, seedHd1Prog, "seedHd1", true, DateTime.UtcNow);
            var (s3, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x00, 0), LibObj.Combi, 1, seedCombi, "seedCombi", true, DateTime.UtcNow);
            var (s4, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.SetList, 0, 0), LibObj.SetList, 1, seedSetList, "seedSetList", true, DateTime.UtcNow);
            Check("seeds-placed", s1 && s2 && s3 && s4);

            // Pulling the Set List is transitive, so the Combi and the EXi Program come along;
            // the HD-1 Program is unrelated to that chain and is staged on its own, which is what
            // makes the staged Program set mixed-format.
            vm.PullIntoMerge(new ObjLoc(LibObj.SetList, 0, 0));
            vm.PullIntoMerge(new ObjLoc(LibObj.Program, 0x40, 0));

            // Hashes are over the WIRE body, which is what the Merge Window stores - only for EXi
            // is that byte-identical to the 4960-byte .pcg slot; an HD-1 slot converts down to
            // 3706 (ProgramFormatConverter), so hashing the raw fixture bytes would never match.
            var hd1PcgEntry = vm.PcgPane.Get(new ObjLoc(LibObj.Program, 0x40, 0));
            var hd1WireBody = hd1PcgEntry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(LibObj.Program, hd1PcgEntry);
            Check("hd1-wire-body-available", hd1WireBody != null);
            string exiHash = LocalObjectStore.ComputeHash(exiProgBody);
            string hd1Hash = hd1WireBody == null ? "" : LocalObjectStore.ComputeHash(hd1WireBody);
            string combiHash = LocalObjectStore.ComputeHash(combiBody);
            string setListHash = LocalObjectStore.ComputeHash(setListBody);
            Check("all-four-staged", vm.MergePane.TryGet(exiHash) != null && vm.MergePane.TryGet(hd1Hash) != null &&
                vm.MergePane.TryGet(combiHash) != null && vm.MergePane.TryGet(setListHash) != null);

            var (ok, message) = await vm.AutoFillFromMergeAsync();
            Check("autofill-ok", ok);
            Check("autofill-message-mentions-commit", message.Contains("Commit Changes", StringComparison.Ordinal));
            Check("nothing-left-staged", vm.MergePane.Entries.Count == 0);

            // Property 2 - each Program partition landed in a bank of its OWN format, one slot
            // past the seed. Landing in the same bank would mean the format filter was off.
            Check("exi-prog-in-exi-bank", cache.GetDisplayName(LibObj.Program, 0x01, 1) == "AF EXI PROG");
            Check("hd1-prog-in-hd1-bank", cache.GetDisplayName(LibObj.Program, 0x00, 1) == "AF HD1 PROG");
            Check("exi-prog-kept-exi-format",
                cache.GetCurrentBody(LibObj.Program, 0x01, 1)?.Length == ProgramFormatConverter.WireSizeExi);
            Check("hd1-prog-kept-hd1-format",
                cache.GetCurrentBody(LibObj.Program, 0x00, 1)?.Length == ProgramFormatConverter.WireSizeHd1);
            Check("exi-seed-not-overwritten", cache.GetDisplayName(LibObj.Program, 0x01, 0) == "SEED EXI");
            Check("hd1-seed-not-overwritten", cache.GetDisplayName(LibObj.Program, 0x00, 0) == "SEED HD1");

            // Property 1 - the Combi landed at Combi I-A:001 and its timbre now points at where
            // the EXi Program ACTUALLY went (I-B:001), not at the I-B:000 the PCG encoded.
            Check("combi-placed", cache.GetDisplayName(LibObj.Combi, 0x00, 1) == "AF COMBI");
            var placedCombi = cache.GetCurrentBody(LibObj.Combi, 0x00, 1);
            Check("combi-repointed-at-actual-program-destination", placedCombi != null &&
                LibRefs.CombiTimbreRef(placedCombi, 0) == (KronosBanks.ObjBankToFunc33(1, 0x01), 1));

            // ...and the Set List, placed last, points at where the Combi actually went.
            Check("setlist-placed", cache.GetDisplayName(LibObj.SetList, 0, 1) == "AF SETLIST");
            var placedSetList = cache.GetCurrentBody(LibObj.SetList, 0, 1);
            Check("setlist-repointed-at-actual-combi-destination", placedSetList != null &&
                LibRefs.SetListSlotRef(placedSetList, 0) == (0, KronosBanks.ObjBankToFunc33(0, 0x00), 1));

            // Auto-Fill only STAGES - a local edit is pending, and nothing was sent to the Kronos.
            Check("autofill-is-local-only", !exec.CallLog.Contains("Write"));
            Check("placements-are-dirty-pending-commit", cache.IsDirty(LibObj.Combi, 0x00, 1));

            // One Ctrl+Z undoes the whole sweep: PlaceMergeGroupSequentially's own per-bank undo
            // scopes are NESTED inside AutoFillFromMergeAsync's, and LibrarianUndo joins nested Begins
            // into the outer step rather than splitting the gesture into one entry per bank.
            Check("autofill-is-one-undo-step", vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Check("undo-restored-everything-to-merge", vm.MergePane.Entries.Count == 4);
            Check("undo-cleared-combi-placement", cache.GetDisplayName(LibObj.Combi, 0x00, 1) != "AF COMBI");
            Check("undo-left-seeds-alone", cache.GetDisplayName(LibObj.Combi, 0x00, 0) == "SEED COMBI");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (bankTypesCacheBackup != null) File.WriteAllText(bankTypesCachePath, bankTypesCacheBackup);
            else if (File.Exists(bankTypesCachePath)) File.Delete(bankTypesCachePath);
        }

        fails.AddRange(await OverflowSelfTestAsync());
        return fails;
    }

    // ── Property 3: a full first bank spills into the next one with room ────────────────────
    // Separate scratch root and its own tiny fixture: the chain test above deliberately leaves
    // every bank nearly empty, so it can't say anything about what happens when one fills up.
    static async Task<List<string>> OverflowSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_autofill_overflow");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), AutoFillHost);

            var pcgBuffer = BuildTwoCombiPcg(out var combiPBody, out var combiQBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("overflow-pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));

            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 0));
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, 1));
            Check("overflow-both-staged",
                vm.MergePane.TryGet(LocalObjectStore.ComputeHash(combiPBody)) != null &&
                vm.MergePane.TryGet(LocalObjectStore.ComputeHash(combiQBody)) != null);

            // Fill Combi I-A completely, except its LAST slot - so exactly one of the two staged
            // Combis fits there and the other has to find the next bank. Real content in every
            // filled slot (a non-default timbre reference), since an init placeholder reads as
            // free space, not as occupied.
            int slotCount = ObjectTypeRegistry.Get(LibObj.Combi).SlotCount;
            int refBank = KronosBanks.ObjBankToFunc33(1, 0x40);
            bool allSeeded = true;
            for (int n = 0; n < slotCount - 1; n++)
            {
                var filler = new byte[CombiSize];
                Encoding.ASCII.GetBytes($"FILL {n:D3}").CopyTo(filler, 0);
                LibRefs.SetCombiTimbreRef(filler, 0, refBank, (n % 100) + 1);
                var (fillOk, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x00, n), LibObj.Combi, 1, filler, $"fill{n}", true, DateTime.UtcNow);
                if (!fillOk) allSeeded = false;
            }
            Check("overflow-bank-filled", allSeeded);

            var (ok, _) = await vm.AutoFillFromMergeAsync();
            Check("overflow-autofill-ok", ok);
            Check("overflow-nothing-left-staged", vm.MergePane.Entries.Count == 0);
            Check("overflow-first-fits-last-slot-of-full-bank",
                cache.GetDisplayName(LibObj.Combi, 0x00, slotCount - 1) == "COMBI P");
            Check("overflow-second-spilled-to-next-bank",
                cache.GetDisplayName(LibObj.Combi, 0x01, 0) == "COMBI Q");
            Check("overflow-filler-untouched", cache.GetDisplayName(LibObj.Combi, 0x00, 0) == "FILL 000");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }

    // Fixture: Program (EXi) <- Combi <- Set List, a straight three-level chain, plus one
    // unrelated HD-1 Program so the staged Program set is mixed-format.
    static byte[] BuildSyntheticPcg(out byte[] exiProgBody, out byte[] hd1ProgBody, out byte[] combiBody, out byte[] setListBody)
    {
        // Every .pcg Program slot is 4960 bytes regardless of tag (MBK1/EXi or PBK1/HD-1) - see
        // ProgramFormatConverter's class comment.
        const int programSize = ProgramFormatConverter.PcgSlotSize;

        // Locals, not the out params, because the bank-writing lambda below can't capture an
        // `out` parameter (CS1628) - they're assigned through at the end.
        var exiProg = new byte[programSize];
        Encoding.ASCII.GetBytes("AF EXI PROG").CopyTo(exiProg, 0);
        var hd1Prog = new byte[programSize];
        Encoding.ASCII.GetBytes("AF HD1 PROG").CopyTo(hd1Prog, 0);

        // The EXi Program sits in I-B, NOT I-A. Deliberate: func-33 bank 0 / number 0 is the zero
        // default every timbre of an INIT Combi already holds, so a Combi whose only reference is
        // (0, 0) satisfies CombiBody.AllTimbresAtDefault, reads as an init placeholder, and
        // InitObjects correctly reports it as having NO dependencies at all - the chain this
        // fixture exists to exercise would silently collapse to two levels.
        // ...and ALL 16 timbres point at it, never just timbre 0: a timbre left at (0, 0) is not
        // "unset", it is a live reference to Program I-A:000, which this PCG doesn't contain, so
        // 15 untouched timbres would stage 15 phantom pending dependencies. All defaults (= an
        // init placeholder with no dependencies), or none - there is no useful middle ground.
        int fbExiProg = KronosBanks.ObjBankToFunc33(1, 0x01);
        var combi = new byte[CombiSize];
        Encoding.ASCII.GetBytes("AF COMBI").CopyTo(combi, 0);
        SetAllTimbres(combi, fbExiProg, 0);   // -> the EXi Program at I-B:000

        var sl = new byte[SetListSize];
        Encoding.ASCII.GetBytes("AF SETLIST").CopyTo(sl, 0);
        sl = SetListBody.WriteSlotName(sl, 0, "AF SLOT ONE");   // non-blank name -> not IsEmpty
        LibRefs.SetSetListSlotRef(sl, 0, KronosBanks.ObjBankToFunc33(0, 0x00), 0, type: 0);   // -> the Combi at I-A:000

        exiProgBody = exiProg;
        hd1ProgBody = hd1Prog;
        combiBody = combi;
        setListBody = sl;

        return BuildPcg(bank =>
        {
            bank("MBK1", 1, programSize, 0x01, exiProg);          // bank 0x01 (I-B) -> EXi
            bank("PBK1", 1, programSize, 0x20000, hd1Prog);       // bank 0x40 (U-A) -> HD-1
            bank("CBK1", 1, CombiSize, 0, combi);
            bank("SBK1", 1, SetListSize, 0, sl);
        });
    }

    // Fixture: two standalone Combis, no dependencies - just two things needing two slots.
    static byte[] BuildTwoCombiPcg(out byte[] combiPBody, out byte[] combiQBody)
    {
        // Non-default timbre references so neither reads as an INIT placeholder (see
        // BuildSyntheticPcg) - an init Combi would be staged, but the assertions below are about
        // real content occupying real slots.
        int refBank = KronosBanks.ObjBankToFunc33(1, 0x40);
        combiPBody = new byte[CombiSize];
        Encoding.ASCII.GetBytes("COMBI P").CopyTo(combiPBody, 0);
        SetAllTimbres(combiPBody, refBank, 21);
        combiQBody = new byte[CombiSize];
        Encoding.ASCII.GetBytes("COMBI Q").CopyTo(combiQBody, 0);
        SetAllTimbres(combiQBody, refBank, 22);

        using var combis = new MemoryStream();
        combis.Write(combiPBody); combis.Write(combiQBody);
        var record = combis.ToArray();
        return BuildPcg(bank => bank("CBK1", 2, CombiSize, 0, record));
    }

    // Points all 16 timbres of a Combi at one Program - see BuildSyntheticPcg for why a fixture
    // Combi must never be left with a mix of real and defaulted timbres.
    static void SetAllTimbres(byte[] combiBody, int func33Bank, int number)
    {
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combiBody, t, func33Bank, number);
    }

    // The minimal .pcg container both fixtures above write their bank chunks into.
    static byte[] BuildPcg(Action<Action<string, int, int, int, byte[]>> writeBanks)
    {
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

        writeBanks(WriteBank);
        return ms.ToArray();
    }
}
