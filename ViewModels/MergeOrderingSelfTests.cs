namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware regression test for a real user-reported bug: "add PCG objects to Merge,
// Auto-Fill works, then immediately pull another object from the PCG into Merge and it
// shows out of order". Root cause: MergeCache stores entries in a plain Dictionary, whose
// enumeration order equals insertion order only until the first removal - a placed/removed
// entry's array slot gets RECYCLED by the next insert, so a post-Auto-Fill pull surfaced
// mid-list in the Merge tree. The pane now walks entries in explicit (source bank, source
// slot, hash) display order (MergePaneViewModel.InDisplayOrder) instead of trusting raw
// enumeration order; this test fails on the old walk.
static class MergeOrderingSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var pcgBuffer = BuildSyntheticPcg(out var bodies);
        var file = PcgFile.Open(pcgBuffer);
        Check("pcg-opens", file != null);
        if (file == null) return fails;
        var pcg = new PcgLibraryView(file);

        var hashes = bodies.Select(LocalObjectStore.ComputeHash).ToList();
        var merge = new MergePaneViewModel(new MergeCache(new InMemoryMergeCachePersistence()));

        // Pull three of the four Combis (slots 0,1,2), then place/remove the middle one -
        // the exact shape Auto-Fill leaves behind (CommitPlacement removes as it places).
        for (int n = 0; n < 3; n++)
            merge.PullFromPcg(pcg, "test.pcg", new ObjLoc(LibObj.Combi, 0x00, n));

        static List<string?> TreeOrder(MergePaneViewModel m) => m.Roots
            .First(r => r.Label == "Combis").Children          // bank groups
            .SelectMany(g => g.Children)                       // entries within the group
            .Select(n => n.MergeContentHash)
            .ToList();

        Check("initial-order-ABC", TreeOrder(merge).SequenceEqual(
            new string?[] { hashes[0], hashes[1], hashes[2] }));

        merge.Remove(new[] { hashes[1] });   // stand-in for CommitPlacement's removal

        // The next pull must still land in source-slot order (A, C, D) - NOT recycled into
        // the freed middle slot (A, D, C), which is what raw Dictionary enumeration produced.
        merge.PullFromPcg(pcg, "test.pcg", new ObjLoc(LibObj.Combi, 0x00, 3));
        Check("post-removal-pull-ordered-ACD", TreeOrder(merge).SequenceEqual(
            new string?[] { hashes[0], hashes[2], hashes[3] }));

        return fails;
    }

    // Four independent Combis (COMBI A..D) in one CBK1 bank; all timbres at the zero
    // default so none of them manufactures dependency pulls (see MergeTreeVisibilitySelfTests'
    // fixture comment for why that matters).
    static byte[] BuildSyntheticPcg(out byte[][] bodies)
    {
        const int combiSize = 7810;
        bodies = new byte[4][];
        for (int n = 0; n < 4; n++)
        {
            bodies[n] = new byte[combiSize];
            Encoding.ASCII.GetBytes($"COMBI {(char)('A' + n)}").CopyTo(bodies[n], 0);
        }

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        WriteAscii("CBK1"); WriteBE32(0); WriteBE32(0);
        WriteBE32(bodies.Length); WriteBE32(combiSize); WriteBE32(0);   // count, itemSize, bankId (I-A)
        foreach (var b in bodies) ms.Write(b);

        return ms.ToArray();
    }
}
