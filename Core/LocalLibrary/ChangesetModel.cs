namespace KronosScreenRemote;

// The push half of the Sync/Commit pipeline's plan shape — a new *shape* of
// IExecutablePlan (Core/LibrarianModel.cs, unchanged), covering the whole dirty set
// instead of one move/batch. Executed by the exact same Librarian.ArmPlanAsync/
// ApplyMoveAsync (backup -> staleness gate -> write -> Store) every other plan uses.
sealed class ChangesetPlan : IExecutablePlan
{
    public List<WriteOp> Writes { get; } = new();
    public List<WriteOp> PreImages { get; } = new();
    public List<(int Obj, int Bank)> Stores { get; } = new();
    public Dictionary<(int, int), byte[]> DigestBaseline { get; } = new();
    public List<byte[]> LivePc { get; } = new();   // always empty — requirement 17, no live 0x43 anywhere in the new pipeline
    public List<string> Warnings { get; } = new();
    public string BackupLabel => "changeset";
    public bool IsRefusable => Warnings.Any(w => w.StartsWith("REFUSE:", StringComparison.Ordinal));

    // Program banks this changeset reformats to HD-1/EXi (func 0x7C) before writing them —
    // requirement 4's whole-bank type change. Implements IExecutablePlan.BankTypeChanges (whose
    // default is empty); ApplyMoveAsync issues each one after the staleness gate, before writes.
    readonly List<(int Bank, bool IsExi)> _bankTypeChanges = new();
    public IReadOnlyList<(int Bank, bool IsExi)> BankTypeChanges => _bankTypeChanges;
    public void AddBankTypeChange(int bank, bool isExi)
    {
        if (!_bankTypeChanges.Any(x => x.Bank == bank)) _bankTypeChanges.Add((bank, isExi));
    }

    // Local objects to advance baseline for on a successful push — distinct from
    // Writes/Stores (hardware-facing); SyncPipeline uses this afterward to update the cache.
    public List<ObjLoc> TargetsOnSuccess { get; } = new();

    // Committed deletions of objects that EXIST on hardware (requirement 2). Each contributes an
    // erase WriteOp + Store (blanking the slot on the instrument); on success the LOCAL slot is
    // advanced to that same blank body — it stays in the tree showing the init/blank object at its
    // address, NOT removed (a bank slot never truly vanishes on the Kronos). BlankBody is the
    // exact bytes written, so the local slot ends up byte-identical to hardware.
    public List<(ObjLoc Loc, byte Version, byte[] BlankBody)> Erasures { get; } = new();

    // Committed deletions of LOCAL-ONLY objects (placed but never pushed — no hardware baseline).
    // Nothing to erase on the instrument (the slot is genuinely empty there), so these are simply
    // removed from the index on success — undoing the local placement back to the empty slot.
    public List<ObjLoc> Deletes { get; } = new();
}
