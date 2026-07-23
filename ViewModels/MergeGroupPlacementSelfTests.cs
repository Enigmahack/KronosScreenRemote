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
