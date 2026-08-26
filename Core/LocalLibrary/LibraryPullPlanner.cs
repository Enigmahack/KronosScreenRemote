namespace KronosScreenRemote;

// Covers ALL registry object types' banks uniformly, including Programs - this cache needs
// every object's body, not just referrers. Read-only GM/g program banks are excluded
// (ObjectTypeRegistry's EditableBanks already scopes to the 21 writable program banks) -
// GM/g factory-content browsing is explicitly future scope, not v1.
static class LibraryPullPlanner
{
    public sealed record BankRef(int ObjType, int Bank);
    public sealed record PullPlan(List<BankRef> BanksToFetch, bool FirstRun);

    public static IEnumerable<BankRef> AllBanks() =>
        ObjectTypeRegistry.All.SelectMany(d => d.EditableBanks().Select(b => new BankRef(d.ObjType, b)));

    // PURE. A bank with no persisted digest baseline (never pulled) is always "changed" -
    // same "unknown = needs work" convention Storage's dumped-bank ledger and
    // LibraryRepository.PlanScan both already use.
    public static PullPlan PlanPull(
        IReadOnlyDictionary<(int ObjType, int Bank), string> persistedDigests,
        IReadOnlyDictionary<(int ObjType, int Bank), string> freshDigests,
        bool full)
    {
        bool firstRun = persistedDigests.Count == 0;
        var all = AllBanks().ToList();
        if (full) return new PullPlan(all, firstRun);

        bool Changed(BankRef b) =>
            !persistedDigests.TryGetValue((b.ObjType, b.Bank), out var baseline) ||
            !freshDigests.TryGetValue((b.ObjType, b.Bank), out var cur) ||
            cur != baseline;

        return new PullPlan(all.Where(Changed).ToList(), firstRun);
    }
}
