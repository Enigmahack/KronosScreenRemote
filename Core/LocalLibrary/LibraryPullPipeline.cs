namespace KronosScreenRemote;

// Orchestrates a full-library Pull: digest every registry bank, diff via
// LibraryPullPlanner (lazy by default, or force everything), re-dump flagged banks'
// slots, and reconcile each re-dumped object against the local cache's dirty state.
//
// A bank that changed on hardware AND has a locally-dirty object in it is a CONFLICT for
// that object: the edit and its old baseline are both left untouched, and it's flagged
// Conflicted for the user to resolve later. Every other re-dumped object (not dirty, or
// dirty but its bank didn't change) advances its baseline to the fresh body — a plain,
// unconflicted refresh.
static class LibraryPullPipeline
{
    public sealed record PullResult(int BanksChecked, int ObjectsFetched, int Conflicts);

    public static async Task<PullResult> PullAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, bool full,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var persisted = cache.BankDigestBaselineHex();
        var fresh = new Dictionary<(int, int), string>();
        foreach (var b in LibraryPullPlanner.AllBanks())
        {
            var d = await sysEx.BankDigestAsync(b.ObjType, b.Bank).ConfigureAwait(false);
            if (d != null) fresh[(b.ObjType, b.Bank)] = Convert.ToHexString(d).ToLowerInvariant();
        }

        var plan = LibraryPullPlanner.PlanPull(persisted, fresh, full);

        var utcNow = DateTime.UtcNow;
        int fetched = 0, conflicts = 0, done = 0;
        int total = plan.BanksToFetch.Sum(b => ObjectTypeRegistry.Get(b.ObjType).SlotCount);
        // Accumulated in memory and written in ONE batch after the loop — see
        // LocalLibraryCache.RecordPullBaselines's own comment for why: appending one
        // op-log line per object (a full pull can mean thousands) meant thousands of
        // separate file-append operations, which over an SMB-mounted DataDir turned a
        // routine Sync Library into a multi-minute stall.
        var pulled = new List<(int ObjType, int Bank, int Number, byte Version, byte[] Body)>();

        foreach (var bankRef in plan.BanksToFetch)
        {
            var descriptor = ObjectTypeRegistry.Get(bankRef.ObjType);
            bool bankChangedOnHardware =
                !persisted.TryGetValue((bankRef.ObjType, bankRef.Bank), out var baseHex) ||
                !fresh.TryGetValue((bankRef.ObjType, bankRef.Bank), out var freshHex) ||
                freshHex != baseHex;

            // One whole-bank request instead of up to 128 individual round-trips — much
            // faster when the Kronos accepts it. HW-unverified for full objects/USER banks
            // (see ISysExService.DumpBankBulkAsync's own comment).
            //
            // A real bulk reply legitimately omits empty slots (most banks are sparse, not
            // fully populated) — so "not in the bulk result" must NOT mean "retry
            // individually" whenever the bulk request clearly worked, or bulk dumping would
            // buy nothing for exactly the common case it's meant to speed up. Only treat a
            // COMPLETELY EMPTY bulk result (bulk.Count == 0) as ambiguous — rejected vs. a
            // genuinely fully-empty bank look identical at this layer — and fall back to a
            // full per-slot sweep only in that one case, same as if bulk didn't exist.
            progress?.Invoke(AppMessages.Librarian.Sync.BulkDumping(descriptor.DisplayName, descriptor.BankLabel(bankRef.Bank)));
            var bulk = await sysEx.DumpBankBulkAsync(bankRef.ObjType, bankRef.Bank, descriptor.SlotCount).ConfigureAwait(false);
            bool bulkWorked = bulk.Count > 0;

            for (int number = 0; number < descriptor.SlotCount; number++)
            {
                if (ct.IsCancellationRequested) break;
                ObjectDump? dump = bulk.TryGetValue(number, out var bulkDump) ? bulkDump
                    : bulkWorked ? null   // bulk worked and omitted this slot -> confirmed empty
                    : await sysEx.DumpObjectAsync(bankRef.ObjType, bankRef.Bank, number).ConfigureAwait(false);
                done++;
                progress?.Invoke(AppMessages.Librarian.Sync.Pulling(done, total, descriptor.DisplayName, descriptor.BankLabel(bankRef.Bank), number));
                if (dump == null) continue;   // empty slot — nothing to pull

                // A dirty object is NEVER overwritten by a pull, full or lazy — whether its
                // bank changed on hardware only decides whether it ALSO gets flagged
                // Conflicted (a genuine "this might now be based on a stale baseline"
                // warning), not whether it's safe to refresh. Getting this backwards would
                // mean Force Pull-All silently discards every unpushed edit whose bank
                // happened not to change underneath it.
                if (cache.IsDirty(bankRef.ObjType, bankRef.Bank, number))
                {
                    if (bankChangedOnHardware)
                    {
                        cache.MarkConflicted(bankRef.ObjType, bankRef.Bank, number);
                        conflicts++;
                    }
                    continue;   // preserve the local edit AND the old baseline either way
                }

                pulled.Add((bankRef.ObjType, bankRef.Bank, number, dump.Version, dump.Body));
                fetched++;
            }
        }

        cache.RecordPullBaselines(pulled, utcNow);

        if (!ct.IsCancellationRequested)
        {
            foreach (var ((objType, bank), hex) in fresh)
                cache.SetBankDigestBaseline(objType, bank, hex);
            cache.Save();
        }

        return new PullResult(plan.BanksToFetch.Count, fetched, conflicts);
    }
}
