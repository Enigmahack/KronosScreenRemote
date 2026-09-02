namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware self-test for the Merge Window's "Auto-Fill" button
// (LibrarianShellViewModel.AutoFillFromMergeAsync): place EVERYTHING staged into Keyboard Library's next
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
//   4. SECOND-RUN ORDERING + DUPLICATION POLICY. A re-copied PCG auto-filled again must land in
//      the same source order as the first run (the recycled-Dictionary-slot regression), and the
//      per-type preserve-duplication toggles decide whether the second run writes fresh copies
//      or reuses the first run's. See the three scenario tests after OverflowSelfTestAsync.
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
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // nothing seeded - empty keyboard library

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
            // Pins that the message still POINTS somewhere real - it named "Commit Changes" until
            // that button was folded into the Sync Library split button's Push Only mode.
            Check("autofill-message-points-at-sync", message.Contains("Sync Library", StringComparison.Ordinal));
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
        fails.AddRange(await SecondRunOrderingSelfTestAsync());
        fails.AddRange(await CombiScanAndReuseSelfTestAsync());
        fails.AddRange(await PreserveDuplicateProgramsSelfTestAsync());
        fails.AddRange(await DrumWaveSelfTestAsync());
        fails.AddRange(await DedupWithoutFreeSlotSelfTestAsync());
        return fails;
    }

    // ── Real bug: AutoFillFromMergeAsync used to check for a free bank BEFORE ever attempting
    // a dedup pass, so once every editable bank of a type was completely full, a staged item
    // that was actually a pure duplicate of existing local content got stranded in the Merge
    // Window forever - it needed no new slot at all, but never got the chance to prove that.
    // Dragging it out by hand deduped fine (PlaceFromMerge checks unconditionally); Auto-Fill
    // just never got there. Fixed by dedup-ing every partition up front, before the free-bank
    // search even starts (LibrarianShellViewModel.DedupMergeGroup).
    static async Task<List<string>> DedupWithoutFreeSlotSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_autofill_dedup_full");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-mergeautofill-dedupfull-host");

            // Fill EVERY Drum Kit slot - the smallest editable slot space of any registry type
            // (Int 40 + 14 User banks x 16 = 264) - so FindBankWithFreeSlot has nowhere left to
            // return for this type.
            var descriptor = ObjectTypeRegistry.Get(LibObj.DrumKit);
            var fillPlacements = new List<BatchPlacement>();
            int n = 0;
            foreach (var bank in descriptor.EditableBanks())
                for (int slot = 0; slot < descriptor.SlotCount(bank); slot++)
                {
                    var filler = DrumKitBody.WriteName(new byte[38424], $"FILL{n}");
                    fillPlacements.Add(new BatchPlacement(null, new ObjLoc(LibObj.DrumKit, bank, slot),
                        new ObjectDump(LibObj.DrumKit, bank, slot, 3, filler), "filler"));
                    n++;
                }
            var (fillOk, _, _) = LocalEditOps.BatchPlace(cache, LibObj.DrumKit, fillPlacements, divertDisplacedToClipboard: true, null, DateTime.UtcNow);
            Check("dedupfull-fill-ok", fillOk);

            // Staged from a PCG: byte-identical to Int:000's filler ("FILL0") - a pure duplicate
            // that needs no new slot at all.
            var dupeBody = DrumKitBody.WriteName(new byte[38424], "FILL0");
            var pcgBuffer = BuildOneDrumKitPcg(dupeBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("dedupfull-pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));
            vm.PullIntoMerge(new ObjLoc(LibObj.DrumKit, 0, 0));
            string dupeHash = LocalObjectStore.ComputeHash(dupeBody);
            Check("dedupfull-staged", vm.MergePane.TryGet(dupeHash) != null);

            var (ok, _) = await vm.AutoFillFromMergeAsync();
            Check("dedupfull-autofill-ok", ok);
            Check("dedupfull-deduped-not-stranded", vm.MergePane.TryGet(dupeHash) == null);
            Check("dedupfull-nothing-left-staged", vm.MergePane.Entries.Count == 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return fails;
    }

    static byte[] BuildOneDrumKitPcg(byte[] drumKitBody)
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
        WriteBank("DBK1", 1, drumKitBody.Length, 0, drumKitBody);   // Int:000
        return ms.ToArray();
    }

    // ── Real bug: AutoFillFromMergeAsync's objType loop was hardcoded to Program/Combi/Set
    // List, so a staged Drum Kit or Wave Sequence (or a Program pulled in only for one) was
    // never iterated at all - "Placed 0 items" however much was actually staged. Also exercises
    // property 1 (dependency order + repoint) for the new Program -> Wave Sequence edge: Wave
    // Sequences/Drum Kits go first (nothing ever references THEM), same reasoning as Programs
    // going before Combis.
    static async Task<List<string>> DrumWaveSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_autofill_drumwave");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-mergeautofill-drumwave-host");

            var pcgBuffer = BuildProgramWithWaveSeqPcg(out var progBody, out var waveBody);
            var file = PcgFile.Open(pcgBuffer);
            Check("dw-pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));

            // Pre-occupy the Wave Sequence's natural destination (Int:000, same as the PCG
            // encodes) with unrelated real content, so the auto-filled copy is forced to Int:001
            // and a repoint has something to prove - same technique as the main fixture above.
            var seedWave = WaveSequenceBody.WriteName(new byte[2216], "SEED WAVE");
            var (seeded, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.WaveSequence, 0, 0), LibObj.WaveSequence, 1, seedWave, "seedWave", true, DateTime.UtcNow);
            Check("dw-seed-placed", seeded);

            vm.PullIntoMerge(new ObjLoc(LibObj.Program, 0x01, 0));   // transitively pulls the Wave Sequence
            string progHash = LocalObjectStore.ComputeHash(progBody);
            string waveHash = LocalObjectStore.ComputeHash(waveBody);
            Check("dw-both-staged", vm.MergePane.TryGet(progHash) != null && vm.MergePane.TryGet(waveHash) != null);

            var (ok, message) = await vm.AutoFillFromMergeAsync();
            Check("dw-autofill-ok", ok);
            Check("dw-nothing-left-staged", vm.MergePane.Entries.Count == 0);

            Check("dw-wave-landed-past-seed", cache.GetDisplayName(LibObj.WaveSequence, 0, 1) == "AF WAVE");
            Check("dw-wave-seed-not-overwritten", cache.GetDisplayName(LibObj.WaveSequence, 0, 0) == "SEED WAVE");

            // The Program's own OSC1 Zone1 reference now points at where the Wave Sequence
            // ACTUALLY landed (Int:001), not the Int:000 the PCG encoded - proves dependency
            // order (Wave Sequence before Program) and MergeCache.ResolveReferencesForPlacement
            // both cover this new reference kind, not just Combi timbre/Set List slot refs.
            var placedProg = cache.GetCurrentBody(LibObj.Program, 0x00, 0);
            Check("dw-program-placed", cache.GetDisplayName(LibObj.Program, 0x00, 0) == "AF PROG WS");
            Check("dw-program-repointed-at-actual-wave-destination",
                placedProg != null && ObjectReferenceWalker.Walk(LibObj.Program, placedProg)
                    .Any(r => r.Ref == new ObjLoc(LibObj.WaveSequence, 0, 1)));

            Check("dw-autofill-is-one-undo-step", vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);
            Check("dw-undo-restored-everything-to-merge", vm.MergePane.Entries.Count == 2);
            Check("dw-undo-left-seed-alone", cache.GetDisplayName(LibObj.WaveSequence, 0, 0) == "SEED WAVE");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return fails;
    }

    // Program "AF PROG WS" (HD-1, I-B:000) with OSC1 Zone1 pointing at Wave Sequence linear
    // index 0 (Int:000) -> "AF WAVE". progBody comes back already truncated to the wire size
    // (PBK1 = HD-1, see PcgObjectExtractor) - the bytes MergeCache actually hashes/stages.
    static byte[] BuildProgramWithWaveSeqPcg(out byte[] progBody, out byte[] waveBody)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, waveSize = 2216;

        var progOnDisk = new byte[programSize];
        Encoding.ASCII.GetBytes("AF PROG WS").CopyTo(progOnDisk, 0);
        progOnDisk[2774] = 2;   // OSC1 Zone1 MS Type = Wave Sequence
        LibRefs.SetProgramZoneNumber(progOnDisk, 0, 0, 0);   // linear 0 -> Int:000
        progBody = progOnDisk[..ProgramFormatConverter.WireSizeHd1];

        waveBody = new byte[waveSize];
        Encoding.ASCII.GetBytes("AF WAVE").CopyTo(waveBody, 0);

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

        WriteBank("PBK1", 1, programSize, 0x01, progOnDisk);   // I-B:000
        WriteBank("WBK1", 1, waveSize, 0, waveBody);           // Int:000

        return ms.ToArray();
    }

    // ── Second Auto-Fill after re-copying the same PCG: order and duplication policy ─────
    // Regression test for the user-reported bug: copy a PCG into the Merge Window, Auto-Fill,
    // copy the SAME PCG in again, Auto-Fill - the second run landed BACKWARDS. Root cause:
    // AutoFillFromMergeAsync walked MergeCache's raw Dictionary enumeration; the first sweep's
    // CommitPlacement removals freed backing-array slots that the second pull's inserts then
    // recycled LIFO, scrambling (at bank size, exactly reversing) the placement walk. The sweep
    // now walks MergePaneViewModel.EntriesInDisplayOrder. Same fixture also pins the DEFAULT
    // duplication policy: Programs are reused (deduped) on the second run, Combis are copied
    // as-is (LibrarianShellViewModel.MergePreserveDuplicatePrograms/Combis).
    static async Task<List<string>> SecondRunOrderingSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string bankTypesCachePath = Path.Combine(Storage.DataDir, "program_bank_types_cache.json");
        string? bankTypesCacheBackup = File.Exists(bankTypesCachePath) ? File.ReadAllText(bankTypesCachePath) : null;
        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_autofill_rerun");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var (vm, cache) = await NewChainScenarioAsync(root, fails);

            PullChainIntoMerge(vm);
            Check("rerun-all-staged", vm.MergePane.Entries.Count == ChainCount * 2);
            var (ok1, _) = await vm.AutoFillFromMergeAsync();
            Check("rerun-first-ok", ok1);
            Check("rerun-first-nothing-staged", vm.MergePane.Entries.Count == 0);
            CheckChainOrder(fails, "rerun-first", cache, 1);
            // The first Combi must point at where its Program ACTUALLY went (I-B:001), not at
            // the I-B:000 the PCG encoded (a seed sits there).
            var combi1 = cache.GetCurrentBody(LibObj.Combi, 0x00, 1);
            Check("rerun-first-repointed", combi1 != null &&
                LibRefs.CombiTimbreRef(combi1, 0) == (KronosBanks.ObjBankToFunc33(1, 0x01), 1));

            // Re-copy the same PCG into the Merge Window and Auto-Fill again - the bug's repro.
            PullChainIntoMerge(vm);
            Check("rerun-restaged", vm.MergePane.Entries.Count == ChainCount * 2);
            var (ok2, _) = await vm.AutoFillFromMergeAsync();
            Check("rerun-second-ok", ok2);
            Check("rerun-second-nothing-staged", vm.MergePane.Entries.Count == 0);

            // Programs: duplication NOT preserved by default - all three were recognized as
            // already local and reused, so the next free Program slot is still free.
            Check("rerun-programs-deduped",
                cache.GetDisplayName(LibObj.Program, 0x01, ChainCount + 1) == "");
            // Combis: duplication preserved by default - fresh copies continue right after the
            // first run's, in the SAME source order (raw enumeration placed them scrambled).
            CheckChainOrder(fails, "rerun-second", cache, ChainCount + 1, includePrograms: false);
            var combiSecondRun = cache.GetCurrentBody(LibObj.Combi, 0x00, ChainCount + 1);
            Check("rerun-second-combi-points-at-reused-programs", combiSecondRun != null &&
                LibRefs.CombiTimbreRef(combiSecondRun, 0) == (KronosBanks.ObjBankToFunc33(1, 0x01), 1));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (bankTypesCacheBackup != null) File.WriteAllText(bankTypesCachePath, bankTypesCacheBackup);
            else if (File.Exists(bankTypesCachePath)) File.Delete(bankTypesCachePath);
        }
        return fails;
    }

    // ── Combis with "preserve duplication" OFF: scan duplicates and reuse ────────────────
    // Same re-copy scenario, opposite Combi policy: the second Auto-Fill must write NOTHING -
    // each re-staged Combi's body, once its timbres are repointed at where its Program already
    // lives locally, is byte-identical to the first run's placed copy, so the merge entry is
    // repointed at that copy instead of consuming a fresh slot (FindExistingLocalCopy's
    // resolved-body comparison - a raw-hash comparison could never see this match).
    static async Task<List<string>> CombiScanAndReuseSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string bankTypesCachePath = Path.Combine(Storage.DataDir, "program_bank_types_cache.json");
        string? bankTypesCacheBackup = File.Exists(bankTypesCachePath) ? File.ReadAllText(bankTypesCachePath) : null;
        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_combi_reuse");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var (vm, cache) = await NewChainScenarioAsync(root, fails);
            vm.MergePreserveDuplicateCombis = false;   // the new "scan duplicates and reuse" option

            PullChainIntoMerge(vm);
            var (ok1, _) = await vm.AutoFillFromMergeAsync();
            Check("reuse-first-ok", ok1);
            CheckChainOrder(fails, "reuse-first", cache, 1);

            PullChainIntoMerge(vm);
            var (ok2, _) = await vm.AutoFillFromMergeAsync();
            Check("reuse-second-ok", ok2);
            Check("reuse-second-nothing-staged", vm.MergePane.Entries.Count == 0);
            Check("reuse-no-second-combi-copies",
                cache.GetDisplayName(LibObj.Combi, 0x00, ChainCount + 1) == "");
            Check("reuse-no-second-program-copies",
                cache.GetDisplayName(LibObj.Program, 0x01, ChainCount + 1) == "");
            Check("reuse-first-copies-intact",
                cache.GetDisplayName(LibObj.Combi, 0x00, 1) == "ORD COMBI 0" &&
                cache.GetDisplayName(LibObj.Combi, 0x00, 2) == "ORD COMBI 1" &&
                cache.GetDisplayName(LibObj.Combi, 0x00, 3) == "ORD COMBI 2");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (bankTypesCacheBackup != null) File.WriteAllText(bankTypesCachePath, bankTypesCacheBackup);
            else if (File.Exists(bankTypesCachePath)) File.Delete(bankTypesCachePath);
        }
        return fails;
    }

    // ── Programs with "preserve duplication" ON: copy as-is instead of reusing ──────────
    // The second Auto-Fill must write FRESH Program copies (in source order), and the second
    // run's Combis must point at THOSE - MergeCache.ResolveReferencesForPlacement consults
    // this session's placements before the library-wide content lookup, so the new copies win.
    static async Task<List<string>> PreserveDuplicateProgramsSelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string bankTypesCachePath = Path.Combine(Storage.DataDir, "program_bank_types_cache.json");
        string? bankTypesCacheBackup = File.Exists(bankTypesCachePath) ? File.ReadAllText(bankTypesCachePath) : null;
        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_merge_prog_preserve");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var (vm, cache) = await NewChainScenarioAsync(root, fails);
            vm.MergePreserveDuplicatePrograms = true;   // copy Programs as-is instead of reusing

            PullChainIntoMerge(vm);
            var (ok1, _) = await vm.AutoFillFromMergeAsync();
            Check("dup-first-ok", ok1);
            CheckChainOrder(fails, "dup-first", cache, 1);

            PullChainIntoMerge(vm);
            var (ok2, _) = await vm.AutoFillFromMergeAsync();
            Check("dup-second-ok", ok2);
            Check("dup-second-nothing-staged", vm.MergePane.Entries.Count == 0);
            // Programs COPIED AS-IS: fresh slots, in source order, right after the first run's -
            // and Combis (preserve still ON for them, the default) likewise.
            CheckChainOrder(fails, "dup-second", cache, ChainCount + 1);
            var combiSecondRun = cache.GetCurrentBody(LibObj.Combi, 0x00, ChainCount + 1);
            Check("dup-combi-points-at-new-program-copy", combiSecondRun != null &&
                LibRefs.CombiTimbreRef(combiSecondRun, 0) == (KronosBanks.ObjBankToFunc33(1, 0x01), ChainCount + 1));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (bankTypesCacheBackup != null) File.WriteAllText(bankTypesCachePath, bankTypesCacheBackup);
            else if (File.Exists(bankTypesCachePath)) File.Delete(bankTypesCachePath);
        }
        return fails;
    }

    const int ChainCount = 3;

    // Shared scaffold for the three scenarios above: an empty keyboard library whose Program bank
    // I-B is known-EXi, real content seeded at Combi I-A:000 and Program I-B:000 (so every
    // placement lands one slot along from the address the PCG encoded - a repoint that no-ops
    // would prove nothing), and the chain PCG loaded. Same bank-types cache backup/restore
    // discipline as the main test applies in each caller (the warm-up persists it).
    static async Task<(LibrarianShellViewModel Vm, LocalLibraryCache Cache)> NewChainScenarioAsync(string root, List<string> fails)
    {
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var exec = new FakeMoveExecutor();
        var cache = new LocalLibraryCache(root);
        await LibraryPullPipeline.PullAsync(exec, cache, full: true);

        var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), AutoFillHost);
        var bits = new bool[21];
        bits[KronosBanks.ProgramBankTypeBitIndex(0x01)!.Value] = true;    // I-B -> EXi
        exec.ProgramBankTypesToReturn = new ProgramBankTypes(bits);
        await vm.WarmProgramBankTypesForTestingAsync();

        var seedCombi = new byte[CombiSize];
        Encoding.ASCII.GetBytes("SEED COMBI").CopyTo(seedCombi, 0);
        LibRefs.SetCombiTimbreRef(seedCombi, 0, KronosBanks.ObjBankToFunc33(1, 0x40), 11);
        var seedExiProg = ProgramBody.WriteName(new byte[ProgramFormatConverter.WireSizeExi], "SEED EXI");
        var (s1, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Program, 0x01, 0), LibObj.Program, 1, seedExiProg, "seedExi", true, DateTime.UtcNow);
        var (s2, _, _) = LocalEditOps.PlaceObject(cache, new ObjLoc(LibObj.Combi, 0x00, 0), LibObj.Combi, 1, seedCombi, "seedCombi", true, DateTime.UtcNow);
        Check("chain-scenario-seeds-placed", s1 && s2);

        var file = PcgFile.Open(BuildChainPcg());
        Check("chain-scenario-pcg-opens", file != null);
        vm.PcgPane.LoadForTesting(new PcgLibraryView(file!));
        return (vm, cache);
    }

    // Stages every fixture Combi (their Programs come along transitively) - one pull per Combi,
    // mirroring how the user drags/copies a bank's worth of objects into the Merge Window.
    static void PullChainIntoMerge(LibrarianShellViewModel vm)
    {
        for (int i = 0; i < ChainCount; i++)
            vm.PullIntoMerge(new ObjLoc(LibObj.Combi, 0x00, i));
    }

    // The chain placed from `firstSlot` onward must read ORD PROG/ORD COMBI 0,1,2 in ascending
    // slots - any scramble (the recycled-Dictionary-slot bug) shows up as a name mismatch here.
    static void CheckChainOrder(List<string> fails, string prefix, LocalLibraryCache cache, int firstSlot, bool includePrograms = true)
    {
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }
        for (int i = 0; i < ChainCount; i++)
        {
            if (includePrograms)
                Check($"{prefix}-prog{i}-lands-slot{firstSlot + i}",
                    cache.GetDisplayName(LibObj.Program, 0x01, firstSlot + i) == $"ORD PROG {i}");
            Check($"{prefix}-combi{i}-lands-slot{firstSlot + i}",
                cache.GetDisplayName(LibObj.Combi, 0x00, firstSlot + i) == $"ORD COMBI {i}");
        }
    }

    // Fixture: ChainCount EXi Programs in I-B, each referenced by ALL 16 timbres of one Combi
    // in I-A (combi i -> program i) - see BuildSyntheticPcg for why a fixture Combi must never
    // mix real and defaulted timbres. Numbered names make any order scramble directly visible.
    static byte[] BuildChainPcg()
    {
        using var progs = new MemoryStream();
        using var combis = new MemoryStream();
        int fbProgBank = KronosBanks.ObjBankToFunc33(1, 0x01);   // Program bank I-B
        for (int i = 0; i < ChainCount; i++)
        {
            var p = new byte[ProgramFormatConverter.PcgSlotSize];
            Encoding.ASCII.GetBytes($"ORD PROG {i}").CopyTo(p, 0);
            progs.Write(p);

            var c = new byte[CombiSize];
            Encoding.ASCII.GetBytes($"ORD COMBI {i}").CopyTo(c, 0);
            SetAllTimbres(c, fbProgBank, i);   // -> Program i in I-B
            combis.Write(c);
        }
        return BuildPcg(bank =>
        {
            bank("MBK1", ChainCount, ProgramFormatConverter.PcgSlotSize, 0x01, progs.ToArray());
            bank("CBK1", ChainCount, CombiSize, 0, combis.ToArray());
        });
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
            int slotCount = ObjectTypeRegistry.Get(LibObj.Combi).SlotCount(0x00);
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
