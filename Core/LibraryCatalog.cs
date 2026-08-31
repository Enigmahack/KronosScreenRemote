namespace KronosScreenRemote;

// Full-body catalog of the referrer objects a specific move touches (re-dumped
// fresh at plan time). PlanMove re-derives the exact sites from these bodies.
sealed class LibraryCatalog
{
    public readonly Dictionary<(int Bank, int Index), ObjectDump> Combis = new();
    public readonly Dictionary<int, ObjectDump> Setlists = new();
    public readonly Dictionary<(int Bank, int Index), ObjectDump> Programs = new();

    public void AddCombi(ObjectDump d) { if (d.Obj == LibObj.Combi) Combis[(d.Bank, d.Index)] = d; }
    public void AddSetlist(ObjectDump d) { if (d.Obj == LibObj.SetList) Setlists[d.Index] = d; }
    public void AddProgram(ObjectDump d) { if (d.Obj == LibObj.Program) Programs[(d.Bank, d.Index)] = d; }

    public List<ReferrerSite> ReferrersOf(ObjLoc loc)
    {
        var outp = new List<ReferrerSite>();
        if (loc.ObjType == LibObj.SetList) return outp;   // nothing ever references a Set List

        if (loc.ObjType == LibObj.Program)
        {
            int wantBank = KronosBanks.ObjBankToFunc33(1, loc.Bank);
            if (wantBank >= 0)
            {
                foreach (var ((bank, index), dump) in Combis)
                    foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(dump.Body))
                        if (fbank == wantBank && num == loc.Number)
                            outp.Add(new ReferrerSite(RefKind.CombiTimbre, LibObj.Combi, bank, index, t, fbank, num));

                foreach (var ((bank, index), dump) in Programs)
                {
                    if (dump.Body.Length <= LibRefs.ProgramDrumTrackBank || !LibRefs.ProgramDrumTrackOn(dump.Body)) continue;
                    var (dtBank, dtNum) = LibRefs.ProgramDrumTrackRef(dump.Body);
                    if (dtBank == wantBank && dtNum == loc.Number)
                        outp.Add(new ReferrerSite(RefKind.DrumTrack, LibObj.Program, bank, index, -1, dtBank, dtNum));
                }
            }
        }
        else if (loc.ObjType is LibObj.DrumKit or LibObj.WaveSequence)
        {
            int? wantLinear = loc.ObjType == LibObj.DrumKit
                ? KronosBanks.DrumKitLocToLinear(loc.Bank, loc.Number)
                : KronosBanks.WaveSeqLocToLinear(loc.Bank, loc.Number);
            if (wantLinear is { } lin)
                foreach (var ((bank, index), dump) in Programs)
                {
                    // HD-1 wire format only - see LibRefs.IterProgramZoneRefs.
                    if (dump.Body.Length != ProgramFormatConverter.WireSizeHd1) continue;
                    int oscMode = LibRefs.ProgramOscillatorMode(dump.Body);
                    foreach (var (osc, zone, msType, number) in LibRefs.IterProgramZoneRefs(dump.Body))
                    {
                        bool match = loc.ObjType == LibObj.WaveSequence
                            ? msType == 2 && number == lin
                            : msType == 1 && oscMode is 4 or 5 && number == lin;
                        if (match)
                            outp.Add(new ReferrerSite(RefKind.OscZone, LibObj.Program, bank, index,
                                osc * LibRefs.ZonesPerOsc + zone, 0, number));
                    }
                }
        }

        // A Set List slot can only ever address a type that HAS a func-33 selector. Gating on
        // that is not tidiness: without it a Drum Kit / Wave Sequence loc falls to refType 0
        // (combi) and matches every slot pointing at the COMBI that happens to share its
        // bank/number - and PlanMove/PlanBatchMove then patch those slots, silently repointing
        // an unrelated Set List when a Drum Kit is swapped. Combi has no type-specific branch
        // above and reaches this scan by falling through, so this must stay a positive gate,
        // not an early return after the branches.
        if (ObjectTypeRegistry.Func33RefType(loc.ObjType) is { } refType)
        {
            int wantSlBank = KronosBanks.ObjBankToFunc33(refType, loc.Bank);
            if (wantSlBank >= 0)
                foreach (var (number, dump) in Setlists)
                    foreach (var (s, type, fbank, idx) in LibRefs.IterSetListSlotRefs(dump.Body))
                        if (type == refType && fbank == wantSlBank && idx == loc.Number)
                            outp.Add(new ReferrerSite(RefKind.SetListSlot, LibObj.SetList, 0, number, s, fbank, idx));
        }
        return outp;
    }

    // Every referrer site in the catalog at once, keyed by the object it points AT - the same
    // answer ReferrersOf gives, for every loc in one pass. PlanBatchMove asks about N targets
    // plus N sources, and each ReferrersOf call re-sweeps every Combi (x16 timbres), every
    // Program (drum track + oscillator zones) and every Set List (x128 slots), so an N-item
    // batch cost N x O(catalog). Handed back to the caller rather than cached on the catalog
    // on purpose: this instance is memoized and patched in place (LocalLibraryCache.
    // PatchCatalog), so a stored index would inherit an invalidation obligation that a
    // per-plan index simply doesn't have. Reads the reference encodings in the opposite
    // direction to ReferrersOf (decoding each site's target rather than encoding the wanted
    // one), so the two must stay in lockstep - "referrer-index-agrees-with-scan" pins that.
    public Dictionary<ObjLoc, List<ReferrerSite>> BuildReferrerIndex()
    {
        var byTarget = new Dictionary<ObjLoc, List<ReferrerSite>>();
        void Add(ObjLoc target, ReferrerSite site)
        {
            if (!byTarget.TryGetValue(target, out var list)) byTarget[target] = list = new();
            list.Add(site);
        }

        foreach (var ((bank, idx), dump) in Combis)
            foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(dump.Body))
            {
                int objBank = KronosBanks.Func33ToObjBank(1, fbank);
                if (objBank >= 0)
                    Add(new ObjLoc(LibObj.Program, objBank, num),
                        new ReferrerSite(RefKind.CombiTimbre, LibObj.Combi, bank, idx, t, fbank, num));
            }

        foreach (var ((bank, idx), dump) in Programs)
        {
            if (dump.Body.Length > LibRefs.ProgramDrumTrackBank && LibRefs.ProgramDrumTrackOn(dump.Body))
            {
                var (dtBank, dtNum) = LibRefs.ProgramDrumTrackRef(dump.Body);
                int objBank = KronosBanks.Func33ToObjBank(1, dtBank);
                if (objBank >= 0)
                    Add(new ObjLoc(LibObj.Program, objBank, dtNum),
                        new ReferrerSite(RefKind.DrumTrack, LibObj.Program, bank, idx, -1, dtBank, dtNum));
            }

            // HD-1 wire format only - see LibRefs.IterProgramZoneRefs.
            if (dump.Body.Length != ProgramFormatConverter.WireSizeHd1) continue;
            int oscMode = LibRefs.ProgramOscillatorMode(dump.Body);
            foreach (var (osc, zone, msType, number) in LibRefs.IterProgramZoneRefs(dump.Body))
            {
                var target = msType switch
                {
                    2 => KronosBanks.WaveSeqLinearToLoc(number),
                    1 when oscMode is 4 or 5 => KronosBanks.DrumKitLinearToLoc(number),
                    _ => null,
                };
                if (target is { } tl)
                    Add(new ObjLoc(msType == 2 ? LibObj.WaveSequence : LibObj.DrumKit, tl.Bank, tl.Slot),
                        new ReferrerSite(RefKind.OscZone, LibObj.Program, bank, idx,
                            osc * LibRefs.ZonesPerOsc + zone, 0, number));
            }
        }

        foreach (var (number, dump) in Setlists)
            foreach (var (s, type, fbank, idx) in LibRefs.IterSetListSlotRefs(dump.Body))
            {
                // Slot type 2 is a Song - no Librarian object behind it, and no descriptor
                // claims that selector, so it drops out here exactly as ReferrersOf's gate
                // drops a type with no selector.
                if (ObjectTypeRegistry.ObjTypeForFunc33RefType(type) is not { } slotObjType) continue;
                int objBank = KronosBanks.Func33ToObjBank(type, fbank);
                if (objBank >= 0)
                    Add(new ObjLoc(slotObjType, objBank, idx),
                        new ReferrerSite(RefKind.SetListSlot, LibObj.SetList, 0, number, s, fbank, idx));
            }

        return byTarget;
    }
}
