namespace KronosScreenRemote;

// The Librarian's off-hardware checks - the pure half of Core/Librarian.cs (plan building,
// bank/reference math) exercised with hand-built bodies, no window and no instrument. Kept
// in its own partial so the planner file reads as production code only.
static partial class Librarian
{
    // ── Off-hardware self-test (invoked at DEBUG startup via App). Returns the list
    //    of failing check names; empty = all passed. ────────────────────────────────
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // 1. 8<->7 codec round-trip over a range of sizes.
        foreach (int n in new[] { 0, 1, 7, 8, 188, 3706, 7810, 69416 })
        {
            var body = new byte[n];
            for (int i = 0; i < n; i++) body[i] = (byte)((i * 37 + 5) & 0xFF);
            var rt = KronosSysEx.Decode8to7(KronosSysEx.Encode7to8(body, 0, body.Length), 0,
                                            KronosSysEx.Encode7to8(body, 0, body.Length).Length);
            Check($"codec-{n}", rt.AsSpan().SequenceEqual(body));
        }

        // 2. Bank-encoding inverse round-trips for every func33 index the forward map accepts.
        for (int idx = 0; idx < 32; idx++)
        {
            int ob = KronosBanks.Func33ToObjBank(1, idx);
            if (ob >= 0) Check($"prog-inv-{idx}", KronosBanks.ObjBankToFunc33(1, ob) == idx);
        }
        for (int idx = 0; idx < 14; idx++)
        {
            int ob = KronosBanks.Func33ToObjBank(0, idx);
            if (ob >= 0) Check($"combi-inv-{idx}", KronosBanks.ObjBankToFunc33(0, ob) == idx);
        }

        // 2b. Program has only SIX internal banks (I-A..I-F) in this encoding, not seven -
        // pinned against ground truth pulled directly from a real .pcg file's own Combi
        // timbre reference bytes (raw byte 28 -> U-EE, byte 26 -> U-CC; see KronosBanks.
        // Func33ToObjBank's own comment for the investigation this fixed). A regression back
        // to the old 7-internal-bank table would silently shift every GM/g/USER Program
        // reference one bank low again.
        Check("prog-no-int-g", KronosBanks.Func33ToObjBank(1, 6) == 0x10);            // idx 6 is GM, not "I-G"
        Check("prog-gm-boundary", KronosBanks.ObjBankToFunc33(1, 0x10) == 6);
        Check("prog-user-a-starts-at-17", KronosBanks.Func33ToObjBank(1, 17) == 0x40); // U-A
        Check("prog-real-byte-28-is-u-ee", KronosBanks.Func33ToObjBank(1, 28) == 0x4B); // U-EE
        Check("prog-real-byte-26-is-u-cc", KronosBanks.Func33ToObjBank(1, 26) == 0x49); // U-CC

        // 3. Combi timbre reference patch round-trip + timbre-15 offset.
        var combi = new byte[7810];
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combi, t, t % 30, (t * 3) & 0x7F);
        foreach (var (t, bank, num) in LibRefs.IterCombiTimbreRefs(combi))
            Check($"timbre-{t}", bank == t % 30 && num == ((t * 3) & 0x7F));

        // 3b. A short/truncated combi body (e.g. a glitched dump) must yield whatever fits,
        // not throw IndexOutOfRangeException - regression test for a real scan crash where
        // a full 128-slot bank sweep hit an unexpectedly short body.
        var shortCombi = new byte[5000];   // shorter than timbre 12's offset (4802 + 11*188 = 6870)
        var shortRefs = LibRefs.IterCombiTimbreRefs(shortCombi).ToList();
        Check("short-combi-no-throw", shortRefs.Count < LibRefs.TimbreCount);

        // 4. Set-list slot patch preserves color/transpose bits.
        var sl = new byte[69416];
        int b0 = 24;
        sl[b0 + 24] = 0b0011_1100;   // color bits, type=0
        sl[b0 + 25] = 0b1110_0000;   // transpose bits, bank=0
        LibRefs.SetSetListSlotRef(sl, 0, 19, 42, type: 1);
        var (st, sb, si) = LibRefs.SetListSlotRef(sl, 0);
        Check("sl-type", st == 1); Check("sl-bank", sb == 19); Check("sl-index", si == 42);
        Check("sl-color", (sl[b0 + 24] & 0b0011_1100) == 0b0011_1100);
        Check("sl-transpose", (sl[b0 + 25] & 0b1110_0000) == 0b1110_0000);

        // 5. Full plan: swap program I-A:007 <-> U-A:005 with a combi + set-list referrer.
        var cat = new LibraryCatalog();
        int fbSrc = KronosBanks.ObjBankToFunc33(1, 0x00);   // 0
        int fbDst = KronosBanks.ObjBankToFunc33(1, 0x40);   // 17
        var src = new ObjLoc(LibObj.Program, 0x00, 7);
        var dst = new ObjLoc(LibObj.Program, 0x40, 5);
        var cbody = new byte[7810];
        LibRefs.SetCombiTimbreRef(cbody, 3, fbSrc, 7);   // -> src
        LibRefs.SetCombiTimbreRef(cbody, 5, fbDst, 5);   // -> dst
        cat.AddCombi(new ObjectDump(LibObj.Combi, 0x00, 0, 3, cbody));
        var slbody = new byte[69416];
        LibRefs.SetSetListSlotRef(slbody, 2, fbSrc, 7, type: 1);   // program slot -> src
        cat.AddSetlist(new ObjectDump(LibObj.SetList, 0, 0, 0, slbody));

        Check("usage-src", cat.ReferrersOf(src).Count == 2);
        Check("usage-dst", cat.ReferrersOf(dst).Count == 1);

        // 5b. Set List slots address Programs/Combis only. A Drum Kit or Wave Sequence sharing
        // a referenced Combi's bank/number used to come back as a referrer of that slot (the
        // scan fell to refType 0 = combi), which PlanMove would then PATCH - silently
        // repointing an unrelated Set List on a Drum Kit swap. The Combi itself must still
        // resolve through the same scan.
        var catSl = new LibraryCatalog();
        var slCombiBody = new byte[69416];
        LibRefs.SetSetListSlotRef(slCombiBody, 4, KronosBanks.ObjBankToFunc33(0, 0x00), 5, type: 0);
        catSl.AddSetlist(new ObjectDump(LibObj.SetList, 0, 0, 0, slCombiBody));
        Check("setlist-no-drumkit-referrer", catSl.ReferrersOf(new ObjLoc(LibObj.DrumKit, 0x00, 5)).Count == 0);
        Check("setlist-no-waveseq-referrer", catSl.ReferrersOf(new ObjLoc(LibObj.WaveSequence, 0x00, 5)).Count == 0);
        Check("setlist-combi-referrer-kept", catSl.ReferrersOf(new ObjLoc(LibObj.Combi, 0x00, 5)).Count == 1);

        // 5c. BuildReferrerIndex (PlanBatchMove's one-pass lookup) decodes each site's target,
        // where ReferrersOf encodes the wanted one - opposite directions over the same tables, so
        // the two are pinned against each other here. A probe set spanning every referrer kind
        // plus locs nothing points at, over both fixtures.
        var probes = new List<ObjLoc>
        {
            src, dst,
            new(LibObj.Program, 0x00, 0), new(LibObj.Program, 0x40, 9), new(LibObj.Program, 0x10, 0),
            new(LibObj.Combi, 0x00, 0), new(LibObj.Combi, 0x00, 5), new(LibObj.Combi, 0x40, 3),
            new(LibObj.DrumKit, 0x00, 5), new(LibObj.DrumKit, 0x40, 0),
            new(LibObj.WaveSequence, 0x00, 5), new(LibObj.WaveSequence, 0x00, 0),
            new(LibObj.SetList, 0, 0),
        };
        foreach (var (name, probeCat) in new[] { ("plan", cat), ("setlist", catSl) })
        {
            var idx = probeCat.BuildReferrerIndex();
            foreach (var probe in probes)
            {
                var scanned = probeCat.ReferrersOf(probe);
                var indexed = idx.TryGetValue(probe, out var hit) ? hit : new List<ReferrerSite>();
                Check($"referrer-index-agrees-with-scan-{name}-{probe.Label()}",
                    scanned.Count == indexed.Count &&
                    scanned.All(s => indexed.Any(i => i.Kind == s.Kind && i.RefObj == s.RefObj &&
                        i.RefBank == s.RefBank && i.RefIndex == s.RefIndex && i.Site == s.Site)));
            }
        }

        var srcDump = new ObjectDump(LibObj.Program, 0x00, 7, 5, new byte[100]);
        var dstDump = new ObjectDump(LibObj.Program, 0x40, 5, 5, new byte[100]);
        var plan = Librarian.PlanMove(cat, src, srcDump, dst, dstDump);
        Check("not-refusable", !plan.IsRefusable);
        Check("referrers", plan.Referrers.Count == 3);
        Check("write-count", plan.Writes.Count == 4);
        Check("preimage-count", plan.PreImages.Count == 4);
        Check("store-count", plan.Stores.Count == 4);

        var combiWrite = plan.Writes.First(w => w.Obj == LibObj.Combi);
        var (b3, n3) = LibRefs.CombiTimbreRef(combiWrite.Body, 3);
        var (b5, n5) = LibRefs.CombiTimbreRef(combiWrite.Body, 5);
        Check("t3-retarget", b3 == fbDst && n3 == 5);
        Check("t5-retarget", b5 == fbSrc && n5 == 7);
        var slWrite = plan.Writes.First(w => w.Obj == LibObj.SetList);
        var (wt, wb, wi) = LibRefs.SetListSlotRef(slWrite.Body, 2);
        Check("sl-retarget", wt == 1 && wb == fbDst && wi == 5);

        var bad = Librarian.PlanMove(cat, src, srcDump, new ObjLoc(LibObj.Program, 0x10, 0),
                                     new ObjectDump(LibObj.Program, 0x10, 0, 5, Array.Empty<byte>()));
        Check("refuse-readonly", bad.IsRefusable);

        // 7. Name-field helpers: rename touches only the first 24 bytes, PadAscii
        // truncates/pads correctly (migrated from the retired StoreBankVerification tool).
        var nameOriginal = new byte[200];
        for (int i = 0; i < nameOriginal.Length; i++) nameOriginal[i] = (byte)((i * 7 + 3) & 0x7F);
        var renamed = BuildRenamedBody(nameOriginal, "STORETEST-000000");
        Check("rename-preserves-tail", renamed.AsSpan(24).SequenceEqual(nameOriginal.AsSpan(24)));
        Check("rename-name-readable", ReadName(renamed) == "STORETEST-000000");
        Check("rename-same-length", renamed.Length == nameOriginal.Length);
        Check("padascii-truncate", PadAscii("THIS NAME IS DEFINITELY TOO LONG", 8).Length == 8);
        Check("padascii-pad", PadAscii("AB", 4).AsSpan().SequenceEqual(new byte[] { 0x41, 0x42, 0x20, 0x20 }));

        // 8. Object-version constants (Documentation/MIDI implementation/SysExDumps/*.txt,
        // each headed "Object Version: N") - the fix for the Reply-3 "mangled message"
        // Program write bug: PCG-imported entries used to default this to a placeholder 0,
        // wrong for Program/Combi (only coincidentally right for Set List).
        Check("objver-program", LibObj.CurrentObjectVersion(LibObj.Program) == 5);
        Check("objver-combi", LibObj.CurrentObjectVersion(LibObj.Combi) == 3);
        Check("objver-setlist", LibObj.CurrentObjectVersion(LibObj.SetList) == 0);
        Check("objver-unknown-null", LibObj.CurrentObjectVersion(0x13) == null);

        return fails;
    }
}
