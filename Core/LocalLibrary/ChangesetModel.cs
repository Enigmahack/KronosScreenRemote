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

    // Local objects to advance baseline for on a successful push — distinct from
    // Writes/Stores (hardware-facing); SyncPipeline uses this afterward to update the cache.
    public List<ObjLoc> TargetsOnSuccess { get; } = new();
}
