namespace KronosScreenRemote;

// Librarian model vocabulary - the types every other Librarian file addresses objects and
// plans with. The behavior that used to sit alongside them lives in its own files now:
// LibRefs.cs (the reference-byte codec), LibraryCatalog.cs (the referrer scan/index),
// Librarian.cs (plan/arm/apply), LibrarianSelfTests.cs (the off-hardware checks).
//
// A "move" swaps a Program or Combi with another slot and rewrites every Combi
// timbre and Set List slot that referenced either, so nothing is left dangling
// (the instrument does NOT auto-repair references). Ported from the Python
// KronosScreenRemotePy librarian's bank-encoding + reference logic.
//
// Split for testability: PlanMove is PURE (Librarian.SelfTest exercises it
// off-hardware); only ApplyMoveAsync talks to the instrument, through IMoveExecutor.
//
// Referrer scope: Combi timbres + Set List slots. Song Timbre Sets (obj 0x02)
// are intentionally out of scope.

static class LibObj
{
    public const int Program      = 0x00;
    public const int Combi        = 0x01;
    public const int DrumKit      = 0x04;
    public const int WaveSequence = 0x05;
    public const int SetList      = 0x0D;

    // The instrument's single Global settings object (bank 0, index 0). NOT a Librarian object
    // type - it's never catalogued, moved, placed or pushed; the Librarian reads exactly one
    // thing out of it (the Category/Sub-Category names, see GlobalBody), which is why it has no
    // ObjectTypeRegistry descriptor and no CurrentObjectVersion entry. PcgObjectExtractor DOES
    // extract a Global entry (bank=0/index=0, checksum-checked, body otherwise unused - see its
    // GLB1 branch), so an ObjLoc with this ObjType is reachable from a loaded .pcg file. Never
    // hand one to ObjectTypeRegistry.Get - it would throw (no descriptor is registered).
    public const int Global  = 0x03;

    // The func-0x73 Object Dump "version" byte the CURRENT Kronos OS's documented structure
    // uses per type (Documentation/MIDI implementation/SysExDumps/{Prog_HD-1,
    // Prog_EXi_Common,CombiAndSongTimbreSet,SetList,DrumKit,WaveSequence}.txt, each headed
    // "Object Version: N" - Program is 5 for both HD-1 and EXi, Combi is 3, Set List is 0,
    // Drum Kit is 3, Wave Seq is 1). This is a fixed constant per type, not something a .pcg
    // file carries (it has no such field) or that should be preserved from wherever an entry
    // happened to originate - every PCG-import path (MergeCache.PullRecursive,
    // LibrarianShellViewModel.PlaceFromPcg/BatchPlaceFromPcg) used to default this to a
    // placeholder 0, which is wrong for Program (0 was coincidentally right only for Set List)
    // and produced a func-0x24 Reply Code 3 ("short or otherwise mangled message") on a real
    // hardware Program write despite a byte-perfect body. Null for object types outside the
    // Librarian's known ones (e.g. name-only sub-dumps), which keep whatever version they
    // already carry.
    public static byte? CurrentObjectVersion(int objType) => objType switch
    {
        Program      => 5,
        Combi        => 3,
        SetList      => 0,
        DrumKit      => 3,
        WaveSequence => 1,
        _            => null,
    };
}

// A movable object location, addressed in object-dump (header) encoding.
readonly record struct ObjLoc(int ObjType, int Bank, int Number)
{
    public string Label() => ObjType switch
    {
        LibObj.Program      => $"{KronosBanks.ProgramLabel(Bank)}:{Number:D3}",
        LibObj.SetList      => $"Set List {Number:D2}",
        LibObj.DrumKit      => $"{KronosBanks.DrumKitLabel(Bank)}:{Number:D3}",
        LibObj.WaveSequence => $"{KronosBanks.WaveSeqLabel(Bank)}:{Number:D3}",
        _                   => $"{KronosBanks.CombiLabel(Bank)}:{Number:D3}",
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
        string typeName = ObjType switch
        {
            LibObj.Program      => "Program",
            LibObj.DrumKit      => "Drum Kit",
            LibObj.WaveSequence => "Wave Sequence",
            LibObj.SetList      => "Set List",
            _                   => "Combi",
        };
        if (Number is int n)
            return ObjType == LibObj.SetList ? $"Set List {n:D2}" : $"{typeName} {BankLabel()}:{n:D3}";
        if (Bank is int)
            return $"{typeName} bank {BankLabel()}";
        return $"all {typeName}s";
    }

    string BankLabel() => ObjType switch
    {
        LibObj.Program      => KronosBanks.ProgramLabel(Bank!.Value),
        LibObj.DrumKit      => KronosBanks.DrumKitLabel(Bank!.Value),
        LibObj.WaveSequence => KronosBanks.WaveSeqLabel(Bank!.Value),
        _                   => KronosBanks.CombiLabel(Bank!.Value),
    };
}

// One reference site pointing at a movable object.
readonly record struct ReferrerSite(
    RefKind Kind, int RefObj, int RefBank, int RefIndex, int Site, int CurBank, int CurNumber)
{
    public string Describe() => Kind switch
    {
        RefKind.CombiTimbre => $"Combi {KronosBanks.CombiLabel(RefBank)}:{RefIndex:D3} {RefKinds.Describe(Kind, Site)}",
        RefKind.SetListSlot => $"Set List {RefIndex:D3} {RefKinds.Describe(Kind, Site)}",
        _                   => $"Program {KronosBanks.ProgramLabel(RefBank)}:{RefIndex:D3} {RefKinds.Describe(Kind, Site)}",
    };
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

    // Arm-time reading of IMoveExecutor.StorageChangeCountFor per affected bank. A bank ABSENT
    // here could not be watched at arm time (no live stream), which ApplyMoveAsync's step 3b
    // reports rather than silently passing.
    Dictionary<(int, int), int> StorageChangeBaseline { get; }
    List<byte[]> LivePc { get; }
    List<PlanWarning> Warnings { get; }
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
    public List<PlanWarning> Warnings { get; } = new();
    public List<byte[]> LivePc { get; } = new();           // optional 0x43 dual-write
    public Dictionary<(int, int), byte[]> DigestBaseline { get; } = new();
    public Dictionary<(int, int), int> StorageChangeBaseline { get; } = new();

    public bool IsRefusable => Warnings.AnyRefusal();
    public string BackupLabel => $"move_{Src.Label()}_{Dst.Label()}".Replace(":", "").Replace(" ", "");
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

    // How many UNSOLICITED func-0x38 storage-change notifications this session has seen for one
    // bank. Monotonic and meaningless in absolute terms - arm a baseline, compare later. The
    // instrument pushes one for a front-panel Store, a PCG load and a bank-type change, and
    // explicitly NOT for the func-0x73 object writes a commit is made of (KRONOS_MIDI_SysEx.txt
    // [38]), which is what makes it the only usable detector for a panel Store landing mid-burst.
    //
    // NULL means pushes cannot be observed at all right now (no live MIDI stream) - deliberately
    // distinct from 0, "observed, nothing seen". A caller must never read null as "no change":
    // ApplyMoveAsync reports such a bank as unwatched, the same way the digest gate reports a
    // bank that answers no digest.
    int? StorageChangeCountFor(int obj, int bank);
}
