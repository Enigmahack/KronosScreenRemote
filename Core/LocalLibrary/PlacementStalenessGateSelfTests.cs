namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for the cross-pane placement staleness gate (Merge Window / Loaded
// PCG File -> Keyboard Library): LibrarianShellViewModel.ConfirmDestinationBankAsync and the
// PlaceFromPcgAsync/PlaceFromMergeAsync wrappers around the pre-existing synchronous
// placement methods. Covers the three baseline states a destination bank can be in
// (never pulled at all, pulled but the Kronos answered NoDigest, and a real confirmed
// digest), and that a Confirm answer of false actually leaves the object unplaced while
// true lets it through - plus that the synchronous methods (every OTHER self-test's own
// call path) are completely unaffected by this gate.
static class PlacementStalenessGateSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_placement_staleness_gate");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            var exec = new FakeMoveExecutor();
            var cache = new LocalLibraryCache(root);
            // Deliberately NO LibraryPullPipeline.PullAsync here - a completely virgin cache,
            // so every bank starts with no digest baseline at all (the "never confirmed" state
            // this gate exists to catch). See CrossPanePlacementSelfTests' own comment for why
            // the host key must be unique.
            var vm = new LibrarianShellViewModel(exec, cache, new AppSettings(), "selftest-staleness-gate-host");

            var pcgBuffer = BuildSyntheticPcg();
            var file = PcgFile.Open(pcgBuffer);
            Check("pcg-opens", file != null);
            if (file == null) return fails;
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));

            var pcgLoc = new ObjLoc(LibObj.Program, 0x01, 0);
            var destLoc = new ObjLoc(LibObj.Program, 0x41, 0);

            // ── Never-confirmed bank, no confirm delegate wired (headless default) - proceeds,
            // same "null defaults to proceeding" convention every other confirm gate here uses.
            var (ok1, err1) = await vm.PlaceFromPcgAsync(pcgLoc, destLoc);
            Check("no-delegate-defaults-to-proceeding", ok1 && err1 == null);
            Check("placed-when-no-delegate", cache.GetCurrentBody(destLoc.ObjType, destLoc.Bank, destLoc.Number) != null);
            cache.RemoveObject(destLoc.ObjType, destLoc.Bank, destLoc.Number, DateTime.UtcNow);

            // ── Never-confirmed bank, delegate wired and declines - placement must be refused
            // and nothing written, distinguishing this from ChangesetBuilder's own push-time gate.
            int confirmCalls = 0;
            (int ObjType, int Bank)? lastAsked = null;
            vm.ConfirmDestinationBankMaybeStale = (objType, bank) =>
            {
                confirmCalls++;
                lastAsked = (objType, bank);
                return Task.FromResult(false);
            };
            var (ok2, err2) = await vm.PlaceFromPcgAsync(pcgLoc, destLoc);
            Check("decline-refuses-placement", !ok2 && err2 == AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);
            Check("decline-writes-nothing", cache.GetCurrentBody(destLoc.ObjType, destLoc.Bank, destLoc.Number) == null);
            Check("delegate-invoked-once", confirmCalls == 1);
            Check("delegate-asked-about-dest-bank", lastAsked == (destLoc.ObjType, destLoc.Bank));

            // ── Same never-confirmed bank, delegate now accepts - placement proceeds.
            vm.ConfirmDestinationBankMaybeStale = (_, _) => { confirmCalls++; return Task.FromResult(true); };
            var (ok3, err3) = await vm.PlaceFromPcgAsync(pcgLoc, destLoc);
            Check("accept-allows-placement", ok3 && err3 == null);
            Check("accept-writes-object", cache.GetCurrentBody(destLoc.ObjType, destLoc.Bank, destLoc.Number) != null);
            Check("delegate-invoked-again", confirmCalls == 2);
            cache.RemoveObject(destLoc.ObjType, destLoc.Bank, destLoc.Number, DateTime.UtcNow);

            // ── A REAL confirmed baseline for the destination bank - the gate must not even ask,
            // regardless of what the delegate would answer.
            cache.SetBankDigestBaseline(destLoc.ObjType, destLoc.Bank, "deadbeef00112233445566778899aabbccddeeff");
            confirmCalls = 0;
            vm.ConfirmDestinationBankMaybeStale = (_, _) => { confirmCalls++; return Task.FromResult(false); };
            var (ok4, err4) = await vm.PlaceFromPcgAsync(pcgLoc, destLoc);
            Check("confirmed-baseline-skips-delegate", ok4 && err4 == null && confirmCalls == 0);
            cache.RemoveObject(destLoc.ObjType, destLoc.Bank, destLoc.Number, DateTime.UtcNow);

            // ── LibraryPullPipeline.NoDigest sentinel (the Kronos didn't answer for this bank on
            // the last Pull) reads the same as never-confirmed - it's still not a real baseline.
            cache.SetBankDigestBaseline(destLoc.ObjType, destLoc.Bank, LibraryPullPipeline.NoDigest);
            confirmCalls = 0;
            vm.ConfirmDestinationBankMaybeStale = (_, _) => { confirmCalls++; return Task.FromResult(false); };
            var (ok5, err5) = await vm.PlaceFromPcgAsync(pcgLoc, destLoc);
            Check("nodigest-sentinel-triggers-gate", !ok5 && confirmCalls == 1);

            // ── Merge Window -> Local goes through the same gate (PlaceFromMergeAsync). Pull the
            // PCG object into the Merge Window first, at an UNCONFIRMED destination bank.
            vm.PcgPane.LoadForTesting(new PcgLibraryView(file));   // reload - the prior RemoveObject above didn't touch PCG
            vm.PullIntoMerge(pcgLoc);
            var pcgEntry = vm.PcgPane.Get(pcgLoc);
            Check("pcg-entry-found-for-merge-pull", pcgEntry != null);
            string contentHash = pcgEntry != null
                ? LocalObjectStore.ComputeHash(ProgramFormatConverter.WireBodyFromPcgEntry(LibObj.Program, pcgEntry)!)
                : "";
            var mergeDestLoc = new ObjLoc(LibObj.Program, 0x42, 0);   // a bank never baselined at all
            confirmCalls = 0;
            vm.ConfirmDestinationBankMaybeStale = (_, _) => { confirmCalls++; return Task.FromResult(false); };
            var (mok, merr) = await vm.PlaceFromMergeAsync(contentHash, mergeDestLoc);
            Check("merge-placement-gated-too", !mok && confirmCalls == 1 && merr == AppMessages.Librarian.Shell.PlacementCancelledOutOfSync);

            // ── The pre-existing SYNCHRONOUS methods (every other self-test's own call path)
            // bypass this gate entirely - no delegate call, placement always proceeds.
            confirmCalls = 0;
            var (syncOk, syncErr) = vm.PlaceFromMerge(contentHash, mergeDestLoc);
            Check("synchronous-path-bypasses-gate", syncOk && syncErr == null && confirmCalls == 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }

    // Minimal fixture: a single Program, no dependencies - this gate doesn't care about
    // reference resolution, only which destination bank a placement targets.
    static byte[] BuildSyntheticPcg()
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize;
        var progBody = new byte[programSize];
        Encoding.ASCII.GetBytes("STALE TEST").CopyTo(progBody, 0);

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

        WriteBank("MBK1", 1, programSize, 0x01, progBody);   // bank 0x01 (I-B)

        return ms.ToArray();
    }
}
