namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware coverage for the PCG pane's live search filter (PcgPaneViewModel.SearchText /
// BuildSearchText / FilterNode - Views/LibrarianShellWindow.xaml's search box, top-right above
// "Loaded PCG File"). PcgPaneLoadSelfTests already proves the tree itself loads correctly; this
// exercises the FILTER's actual matching logic against a synthetic fixture - name, bank type,
// EXi engine type, category, and one-hop object-dependency matching - plus that IsVisible/
// IsExpanded/LeafLocs() all agree with each other and that clearing the query restores full
// visibility.
static class PcgSearchFilterSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var pane = new PcgPaneViewModel();
        var categoryNames = CategoryNames.Numeric();
        categoryNames.Program[3] = "Guitar";   // matches ALPHA's packed category byte below (3/1)
        pane.GetCategoryNames = () => categoryNames;

        pane.LoadBytesForTesting(BuildFixture(), "search-fixture.pcg");

        var alphaLoc    = new ObjLoc(LibObj.Program, 0x00, 0);
        var cx3Loc      = new ObjLoc(LibObj.Program, 0x00, 1);
        var pianoLoc    = new ObjLoc(LibObj.Program, 0x01, 0);
        var basslineLoc = new ObjLoc(LibObj.Program, 0x02, 0);
        var combiLoc    = new ObjLoc(LibObj.Combi, 0x00, 0);
        Check("fixture-loaded", pane.Get(alphaLoc) != null && pane.Get(cx3Loc) != null
            && pane.Get(pianoLoc) != null && pane.Get(basslineLoc) != null && pane.Get(combiLoc) != null);

        bool Visible(ObjLoc loc) => FindLeaf(pane.Roots, loc)?.IsVisible == true;
        ObjectTreeNode? BankNode(int objType, string rootLabel, int bank) =>
            pane.Roots.FirstOrDefault(r => r.Label == rootLabel)?.Children.FirstOrDefault(c => c.BankRef == (objType, bank));

        // ── Name ──
        pane.SearchText = "ALPHA LEAD";
        Check("name-match-visible", Visible(alphaLoc));
        Check("name-mismatch-hidden", !Visible(cx3Loc));

        // ── Bank type: "searching I-A would start to show everything in the I-A banks" ──
        pane.SearchText = "I-A";
        Check("bank-I-A-alpha-visible", Visible(alphaLoc));
        Check("bank-I-A-cx3-visible", Visible(cx3Loc));
        Check("bank-I-B-piano-hidden", !Visible(pianoLoc));
        Check("bank-I-A-node-visible", BankNode(LibObj.Program, "Programs", 0x00)?.IsVisible == true);
        Check("bank-I-B-node-hidden", BankNode(LibObj.Program, "Programs", 0x01)?.IsVisible == false);

        // ── Engine type: "AL-1" matches the ENGINE a Program IS, not just a name substring ──
        pane.SearchText = "AL-1";
        Check("engine-match-visible", Visible(alphaLoc));
        Check("engine-mismatch-hidden", !Visible(cx3Loc));
        Check("hd1-program-has-no-engine-to-match", !Visible(pianoLoc));

        // ── Category ──
        pane.SearchText = "Guitar";
        Check("category-match-visible", Visible(alphaLoc));
        Check("category-mismatch-hidden", !Visible(cx3Loc));

        // ── Object Dependencies (one-hop): searching a Program's name also surfaces the Combi
        // that references it. BASSLINE lives in a distinct bank (I-C) specifically so its
        // resolved func33 target isn't the same zero default an untouched timbre already holds
        // (see DependencyResolutionSelfTests.BuildSyntheticPcg's identical precaution). ──
        pane.SearchText = "BASSLINE";
        Check("dependency-search-surfaces-referrer", Visible(combiLoc));
        Check("dependency-match-force-expands-ancestor", pane.Roots.FirstOrDefault(r => r.Label == "Combis")?.IsExpanded == true);

        // ── LeafLocs() only returns what's actually visible - "what you see is what you drag"
        // (ObjectTreeNode.LeafLocs feeds Cut/Copy/Move-to-Merge/BatchPlaceFromPcg for a bank/
        // type-root selection). ──
        pane.SearchText = "ALPHA LEAD";
        var programsRoot = pane.Roots.First(r => r.Label == "Programs");
        var visibleLocs = programsRoot.LeafLocs().ToList();
        Check("leaflocs-includes-match", visibleLocs.Contains(alphaLoc));
        Check("leaflocs-excludes-filtered-out", !visibleLocs.Contains(cx3Loc) && !visibleLocs.Contains(pianoLoc) && !visibleLocs.Contains(basslineLoc));

        // ── Clearing the query restores full visibility ──
        pane.SearchText = "";
        Check("clear-restores-all-visible", pane.Roots.SelectMany(AllNodes).All(n => n.IsVisible));

        // ── SetExpandedRecursive (LibrarianShellWindow.xaml.cs's Expand/Collapse Selected/All
        // context menu items, all three trees - PCG's own tree stands in for all of them, since
        // the method itself is pane-agnostic) ──
        var allNodes = pane.Roots.SelectMany(AllNodes).ToList();
        ObjectTreeNode.SetExpandedRecursive(pane.Roots, true);
        Check("expand-all-sets-every-node", allNodes.All(n => n.IsExpanded));
        ObjectTreeNode.SetExpandedRecursive(pane.Roots, false);
        Check("collapse-all-clears-every-node", allNodes.All(n => !n.IsExpanded));

        return fails;
    }

    static ObjectTreeNode? FindLeaf(IEnumerable<ObjectTreeNode> roots, ObjLoc loc) =>
        roots.SelectMany(AllNodes).FirstOrDefault(n => n.Loc.HasValue && n.Loc.Value.Equals(loc));

    static IEnumerable<ObjectTreeNode> AllNodes(ObjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in AllNodes(child)) yield return descendant;
    }

    static byte[] BuildFixture()
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810;
        // Mirror LibRefs's own (private) offsets - see LibRefs.ProgramEngineName and
        // ProgramBody.ReadCategory for the real, non-duplicated reads.
        const int ExiAlgorithmTypeOffset = 2857, CategoryOffset = 2568;

        static byte[] MakeProgram(string name, int engineByte, int category, int sub)
        {
            var body = new byte[programSize];
            Encoding.ASCII.GetBytes(name).CopyTo(body, 0);
            body[ExiAlgorithmTypeOffset] = (byte)engineByte;
            body[CategoryOffset] = (byte)((category & 0x1F) | ((sub & 0x07) << 5));
            return body;
        }

        var alpha    = MakeProgram("ALPHA LEAD", engineByte: 2 /*AL-1*/, category: 3, sub: 1);
        var cx3      = MakeProgram("ORGAN PATCH", engineByte: 3 /*CX-3*/, category: 5, sub: 0);
        var piano    = MakeProgram("PIANO SOUND", engineByte: 0, category: 0, sub: 0);   // HD-1 bank - engine byte irrelevant
        var bassline = MakeProgram("BASSLINE ONE", engineByte: 7 /*MOD-7*/, category: 0, sub: 0);

        int fbBassline = KronosBanks.ObjBankToFunc33(1, 0x02);   // -> BASSLINE's bank (I-C), never (0,0)
        var combiBody = new byte[combiSize];
        Encoding.ASCII.GetBytes("MY COMBI").CopyTo(combiBody, 0);
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combiBody, t, fbBassline, 0);

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void WriteBank(string tag, int count, int itemSize, int bankId, byte[] records)
        {
            WriteAscii(tag); WriteBE32(0); WriteBE32(0); WriteBE32(count); WriteBE32(itemSize); WriteBE32(bankId);
            ms.Write(records);
        }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        using var iA = new MemoryStream();
        iA.Write(alpha); iA.Write(cx3);
        WriteBank("MBK1", 2, programSize, 0x00, iA.ToArray());   // I-A: ALPHA (000), CX3 (001) - EXi
        WriteBank("PBK1", 1, programSize, 0x01, piano);          // I-B: PIANO (000) - HD-1
        WriteBank("MBK1", 1, programSize, 0x02, bassline);       // I-C: BASSLINE (000) - EXi

        WriteBank("CBK1", 1, combiSize, 0x00, combiBody);        // I-A combi bank: MY COMBI (000)

        return ms.ToArray();
    }
}
