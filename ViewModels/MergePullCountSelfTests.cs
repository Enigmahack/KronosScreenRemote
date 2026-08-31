namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware regression test for a real user-reported bug: pulling a bunch of Combis into the
// Merge Window reported "Pulled 0 object(s) into the Merge Window" even though objects were
// staged. Two causes, both covered here:
//   1. The multi-item pull looped the single-loc overload, so StatusText was rewritten per loc and
//      the user only ever saw the LAST one's result. A PCG Combi bank ends in unused INIT slots -
//      byte-identical content that dedups - so the last loc's Added was routinely empty.
//   2. The count came from Added (genuinely NEW content only), so re-pulling a selection that was
//      already staged reported 0 however much it covered.
// Fixed by handing the whole gesture to MergePaneViewModel.PullFromPcg's list overload, which
// counts the distinct content the gesture staged (MergeCache's stagedHashes), new or not.
static class MergePullCountSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var pcgBuffer = BuildSyntheticPcg();
        var file = PcgFile.Open(pcgBuffer);
        Check("pcg-opens", file != null);
        if (file == null) return fails;
        var pcg = new PcgLibraryView(file);

        var merge = new MergePaneViewModel(new MergeCache(new InMemoryMergeCachePersistence()));
        var locs = Enumerable.Range(0, 4).Select(n => new ObjLoc(LibObj.Combi, 0x00, n)).ToList();

        // 3 distinct Combis (slot 3 duplicates slot 2) + the one Program they all reference.
        merge.PullFromPcg(pcg, "test.pcg", locs);
        Check("staged-four-distinct", merge.Entries.Count == 4);
        Check("reports-staged-not-last-loc",
              merge.StatusText == AppMessages.Librarian.Merge.PulledIntoMerge(4));

        // Re-pulling the identical selection stages nothing new - but it did cover 4 objects, and
        // "Pulled 0 object(s)" is exactly the report this test exists to prevent. Also guards the
        // union: summing the four locs' own staged sets (each carries the shared Program) would
        // say 8.
        merge.PullFromPcg(pcg, "test.pcg", locs);
        Check("repull-reports-staged-not-zero",
              merge.StatusText == AppMessages.Librarian.Merge.PulledIntoMerge(4));

        return fails;
    }

    // Four Combi slots in one CBK1 bank, all referencing Program A (I-B) from every timbre - see
    // MergeTreeVisibilitySelfTests' fixture comment for why I-B and why every timbre. Slot 3 is a
    // byte-for-byte copy of slot 2, standing in for a real PCG's unused INIT Combi slots: the
    // duplicate content that makes a whole-bank drag's last loc dedup to nothing.
    static byte[] BuildSyntheticPcg()
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810;

        var progABody = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG A").CopyTo(progABody, 0);

        int fbProgA = KronosBanks.ObjBankToFunc33(1, 0x01);
        var combis = new byte[4][];
        for (int n = 0; n < 3; n++)
        {
            combis[n] = new byte[combiSize];
            Encoding.ASCII.GetBytes($"COMBI {(char)('A' + n)}").CopyTo(combis[n], 0);
            for (int t = 0; t < LibRefs.TimbreCount; t++)
                LibRefs.SetCombiTimbreRef(combis[n], t, fbProgA, 0);
        }
        combis[3] = (byte[])combis[2].Clone();

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        WriteAscii("MBK1"); WriteBE32(0); WriteBE32(0); WriteBE32(1); WriteBE32(programSize); WriteBE32(0x01);
        ms.Write(progABody);

        WriteAscii("CBK1"); WriteBE32(0); WriteBE32(0); WriteBE32(combis.Length); WriteBE32(combiSize); WriteBE32(0);
        foreach (var b in combis) ms.Write(b);

        return ms.ToArray();
    }
}
