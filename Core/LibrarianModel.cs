namespace KronosScreenRemote;

// Librarian model - catalog, dependency graph, and coherent-move planning.
//
// A "move" swaps a Program or Combi with another slot and rewrites every Combi
// timbre and Set List slot that referenced either, so nothing is left dangling
// (the instrument does NOT auto-repair references). Ported from the Python
// KronosScreenRemotePy librarian's bank-encoding + reference logic.
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

    // The instrument's single Global settings object (bank 0, index 0). NOT a Librarian object
    // type - it's never catalogued, moved, placed or pushed; the Librarian reads exactly one
    // thing out of it (the Category/Sub-Category names, see GlobalBody), which is why it has no
    // ObjectTypeRegistry descriptor and no CurrentObjectVersion entry.
    public const int Global  = 0x03;

    // The func-0x73 Object Dump "version" byte the CURRENT Kronos OS's documented structure
    // uses per type (Documentation/MIDI implementation/SysExDumps/{Prog_HD-1,
    // Prog_EXi_Common,CombiAndSongTimbreSet,SetList}.txt, each headed "Object Version: N" -
    // Program is 5 for both HD-1 and EXi, Combi is 3, Set List is 0). This is a fixed
    // constant per type, not something a .pcg file carries (it has no such field) or that
    // should be preserved from wherever an entry happened to originate - every PCG-import
    // path (MergeCache.PullRecursive, LibrarianShellViewModel.PlaceFromPcg/BatchPlaceFromPcg)
    // used to default this to a placeholder 0, which is wrong for Program (0 was coincidentally
    // right only for Set List) and produced a func-0x24 Reply Code 3 ("short or otherwise
    // mangled message") on a real hardware Program write despite a byte-perfect body. Null for
    // object types outside the Librarian's 3 known ones (e.g. name-only sub-dumps), which keep
    // whatever version they already carry.
    public static byte? CurrentObjectVersion(int objType) => objType switch
    {
        Program => 5,
        Combi   => 3,
        SetList => 0,
        _       => null,
    };
}

// A movable object location, addressed in object-dump (header) encoding.
readonly record struct ObjLoc(int ObjType, int Bank, int Number)
{
    public string Label() => ObjType switch
    {
        LibObj.Program => $"{KronosBanks.ProgramLabel(Bank)}:{Number:D3}",
        LibObj.SetList => $"Set List {Number:D2}",
        _              => $"{KronosBanks.CombiLabel(Bank)}:{Number:D3}",
    };
}

// What a right-click "Rescan" at a given tree node means: one slot (Number set), one
// bank (Bank set, Number null), or every bank of that type (both null). Used instead
// of re-deriving scope from a node's label, which is ambiguous (Program and Combi
// trees reuse the same bank letters).
readonly record struct RescanScope(int ObjType, int? Bank, int? Number)
{
    public string Describe()
    {
        string typeName = ObjType switch { LibObj.Program => "Program", LibObj.Combi => "Combi", _ => "Set List" };
        if (Number is int n)
            return ObjType == LibObj.SetList ? $"Set List {n:D2}" : $"{typeName} {BankLabel()}:{n:D3}";
        if (Bank is int)
            return $"{typeName} bank {BankLabel()}";
        return $"all {typeName}s";
    }

    string BankLabel() => ObjType == LibObj.Program ? KronosBanks.ProgramLabel(Bank!.Value) : KronosBanks.CombiLabel(Bank!.Value);
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

// What ArmPlanAsync/ApplyMoveAsync actually need from a plan - implemented by both the
// single-pair MovePlan and (Core/BatchMoveModel.cs) BatchMovePlan, so both share the exact
// same backup/staleness-gate/write/Store/live-preview discipline with zero duplicated logic.
interface IExecutablePlan
{
    List<WriteOp> Writes { get; }
    List<WriteOp> PreImages { get; }
    List<(int Obj, int Bank)> Stores { get; }
    Dictionary<(int, int), byte[]> DigestBaseline { get; }
    List<byte[]> LivePc { get; }
    List<string> Warnings { get; }
    string BackupLabel { get; }            // filename-safe; ApplyMoveAsync prefixes it with the stamp
    bool IsRefusable { get; }

    // Program banks whose HD-1/EXi type this plan changes (func 0x7C) before writing them -
    // only the whole-bank-copy changeset (requirement 4) ever populates this; every other plan
    // shape inherits the empty default. Applied by ApplyMoveAsync after the staleness gate and
    // before the object writes, since 0x7C erases the bank.
    IReadOnlyList<(int Bank, bool IsExi)> BankTypeChanges => Array.Empty<(int, bool)>();
}

sealed class MovePlan : IExecutablePlan
{
    public ObjLoc Src, Dst;
    public List<WriteOp> Writes { get; } = new();
    public List<WriteOp> PreImages { get; } = new();       // ORIGINAL objects (backup/restore)
    public List<(int Obj, int Bank)> Stores { get; } = new();   // banks to Store (deduped)
    public List<ReferrerSite> Referrers = new();
    public List<string> Preview = new();
    public List<string> Warnings { get; } = new();
    public List<byte[]> LivePc { get; } = new();           // optional 0x43 dual-write
    public Dictionary<(int, int), byte[]> DigestBaseline { get; } = new();

    public bool IsRefusable => Warnings.Any(w => w.StartsWith("REFUSE:", StringComparison.Ordinal));
    public string BackupLabel => $"move_{Src.Label()}_{Dst.Label()}".Replace(":", "").Replace(" ", "");
}

// Read/patch the (bank, number) reference bytes inside a DECODED object body.
static class LibRefs
{
    public const int TimbreCount = 16;
    const int Timbre0Num  = 4802;   // timbre 0 program NUMBER byte
    const int Timbre0Bank = 4803;   // timbre 0 program BANK byte (internal linear)
    const int TimbreStride = 188;

    // Set-list slot layout mirrors SetListData.
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
            int b = Timbre0Num + t * TimbreStride;
            if (b + 1 >= body.Length) yield break;   // truncated/short dump - stop, don't throw
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
        if (loc.ObjType == LibObj.SetList) return outp;   // nothing ever references a Set List

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

    // Change a Program bank's HD-1/EXi type (func 0x7C) - REFORMATS AND ERASES the bank when
    // the type actually changes (requirement 4). Reply code (0 = OK); -1 = timeout. Issued by
    // ApplyMoveAsync after the staleness gate and before that bank's writes, since the whole
    // bank must be re-written after the erase.
    Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi);
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

    // ── Name field helpers (Program/Combi bodies: 24-byte ASCII name at offset 0) ──
    // Used by both a single-slot rename write (LibrarianWindow's double-click-to-rename)
    // and anywhere else that needs to read or patch just the name without touching the
    // rest of an object's body.

    public static byte[] PadAscii(string s, int len)
    {
        var data = new byte[len];
        Array.Fill(data, (byte)0x20);
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, data, Math.Min(bytes.Length, len));
        return data;
    }

    public static string ReadName(byte[] body) =>
        System.Text.Encoding.ASCII.GetString(body, 0, Math.Min(24, body.Length)).TrimEnd('\0', ' ');

    // Same bytes as `original`, only the first 24 (the name field) replaced - every other
    // byte of the object (parameters, timbres, whatever) is preserved exactly.
    public static byte[] BuildRenamedBody(byte[] original, string newName)
    {
        var body = (byte[])original.Clone();
        var padded = PadAscii(newName, 24);
        Array.Copy(padded, body, Math.Min(24, body.Length));
        return body;
    }

    // Compute a coherent swap(src, dst). PURE - no hardware access. srcDump/dstDump
    // are the freshly dumped bodies of the two objects being swapped.
    public static MovePlan PlanMove(LibraryCatalog cat, ObjLoc src, ObjectDump srcDump,
                                    ObjLoc dst, ObjectDump dstDump, ObjLoc? active = null)
    {
        var plan = new MovePlan { Src = src, Dst = dst };

        if (src.ObjType != dst.ObjType)
            plan.Warnings.Add(AppMessages.Librarian.Move.CannotMoveBetweenTypes);
        if (src.ObjType == LibObj.Program && ReadOnlyProgramBanks.Contains(dst.Bank))
            plan.Warnings.Add(AppMessages.Librarian.Move.DestinationReadOnlyBank(dst.Label()));
        if (src.Equals(dst))
            plan.Warnings.Add(AppMessages.Librarian.Move.SameLocation);

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
                plan.Warnings.Add(AppMessages.Librarian.Move.ReferringObjectMissing(refObj, refBank, refIndex));
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

        // (5) Program bank-type reminder (HD-1/EXi) - enforced at apply via Reply 64.
        if (src.ObjType == LibObj.Program && src.Bank != dst.Bank)
            plan.Warnings.Add(AppMessages.Librarian.Move.CheckProgramMoveAcrossBanks);

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

    // Capture the storage digest of every affected bank - the staleness baseline
    // ApplyMoveAsync re-checks immediately before Storing.
    public static async Task ArmPlanAsync(IExecutablePlan plan, IMoveExecutor ex)
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
        IExecutablePlan plan, IMoveExecutor ex, string backupDir, string stamp,
        Action<string>? progress = null, bool doLive = true)
    {
        var steps = new List<string>();
        void Note(string m) { steps.Add(m); progress?.Invoke(m); }

        if (plan.IsRefusable)
            return (false, steps, string.Join("; ", plan.Warnings));

        // 1. Backup the pre-image of every object we overwrite (restore = replay + Store).
        var safe = $"{stamp}_{plan.BackupLabel}";
        var backupPath = System.IO.Path.Combine(backupDir, safe + ".syx");
        Note($"backup {plan.PreImages.Count} pre-image object(s) -> {backupPath}");
        await ex.BackupObjectsAsync(plan.PreImages, backupPath).ConfigureAwait(false);

        // 2. Staleness gate - abort if any affected bank changed since arm.
        foreach (var ((obj, bank), baseline) in plan.DigestBaseline)
        {
            var cur = await ex.BankDigestAsync(obj, bank).ConfigureAwait(false);
            if (cur != null && !cur.AsSpan().SequenceEqual(baseline))
                return (false, steps, $"ABORT: {StoreLabel(obj, bank)} changed since preview (edited at the panel?) - nothing was Stored");
        }
        Note("staleness gate passed");

        // 2b. Program bank-type changes (func 0x7C) - REFORMATS AND ERASES each bank to the
        // requested EXi/HD-1 type. After the staleness gate (an external change is still caught
        // first) but before the writes, because 0x7C erases the bank; the changeset guarantees
        // every slot of that bank is in Writes so the whole bank is rebuilt immediately after.
        foreach (var (bank, isExi) in plan.BankTypeChanges)
        {
            int rc = await ex.ChangeProgramBankTypeAsync(bank, isExi).ConfigureAwait(false);
            Note($"change bank type {KronosBanks.ProgramLabel(bank)} -> {(isExi ? "EXi" : "HD-1")} -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: bank-type change rejected (Reply {rc}) for {KronosBanks.ProgramLabel(bank)} - nothing Stored");
        }

        // 3. Send all object writes (volatile).
        foreach (var w in plan.Writes)
        {
            int rc = await ex.WriteObjectAsync(w).ConfigureAwait(false);
            Note($"write 0x73 {StoreLabel(w.Obj, w.Bank)} idx {w.Index} ({w.Note}) -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: write rejected (Reply {rc}) for {StoreLabel(w.Obj, w.Bank)} idx {w.Index} - nothing Stored; replay backups to be safe");
        }

        // 4. Commit each affected bank.
        foreach (var (obj, bank) in plan.Stores)
        {
            int rc = await ex.StoreBankAsync(obj, bank).ConfigureAwait(false);
            Note($"Store 0x76 {StoreLabel(obj, bank)} -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: Store rejected (Reply {rc}) for {StoreLabel(obj, bank)} - replay backups");
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
