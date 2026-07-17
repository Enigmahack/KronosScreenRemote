namespace KronosScreenRemote;

// Librarian model — catalog, dependency graph, and coherent-move planning.
//
// A "move" swaps a Program or Combi with another slot and rewrites every Combi
// timbre and Set List slot that referenced either, so nothing is left dangling
// (the instrument does NOT auto-repair references). Ported from the Python
// KronosScreenRemotePy librarian, whose bank-encoding + reference logic was
// validated against real hardware data (99.3% of 500+ real set-list references
// resolved through the same translation used here).
//
// Split for testability: PlanMove is PURE (LibrarianModel.SelfTest exercises it
// off-hardware); only ApplyMoveAsync talks to the instrument, through IMoveExecutor.
//
// Referrer scope: Combi timbres + Set List slots. Song Timbre Sets (obj 0x02)
// are intentionally out of scope.

static class LibObj
{
    public const int Program = 0x00;
    public const int Combi   = 0x01;
    public const int SetList = 0x0D;
}

// A movable object location, addressed in object-dump (header) encoding.
readonly record struct ObjLoc(int ObjType, int Bank, int Number)
{
    public string Label() => ObjType == LibObj.Program
        ? $"{KronosBanks.ProgramLabel(Bank)}:{Number:D3}"
        : $"{KronosBanks.CombiLabel(Bank)}:{Number:D3}";
}

// One reference site pointing at a movable object.
readonly record struct ReferrerSite(
    string Kind, int RefObj, int RefBank, int RefIndex, int Site, int CurBank, int CurNumber)
{
    public string Describe() => Kind == "combi_timbre"
        ? $"Combi {KronosBanks.CombiLabel(RefBank)}:{RefIndex:D3} timbre {Site + 1}"
        : $"Set List {RefIndex:D3} slot {Site + 1}";
}

// A single re-addressed 0x73 Object Dump write (volatile until Stored).
sealed class WriteOp
{
    public int Obj; public int Bank; public int Index; public byte Version;
    public byte[] Body; public string Note;
    public WriteOp(int obj, int bank, int index, byte version, byte[] body, string note = "")
    { Obj = obj; Bank = bank; Index = index; Version = version; Body = body; Note = note; }
}

sealed class MovePlan
{
    public ObjLoc Src, Dst;
    public List<WriteOp> Writes = new();
    public List<WriteOp> PreImages = new();               // ORIGINAL objects (backup/restore)
    public List<(int Obj, int Bank)> Stores = new();      // banks to Store (deduped)
    public List<ReferrerSite> Referrers = new();
    public List<string> Preview = new();
    public List<string> Warnings = new();
    public List<byte[]> LivePc = new();                   // optional 0x43 dual-write
    public Dictionary<(int, int), byte[]> DigestBaseline = new();

    public bool IsRefusable => Warnings.Any(w => w.StartsWith("REFUSE:", StringComparison.Ordinal));
}

// Read/patch the (bank, number) reference bytes inside a DECODED object body.
static class LibRefs
{
    public const int TimbreCount = 16;
    const int Timbre0Num  = 4802;   // timbre 0 program NUMBER byte
    const int Timbre0Bank = 4803;   // timbre 0 program BANK byte (internal linear)
    const int TimbreStride = 188;

    // Set-list slot layout mirrors SetListData (hardware-confirmed).
    const int SlBase = 24, SlStride = 542;
    const int SlTypeOfs = 24, SlBankOfs = 25, SlIndexOfs = 26;
    public const int SlSlotCount = 128;

    // ── Combi timbre → program ──
    public static (int Bank, int Number) CombiTimbreRef(byte[] body, int timbre)
    {
        int b = Timbre0Num + timbre * TimbreStride;
        return (body[b + 1], body[b]);              // bank @ +1 (4803), number @ +0 (4802)
    }

    public static void SetCombiTimbreRef(byte[] body, int timbre, int func33Bank, int number)
    {
        int b = Timbre0Num + timbre * TimbreStride;
        body[b]     = (byte)(number & 0x7F);
        body[b + 1] = (byte)(func33Bank & 0x7F);
    }

    public static IEnumerable<(int T, int Bank, int Number)> IterCombiTimbreRefs(byte[] body)
    {
        for (int t = 0; t < TimbreCount; t++)
        {
            var (bank, num) = CombiTimbreRef(body, t);
            yield return (t, bank, num);
        }
    }

    // ── Set-list slot → object ── (type: 0=Combi, 1=Prog, 2=Song)
    public static (int Type, int Bank, int Index) SetListSlotRef(byte[] body, int slot)
    {
        int b = SlBase + slot * SlStride;
        return (body[b + SlTypeOfs] & 0x03, body[b + SlBankOfs] & 0x1F, body[b + SlIndexOfs]);
    }

    // Patch a slot's reference in place, preserving the color/transpose bits that
    // share the type/bank bytes. type == null keeps the existing type bits.
    public static void SetSetListSlotRef(byte[] body, int slot, int func33Bank, int index, int? type)
    {
        int b = SlBase + slot * SlStride;
        if (type.HasValue)
            body[b + SlTypeOfs] = (byte)((body[b + SlTypeOfs] & ~0x03) | (type.Value & 0x03));
        body[b + SlBankOfs]  = (byte)((body[b + SlBankOfs] & ~0x1F) | (func33Bank & 0x1F));
        body[b + SlIndexOfs] = (byte)(index & 0xFF);
    }

    public static IEnumerable<(int S, int Type, int Bank, int Index)> IterSetListSlotRefs(byte[] body)
    {
        for (int s = 0; s < SlSlotCount; s++)
        {
            int b = SlBase + s * SlStride;
            if (b + SlIndexOfs >= body.Length) yield break;
            var (t, bk, ix) = SetListSlotRef(body, s);
            yield return (s, t, bk, ix);
        }
    }
}

// Lightweight reference index — only the reference tuples, not full bodies. Cheap
// to build in a scan and fast to query; full bodies are re-dumped on demand for the
// few objects a move actually rewrites (which also closes the body-staleness window).
sealed class RefIndex
{
    public readonly Dictionary<(int Bank, int Index), List<(int Bank, int Number)>> CombiRefs = new();
    public readonly Dictionary<int, List<(int Slot, int Type, int Bank, int Index)>> SetlistRefs = new();
    // (obj, bank) -> SHA-1 digest captured AT SCAN TIME (freshness gate for discovery).
    public readonly Dictionary<(int Obj, int Bank), byte[]> ScanDigests = new();

    public void AddCombi(ObjectDump d) =>
        CombiRefs[(d.Bank, d.Index)] = LibRefs.IterCombiTimbreRefs(d.Body).Select(r => (r.Bank, r.Number)).ToList();

    public void AddSetlist(ObjectDump d) =>
        SetlistRefs[d.Index] = LibRefs.IterSetListSlotRefs(d.Body).Select(r => (r.S, r.Type, r.Bank, r.Index)).ToList();

    public List<ReferrerSite> ReferrersOf(ObjLoc loc)
    {
        var outp = new List<ReferrerSite>();
        int refType = loc.ObjType == LibObj.Program ? 1 : 0;
        int wantBank = KronosBanks.ObjBankToFunc33(refType, loc.Bank);
        if (wantBank < 0) return outp;

        if (loc.ObjType == LibObj.Program)
            foreach (var ((bank, index), refs) in CombiRefs)
                for (int t = 0; t < refs.Count; t++)
                    if (refs[t].Bank == wantBank && refs[t].Number == loc.Number)
                        outp.Add(new ReferrerSite("combi_timbre", LibObj.Combi, bank, index, t, refs[t].Bank, refs[t].Number));

        foreach (var (number, slots) in SetlistRefs)
            foreach (var (slot, type, fbank, idx) in slots)
                if (type == refType && fbank == wantBank && idx == loc.Number)
                    outp.Add(new ReferrerSite("setlist_slot", LibObj.SetList, 0, number, slot, fbank, idx));
        return outp;
    }

    public int UsageCount(ObjLoc loc) => ReferrersOf(loc).Count;

    public HashSet<(int Obj, int Bank, int Index)> ReferrerObjectIds(ObjLoc loc) =>
        ReferrersOf(loc).Select(r => (r.RefObj, r.RefBank, r.RefIndex)).ToHashSet();

    public void RecordDigest(int obj, int bank, byte[]? sha1)
    {
        if (sha1 != null) ScanDigests[(obj, bank)] = sha1;
    }

    // Re-read scan-time digests via reader(obj,bank) and return banks that changed
    // since the scan — meaning the index may have MISSED a newly-created referrer.
    public async Task<List<(int Obj, int Bank)>> StaleBanksAsync(Func<int, int, Task<byte[]?>> reader)
    {
        var stale = new List<(int, int)>();
        foreach (var ((obj, bank), baseline) in ScanDigests)
        {
            var cur = await reader(obj, bank).ConfigureAwait(false);
            if (cur != null && !cur.AsSpan().SequenceEqual(baseline)) stale.Add((obj, bank));
        }
        return stale;
    }
}

// Full-body catalog of the referrer objects a specific move touches (re-dumped
// fresh at plan time). PlanMove re-derives the exact sites from these bodies.
sealed class LibraryCatalog
{
    public readonly Dictionary<(int Bank, int Index), ObjectDump> Combis = new();
    public readonly Dictionary<int, ObjectDump> Setlists = new();

    public void AddCombi(ObjectDump d) { if (d.Obj == LibObj.Combi) Combis[(d.Bank, d.Index)] = d; }
    public void AddSetlist(ObjectDump d) { if (d.Obj == LibObj.SetList) Setlists[d.Index] = d; }

    public List<ReferrerSite> ReferrersOf(ObjLoc loc)
    {
        var outp = new List<ReferrerSite>();
        int refType = loc.ObjType == LibObj.Program ? 1 : 0;
        int wantBank = KronosBanks.ObjBankToFunc33(refType, loc.Bank);
        if (wantBank < 0) return outp;

        if (loc.ObjType == LibObj.Program)
            foreach (var ((bank, index), dump) in Combis)
                foreach (var (t, fbank, num) in LibRefs.IterCombiTimbreRefs(dump.Body))
                    if (fbank == wantBank && num == loc.Number)
                        outp.Add(new ReferrerSite("combi_timbre", LibObj.Combi, bank, index, t, fbank, num));

        foreach (var (number, dump) in Setlists)
            foreach (var (s, type, fbank, idx) in LibRefs.IterSetListSlotRefs(dump.Body))
                if (type == refType && fbank == wantBank && idx == loc.Number)
                    outp.Add(new ReferrerSite("setlist_slot", LibObj.SetList, 0, number, s, fbank, idx));
        return outp;
    }
}

// The instrument-facing side of ApplyMoveAsync (implemented by SysExService).
interface IMoveExecutor
{
    Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path);
    Task<byte[]?> BankDigestAsync(int obj, int bank);
    Task<int> WriteObjectAsync(WriteOp op);   // Reply code (0 = OK); -1 = timeout
    Task<int> StoreBankAsync(int obj, int bank);
    Task SendRawAsync(byte[] data);
}

static class Librarian
{
    static readonly HashSet<int> ReadOnlyProgramBanks =
        new(new[] { 0x10 }.Concat(Enumerable.Range(0x11, 0x0A)));   // GM, g(1)..g(d)

    public static string StoreLabel(int obj, int bank) => obj switch
    {
        LibObj.Program => $"Prog {KronosBanks.ProgramLabel(bank)}",
        LibObj.Combi   => $"Combi {KronosBanks.CombiLabel(bank)}",
        LibObj.SetList => "Set Lists",
        _ => $"obj{obj:X2}:bank{bank:X2}",
    };

    // Compute a coherent swap(src, dst). PURE — no hardware access. srcDump/dstDump
    // are the freshly dumped bodies of the two objects being swapped.
    public static MovePlan PlanMove(LibraryCatalog cat, ObjLoc src, ObjectDump srcDump,
                                    ObjLoc dst, ObjectDump dstDump, ObjLoc? active = null)
    {
        var plan = new MovePlan { Src = src, Dst = dst };

        if (src.ObjType != dst.ObjType)
            plan.Warnings.Add("REFUSE: cannot move between different object types (program vs combi)");
        if (src.ObjType == LibObj.Program && ReadOnlyProgramBanks.Contains(dst.Bank))
            plan.Warnings.Add($"REFUSE: destination {dst.Label()} is a read-only (GM/g) program bank");
        if (src.Equals(dst))
            plan.Warnings.Add("REFUSE: source and destination are the same location");

        int refType = src.ObjType == LibObj.Program ? 1 : 0;
        int dstFunc33 = KronosBanks.ObjBankToFunc33(refType, dst.Bank);
        int srcFunc33 = KronosBanks.ObjBankToFunc33(refType, src.Bank);

        var srcReferrers = cat.ReferrersOf(src);   // → point to dst
        var dstReferrers = cat.ReferrersOf(dst);   // → point back to src
        plan.Referrers.AddRange(srcReferrers);
        plan.Referrers.AddRange(dstReferrers);

        // Group site patches by referring object so each object is written once.
        var grouped = new Dictionary<(int, int, int), List<(int Site, string Kind, int NewBank, int NewNumber)>>();
        void AddPatch(ReferrerSite r, int newBank, int newNumber)
        {
            var key = (r.RefObj, r.RefBank, r.RefIndex);
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = new();
            list.Add((r.Site, r.Kind, newBank, newNumber));
        }
        foreach (var r in srcReferrers) AddPatch(r, dstFunc33, dst.Number);
        foreach (var r in dstReferrers) AddPatch(r, srcFunc33, src.Number);

        // (1) The two swapped objects (patched write to the OTHER location; pre-image
        //     records each at its ORIGINAL location for restore).
        plan.Writes.Add(new WriteOp(src.ObjType, dst.Bank, dst.Number, srcDump.Version, srcDump.Body, $"{src.Label()} -> {dst.Label()}"));
        plan.Writes.Add(new WriteOp(dst.ObjType, src.Bank, src.Number, dstDump.Version, dstDump.Body, $"{dst.Label()} -> {src.Label()}"));
        plan.PreImages.Add(new WriteOp(src.ObjType, src.Bank, src.Number, srcDump.Version, srcDump.Body, "original"));
        plan.PreImages.Add(new WriteOp(dst.ObjType, dst.Bank, dst.Number, dstDump.Version, dstDump.Body, "original"));

        // (2) Patched referrer objects (pre-image = the unpatched original body).
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

        // (3) Banks to Store (deduped). Set lists all live under obj 0x0D bank 0.
        foreach (var w in plan.Writes)
            if (!plan.Stores.Contains((w.Obj, w.Bank))) plan.Stores.Add((w.Obj, w.Bank));

        // (4) Optional live dual-write: if the current performance is a combi we are
        //     patching, mirror the change into its edit buffer with 0x43 (audible now).
        if (active is { } act && act.ObjType == LibObj.Combi)
            foreach (var r in plan.Referrers)
                if (r.Kind == "combi_timbre" && r.RefObj == LibObj.Combi &&
                    r.RefBank == act.Bank && r.RefIndex == act.Number)
                {
                    bool isSrc = r.CurBank == srcFunc33 && r.CurNumber == src.Number;
                    int newBank = isSrc ? dstFunc33 : srcFunc33;
                    int newNum  = isSrc ? dst.Number : src.Number;
                    plan.LivePc.Add(KronosSysEx.BuildParamChange(4, r.Site, 0, 8, 0, newBank));   // pid 8 = bank
                    plan.LivePc.Add(KronosSysEx.BuildParamChange(4, r.Site, 0, 9, 0, newNum));    // pid 9 = number
                }

        // (5) Program bank-type reminder (HD-1/EXi) — enforced at apply via Reply 64.
        if (src.ObjType == LibObj.Program && src.Bank != dst.Bank)
            plan.Warnings.Add("CHECK: program move across banks — destination bank must be the same type (HD-1/EXi) or the write is rejected (Reply 64).");

        plan.Preview.Add($"SWAP  {src.Label()}  <->  {dst.Label()}  ({(src.ObjType == LibObj.Program ? "programs" : "combis")})");
        plan.Preview.Add($"  references to rewrite: {plan.Referrers.Count}");
        foreach (var r in srcReferrers) plan.Preview.Add($"    - {r.Describe()}  ->  {dst.Label()}");
        foreach (var r in dstReferrers) plan.Preview.Add($"    - {r.Describe()}  ->  {src.Label()}");
        plan.Preview.Add($"  objects to write (0x73): {plan.Writes.Count}");
        plan.Preview.Add("  banks to Store (0x76): " + string.Join(", ", plan.Stores.Select(s => StoreLabel(s.Obj, s.Bank))));
        if (plan.LivePc.Count > 0)
            plan.Preview.Add($"  live edit-buffer preview (0x43): {plan.LivePc.Count} message(s) to active combi");

        return plan;
    }

    // Capture the storage digest of every affected bank — the staleness baseline
    // ApplyMoveAsync re-checks immediately before Storing.
    public static async Task ArmPlanAsync(MovePlan plan, IMoveExecutor ex)
    {
        foreach (var (obj, bank) in plan.Stores)
        {
            var d = await ex.BankDigestAsync(obj, bank).ConfigureAwait(false);
            if (d != null) plan.DigestBaseline[(obj, bank)] = d;
        }
    }

    // Execute a coherent move. Order: backup pre-images -> staleness re-check ->
    // writes -> Store -> optional live 0x43. Aborts (before any Store) on a stale
    // digest or a rejected write. `stamp` is an externally supplied timestamp.
    public static async Task<(bool Ok, List<string> Steps, string? Aborted)> ApplyMoveAsync(
        MovePlan plan, IMoveExecutor ex, string backupDir, string stamp,
        Action<string>? progress = null, bool doLive = true)
    {
        var steps = new List<string>();
        void Note(string m) { steps.Add(m); progress?.Invoke(m); }

        if (plan.IsRefusable)
            return (false, steps, string.Join("; ", plan.Warnings));

        // 1. Backup the pre-image of every object we overwrite (restore = replay + Store).
        var safe = $"{stamp}_move_{plan.Src.Label()}_{plan.Dst.Label()}".Replace(":", "").Replace(" ", "");
        var backupPath = System.IO.Path.Combine(backupDir, safe + ".syx");
        Note($"backup {plan.PreImages.Count} pre-image object(s) -> {backupPath}");
        await ex.BackupObjectsAsync(plan.PreImages, backupPath).ConfigureAwait(false);

        // 2. Staleness gate — abort if any affected bank changed since arm.
        foreach (var ((obj, bank), baseline) in plan.DigestBaseline)
        {
            var cur = await ex.BankDigestAsync(obj, bank).ConfigureAwait(false);
            if (cur != null && !cur.AsSpan().SequenceEqual(baseline))
                return (false, steps, $"ABORT: {StoreLabel(obj, bank)} changed since preview (edited at the panel?) — nothing was Stored");
        }
        Note("staleness gate passed");

        // 3. Send all object writes (volatile).
        foreach (var w in plan.Writes)
        {
            int rc = await ex.WriteObjectAsync(w).ConfigureAwait(false);
            Note($"write 0x73 {StoreLabel(w.Obj, w.Bank)} idx {w.Index} ({w.Note}) -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: write rejected (Reply {rc}) for {StoreLabel(w.Obj, w.Bank)} idx {w.Index} — nothing Stored; replay backups to be safe");
        }

        // 4. Commit each affected bank.
        foreach (var (obj, bank) in plan.Stores)
        {
            int rc = await ex.StoreBankAsync(obj, bank).ConfigureAwait(false);
            Note($"Store 0x76 {StoreLabel(obj, bank)} -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: Store rejected (Reply {rc}) for {StoreLabel(obj, bank)} — replay backups");
        }

        // 5. Optional live edit-buffer dual-write (audible-now, non-persisting).
        if (doLive && plan.LivePc.Count > 0)
        {
            foreach (var msg in plan.LivePc) await ex.SendRawAsync(msg).ConfigureAwait(false);
            Note($"live 0x43 x{plan.LivePc.Count} to active combi");
        }

        return (true, steps, null);
    }

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

        // 3. Combi timbre reference patch round-trip + timbre-15 offset.
        var combi = new byte[7810];
        for (int t = 0; t < LibRefs.TimbreCount; t++)
            LibRefs.SetCombiTimbreRef(combi, t, t % 30, (t * 3) & 0x7F);
        foreach (var (t, bank, num) in LibRefs.IterCombiTimbreRefs(combi))
            Check($"timbre-{t}", bank == t % 30 && num == ((t * 3) & 0x7F));

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
        int fbDst = KronosBanks.ObjBankToFunc33(1, 0x40);   // 18
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

        // 6. RefIndex agrees with the catalog + freshness gate flags a changed digest.
        var ri = new RefIndex();
        ri.AddCombi(new ObjectDump(LibObj.Combi, 0x00, 0, 3, cbody));
        ri.AddSetlist(new ObjectDump(LibObj.SetList, 0, 0, 0, slbody));
        Check("refindex-usage", ri.UsageCount(src) == 2 && ri.UsageCount(dst) == 1);
        Check("refindex-ids", ri.ReferrerObjectIds(src).SetEquals(
            new HashSet<(int, int, int)> { (LibObj.Combi, 0x00, 0), (LibObj.SetList, 0, 0) }));

        return fails;
    }
}
