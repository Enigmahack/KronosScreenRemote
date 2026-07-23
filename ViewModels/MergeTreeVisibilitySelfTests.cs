namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware self-test for a real bug: MergePaneViewModel.RefreshTree only showed a
// Combi/Program at its own top-level section when IsTopLevelPull was true — a Set List's own
// dependencies were only ever reachable by nesting under the Set List's own tree node. Once
// the Set List got placed (removed from the cache), its still-staged dependency Combi/Program
// had nothing left to nest under and simply vanished from the tree entirely, even though it
// was still fully staged and placeable — the user had no way to even find it, let alone place
// it. Fixed by letting an entry "graduate" to flat top-level display the moment nothing
// still-staged references it anymore (MergePaneViewModel.RefreshTree's HasCurrentReferrer).
static class MergeTreeVisibilitySelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var pcgBuffer = BuildSyntheticPcg(out var progABody, out var combiXBody, out var setListBody);
        var file = PcgFile.Open(pcgBuffer);
        Check("pcg-opens", file != null);
        if (file == null) return fails;
        var pcg = new PcgLibraryView(file);

        var merge = new MergePaneViewModel(new MergeCache(new InMemoryMergeCachePersistence()));
        var setListLoc = new ObjLoc(LibObj.SetList, 0, 0);
        merge.PullFromPcg(pcg, "test.pcg", setListLoc);

        string setListHash = LocalObjectStore.ComputeHash(setListBody);
        string combiXHash = LocalObjectStore.ComputeHash(combiXBody);
        string progAHash = LocalObjectStore.ComputeHash(progABody);
        Check("setlist-staged", merge.TryGet(setListHash) != null);
        Check("combiX-staged", merge.TryGet(combiXHash) != null);
        Check("progA-staged", merge.TryGet(progAHash) != null);

        // Before removal: Combi X and Program A are non-top-level dependencies with a CURRENT
        // referrer (the still-staged Set List) — reachable only nested under it, not flatly.
        Check("combis-root-absent-before-removal", !merge.Roots.Any(r => r.Label == "Combis"));
        Check("programs-root-absent-before-removal", !merge.Roots.Any(r => r.Label == "Programs"));
        var setListNode = merge.Roots.FirstOrDefault(r => r.Label == "Set Lists")?.Children
            .FirstOrDefault(n => n.MergeContentHash == setListHash);
        Check("combiX-nested-under-setlist", setListNode?.Children.Any(n => n.MergeContentHash == combiXHash) == true);

        // Placing (or explicitly removing) the Set List must NOT make Combi X/Program A
        // disappear from the tree — they're still fully staged and still need to be placed.
        merge.Remove(new[] { setListHash });
        Check("setlist-gone", merge.TryGet(setListHash) == null);
        Check("combiX-still-staged-after-setlist-removed", merge.TryGet(combiXHash) != null);

        var combisRoot = merge.Roots.FirstOrDefault(r => r.Label == "Combis");
        Check("combiX-graduates-to-flat-display", combisRoot?.Children.Any(n => n.MergeContentHash == combiXHash) == true);

        // Program A is still nested under Combi X (Combi X is still its current referrer) —
        // not ALSO duplicated flatly under "Programs".
        var combiXNode = combisRoot?.Children.FirstOrDefault(n => n.MergeContentHash == combiXHash);
        Check("progA-still-nested-under-combiX", combiXNode?.Children.Any(n => n.MergeContentHash == progAHash) == true);
        Check("programs-root-still-absent", !merge.Roots.Any(r => r.Label == "Programs"));

        // Once Combi X ALSO gets removed (simulating it being placed), Program A must in turn
        // graduate to flat display under "Programs" — the same rule, one level deeper.
        merge.Remove(new[] { combiXHash });
        var programsRoot = merge.Roots.FirstOrDefault(r => r.Label == "Programs");
        bool progAFlat = programsRoot?.Children.SelectMany(formatGroup => formatGroup.Children)
            .Any(n => n.MergeContentHash == progAHash) == true;
        Check("progA-graduates-to-flat-display-after-combiX-removed", progAFlat);

        return fails;
    }

    // Minimal fixture: Program A <- Combi X <- Set List S (a straight three-level chain).
    static byte[] BuildSyntheticPcg(out byte[] progABody, out byte[] combiXBody, out byte[] setListBody)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810, setListSize = 700;

        progABody = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG A").CopyTo(progABody, 0);

        int fbProgA = KronosBanks.ObjBankToFunc33(1, 0x00);
        combiXBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI X").CopyTo(combiXBody, 0);
        LibRefs.SetCombiTimbreRef(combiXBody, 0, fbProgA, 0);   // -> Program A

        int fbCombiX = KronosBanks.ObjBankToFunc33(0, 0x00);
        var slBody = new byte[setListSize];
        Encoding.ASCII.GetBytes("SETLIST S").CopyTo(slBody, 0);
        slBody = SetListBody.WriteSlotName(slBody, 0, "SLOT ONE");   // non-blank name -> not IsEmpty
        LibRefs.SetSetListSlotRef(slBody, 0, fbCombiX, 0, type: 0);  // slot 0 -> Combi (type 0), index 0 (Combi X)
        setListBody = slBody;

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

        WriteBank("MBK1", 1, programSize, 0, progABody);
        WriteBank("CBK1", 1, combiSize, 0, combiXBody);
        WriteBank("SBK1", 1, setListSize, 0, setListBody);

        return ms.ToArray();
    }
}
