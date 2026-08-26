namespace KronosScreenRemote;

// Off-hardware self-test for the object-type registry and the shared raw-body decoders
// (Core/ObjectBody). Same convention as Librarian.SelfTest/BatchLibrarian.SelfTest - pure,
// synchronous, returns failing check names (empty = all passed). Invoked from App.xaml.cs's
// --librarian-selftest.
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

        // ── INIT-Program detection (requirement 5) ──
        // Drives the placement gate that lets an INIT slot be overwritten without Force Overwrite
        // (BatchLibrarian.PlanBatchMove's orphan gate), so it must accept both the instrument's own
        // spellings and this app's erase output, and reject anything that merely mentions "init".
        Check("isinit-hd1", ProgramBody.IsInit(ProgramBody.WriteName(prog, "Init Program")));
        Check("isinit-exi", ProgramBody.IsInit(ProgramBody.WriteName(prog, "Init EXi Program")));
        Check("isinit-erasebody-spelling", ProgramBody.IsInit(ProgramBody.WriteName(prog, "INIT PROGRAM")));
        Check("isinit-padded", ProgramBody.IsInit(ProgramBody.WriteName(prog, "  Init Program  ")));
        Check("isinit-rejects-real-patch", !ProgramBody.IsInit(ProgramBody.WriteName(prog, "Grand Piano")));
        Check("isinit-rejects-partial-match", !ProgramBody.IsInit(ProgramBody.WriteName(prog, "Initial Attack")));

        // ── Global: Category / Sub-Category names (requirement 4) ──
        // Offsets come from Documentation/MIDI implementation/SysExDumps/Global.txt's offset
        // column; this pins them (and the Program-vs-Combi separation) against a synthetic body
        // written at exactly those addresses. A real Global dump is ~24 KB.
        var global = new byte[GlobalBody.MinimumBodyLength];
        void WriteField(int offset, string text)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text.PadRight(24).Substring(0, 24));
            bytes.CopyTo(global, offset);
        }
        WriteField(12912, "Keyboard");                        // Program category 00
        WriteField(12912 + 5 * 24, "Guitar/Plucked");         // Program category 05
        WriteField(13344 + (5 * 8 + 2) * 24, "Acoustic");     // Program category 05, sub 02
        WriteField(16800 + 3 * 24, "Combi Keys");             // Combi category 03
        WriteField(17232 + (3 * 8 + 1) * 24, "Layered");      // Combi category 03, sub 01

        var names = GlobalBody.ReadCategoryNames(global);
        Check("global-names-decoded", names != null);
        Check("global-program-category", names?.CategoryLabel(LibObj.Program, 5) == "Guitar/Plucked");
        Check("global-program-subcategory", names?.SubCategoryLabel(LibObj.Program, 5, 2) == "Acoustic");
        Check("global-combi-category", names?.CategoryLabel(LibObj.Combi, 3) == "Combi Keys");
        Check("global-combi-subcategory", names?.SubCategoryLabel(LibObj.Combi, 3, 1) == "Layered");
        // Program and Combi tables are independent - the same index must not bleed across.
        Check("global-program-combi-independent", names?.CategoryLabel(LibObj.Combi, 5) != "Guitar/Plucked");
        // An unnamed category falls back to its numeric label rather than showing an empty row.
        Check("global-unnamed-falls-back", names?.CategoryLabel(LibObj.Program, 17) == "Category 17");
        // Out-of-range indexes are labelled, never thrown on.
        Check("global-out-of-range-category", names?.CategoryLabel(LibObj.Program, 99) == "Category 99");
        Check("global-out-of-range-sub", names?.SubCategoryLabel(LibObj.Program, 5, 99) == "Sub 99");
        // A truncated/rejected reply must decode to null so the caller keeps its existing labels.
        Check("global-short-body-null", GlobalBody.ReadCategoryNames(new byte[1000]) == null);
        // The always-available fallback has the same shape as a real decode.
        var numeric = CategoryNames.Numeric();
        Check("global-numeric-fallback", numeric.CategoryLabel(LibObj.Program, 5) == "Category 05" &&
            numeric.SubCategoryLabel(LibObj.Combi, 5, 2) == "Sub 02");

        // ── SetListBody.FromRawBody: build a synthetic raw body, decode it directly,
        //    then confirm SetListData.FromObjectDump (via the refactored wire path)
        //    produces the byte-identical result - the shared-decoder regression pin. ──
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

        // ── EraseBody (requirement 2): the blank/INIT body written to a slot on a committed
        //    delete. Derived from the existing body, so wire length/format is always preserved. ──
        var slForErase = new byte[69416];
        Array.Fill(slForErase, (byte)0x20);
        WriteAscii(slForErase, 0, "TO DELETE");
        slForErase = SetListBody.WriteSlotName(slForErase, 0, "SLOT ZERO");
        slForErase = SetListBody.WriteSlotName(slForErase, 7, "SLOT SEVEN");
        var slErased = EraseBody.Build(LibObj.SetList, slForErase);
        Check("erase-setlist-length-preserved", slErased.Length == slForErase.Length);
        Check("erase-setlist-is-empty", SetListBody.FromRawBody(0, slErased)?.IsEmpty ?? false);

        var progForErase = ProgramBody.WriteCategory(ProgramBody.WriteName(new byte[3706], "MY SOUND"), 5, 2);
        var progErased = EraseBody.Build(LibObj.Program, progForErase);
        Check("erase-program-length-preserved", progErased.Length == progForErase.Length);
        Check("erase-program-name-init", ProgramBody.ReadName(progErased) == "INIT PROGRAM");
        Check("erase-program-category-cleared", ProgramBody.ReadCategory(progErased) == (0, 0));

        var combiForErase = CombiBody.WriteName(new byte[7810], "MY COMBI");
        LibRefs.SetCombiTimbreRef(combiForErase, 0, 5, 42);
        var combiErased = EraseBody.Build(LibObj.Combi, combiForErase);
        Check("erase-combi-length-preserved", combiErased.Length == combiForErase.Length);
        Check("erase-combi-name-init", CombiBody.ReadName(combiErased) == "INIT COMBI");
        var (etBank, etNum) = LibRefs.CombiTimbreRef(combiErased, 0);
        Check("erase-combi-timbre-cleared", etBank == 0 && etNum == 0);

        // ── Registry: bank enumeration matches the pre-existing hardcoded ranges ──
        // SIX internal Program banks (I-A..I-F), not seven - object-dump bank 0x06 is not a real
        // Program bank; see ProgramDescriptor.EditableBanks. Combi below genuinely has seven.
        var expectedProgramBanks = Enumerable.Range(0x00, 6).Concat(Enumerable.Range(0x40, 14)).ToList();
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
