namespace KronosScreenRemote;

// Off-hardware self-test for Phase 0 of the Librarian rebuild: the object-type
// registry and the shared raw-body decoders (Core/ObjectBody). Same convention as
// Librarian.SelfTest/BatchLibrarian.SelfTest — pure, synchronous, returns failing
// check names (empty = all passed). Invoked from App.xaml.cs's --librarian-selftest.
static class ObjectBodySelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── Program: category round-trip preserves every other byte ──
        var prog = new byte[3706];
        for (int i = 0; i < prog.Length; i++) prog[i] = (byte)((i * 11 + 1) & 0xFF);
        var progWithCat = ProgramBody.WriteCategory(prog, 5, 3);
        var (pCat, pSub) = ProgramBody.ReadCategory(progWithCat);
        Check("program-category-read", pCat == 5 && pSub == 3);
        var progExpectedTail = (byte[])prog.Clone();
        progExpectedTail[2568] = progWithCat[2568];
        Check("program-category-preserves-tail", progWithCat.AsSpan().SequenceEqual(progExpectedTail));

        var progNamed = ProgramBody.WriteName(prog, "TESTPROG");
        Check("program-name-roundtrip", ProgramBody.ReadName(progNamed) == "TESTPROG");

        // ── Combi: category round-trip preserves every other byte ──
        var combi = new byte[7810];
        for (int i = 0; i < combi.Length; i++) combi[i] = (byte)((i * 13 + 2) & 0xFF);
        var combiWithCat = CombiBody.WriteCategory(combi, 7, 1);
        var (cCat, cSub) = CombiBody.ReadCategory(combiWithCat);
        Check("combi-category-read", cCat == 7 && cSub == 1);
        var combiExpectedTail = (byte[])combi.Clone();
        combiExpectedTail[4790] = combiWithCat[4790];
        Check("combi-category-preserves-tail", combiWithCat.AsSpan().SequenceEqual(combiExpectedTail));

        var combiNamed = CombiBody.WriteName(combi, "TESTCOMBI");
        Check("combi-name-roundtrip", CombiBody.ReadName(combiNamed) == "TESTCOMBI");

        // ── SetListBody.FromRawBody: build a synthetic raw body, decode it directly,
        //    then confirm SetListData.FromObjectDump (via the refactored wire path)
        //    produces the byte-identical result — the shared-decoder regression pin. ──
        var slBody = new byte[69416];
        Array.Fill(slBody, (byte)0x20);   // blank-padded baseline (space, like real hardware)
        WriteAscii(slBody, 0, "TESTLIST");

        const int b0 = 24;
        WriteAscii(slBody, b0, "SLOT0");
        slBody[b0 + 24] = (byte)(1 | (5 << 2));   // type=1 (program), color=5
        slBody[b0 + 25] = 3;                       // bank
        slBody[b0 + 26] = 9;                       // index

        var viaBody = SetListBody.FromRawBody(2, slBody);
        Check("setlistbody-not-null", viaBody != null);
        if (viaBody != null)
        {
            Check("setlistbody-name", viaBody.Name == "TESTLIST");
            Check("setlistbody-slot0-name", viaBody.Slots[0].Name == "SLOT0");
            Check("setlistbody-slot0-type", viaBody.Slots[0].Type == 1);
            Check("setlistbody-slot0-bank", viaBody.Slots[0].Bank == 3);
            Check("setlistbody-slot0-index", viaBody.Slots[0].Index == 9);
            Check("setlistbody-slot0-color", viaBody.Slots[0].Color == 5);
        }

        var encoded = KronosSysEx.Encode7to8(slBody, 0, slBody.Length);
        var msg = new byte[10 + encoded.Length + 1];
        msg[0] = 0xF0; msg[1] = 0x42; msg[2] = 0x30; msg[3] = 0x68; msg[4] = 0x73; msg[5] = 0x0D;
        msg[6] = 0x00;                                     // bank
        msg[7] = (byte)((2 >> 7) & 0x7F);
        msg[8] = (byte)(2 & 0x7F);                         // number = 2
        msg[9] = 0;                                        // version
        Array.Copy(encoded, 0, msg, 10, encoded.Length);
        msg[^1] = 0xF7;

        var viaWire = SetListData.FromObjectDump(msg);
        Check("wire-matches-rawbody",
            viaWire != null && viaBody != null &&
            viaWire.Name == viaBody.Name &&
            viaWire.Slots.Count == viaBody.Slots.Count &&
            viaWire.Slots[0].Equals(viaBody.Slots[0]));

        // ── SetListBody mutators: bit-preserving color write, comments write ──
        var withColor = SetListBody.WriteSlotColor(slBody, 0, 9);
        var (t2, b2, i2) = LibRefs.SetListSlotRef(withColor, 0);
        Check("setlist-color-preserves-refs", t2 == 1 && b2 == 3 && i2 == 9);
        var afterColor = SetListBody.FromRawBody(2, withColor);
        Check("setlist-color-write", afterColor != null && afterColor.Slots[0].Color == 9);

        var withComments = SetListBody.WriteSlotComments(slBody, 0, "hello world");
        var afterComments = SetListBody.FromRawBody(2, withComments);
        Check("setlist-comments-write", afterComments != null && afterComments.Slots[0].Comments == "hello world");

        // ── Registry: bank enumeration matches the pre-existing hardcoded ranges ──
        var expectedProgramBanks = Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 14)).ToList();
        Check("registry-program-banks",
            ObjectTypeRegistry.Get(LibObj.Program).EditableBanks().SequenceEqual(expectedProgramBanks));
        var expectedCombiBanks = Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7)).ToList();
        Check("registry-combi-banks",
            ObjectTypeRegistry.Get(LibObj.Combi).EditableBanks().SequenceEqual(expectedCombiBanks));
        Check("registry-readonly-program",
            ObjectTypeRegistry.Get(LibObj.Program).IsReadOnlyBank(0x10) &&
            !ObjectTypeRegistry.Get(LibObj.Program).IsReadOnlyBank(0x00));
        Check("registry-all-three", ObjectTypeRegistry.All.Count() == 3);

        return fails;
    }

    static void WriteAscii(byte[] body, int offset, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, body, offset, Math.Min(bytes.Length, body.Length - offset));
    }
}
