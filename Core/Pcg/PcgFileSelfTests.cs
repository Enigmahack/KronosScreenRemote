namespace KronosScreenRemote;

using System.IO;
using System.Text;

// Off-hardware self-test for Phase 4: PcgFile/PcgObjectExtractor/PcgLibraryView. Builds a
// synthetic minimal .pcg buffer in-memory (no sample .pcg file ships in this repo) and
// asserts extraction correctness against it, plus the shared-decoder proof (a PCG-sliced
// Set List body decodes identically to an equivalent live-dump-shaped wire message).
//
// IMPORTANT: this proves internal self-consistency of THIS parser's assumed header
// layout — it does not, and cannot, prove that layout matches a real Kronos-exported .pcg
// file. That still needs a real file (see the plan's Phase 4 manual verification step).
static class PcgFileSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var buffer = BuildSyntheticPcg(out var programName, out var combiName, out var setListComment);

        var file = PcgFile.Open(buffer);
        Check("opens-valid-file", file != null);
        if (file == null) return fails;

        var view = new PcgLibraryView(file);

        var progLoc = new ObjLoc(LibObj.Program, 0x00, 0);   // bankId 0 -> I-A
        Check("program-found", view.GetName(progLoc) == programName);

        var combiLoc = new ObjLoc(LibObj.Combi, 0x00, 0);
        Check("combi-found", view.GetName(combiLoc) == combiName);

        var slLoc = new ObjLoc(LibObj.SetList, 0, 0);
        var slBody = view.GetBody(slLoc);
        Check("setlist-found", slBody != null);
        var decoded = slBody != null ? SetListBody.FromRawBody(0, slBody) : null;
        Check("setlist-slot0-comment", decoded != null && decoded.Slots[0].Comments == setListComment);

        // Shared-decoder proof: decoding the PCG-sliced Set List body directly must match
        // decoding an equivalent live-dump-shaped wire message wrapping the SAME raw bytes
        // — the concrete evidence for design point (d)'s "one decoder, two ingestion paths."
        if (slBody != null && decoded != null)
        {
            var encoded = KronosSysEx.Encode7to8(slBody, 0, slBody.Length);
            var msg = new byte[10 + encoded.Length + 1];
            msg[0] = 0xF0; msg[1] = 0x42; msg[2] = 0x30; msg[3] = 0x68; msg[4] = 0x73; msg[5] = 0x0D;
            msg[6] = 0x00; msg[7] = 0; msg[8] = 0; msg[9] = 0;
            Array.Copy(encoded, 0, msg, 10, encoded.Length);
            msg[^1] = 0xF7;
            var viaWire = SetListData.FromObjectDump(msg);
            Check("pcg-body-matches-wire-decode", viaWire != null &&
                viaWire.Name == decoded.Name && viaWire.Slots[0].Equals(decoded.Slots[0]));
        }

        // A stray tag string with no valid count/item-size following (e.g. a coincidental
        // 4-byte match inside unrelated parameter data) must be skipped, not mis-extracted
        // or crash the scan.
        var garbageTag = Encoding.ASCII.GetBytes("MBK1");
        var garbageBlock = new byte[16];
        garbageTag.CopyTo(garbageBlock, 0);   // tag followed by all-zero header fields -> count=0, fails validation
        var withGarbage = new byte[16 + garbageBlock.Length + (buffer.Length - 16)];
        Array.Copy(buffer, 0, withGarbage, 0, 16);
        Array.Copy(garbageBlock, 0, withGarbage, 16, garbageBlock.Length);
        Array.Copy(buffer, 16, withGarbage, 16 + garbageBlock.Length, buffer.Length - 16);

        var fileWithGarbage = PcgFile.Open(withGarbage);
        Check("garbage-tag-does-not-crash", fileWithGarbage != null);
        if (fileWithGarbage != null)
        {
            var viewG = new PcgLibraryView(fileWithGarbage);
            Check("garbage-does-not-corrupt-real-extraction", viewG.GetName(progLoc) == programName);
            // The rejection diagnostic (surfaced by PcgPaneViewModel.Load) must actually see
            // this — a bank silently missing from the tree should never be invisible again.
            Check("garbage-tag-tracked-as-rejected",
                fileWithGarbage.RejectedBanks.Any(r => r.Tag == "MBK1" && r.Reason.Contains("count")));
        }

        // Not a valid PCG file at all (bad magic) -> Open returns null, not an exception.
        Check("rejects-non-pcg", PcgFile.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) == null);

        // Real-file bank-id encoding (confirmed against an actual factory PRELOAD.PCG, and —
        // critically — against a real user file with confirmed U-GG content; see
        // PcgObjectExtractor's class comment). Program: literal 0..4 for I-A..I-E, a
        // dedicated 0x8000 flag for I-F, then 0x20000+N (N=0..13) directly for U-A..U-GG —
        // NO "I-G" slot (Program has only 6 int banks, unlike Combi's 7). This pins the exact
        // regression that silently dropped U-GG: an earlier version routed Program through
        // the 7-int-bank EditableBanks() list, shifting every user bank down by one so
        // bankId 0x2000D (real U-GG data) decoded as U-FF instead, and N=14 (what WOULD have
        // been needed for U-GG under that wrong model) correctly doesn't exist at all.
        var bankIdBuffer = BuildBankIdEncodedPcg();
        var bankIdFile = PcgFile.Open(bankIdBuffer);
        Check("bankid-file-opens", bankIdFile != null);
        if (bankIdFile != null)
        {
            var bankIdView = new PcgLibraryView(bankIdFile);
            Check("bankid-program-I-A", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x00, 0)) == "I-A PROG");
            Check("bankid-program-I-E", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x04, 0)) == "I-E PROG");
            Check("bankid-program-I-F-via-0x8000-flag", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x05, 0)) == "I-F PROG");
            Check("bankid-program-no-I-G-slot", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x06, 0)) == null);
            Check("bankid-program-U-A-via-0x20000", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x40, 0)) == "U-A PROG");
            Check("bankid-program-U-G-via-0x20006", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x46, 0)) == "U-G PROG");
            Check("bankid-program-U-AA-via-0x20007", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x47, 0)) == "U-AA PROG");
            Check("bankid-program-U-GG-via-0x2000D", bankIdView.GetName(new ObjLoc(LibObj.Program, 0x4D, 0)) == "U-GG PROG");
            // 0x2000E (N=14) is out of range -- must be rejected, not silently mapped somewhere.
            Check("bankid-program-N14-rejected", bankIdFile.RejectedBanks.Any(r => r.BankIdRaw == 0x2000E));

            Check("bankid-combi-I-G", bankIdView.GetName(new ObjLoc(LibObj.Combi, 0x06, 0)) == "I-G COMBI");
            Check("bankid-combi-U-A-via-0x20000", bankIdView.GetName(new ObjLoc(LibObj.Combi, 0x40, 0)) == "U-A COMBI");
            Check("bankid-combi-U-G-via-0x20006", bankIdView.GetName(new ObjLoc(LibObj.Combi, 0x46, 0)) == "U-G COMBI");
        }

        return fails;
    }

    static byte[] BuildBankIdEncodedPcg()
    {
        const int programSize = 64, combiSize = 64;

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void WriteBank(string tag, int itemSize, int bankId, string name)
        {
            var record = new byte[itemSize];
            Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
            WriteAscii(tag);
            WriteBE32(0); WriteBE32(0);
            WriteBE32(1);          // count = 1 record per bank, enough to prove bank assignment
            WriteBE32(itemSize);
            WriteBE32(bankId);
            ms.Write(record);
        }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        WriteBank("MBK1", programSize, 0, "I-A PROG");
        WriteBank("MBK1", programSize, 4, "I-E PROG");
        WriteBank("MBK1", programSize, 0x8000, "I-F PROG");
        WriteBank("PBK1", programSize, 0x20000, "U-A PROG");
        WriteBank("PBK1", programSize, 0x20006, "U-G PROG");
        WriteBank("PBK1", programSize, 0x20007, "U-AA PROG");
        WriteBank("PBK1", programSize, 0x2000D, "U-GG PROG");
        WriteBank("MBK1", programSize, 0x2000E, "OUT OF RANGE PROG");   // N=14 -- must be rejected
        WriteBank("CBK1", combiSize, 6, "I-G COMBI");
        WriteBank("CBK1", combiSize, 0x20000, "U-A COMBI");
        WriteBank("CBK1", combiSize, 0x20006, "U-G COMBI");

        return ms.ToArray();
    }

    static byte[] BuildSyntheticPcg(out string programName, out string combiName, out string setListComment)
    {
        programName = "SYNTH PROGRAM";
        combiName = "SYNTH COMBI";
        setListComment = "synthetic slot comment";

        // Real .pcg Program slots are always 4960 bytes on disk (the wire dump can be
        // smaller for HD-1 — see ProgramFormatConverter — but that's a placement-time
        // conversion, not how the file itself stores the record).
        const int programSize = ProgramFormatConverter.PcgSlotSize, combiSize = 7810, setListSize = 69416;

        var programBody = new byte[programSize];
        Encoding.ASCII.GetBytes(programName).CopyTo(programBody, 0);

        var combiBody = new byte[combiSize];
        Encoding.ASCII.GetBytes(combiName).CopyTo(combiBody, 0);

        var setListBody = SetListBody.WriteSlotComments(new byte[setListSize], 0, setListComment);

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void WriteBank(string tag, int count, int itemSize, int bankId, byte[] record)
        {
            WriteAscii(tag);
            WriteBE32(0);           // chunk length — not read by the extractor
            WriteBE32(0);           // reserved/meta
            WriteBE32(count);
            WriteBE32(itemSize);
            WriteBE32(bankId);
            ms.Write(record);
        }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);   // checksum flag + reserved, pads the intro to 16 bytes

        WriteBank("MBK1", 1, programSize, 0, programBody);
        WriteBank("CBK1", 1, combiSize, 0, combiBody);
        WriteBank("SBK1", 1, setListSize, 0, setListBody);

        return ms.ToArray();
    }
}
