namespace KronosScreenRemote.ViewModels;

using System.IO;
using System.Text;

// Off-hardware self-test for a real bug: MergePaneViewModel.RefreshTree only showed a
// Combi/Program at its own top-level section when IsTopLevelPull was true - a Set List's own
// dependencies were only ever reachable by nesting under the Set List's own tree node. Once
// the Set List got placed (removed from the cache), its still-staged dependency Combi/Program
// had nothing left to nest under and simply vanished from the tree entirely, even though it
// was still fully staged and placeable - the user had no way to even find it, let alone place
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
        // referrer (the still-staged Set List) - reachable only nested under it, not flatly.
        Check("combis-root-absent-before-removal", !merge.Roots.Any(r => r.Label == "Combis"));
        Check("programs-root-absent-before-removal", !merge.Roots.Any(r => r.Label == "Programs"));
        var setListNode = merge.Roots.FirstOrDefault(r => r.Label == "Set Lists")?.Children
            .FirstOrDefault(n => n.MergeContentHash == setListHash);
        Check("combiX-nested-under-setlist", setListNode?.Children.Any(n => n.MergeContentHash == combiXHash) == true);

        // Placing (or explicitly removing) the Set List must NOT make Combi X/Program A
        // disappear from the tree - they're still fully staged and still need to be placed.
        merge.Remove(new[] { setListHash });
        Check("setlist-gone", merge.TryGet(setListHash) == null);
        Check("combiX-still-staged-after-setlist-removed", merge.TryGet(combiXHash) != null);

        // Top-level Combis/Programs are now grouped by SOURCE bank (requirement 4), so a
        // graduated entry sits one level deeper - under its bank group, not directly under the
        // type root. SelectMany through the bank groups to find it.
        var combisRoot = merge.Roots.FirstOrDefault(r => r.Label == "Combis");
        Check("combiX-graduates-to-flat-display", combisRoot?.Children.SelectMany(b => b.Children).Any(n => n.MergeContentHash == combiXHash) == true);

        // Program A is still nested under Combi X (Combi X is still its current referrer) -
        // not ALSO duplicated flatly under "Programs".
        var combiXNode = combisRoot?.Children.SelectMany(b => b.Children).FirstOrDefault(n => n.MergeContentHash == combiXHash);
        Check("progA-still-nested-under-combiX", combiXNode?.Children.Any(n => n.MergeContentHash == progAHash) == true);
        Check("programs-root-still-absent", !merge.Roots.Any(r => r.Label == "Programs"));

        // Once Combi X ALSO gets removed (simulating it being placed), Program A must in turn
        // graduate to flat display under "Programs" - the same rule, one level deeper.
        merge.Remove(new[] { combiXHash });
        var programsRoot = merge.Roots.FirstOrDefault(r => r.Label == "Programs");
        bool progAFlat = programsRoot?.Children.SelectMany(bankGroup => bankGroup.Children)
            .Any(n => n.MergeContentHash == progAHash) == true;
        Check("progA-graduates-to-flat-display-after-combiX-removed", progAFlat);

        // ── A Program's own Wave Sequence dependency must be reachable/placeable too (real bug:
        // MergePaneViewModel never built a Drum Kit/Wave Sequence root at all, and gave Program
        // entries plain MakeNode instead of MakeNodeWithChildren, so a pulled Program's Wave
        // Sequence/Drum Kit reference had NO tree node anywhere - visible in Object Dependencies
        // but with no way to actually place it). ─────────────────────────────────────────────
        var wsPcgBuffer = BuildProgramWithWaveSeqPcg(out var progWsBody, out var waveBody);
        var wsFile = PcgFile.Open(wsPcgBuffer);
        Check("wspcg-opens", wsFile != null);
        if (wsFile == null) return fails;
        var wsPcg = new PcgLibraryView(wsFile);

        var wsMerge = new MergePaneViewModel(new MergeCache(new InMemoryMergeCachePersistence()));
        wsMerge.PullFromPcg(wsPcg, "test-ws.pcg", new ObjLoc(LibObj.Program, 0x01, 0));

        string progWsHash = LocalObjectStore.ComputeHash(progWsBody);
        string waveHash = LocalObjectStore.ComputeHash(waveBody);
        Check("progWs-staged", wsMerge.TryGet(progWsHash) != null);
        Check("wave-staged", wsMerge.TryGet(waveHash) != null);

        var progWsNode = wsMerge.Roots.FirstOrDefault(r => r.Label == "Programs")?.Children
            .SelectMany(b => b.Children).FirstOrDefault(n => n.MergeContentHash == progWsHash);
        Check("wave-nested-under-program", progWsNode?.Children.Any(n => n.MergeContentHash == waveHash) == true);

        // Placing the Program removes it from the cache - its Wave Sequence dependency must
        // graduate to its own "Wave Sequences" root, exactly like Combi X/Program A did above.
        wsMerge.Remove(new[] { progWsHash });
        var waveSequencesRoot = wsMerge.Roots.FirstOrDefault(r => r.Label == "Wave Sequences");
        bool waveFlat = waveSequencesRoot?.Children.SelectMany(b => b.Children).Any(n => n.MergeContentHash == waveHash) == true;
        Check("wave-graduates-to-flat-display-after-program-removed", waveFlat);

        return fails;
    }

    // Program "PROG WS" (HD-1, I-B:000) with OSC1 Zone1 pointing at Wave Sequence linear index 0
    // (Int:000, KronosBanks.WaveSeqLinearToLoc) -> Wave Sequence "WAVE FIVE". progWsBody comes
    // back already truncated to the WIRE size (see below) - the same bytes MergeCache actually
    // hashes/stages, not the on-disk .pcg slot size.
    static byte[] BuildProgramWithWaveSeqPcg(out byte[] progWsBody, out byte[] waveBody)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, waveSize = 2216;

        var progOnDisk = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG WS").CopyTo(progOnDisk, 0);
        progOnDisk[2774] = 2;   // OSC1 Zone1 MS Type = Wave Sequence
        LibRefs.SetProgramZoneNumber(progOnDisk, 0, 0, 0);   // linear 0 -> Int:000
        // PBK1 = HD-1 (see PcgObjectExtractor's own comment); MBK1 is EXi and would leave the
        // OSC1/OSC2 zone layout ObjectReferenceWalker expects entirely unwritten-to for real
        // hardware bytes, since a real EXi body's own layout there means something else.
        progWsBody = progOnDisk[..ProgramFormatConverter.WireSizeHd1];

        waveBody = new byte[waveSize];
        Encoding.ASCII.GetBytes("WAVE FIVE").CopyTo(waveBody, 0);

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

        WriteBank("PBK1", 1, programSize, 0x01, progOnDisk);   // I-B:000, see BuildSyntheticPcg
        WriteBank("WBK1", 1, waveSize, 0, waveBody);           // Int:000

        return ms.ToArray();
    }

    // Minimal fixture: Program A <- Combi X <- Set List S (a straight three-level chain).
    static byte[] BuildSyntheticPcg(out byte[] progABody, out byte[] combiXBody, out byte[] setListBody)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810, setListSize = 700;

        progABody = new byte[programSize];
        Encoding.ASCII.GetBytes("PROG A").CopyTo(progABody, 0);

        // Program A lives in I-B, NOT I-A. Deliberate: func-33 bank 0 / number 0 is the zero
        // default every timbre of an INIT Combi already holds, so a Combi whose only reference
        // is (0, 0) satisfies CombiBody.AllTimbresAtDefault and reads as an init placeholder
        // with NO dependencies at all (InitObjects) - Program A would never be staged, and the
        // three-level chain this test exists to exercise would collapse to two.
        int fbProgA = KronosBanks.ObjBankToFunc33(1, 0x01);
        // ...and every timbre points at it, never just timbre 0: a timbre left at (0, 0) is a live
        // reference to Program I-A:000, which this PCG doesn't contain, so 15 untouched timbres
        // would manufacture 15 phantom gaps. All defaults, or none.
        combiXBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("COMBI X").CopyTo(combiXBody, 0);
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combiXBody, t, fbProgA, 0);   // -> Program A

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

        WriteBank("MBK1", 1, programSize, 0x01, progABody);   // bank 0x01 (I-B) - see fbProgA
        WriteBank("CBK1", 1, combiSize, 0, combiXBody);
        WriteBank("SBK1", 1, setListSize, 0, setListBody);

        return ms.ToArray();
    }
}
