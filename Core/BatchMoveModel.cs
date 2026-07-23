namespace KronosScreenRemote;

// Batch move: relocate MANY Programs or Combis (never mixed) into sequential slots of one
// destination bank, with a "clipboard" for objects displaced or that can't land there at all.
// Generalizes Librarian.PlanMove's pairwise swap into an arbitrary N-item reassignment —
// see PlanBatchMove's doc comment for why this can't be N independent PlanMove calls.
//
// Locked semantics (confirmed with the user, do not re-derive):
//   - Sequential fill always starts at destination-bank slot 0, no skipping.
//   - A destination slot's current occupant is overwritten by default; a caller-supplied
//     `divertDisplacedToClipboard` flag instead cuts it into the clipboard.
//   - Capacity overflow (more sources than 128 slots) and (Programs only) a source/destination
//     bank-type mismatch ALWAYS auto-clipboard, regardless of that flag — there's no valid
//     destination at all in either case.
//   - A placement is a REFERENCE RELOCATION, not a swap: the source's own physical slot is
//     NEVER written. Only referrers (Combi timbres / Set List slots) that pointed at the
//     source's OLD location get repointed to the NEW one. This is the only reading consistent
//     with references needing to move at all — a hidden swap-back was never asked for and
//     would silently duplicate/destroy data outside what was requested.
//
// Split for testability like LibrarianModel.cs: ResolveSequentialFill and PlanBatchMove are
// PURE (BatchLibrarian.SelfTest exercises them off-hardware); the caller (LibrarianWindow) does
// the actual dumping/writing through the same ISysExService/IMoveExecutor primitives PlanMove
// uses, via the shared IExecutablePlan apply path (Librarian.ArmPlanAsync/ApplyMoveAsync).

enum ClipboardProvenance { DisplacedDestination, UnplaceableSource, UserCopy }

// One clipboard entry: cut on add, kept forever as history (never removed on paste — just
// marked). `Origin` is where the object WAS *at the moment it was cut* — for a
// DisplacedDestination entry that address has already been overwritten by the incoming
// placement, so it must never be used for a fresh referrer lookup (whatever now occupies it is
// an unrelated object). This invariant is exactly why the clipboard can never hold a
// reference-integrity hazard: a DisplacedDestination entry is only ever created after
// PlanBatchMove's orphan gate has confirmed it has zero live referrers, and an
// UnplaceableSource or UserCopy entry's own original location was never touched
// (source-untouched rule), so its referrers — if any — still resolve correctly right where
// they always did (see NeedsOriginRepoint).
sealed class ClipboardEntry
{
    public int ObjType;
    public ObjLoc Origin;
    public byte Version;
    public byte[] Body = Array.Empty<byte>();
    public ClipboardProvenance Provenance;
    public string Reason = "";
    public DateTime CutAt;
    public ObjLoc? PastedTo;
    public DateTime? PastedAt;
    public bool Pending => PastedTo is null;

    // Ties every entry created by ONE "Copy Bank to Clipboard" action together (null for
    // individual/multi Copy-to-Clipboard entries). Paste Bank finds the most-recently-cut pending
    // group sharing this value and stages the whole group in one action, preserving each entry's
    // original slot-within-bank offset — see BatchLibrarian's Paste Bank notes.
    public Guid? BankCopyGroup;
}

sealed class BatchClipboard
{
    public List<ClipboardEntry> Entries = new();
    public IEnumerable<ClipboardEntry> Pending => Entries.Where(e => e.Pending);
}

static class ClipboardProvenanceExtensions
{
    // Every provenance except DisplacedDestination leaves the entry's Origin untouched (never
    // written), so a paste of one of those must repoint Origin's live referrers to the paste
    // destination — a DisplacedDestination entry never needs this (orphan-gate guaranteed
    // referrer-free before it could ever become a clipboard entry). Expressed as "!= Displaced"
    // rather than an explicit OR-list so a future provenance defaults to the safer behavior.
    public static bool NeedsOriginRepoint(this ClipboardProvenance p) => p != ClipboardProvenance.DisplacedDestination;
}

// One item's placement in a batch. From is the pre-state address whose LIVE referrers (if any)
// get repointed to To — null for a clipboard paste, whose referrers (if any) were already
// resolved as correct-in-place at cut time (see ClipboardEntry's doc comment); never re-derive
// a paste's referrers from Origin.
readonly record struct BatchPlacement(ObjLoc? From, ObjLoc To, ObjectDump Body, string SourceLabel);

sealed class BatchMovePlan : IExecutablePlan
{
    public int ObjType;
    public List<ClipboardEntry> ClipboardAdds = new();     // DisplacedDestination entries created by this plan
    public List<WriteOp> Writes { get; } = new();
    public List<WriteOp> PreImages { get; } = new();
    public List<(int Obj, int Bank)> Stores { get; } = new();
    public List<ReferrerSite> Referrers = new();
    public List<string> Preview = new();
    public List<string> Warnings { get; } = new();
    public List<byte[]> LivePc { get; } = new();           // always empty — batch live-preview (0x43) is out of scope for v1
    public Dictionary<(int, int), byte[]> DigestBaseline { get; } = new();
    public string BackupLabel { get; set; } = "batchmove";

    public bool IsRefusable => Warnings.Any(w => w.StartsWith("REFUSE:", StringComparison.Ordinal));
}

static class BatchLibrarian
{
    public const int BankSlotCount = 128;   // every Program/Combi bank is exactly 128 slots

    // PURE. Assigns sequential destination slots to a set of PENDING clipboard entries — the
    // algorithm behind drag-drop auto-fill onto a bank (ViewModels/LibrarianShellViewModel.cs's
    // BatchPlaceFromPcg): fill starting at
    // `startSlot` (the exact slot the user right-clicked, NOT always 0 — replaces the earlier
    // "always slot 0" sequential-fill tool), skip anything that doesn't fit (past slot 127) or
    // fails a Program HD-1/EXi type check, leaving it pending rather than losing it (it's already
    // safely sitting in the clipboard — nothing to auto-clipboard here, unlike the old
    // tree-selection-based version this replaced).
    //   bankTypeOf — Program HD-1/EXi lookup (true=EXi/false=HD-1/null=unknown-or-untyped-bank,
    //   e.g. I-G). Ignored entirely for Combis and Set Lists, which have no such typing.
    public static (List<(ClipboardEntry Entry, int Slot)> Placed, List<(ClipboardEntry Entry, string Reason)> StillPending)
        ResolveSequentialFill(IReadOnlyList<ClipboardEntry> pending, int objType, int destBank, int startSlot, Func<int, bool?>? bankTypeOf)
    {
        var placeable = new List<ClipboardEntry>();
        var stillPending = new List<(ClipboardEntry, string)>();

        if (objType == LibObj.Program && bankTypeOf != null)
        {
            bool? destType = bankTypeOf(destBank);
            foreach (var e in pending)
            {
                bool? srcType = bankTypeOf(e.Origin.Bank);
                if (destType is bool dt && srcType is bool st && dt != st)
                    stillPending.Add((e, $"type mismatch: entry is {(st ? "EXi" : "HD-1")}, destination bank is {(dt ? "EXi" : "HD-1")}"));
                else
                    placeable.Add(e);   // includes the "can't verify" case (I-G/unknown) — CHECK-only in PlanBatchMove
            }
        }
        else placeable.AddRange(pending);

        var placed = new List<(ClipboardEntry, int)>();
        for (int i = 0; i < placeable.Count; i++)
        {
            int slot = startSlot + i;
            if (slot >= BankSlotCount)
                stillPending.Add((placeable[i], $"destination bank full — slot {slot} is past the last slot ({BankSlotCount - 1}) starting from {startSlot}"));
            else
                placed.Add((placeable[i], slot));
        }
        return (placed, stillPending);
    }

    // PURE. Generalizes Librarian.PlanMove's referrer-patch grouping across an arbitrary
    // relocation set instead of one fixed src/dst pair — the actual crux of "batch" vs. running
    // PlanMove N times: a single Combi/Set-List referrer touched by MULTIPLE placements in this
    // same batch must get every patch merged into ONE write, computed from a single
    // old-loc->new-loc map, not N independent unaware writes that would stomp each other.
    //   destOccupants — fresh dump of the CURRENT content at every distinct placement.To (the
    //                   batch analog of PlanMove's dstDump; pre-image + orphan-gate source).
    //   bankTypeOf    — same lookup as ResolveSequentialFill; defense-in-depth only (a manual
    //                   clipboard paste in a later phase can stage a crossing ResolveSequentialFill
    //                   never saw) — REFUSE a known mismatch, CHECK an unverifiable crossing.
    public static BatchMovePlan PlanBatchMove(
        LibraryCatalog cat, int objType,
        IReadOnlyList<BatchPlacement> placements,
        IReadOnlyDictionary<ObjLoc, ObjectDump> destOccupants,
        bool divertDisplacedToClipboard,
        Func<int, bool?>? bankTypeOf = null)
    {
        var plan = new BatchMovePlan { ObjType = objType };

        var real = placements.Where(p => p.From is not { } f || !f.Equals(p.To)).ToList();
        int skipped = placements.Count - real.Count;

        if (real.Count == 0)
        {
            plan.Warnings.Add("REFUSE: no placements to perform");
            return plan;
        }

        foreach (var g in real.GroupBy(p => p.To).Where(g => g.Count() > 1))
            plan.Warnings.Add($"REFUSE: duplicate destination {g.Key.Label()} targeted by {g.Count()} placement(s)");

        if (real.Any(p => p.To.ObjType != objType || (p.From is { } fo && fo.ObjType != objType)))
            plan.Warnings.Add("REFUSE: batch contains an object of a different type than the batch's object type");

        if (objType == LibObj.Program && real.Any(p => KronosBanks.IsReadOnlyProgramBank(p.To.Bank)))
            plan.Warnings.Add("REFUSE: a destination bank is read-only (GM/g)");

        if (objType == LibObj.Program && bankTypeOf != null)
        {
            foreach (var p in real)
            {
                if (p.From is { } from)
                {
                    // Local-to-local move — compare the two REAL banks' own configured types.
                    if (from.Bank == p.To.Bank) continue;
                    bool? srcType = bankTypeOf(from.Bank);
                    bool? dstType = bankTypeOf(p.To.Bank);
                    if (srcType is bool st && dstType is bool dt)
                    {
                        if (st != dt)
                            plan.Warnings.Add($"REFUSE: {from.Label()} ({(st ? "EXi" : "HD-1")}) cannot move to {p.To.Label()} ({(dt ? "EXi" : "HD-1")}) — bank types differ");
                    }
                    else
                        plan.Warnings.Add($"CHECK: {from.Label()} -> {p.To.Label()} crosses banks whose HD-1/EXi type couldn't be fully verified — the write may be rejected (Reply 64).");
                }
                else
                {
                    // Fresh placement (from a loaded PCG file or the Merge Window) — there's no
                    // local source bank to compare against here, so check the actual wire
                    // bytes about to be written against what the destination bank really
                    // expects instead. The body's own length already deterministically says
                    // EXi (4960B) or HD-1 (3706B) — no lookup needed for that half of the
                    // comparison, only for what the destination itself currently is. This is
                    // the gap that let a fresh placement's wrong-format Program body reach
                    // hardware and get rejected (func 0x24 Reply — 3 "mangled message" or 64
                    // "wrong bank type") instead of being caught here first.
                    if (bankTypeOf(p.To.Bank) is bool dt)
                    {
                        int expectedLen = dt ? ProgramFormatConverter.WireSizeExi : ProgramFormatConverter.WireSizeHd1;
                        if (p.Body.Body.Length != expectedLen)
                            plan.Warnings.Add($"REFUSE: {p.To.Label()} is a {(dt ? "EXi" : "HD-1")} bank ({expectedLen}-byte Programs), but {p.SourceLabel} is {p.Body.Body.Length} bytes — wrong format for this bank.");
                    }
                    else
                        plan.Warnings.Add($"CHECK: {p.To.Label()}'s HD-1/EXi type couldn't be fully verified — the write may be rejected (Reply 64).");
                }
            }
        }

        // (1) Pre-state old->new relocation map, keyed by ORIGIN. Never keyed by post-state slot
        // occupancy — that's what lets a chain (A->B, B's occupant->C) resolve both referrer
        // classes correctly (see SelfTest's merged-referrer case for the non-chain crux case,
        // and the orphan gate below for how a chain is recognized as safe).
        var relocation = new Dictionary<ObjLoc, ObjLoc>();
        foreach (var p in real) if (p.From is { } f) relocation[f] = p.To;

        // (2) Orphan gate — UNCONDITIONAL, independent of divertDisplacedToClipboard. A
        // clipboard entry has no address for a referrer to repoint to, so overwriting a
        // referenced slot is only safe when that slot's occupant is ITSELF also being relocated
        // somewhere in this same batch (i.e. it's also a From — a chain, not an orphan).
        var distinctTargets = real.Select(p => p.To).Distinct().ToList();
        foreach (var to in distinctTargets)
        {
            var displacedRefs = cat.ReferrersOf(to);
            if (displacedRefs.Count == 0 || relocation.ContainsKey(to)) continue;

            // The common, non-alarming trigger for this gate: the destination already holds
            // BYTE-IDENTICAL content (e.g. re-dropping a Program the Merge Window already
            // placed there once) — nothing would actually change, so say that plainly instead
            // of the generic dependency-safety warning below, which is for the genuinely
            // dangerous case (a DIFFERENT occupant, still referenced elsewhere, about to be
            // silently destroyed).
            bool identical = destOccupants.TryGetValue(to, out var occ)
                && real.First(p => p.To.Equals(to)).Body.Body.AsSpan().SequenceEqual(occ.Body);
            plan.Warnings.Add(identical
                ? $"REFUSE: {to.Label()} already contains this exact object — nothing to place."
                : $"REFUSE: {to.Label()} is referenced by {displacedRefs.Count} object(s) and would be overwritten without being relocated itself — add it to this batch as a source, or choose a different destination.");
        }

        // (3) Referrer collection + grouping — direct generalization of PlanMove's `grouped` dict.
        int refType = objType == LibObj.Program ? 1 : 0;
        var grouped = new Dictionary<(int, int, int), List<(int Site, string Kind, int NewBank, int NewNumber)>>();
        void AddPatch(ReferrerSite r, int newBank, int newNumber)
        {
            var key = (r.RefObj, r.RefBank, r.RefIndex);
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = new();
            list.Add((r.Site, r.Kind, newBank, newNumber));
        }
        foreach (var (from, to) in relocation)
        {
            var sites = cat.ReferrersOf(from);
            plan.Referrers.AddRange(sites);
            int newFunc33 = KronosBanks.ObjBankToFunc33(refType, to.Bank);
            foreach (var r in sites) AddPatch(r, newFunc33, to.Number);
        }

        // (4) Placement writes + pre-images. Source stays UNTOUCHED — no write at From, ever.
        foreach (var p in real)
        {
            plan.Writes.Add(new WriteOp(objType, p.To.Bank, p.To.Number, p.Body.Version, p.Body.Body, $"{p.SourceLabel} -> {p.To.Label()}"));
            if (destOccupants.TryGetValue(p.To, out var occ))
                plan.PreImages.Add(new WriteOp(objType, p.To.Bank, p.To.Number, occ.Version, occ.Body, "original (displaced)"));
        }

        // (5) Displaced-occupant disposition — only for targets NOT already covered by their own
        // relocation entry (§2's chain exemption).
        foreach (var to in distinctTargets)
        {
            if (relocation.ContainsKey(to)) continue;
            if (!destOccupants.TryGetValue(to, out var occ)) continue;
            if (divertDisplacedToClipboard)
                plan.ClipboardAdds.Add(new ClipboardEntry
                {
                    ObjType = objType, Origin = to, Version = occ.Version, Body = occ.Body,
                    Provenance = ClipboardProvenance.DisplacedDestination,
                    Reason = $"displaced by incoming placement to {to.Label()}", CutAt = DateTime.Now,
                });
            else
                plan.Warnings.Add($"CHECK: {to.Label()} is overwritten and not diverted — its prior contents are only recoverable from the automatic backup.");
        }

        // (6) Grouped referrer-patch writes — identical shape to PlanMove's step 2.
        foreach (var ((refObj, refBank, refIndex), patches) in grouped)
        {
            ObjectDump? baseDump = refObj == LibObj.Combi
                ? (cat.Combis.TryGetValue((refBank, refIndex), out var c) ? c : null)
                : (cat.Setlists.TryGetValue(refIndex, out var s) ? s : null);
            if (baseDump == null)
            {
                plan.Warnings.Add($"REFUSE: referring object missing from catalog (obj {refObj:X2} bank {refBank:X2} idx {refIndex}) — re-scan before moving");
                continue;
            }
            plan.PreImages.Add(new WriteOp(refObj, refBank, refIndex, baseDump.Version, baseDump.Body, "original"));

            var body = (byte[])baseDump.Body.Clone();
            foreach (var (site, kind, newBank, newNumber) in patches)
            {
                if (kind == "combi_timbre") LibRefs.SetCombiTimbreRef(body, site, newBank, newNumber);
                else                         LibRefs.SetSetListSlotRef(body, site, newBank, newNumber, type: null);
            }
            plan.Writes.Add(new WriteOp(refObj, refBank, refIndex, baseDump.Version, body, $"fix {patches.Count} ref(s)"));
        }

        foreach (var w in plan.Writes)
            if (!plan.Stores.Contains((w.Obj, w.Bank))) plan.Stores.Add((w.Obj, w.Bank));

        string typeTag = objType switch { LibObj.Program => "prog", LibObj.Combi => "combi", _ => "setlist" };
        plan.BackupLabel = $"batchmove_{typeTag}_{real.Count}items";

        string typeNoun = objType switch { LibObj.Program => "programs", LibObj.Combi => "combis", _ => "set lists" };
        plan.Preview.Add($"BATCH MOVE  {real.Count} placement(s)  ({typeNoun})");
        if (skipped > 0) plan.Preview.Add($"  ({skipped} placement(s) already at their destination — skipped)");
        foreach (var p in real) plan.Preview.Add($"  {p.SourceLabel}  ->  {p.To.Label()}");
        plan.Preview.Add("  source slots keep their original contents — only references now resolve to the new copies.");
        if (plan.ClipboardAdds.Count > 0) plan.Preview.Add($"  {plan.ClipboardAdds.Count} displaced object(s) diverted to clipboard.");
        plan.Preview.Add($"  references to rewrite: {plan.Referrers.Count}");
        plan.Preview.Add($"  objects to write (0x73): {plan.Writes.Count}");
        plan.Preview.Add("  banks to Store (0x76): " + string.Join(", ", plan.Stores.Select(s => Librarian.StoreLabel(s.Obj, s.Bank))));
        return plan;
    }

    // ── BatchClipboard <-> persisted DTO (Storage.ClipboardEntryDto) ─────────
    // Flat mapping only — Provenance round-trips through its string name, PastedTo through
    // plain bank/number ints (its ObjType always matches the entry's own, so isn't stored twice).
    internal static Storage.ClipboardEntryDto ToDto(ClipboardEntry e) => new(
        e.ObjType, e.Origin.Bank, e.Origin.Number, e.Version, e.Body,
        e.Provenance.ToString(), e.Reason, e.CutAt,
        e.PastedTo?.Bank, e.PastedTo?.Number, e.PastedAt, e.BankCopyGroup);

    internal static ClipboardEntry FromDto(Storage.ClipboardEntryDto d) => new()
    {
        ObjType = d.ObjType,
        Origin = new ObjLoc(d.ObjType, d.OriginBank, d.OriginNumber),
        Version = d.Version,
        Body = d.Body,
        Provenance = Enum.Parse<ClipboardProvenance>(d.Provenance),
        Reason = d.Reason,
        CutAt = d.CutAt,
        PastedTo = d.PastedBank is int pb && d.PastedNumber is int pn ? new ObjLoc(d.ObjType, pb, pn) : null,
        PastedAt = d.PastedAt,
        BankCopyGroup = d.BankCopyGroup,
    };

    // The persisted pending-change clipboard is a single global store, not per-host — see
    // LocalLibraryCache's own doc comment for why (the Kronos's IP can change; the objects
    // don't). The pre-Phase-7 host-keyed variant (for the classic, now-retired
    // LibrarianWindow) has been removed along with that window.
    public static BatchClipboard LoadClipboardGlobal()
    {
        var clip = new BatchClipboard();
        clip.Entries.AddRange(Storage.LoadClipboardGlobal().Select(FromDto));
        return clip;
    }

    public static void SaveClipboardGlobal(BatchClipboard clipboard) =>
        Storage.SaveClipboardGlobal(clipboard.Entries.Select(ToDto).ToList());

    // ── Off-hardware self-test (wired into --librarian-selftest) ─────────────
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        ClipboardEntry Entry(int objType, int bank, int number) => new()
        {
            ObjType = objType, Origin = new ObjLoc(objType, bank, number), Version = 1,
            Provenance = ClipboardProvenance.UserCopy, CutAt = DateTime.Now,
        };

        // 1. ResolveSequentialFill: plain fill starting at slot 0, no type gate for Combis.
        var combiSrcs = new List<ClipboardEntry> { Entry(LibObj.Combi, 0x00, 1), Entry(LibObj.Combi, 0x00, 2), Entry(LibObj.Combi, 0x00, 3) };
        var (placedC, clipC) = ResolveSequentialFill(combiSrcs, LibObj.Combi, 0x40, startSlot: 0, null);
        Check("resolve-combi-sequential", placedC.Count == 3 && clipC.Count == 0 &&
            placedC[0] == (combiSrcs[0], 0) && placedC[1] == (combiSrcs[1], 1) && placedC[2] == (combiSrcs[2], 2));

        // 1b. ResolveSequentialFill: fill starting at a non-zero slot (Paste All's actual use —
        // starts exactly where the user right-clicked, not always slot 0).
        var (placedStart, clipStart) = ResolveSequentialFill(combiSrcs, LibObj.Combi, 0x40, startSlot: 12, null);
        Check("resolve-start-slot", placedStart.Count == 3 &&
            placedStart[0] == (combiSrcs[0], 12) && placedStart[1] == (combiSrcs[1], 13) && placedStart[2] == (combiSrcs[2], 14));

        // 2. ResolveSequentialFill: capacity overflow (130 entries, 128 slots) from slot 0.
        var many = Enumerable.Range(0, 130).Select(i => Entry(LibObj.Combi, 0x00, i)).ToList();
        var (placedMany, clipMany) = ResolveSequentialFill(many, LibObj.Combi, 0x40, startSlot: 0, null);
        Check("resolve-overflow", placedMany.Count == 128 && clipMany.Count == 2);

        // 2b. ResolveSequentialFill: overflow triggers earlier when starting mid-bank (10 entries
        // starting at slot 120 only fit 8 before running past slot 127).
        var ten = Enumerable.Range(0, 10).Select(i => Entry(LibObj.Combi, 0x00, i)).ToList();
        var (placedMid, clipMid) = ResolveSequentialFill(ten, LibObj.Combi, 0x40, startSlot: 120, null);
        Check("resolve-overflow-mid-start", placedMid.Count == 8 && clipMid.Count == 2 &&
            placedMid[^1].Slot == 127);

        // 3. ResolveSequentialFill: Program bank-type mismatch leaves the entry pending (not lost —
        // it's already safely in the clipboard).
        bool? BankType(int bank) => bank == 0x00 ? true /*EXi*/ : bank == 0x40 ? false /*HD-1*/ : (bool?)null;
        var progSrcs = new List<ClipboardEntry> { Entry(LibObj.Program, 0x00, 1), Entry(LibObj.Program, 0x40, 2) };
        var (placedP, clipP) = ResolveSequentialFill(progSrcs, LibObj.Program, 0x40, startSlot: 0, BankType);
        Check("resolve-type-mismatch", placedP.Count == 1 && placedP[0].Entry == progSrcs[1] &&
            clipP.Count == 1 && clipP[0].Entry == progSrcs[0]);

        // 3b. PlanBatchMove: FRESH placement (From: null) bank-type check — the gap behind a
        // real hardware write rejection (func 0x24 Reply — "mangled message"/"wrong bank
        // type"): a Program placed fresh (from a loaded PCG file or the Merge Window) was
        // never checked against what its destination bank is ACTUALLY configured as, only a
        // local-to-local move was. The body's own length (EXi=4960B, HD-1=3706B) must be
        // compared directly against bankTypeOf(destBank) — no "source bank" to look up here.
        var freshCat = new LibraryCatalog();
        var freshDest = new ObjLoc(LibObj.Program, 0x40, 20);   // BankType(0x40) => false (HD-1)
        var noOccupants = new Dictionary<ObjLoc, ObjectDump>();

        var wrongFormatBody = new ObjectDump(LibObj.Program, 0x40, 20, 1, new byte[ProgramFormatConverter.WireSizeExi]);
        var freshWrongFormatPlan = PlanBatchMove(freshCat, LibObj.Program,
            new List<BatchPlacement> { new(null, freshDest, wrongFormatBody, "fresh") },
            noOccupants, divertDisplacedToClipboard: false, BankType);
        Check("fresh-placement-wrong-format-refuses", freshWrongFormatPlan.IsRefusable &&
            freshWrongFormatPlan.Warnings.Any(w => w.Contains("wrong format for this bank")));

        var correctFormatBody = new ObjectDump(LibObj.Program, 0x40, 20, 1, new byte[ProgramFormatConverter.WireSizeHd1]);
        var freshCorrectFormatPlan = PlanBatchMove(freshCat, LibObj.Program,
            new List<BatchPlacement> { new(null, freshDest, correctFormatBody, "fresh") },
            noOccupants, divertDisplacedToClipboard: false, BankType);
        Check("fresh-placement-correct-format-not-refused",
            !freshCorrectFormatPlan.Warnings.Any(w => w.Contains("wrong format for this bank")));

        var unknownBankDest = new ObjLoc(LibObj.Program, 0x99, 0);   // BankType(0x99) => null, "can't verify"
        var unknownBankBody = new ObjectDump(LibObj.Program, 0x99, 0, 1, new byte[ProgramFormatConverter.WireSizeExi]);
        var freshUnknownBankPlan = PlanBatchMove(freshCat, LibObj.Program,
            new List<BatchPlacement> { new(null, unknownBankDest, unknownBankBody, "fresh") },
            noOccupants, divertDisplacedToClipboard: false, BankType);
        Check("fresh-placement-unknown-bank-check-only", !freshUnknownBankPlan.IsRefusable &&
            freshUnknownBankPlan.Warnings.Any(w => w.StartsWith("CHECK:", StringComparison.Ordinal) && w.Contains("couldn't be fully verified")));

        // 4. Orphan gate: overwriting a referenced, non-relocated slot REFUSES.
        var cat1 = new LibraryCatalog();
        int fbX = KronosBanks.ObjBankToFunc33(1, 0x40);
        var combiBody1 = new byte[7810];
        LibRefs.SetCombiTimbreRef(combiBody1, 0, fbX, 10);
        cat1.AddCombi(new ObjectDump(LibObj.Combi, 0x00, 0, 3, combiBody1));
        var orphanSrc = new ObjLoc(LibObj.Program, 0x00, 5);
        var orphanDst = new ObjLoc(LibObj.Program, 0x40, 10);   // referenced, not itself relocated
        var incomingBody = new byte[100];
        var occupantBody = new byte[100];
        occupantBody[0] = 0xFF;   // deliberately different from incomingBody — a real displacement, not a re-drop of the same content
        var orphanPlacements = new List<BatchPlacement> { new(orphanSrc, orphanDst, new ObjectDump(LibObj.Program, 0x00, 5, 1, incomingBody), orphanSrc.Label()) };
        var orphanOccupants = new Dictionary<ObjLoc, ObjectDump> { [orphanDst] = new ObjectDump(LibObj.Program, 0x40, 10, 1, occupantBody) };
        var orphanPlan = PlanBatchMove(cat1, LibObj.Program, orphanPlacements, orphanOccupants, divertDisplacedToClipboard: false);
        Check("orphan-gate-refuses", orphanPlan.IsRefusable && orphanPlan.Warnings.Any(w => w.Contains("referenced by")));

        // 4b. Orphan gate: same referenced/non-relocated shape, but the incoming content is
        // BYTE-IDENTICAL to what's already there — a friendlier "already exists" message, not
        // the alarming "would be overwritten" one, since nothing would actually change.
        var dupOrphanPlacements = new List<BatchPlacement> { new(orphanSrc, orphanDst, new ObjectDump(LibObj.Program, 0x00, 5, 1, occupantBody), orphanSrc.Label()) };
        var dupOrphanPlan = PlanBatchMove(cat1, LibObj.Program, dupOrphanPlacements, orphanOccupants, divertDisplacedToClipboard: false);
        Check("orphan-gate-identical-content", dupOrphanPlan.IsRefusable &&
            dupOrphanPlan.Warnings.Any(w => w.Contains("already contains this exact object")) &&
            !dupOrphanPlan.Warnings.Any(w => w.Contains("referenced by")));

        // 5. THE crux case: one referrer touched by TWO placements in the same batch gets
        // both patches merged into a single write, not two independent (stomping) writes.
        var cat2 = new LibraryCatalog();
        int fbA = KronosBanks.ObjBankToFunc33(1, 0x00);
        var combiBody2 = new byte[7810];
        LibRefs.SetCombiTimbreRef(combiBody2, 3, fbA, 7);   // -> I-A:007
        LibRefs.SetCombiTimbreRef(combiBody2, 5, fbA, 9);   // -> I-A:009
        cat2.AddCombi(new ObjectDump(LibObj.Combi, 0x00, 0, 3, combiBody2));
        var progA = new ObjLoc(LibObj.Program, 0x00, 7);
        var progB = new ObjLoc(LibObj.Program, 0x00, 9);
        var toA = new ObjLoc(LibObj.Program, 0x40, 0);
        var toB = new ObjLoc(LibObj.Program, 0x40, 1);
        var mergedPlacements = new List<BatchPlacement>
        {
            new(progA, toA, new ObjectDump(LibObj.Program, 0x00, 7, 1, new byte[100]), progA.Label()),
            new(progB, toB, new ObjectDump(LibObj.Program, 0x00, 9, 1, new byte[100]), progB.Label()),
        };
        var mergedOccupants = new Dictionary<ObjLoc, ObjectDump>
        {
            [toA] = new ObjectDump(LibObj.Program, 0x40, 0, 1, new byte[100]),
            [toB] = new ObjectDump(LibObj.Program, 0x40, 1, 1, new byte[100]),
        };
        var mergedPlan = PlanBatchMove(cat2, LibObj.Program, mergedPlacements, mergedOccupants, divertDisplacedToClipboard: false);
        Check("batch-not-refusable", !mergedPlan.IsRefusable);
        var combiWrites = mergedPlan.Writes.Where(w => w.Obj == LibObj.Combi).ToList();
        Check("merged-referrer-single-write", combiWrites.Count == 1);
        if (combiWrites.Count == 1)
        {
            int fbToA = KronosBanks.ObjBankToFunc33(1, 0x40);
            var (b3, n3) = LibRefs.CombiTimbreRef(combiWrites[0].Body, 3);
            var (b5, n5) = LibRefs.CombiTimbreRef(combiWrites[0].Body, 5);
            Check("merged-timbre3-retarget", b3 == fbToA && n3 == 0);
            Check("merged-timbre5-retarget", b5 == fbToA && n5 == 1);
        }

        // 6. Duplicate-destination and mixed-type REFUSE.
        var dupPlacements = new List<BatchPlacement>
        {
            new(new ObjLoc(LibObj.Program, 0x00, 1), new ObjLoc(LibObj.Program, 0x40, 0), new ObjectDump(LibObj.Program, 0x00, 1, 1, new byte[10]), "a"),
            new(new ObjLoc(LibObj.Program, 0x00, 2), new ObjLoc(LibObj.Program, 0x40, 0), new ObjectDump(LibObj.Program, 0x00, 2, 1, new byte[10]), "b"),
        };
        var dupPlan = PlanBatchMove(new LibraryCatalog(), LibObj.Program, dupPlacements, new Dictionary<ObjLoc, ObjectDump>(), false);
        Check("duplicate-dest-refuses", dupPlan.IsRefusable);

        var mixedPlacements = new List<BatchPlacement>
        {
            new(new ObjLoc(LibObj.Program, 0x00, 1), new ObjLoc(LibObj.Program, 0x40, 0), new ObjectDump(LibObj.Program, 0x00, 1, 1, new byte[10]), "a"),
        };
        var mixedPlan = PlanBatchMove(new LibraryCatalog(), LibObj.Combi, mixedPlacements, new Dictionary<ObjLoc, ObjectDump>(), false);
        Check("mixed-type-refuses", mixedPlan.IsRefusable);

        // 7. Unreferenced overwrite: CHECK-warns with the checkbox off, clipboard-adds with it on.
        var soloSrc = new ObjLoc(LibObj.Combi, 0x00, 1);
        var soloDst = new ObjLoc(LibObj.Combi, 0x40, 0);
        var soloPlacements = new List<BatchPlacement> { new(soloSrc, soloDst, new ObjectDump(LibObj.Combi, 0x00, 1, 1, new byte[7810]), soloSrc.Label()) };
        var soloOccupants = new Dictionary<ObjLoc, ObjectDump> { [soloDst] = new ObjectDump(LibObj.Combi, 0x40, 0, 1, new byte[7810]) };

        var checkPlan = PlanBatchMove(new LibraryCatalog(), LibObj.Combi, soloPlacements, soloOccupants, divertDisplacedToClipboard: false);
        Check("check-warning-on-overwrite", !checkPlan.IsRefusable && checkPlan.Warnings.Any(w => w.StartsWith("CHECK:")) && checkPlan.ClipboardAdds.Count == 0);

        var clipPlan = PlanBatchMove(new LibraryCatalog(), LibObj.Combi, soloPlacements, soloOccupants, divertDisplacedToClipboard: true);
        Check("clipboard-add-on-overwrite", !clipPlan.IsRefusable && clipPlan.ClipboardAdds.Count == 1 &&
            clipPlan.ClipboardAdds[0].Provenance == ClipboardProvenance.DisplacedDestination);

        // 8. Clipboard <-> DTO round-trip (pure in-memory — no disk I/O in a self-test).
        var clip = new BatchClipboard();
        clip.Entries.Add(new ClipboardEntry
        {
            ObjType = LibObj.Combi, Origin = new ObjLoc(LibObj.Combi, 0x40, 3), Version = 5,
            Body = new byte[] { 1, 2, 3 }, Provenance = ClipboardProvenance.DisplacedDestination,
            Reason = "test", CutAt = new DateTime(2026, 1, 1),
        });
        clip.Entries.Add(new ClipboardEntry
        {
            ObjType = LibObj.Program, Origin = new ObjLoc(LibObj.Program, 0x00, 1), Version = 2,
            Body = new byte[] { 9 }, Provenance = ClipboardProvenance.UnplaceableSource,
            Reason = "overflow", CutAt = new DateTime(2026, 1, 2),
            PastedTo = new ObjLoc(LibObj.Program, 0x40, 7), PastedAt = new DateTime(2026, 1, 3),
        });
        var dtos = clip.Entries.Select(ToDto).ToList();
        var restored = new BatchClipboard();
        restored.Entries.AddRange(dtos.Select(FromDto));
        Check("clipboard-roundtrip-count", restored.Entries.Count == 2);
        Check("clipboard-roundtrip-origin", restored.Entries[0].Origin.Equals(clip.Entries[0].Origin));
        Check("clipboard-roundtrip-body", restored.Entries[0].Body.SequenceEqual(clip.Entries[0].Body));
        Check("clipboard-roundtrip-provenance", restored.Entries[1].Provenance == ClipboardProvenance.UnplaceableSource);
        Check("clipboard-roundtrip-pastedto", restored.Entries[1].PastedTo == clip.Entries[1].PastedTo);
        Check("clipboard-roundtrip-pending", restored.Entries[0].Pending && !restored.Entries[1].Pending);

        // 9. NeedsOriginRepoint truth table.
        Check("repoint-displaced-false", !ClipboardProvenance.DisplacedDestination.NeedsOriginRepoint());
        Check("repoint-unplaceable-true", ClipboardProvenance.UnplaceableSource.NeedsOriginRepoint());
        Check("repoint-usercopy-true", ClipboardProvenance.UserCopy.NeedsOriginRepoint());

        // 10. LibraryCatalog.ReferrersOf must return empty for a Set List loc — nothing ever
        // references one, and the old binary Program/Combi refType assumption would otherwise
        // mistranslate it through the Combi branch.
        var slLoc = new ObjLoc(LibObj.SetList, 0, 5);
        Check("catalog-setlist-no-referrers", new LibraryCatalog().ReferrersOf(slLoc).Count == 0);

        // 11. A Set List placement (via the batch/clipboard pipeline, now that Set Lists are
        // copy/paste-able) produces zero referrer-patch writes and never spuriously REFUSEs via the
        // orphan gate — the direct consequence of #10 flowing through PlanBatchMove unmodified.
        var slFrom = new ObjLoc(LibObj.SetList, 0, 10);
        var slTo = new ObjLoc(LibObj.SetList, 0, 20);
        var slPlacements = new List<BatchPlacement>
        {
            new(slFrom, slTo, new ObjectDump(LibObj.SetList, 0, 10, 1, new byte[69416]), slFrom.Label()),
        };
        var slOccupants = new Dictionary<ObjLoc, ObjectDump> { [slTo] = new ObjectDump(LibObj.SetList, 0, 20, 1, new byte[69416]) };
        var slPlan = PlanBatchMove(new LibraryCatalog(), LibObj.SetList, slPlacements, slOccupants, divertDisplacedToClipboard: false);
        Check("setlist-batch-not-refusable", !slPlan.IsRefusable);
        Check("setlist-batch-no-referrer-writes", slPlan.Referrers.Count == 0);
        Check("setlist-batch-one-write", slPlan.Writes.Count == 1);
        Check("setlist-batch-check-on-overwrite", slPlan.Warnings.Any(w => w.StartsWith("CHECK:")));

        return fails;
    }
}
