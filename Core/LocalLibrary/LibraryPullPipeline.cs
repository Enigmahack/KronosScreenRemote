namespace KronosScreenRemote;

// Orchestrates a full-library Pull: digest every registry bank, diff via
// LibraryPullPlanner (lazy by default, or force everything), re-dump flagged banks'
// slots, and reconcile each re-dumped object against the local cache's dirty state.
//
// A bank that changed on hardware AND has a locally-dirty object in it is a CONFLICT for
// that object: the edit and its old baseline are both left untouched, and it's flagged
// Conflicted for the user to resolve later. Every other re-dumped object (not dirty, or
// dirty but its bank didn't change) advances its baseline to the fresh body - a plain,
// unconflicted refresh.
static class LibraryPullPipeline
{
    // Aborted is non-null only when the sweep gave up before doing any work because the
    // instrument answered nothing at all (see NoReplyGiveUp) - every normal outcome, including
    // a cancelled or entirely empty pull, leaves it null.
    public sealed record PullResult(int BanksChecked, int ObjectsFetched, int Conflicts, PlanWarning? Aborted = null);

    // Consecutive unanswered digest requests tolerated at the START of the sweep before it gives
    // up. Program INT A is the first bank AllBanks() yields and always answers on a live
    // instrument, so a run of silence there is never a per-bank quirk - it means the instrument
    // is not talking at all (most commonly SysEx switched off in GLOBAL > MIDI).
    //
    // Without this the failure was invisible rather than loud: every request times out instead of
    // erroring, so the sweep alone is 65 banks x 5 s, and a Force Full Sync then goes on to
    // attempt every registry bank's 128 slots at 6 s each - days of "Indexing local library..."
    // with no way to tell it apart from a hang.
    const int NoReplyGiveUp = 4;

    // Baseline value meaning "this bank answered no digest at all" - see PullAsync's own
    // comment. Distinct from every real digest (40 hex chars) and from "no baseline recorded
    // yet" (the key being absent), which is what makes a first pull still sweep the bank once.
    public const string NoDigest = "";

    public static async Task<PullResult> PullAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, bool full,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var persisted = cache.BankDigestBaselineHex();
        var fresh = new Dictionary<(int, int), string>();
        var noDigest = new List<(int, int)>();
        // Checked per bank, not just in the fetch loop below - this digest sweep alone is
        // ~100-200 round trips over EVERY registry bank regardless of `full`, so a window closed
        // (or a Sync cancelled) mid-sweep must stop here too, not keep querying every remaining
        // bank before cancellation ever gets a chance to matter. A partial `fresh` is safe:
        // PlanPull already treats a missing digest the same as NoDigest (see below), which is
        // conservative (re-checks more next time) rather than lossy.
        int silentRun = 0;
        foreach (var b in LibraryPullPlanner.AllBanks())
        {
            if (ct.IsCancellationRequested) break;
            var d = await sysEx.BankDigestAsync(b.ObjType, b.Bank).ConfigureAwait(false);
            if (d != null)
            {
                fresh[(b.ObjType, b.Bank)] = Convert.ToHexString(d).ToLowerInvariant();
                silentRun = 0;
                continue;
            }
            noDigest.Add((b.ObjType, b.Bank));
            // Only while NOTHING has answered yet. A run of nulls LATER in the sweep is normal
            // (a whole object type the unit gives no digest for), and the NoDigest sentinel below
            // is the right answer for those; a run of nulls from the very first bank is not.
            // Returning here leaves every persisted baseline untouched, so the next connected
            // Sync is unaffected - nothing has been written at this point.
            if (fresh.Count == 0 && ++silentRun >= NoReplyGiveUp)
            {
                AppLog.Warn($"[librarian] pull aborted: {silentRun} banks answered no digest and none answered at all");
                return new PullResult(0, 0, 0, AppMessages.Librarian.Sync.RefuseNoInstrumentReply);
            }
        }

        // A bank the instrument never answers a digest request for still needs a PERSISTED
        // baseline, or it is "changed" forever: LibraryPullPlanner.PlanPull treats a missing
        // fresh OR missing persisted digest as changed, so without this the bank was re-swept
        // in full on EVERY lazy Sync Library - 128 slots, and (since a bank that won't answer
        // a digest generally won't answer a bulk dump either) 128 individual DumpObjectAsync
        // round-trips through the bulk-empty fallback below. The NoDigest sentinel is the same
        // empty-string convention ChangesetBuilder's conflict
        // pre-scan and LocalLibraryIndex.NoBaselineSentinel already use, and it can never
        // collide with a real digest (always 40 hex chars). A bank pinned this way is then
        // only re-fetched by an explicit Force Pull-All, which bypasses Changed() entirely.
        //
        // Only when at least one bank DID answer, though: if none did, the instrument is
        // unreachable rather than quiet about one bank, and overwriting every good baseline
        // with the sentinel would silently mark the whole library up to date. Leaving those
        // baselines untouched keeps the next connected sync honest.
        if (fresh.Count > 0)
            foreach (var key in noDigest) fresh[key] = NoDigest;

        var plan = LibraryPullPlanner.PlanPull(persisted, fresh, full);

        var utcNow = DateTime.UtcNow;
        int fetched = 0, conflicts = 0, done = 0;
        int total = plan.BanksToFetch.Sum(b => ObjectTypeRegistry.Get(b.ObjType).SlotCount(b.Bank));
        // Accumulated in memory and written in ONE batch after the loop - see
        // LocalLibraryCache.RecordPullBaselines's own comment for why: appending one
        // op-log line per object (a full pull can mean thousands) meant thousands of
        // separate file-append operations, which over an SMB-mounted DataDir turned a
        // routine Sync Library into a multi-minute stall.
        var pulled = new List<(int ObjType, int Bank, int Number, byte Version, byte[] Body)>();

        foreach (var bankRef in plan.BanksToFetch)
        {
            // Same reasoning as the digest sweep above: without this, a cancelled pull still
            // issued one more full DumpBankBulkAsync round trip per remaining bank before the
            // per-slot check further down ever got a chance to observe cancellation - for a
            // Force Full Sync that's potentially every registry bank, not "stops within a call."
            if (ct.IsCancellationRequested) break;
            var descriptor = ObjectTypeRegistry.Get(bankRef.ObjType);
            bool bankChangedOnHardware =
                !persisted.TryGetValue((bankRef.ObjType, bankRef.Bank), out var baseHex) ||
                !fresh.TryGetValue((bankRef.ObjType, bankRef.Bank), out var freshHex) ||
                freshHex != baseHex;

            // One whole-bank request instead of up to 128 individual round-trips - much
            // faster when the Kronos accepts it. HW-unverified for full objects/USER banks
            // (see ISysExService.DumpBankBulkAsync's own comment).
            //
            // A real bulk reply legitimately omits empty slots (most banks are sparse, not
            // fully populated) - so "not in the bulk result" must NOT mean "retry
            // individually" whenever the bulk request clearly worked, or bulk dumping would
            // buy nothing for exactly the common case it's meant to speed up. Only treat a
            // COMPLETELY EMPTY bulk result (bulk.Count == 0) as ambiguous - rejected vs. a
            // genuinely fully-empty bank look identical at this layer - and fall back to a
            // full per-slot sweep only in that one case, same as if bulk didn't exist.
            progress?.Invoke(AppMessages.Librarian.Sync.BulkDumping(descriptor.DisplayName, descriptor.BankLabel(bankRef.Bank)));
            var bulk = await sysEx.DumpBankBulkAsync(bankRef.ObjType, bankRef.Bank, descriptor.SlotCount(bankRef.Bank)).ConfigureAwait(false);
            bool bulkWorked = bulk.Count > 0;

            for (int number = 0; number < descriptor.SlotCount(bankRef.Bank); number++)
            {
                if (ct.IsCancellationRequested) break;
                ObjectDump? dump = bulk.TryGetValue(number, out var bulkDump) ? bulkDump
                    : bulkWorked ? null   // bulk worked and omitted this slot -> confirmed empty
                    : await sysEx.DumpObjectAsync(bankRef.ObjType, bankRef.Bank, number).ConfigureAwait(false);
                done++;
                progress?.Invoke(AppMessages.Librarian.Sync.Pulling(done, total, descriptor.DisplayName, descriptor.BankLabel(bankRef.Bank), number));
                if (dump == null) continue;   // empty slot - nothing to pull

                // A dirty object is NEVER overwritten by a pull, full or lazy - whether its
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
