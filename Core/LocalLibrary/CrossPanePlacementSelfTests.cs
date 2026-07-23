namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for Phase 6's cross-pane placement (PCG -> local), the logic
// living in LibrarianShellViewModel.PlaceFromPcg/BatchPlaceFromPcg. Constructs the
// ViewModel directly against FakeMoveExecutor and a synthetic in-memory PCG buffer —
// PcgPaneViewModel.LoadForTesting sidesteps the file-dialog/FTP-picker paths, which need a
// real Window.
static class CrossPanePlacementSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_cross_pane_placement");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        // Storage.SaveProgramBankTypes persists to a REAL, GLOBAL, host-keyed file next to the
        // running exe — not scratch state under `root` like everything else here. The
        // bank-type test below writes fake data under this ViewModel's own (empty) host key,
        // which every OTHER self-test file that also constructs a ViewModel with host "" would
        // otherwise load right back out via Storage.LoadProgramBankTypes at construction —
        // exactly the cross-test pollution that broke DependencyResolutionSelfTests the first
        // time this was written. Snapshot and restore it verbatim, regardless of outcome.
        string bankTypesCachePath = Path.Combine(Storage.DataDir, "program_bank_types_cache.json");
        string? bankTypesCacheBackup = File.Exists(bankTypesCachePath) ? File.ReadAllText(bankTypesCachePath) : null;
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            await LibraryPullPipeline.PullAsync(exec, cache, full: true);   // nothing seeded — empty local library

            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "");

            var pcgBuffer = BuildSyntheticPcg(out var programExiName, out var programHd1Name, out var combiName,
                out var combiDepExiName, out var combiDepHd1Name);
            var file = PcgFile.Open(pcgBuffer);
            Check("pcg-opens", file != null);
            if (file == null) return fails;

            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));
            var pcgExiProgLoc = new ObjLoc(LibObj.Program, 0x00, 0);   // MBK1 bank -> EXi
            var pcgHd1ProgLoc = new ObjLoc(LibObj.Program, 0x40, 0);   // PBK1 bank -> HD-1
            var pcgCombiLoc = new ObjLoc(LibObj.Combi, 0x00, 0);

            // ── Auto-heal for a DIRECT PCG -> Local placement (no Merge Window involved) —
            // this path used to leave every reference exactly as the PCG encoded it. See
            // DependencyScanner.RepointPcgReferences / LibrarianShellViewModel.
            // StageAndTrackPcgDependencies. Runs FIRST, before Local Library has anything in
            // it, so "the dependency exists elsewhere" / "the dependency exists nowhere" are
            // unambiguous — every OTHER test below places these same two Programs at several
            // more addresses, which would otherwise make FindByContentHash's result ambiguous.
            var combiDepExiLoc = new ObjLoc(LibObj.Combi, 0x00, 1);   // references the EXi Program's own PCG address
            var combiDepHd1Loc = new ObjLoc(LibObj.Combi, 0x00, 2);   // references the HD-1 Program's own PCG address

            // Step 1 — repoint: the EXi Program's content exists locally, but at a DIFFERENT
            // address than the PCG's own (0x00:000 is left empty). Placing a Combi that
            // references the PCG address must repoint the reference to where the content
            // ACTUALLY lives, not leave it pointing at the empty original address.
            var exiElsewhere = new ObjLoc(LibObj.Program, 0x41, 20);
            vm.PlaceFromPcg(pcgExiProgLoc, exiElsewhere);

            var repointDestLoc = new ObjLoc(LibObj.Combi, 0x45, 0);
            var (repointOk, repointErr) = vm.PlaceFromPcg(combiDepExiLoc, repointDestLoc);
            Check("repoint-place-ok", repointOk && repointErr == null);
            var repointedBody = cache.GetCurrentBody(repointDestLoc.ObjType, repointDestLoc.Bank, repointDestLoc.Number);
            int fbElsewhere = KronosBanks.ObjBankToFunc33(1, exiElsewhere.Bank);
            Check("repoint-points-at-found-location", repointedBody != null &&
                LibRefs.CombiTimbreRef(repointedBody, 0) == (fbElsewhere, exiElsewhere.Number));

            // Step 2 — auto-stage: the HD-1 Program isn't local ANYWHERE yet — placing a Combi
            // that references it must pull it into the Merge Window automatically (instead of
            // leaving a silently wrong/missing reference) and track it for a later retry.
            var hd1PcgEntry = vm.PcgPane.Get(pcgHd1ProgLoc);
            var hd1WireBody = hd1PcgEntry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(LibObj.Program, hd1PcgEntry);
            string? hd1ExpectedHash = hd1WireBody == null ? null : LocalObjectStore.ComputeHash(hd1WireBody);
            Check("hd1-not-staged-before-placement", hd1ExpectedHash != null && vm.MergePane.TryGet(hd1ExpectedHash) == null);

            int pendingBefore = vm.SessionClipboardRows.Count;
            var stageDestLoc = new ObjLoc(LibObj.Combi, 0x45, 1);
            var (stageOk, stageErr) = vm.PlaceFromPcg(combiDepHd1Loc, stageDestLoc);
            Check("autostage-place-ok", stageOk && stageErr == null);
            Check("autostage-dependency-pulled-into-merge", hd1ExpectedHash != null && vm.MergePane.TryGet(hd1ExpectedHash) != null);
            Check("autostage-dependency-name", hd1ExpectedHash != null &&
                vm.MergePane.TryGet(hd1ExpectedHash)?.DisplayName == programHd1Name);
            Check("autostage-tracked-as-pending", vm.SessionClipboardRows.Count > pendingBefore);

            // EXi Program: .pcg (4960) and wire (4960) already match — placed as-is,
            // untruncated. See ProgramFormatConverter.
            var exiDestLoc = new ObjLoc(LibObj.Program, 0x41, 5);
            var (exiOk, exiError) = vm.PlaceFromPcg(pcgExiProgLoc, exiDestLoc);
            Check("place-exi-program-ok", exiOk && exiError == null);
            Check("place-exi-program-untruncated", cache.GetCurrentBody(exiDestLoc.ObjType, exiDestLoc.Bank, exiDestLoc.Number)?.Length == ProgramFormatConverter.WireSizeExi);
            Check("place-exi-program-name", cache.GetDisplayName(exiDestLoc.ObjType, exiDestLoc.Bank, exiDestLoc.Number) == programExiName);
            Check("place-exi-program-is-exi", cache.IsExi(exiDestLoc.ObjType, exiDestLoc.Bank, exiDestLoc.Number));

            // HD-1 Program: the wire body is the first 3706 bytes of the 4960-byte .pcg slot
            // — placement must apply that truncation, not write the raw 4960-byte record.
            var hd1DestLoc = new ObjLoc(LibObj.Program, 0x42, 5);
            var (hd1Ok, hd1Error) = vm.PlaceFromPcg(pcgHd1ProgLoc, hd1DestLoc);
            Check("place-hd1-program-ok", hd1Ok && hd1Error == null);
            Check("place-hd1-program-truncated", cache.GetCurrentBody(hd1DestLoc.ObjType, hd1DestLoc.Bank, hd1DestLoc.Number)?.Length == ProgramFormatConverter.WireSizeHd1);
            Check("place-hd1-program-name", cache.GetDisplayName(hd1DestLoc.ObjType, hd1DestLoc.Bank, hd1DestLoc.Number) == programHd1Name);
            Check("place-hd1-program-is-not-exi", !cache.IsExi(hd1DestLoc.ObjType, hd1DestLoc.Bank, hd1DestLoc.Number));

            // Batch auto-fill goes through the same conversion.
            var (progBatchOk, _) = vm.BatchPlaceFromPcg(LibObj.Program, new[] { pcgHd1ProgLoc }, 0x43);
            Check("place-batch-program-ok", progBatchOk);
            Check("place-batch-program-truncated", cache.GetCurrentBody(LibObj.Program, 0x43, 0)?.Length == ProgramFormatConverter.WireSizeHd1);

            // Exact placement (drop on a specific slot) — Combi's on-disk (7810) and wire
            // (7810) sizes match, so this IS safe to place directly.
            var destLoc = new ObjLoc(LibObj.Combi, 0x40, 5);
            var (ok, error) = vm.PlaceFromPcg(pcgCombiLoc, destLoc);
            Check("place-exact-ok", ok && error == null);
            Check("place-exact-lands", cache.GetDisplayName(destLoc.ObjType, destLoc.Bank, destLoc.Number) == combiName);
            Check("place-exact-dirty", cache.IsDirty(destLoc.ObjType, destLoc.Bank, destLoc.Number));

            // Placing a Combi that references a Program not present locally must populate
            // the session dependency clipboard (requirement 13/14).
            Check("dependency-tracked", vm.SessionClipboardRows.Count > 0);

            // Auto-fill (drop on a bank) — next free slot in a fresh bank is 0.
            var (ok2, msg2) = vm.BatchPlaceFromPcg(LibObj.Combi, new[] { pcgCombiLoc }, 0x41);
            Check("place-batch-ok", ok2);
            Check("place-batch-lands-at-slot-0", cache.GetDisplayName(LibObj.Combi, 0x41, 0) == combiName);

            // Auto-fill again into the SAME bank -> next free slot must now be 1, not 0 again.
            var (ok3, msg3) = vm.BatchPlaceFromPcg(LibObj.Combi, new[] { pcgCombiLoc }, 0x41);
            Check("place-batch-advances-slot", ok3 && cache.GetDisplayName(LibObj.Combi, 0x41, 1) == combiName);

            // ── Fresh-placement bank-type check (end-to-end): a real hardware write got
            // rejected because nothing ever verified a fresh Program placement's wire-format
            // size against what its destination bank is ACTUALLY configured as. Wire a fake
            // "real hardware" answer through FakeMoveExecutor.ProgramBankTypesToReturn and
            // confirm LibrarianShellViewModel.WarmProgramBankTypesAsync/BankTypeOf/
            // PlanBatchMove's new check actually catch it — every prior test in this file ran
            // with ProgramBankTypesToReturn still null (BankTypeOf returns null for every
            // bank, CHECK-only, never refuses), confirming this whole feature is opt-in and
            // doesn't disturb any of the above.
            int bankTypeBit = KronosBanks.ProgramBankTypeBitIndex(0x46)!.Value;   // U-G
            var mismatchedTypes = new bool[bankTypeBit + 1];
            mismatchedTypes[bankTypeBit] = false;   // "hardware" says U-G is HD-1
            exec.ProgramBankTypesToReturn = new ProgramBankTypes(mismatchedTypes);
            await vm.WarmProgramBankTypesForTestingAsync();

            var wrongTypeDestLoc = new ObjLoc(LibObj.Program, 0x46, 0);
            var (wrongTypeOk, wrongTypeError) = vm.PlaceFromPcg(pcgExiProgLoc, wrongTypeDestLoc);   // pcgExiProgLoc is EXi (4960B) — bank says HD-1
            Check("bank-type-mismatch-refused", !wrongTypeOk &&
                wrongTypeError != null && wrongTypeError.Contains("wrong format for this bank"));
            Check("bank-type-mismatch-nothing-written", cache.GetCurrentBody(wrongTypeDestLoc.ObjType, wrongTypeDestLoc.Bank, wrongTypeDestLoc.Number) == null);

            var matchedTypes = new bool[bankTypeBit + 1];
            matchedTypes[bankTypeBit] = true;   // "hardware" now says U-G is EXi — matches pcgExiProgLoc
            exec.ProgramBankTypesToReturn = new ProgramBankTypes(matchedTypes);
            await vm.WarmProgramBankTypesForTestingAsync();

            var (matchedTypeOk, matchedTypeError) = vm.PlaceFromPcg(pcgExiProgLoc, wrongTypeDestLoc);
            Check("bank-type-match-succeeds", matchedTypeOk && matchedTypeError == null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (bankTypesCacheBackup != null) File.WriteAllText(bankTypesCachePath, bankTypesCacheBackup);
            else if (File.Exists(bankTypesCachePath)) File.Delete(bankTypesCachePath);
        }

        return fails;
    }

    static byte[] BuildSyntheticPcg(out string programExiName, out string programHd1Name, out string combiName,
        out string combiDepExiName, out string combiDepHd1Name)
    {
        programExiName = "PCG EXI PROGRAM";
        programHd1Name = "PCG HD1 PROGRAM";
        combiName = "PCG COMBI";
        combiDepExiName = "PCG COMBI DEP EXI";
        combiDepHd1Name = "PCG COMBI DEP HD1";
        // Every .pcg Program slot is 4960 bytes regardless of tag (MBK1/EXi or PBK1/HD-1) —
        // see ProgramFormatConverter's class comment for the confirmed real-file evidence.
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810;

        var exiProgramBody = new byte[programSize];
        Encoding.ASCII.GetBytes(programExiName).CopyTo(exiProgramBody, 0);

        var hd1ProgramBody = new byte[programSize];
        Encoding.ASCII.GetBytes(programHd1Name).CopyTo(hd1ProgramBody, 0);

        // Combi references a Program (func33 bank 5, index 42) that this synthetic PCG does
        // NOT itself contain — deliberately, to exercise the dependency-tracking check.
        var combiBody = new byte[combiSize];
        Encoding.ASCII.GetBytes(combiName).CopyTo(combiBody, 0);
        LibRefs.SetCombiTimbreRef(combiBody, 0, 5, 42);

        // Two more Combis, each referencing one of THIS PCG's own Programs at its natural
        // address — for the auto-heal section below (DependencyScanner.RepointPcgReferences):
        // placing one of these directly exercises repoint-if-found-elsewhere and
        // stage-if-genuinely-missing against a real, known dependency.
        int fbExiProg = KronosBanks.ObjBankToFunc33(1, 0x00);
        int fbHd1Prog = KronosBanks.ObjBankToFunc33(1, 0x40);
        var combiDepExiBody = new byte[combiSize];
        Encoding.ASCII.GetBytes(combiDepExiName).CopyTo(combiDepExiBody, 0);
        LibRefs.SetCombiTimbreRef(combiDepExiBody, 0, fbExiProg, 0);
        var combiDepHd1Body = new byte[combiSize];
        Encoding.ASCII.GetBytes(combiDepHd1Name).CopyTo(combiDepHd1Body, 0);
        LibRefs.SetCombiTimbreRef(combiDepHd1Body, 0, fbHd1Prog, 0);

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

        WriteBank("MBK1", 1, programSize, 0, exiProgramBody);        // bank 0x00 (I-A) -> EXi
        WriteBank("PBK1", 1, programSize, 0x20000, hd1ProgramBody);  // bank 0x40 (U-A) -> HD-1

        using var combis = new MemoryStream();
        combis.Write(combiBody); combis.Write(combiDepExiBody); combis.Write(combiDepHd1Body);
        WriteBank("CBK1", 3, combiSize, 0, combis.ToArray());

        return ms.ToArray();
    }
}
