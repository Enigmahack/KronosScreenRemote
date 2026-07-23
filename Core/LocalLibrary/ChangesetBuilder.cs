namespace KronosScreenRemote;

// Builds a ChangesetPlan from whatever's currently dirty in the local cache — the Push
// half of the Sync/Commit pipeline. In order:
//  1. Dependency-completeness gate (requirement 14): REFUSE outright if the session
//     clipboard still has unresolved placements.
//  2. Conflict pre-scan: re-check each distinct (type,bank) containing a dirty object
//     against hardware's CURRENT digest. A mismatch excludes every dirty object in that
//     bank from this push (flagged Conflicted, same marker Pull uses) instead of silently
//     overwriting a possible concurrent front-panel edit.
//  3. Defense-in-depth referential check: any surviving dirty Combi/Set List whose
//     references point at a target with NO local body at all (never pulled/placed) is
//     REFUSEd — catches an edit+discard interaction leaving a referrer pointing at
//     nothing. (Presence only, not content-coherence — a discard reverting a target to
//     stale-but-present baseline content is a known, narrower residual gap; the real
//     hardware-corruption guard remains ApplyMoveAsync's pre-write backup + staleness gate.)
//  4. Assemble Writes/PreImages from the surviving dirty set.
static class ChangesetBuilder
{
    public static async Task<(ChangesetPlan Plan, List<ObjLoc> Conflicted)> BuildAsync(
        LocalLibraryCache cache, ILibrarianService sysEx, SessionDependencyClipboard sessionClip)
    {
        var plan = new ChangesetPlan();
        var conflicted = new List<ObjLoc>();

        if (sessionClip.Pending.Count > 0)
        {
            plan.Warnings.Add($"REFUSE: {sessionClip.Pending.Count} dependency(ies) still pending in the session clipboard — place them before pushing");
            return (plan, conflicted);
        }

        var dirty = cache.DirtyObjects().ToList();
        if (dirty.Count == 0)
        {
            plan.Warnings.Add("CHECK: nothing to push — no local changes are pending");
            return (plan, conflicted);
        }

        // Step 2: conflict pre-scan, one BankDigestAsync per distinct (type,bank).
        var baseline = cache.BankDigestBaselineHex();
        var excludedBanks = new HashSet<(int, int)>();
        foreach (var (objType, bank) in dirty.Select(loc => (loc.ObjType, loc.Bank)).Distinct())
        {
            var fresh = await sysEx.BankDigestAsync(objType, bank).ConfigureAwait(false);
            string freshHex = fresh != null ? Convert.ToHexString(fresh).ToLowerInvariant() : "";
            bool changed = !baseline.TryGetValue((objType, bank), out var baseHex) || freshHex != baseHex;
            if (changed) excludedBanks.Add((objType, bank));
        }

        var surviving = new List<ObjLoc>();
        foreach (var loc in dirty)
        {
            if (excludedBanks.Contains((loc.ObjType, loc.Bank)))
            {
                cache.MarkConflicted(loc.ObjType, loc.Bank, loc.Number);
                conflicted.Add(loc);
            }
            else surviving.Add(loc);
        }

        // Step 3: defense-in-depth referential check over the surviving set.
        foreach (var loc in surviving)
        {
            if (loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) continue;
            var body = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
            if (body == null) continue;
            foreach (var (missingRef, kind) in DependencyScanner.Scan(cache, loc.ObjType, body))
                plan.Warnings.Add($"REFUSE: {loc.Label()} references {missingRef.Label()} ({kind}), which does not exist locally");
        }

        // Step 3.5: Program EXi/HD-1 bank-type re-verification — a single FRESH func 0x61
        // query, right before ArmPlanAsync/ApplyMoveAsync ever touches hardware. This is
        // deliberately independent of LibrarianShellViewModel's own placement-time check
        // (BatchMoveModel.cs's PlanBatchMove, via BankTypeOf): that one runs against a
        // background-warmed cache that can still be null the instant after the Librarian
        // window opens (a real race the very first placement can lose), silently degrading
        // to a non-blocking CHECK and letting a genuine mismatch reach hardware as a Reply
        // Code 3 ("short or otherwise mangled message"). Querying fresh here — the one place
        // guaranteed to run right before the write — closes that gap regardless of timing.
        if (surviving.Any(l => l.ObjType == LibObj.Program))
        {
            var liveTypes = await sysEx.RequestProgramBankTypesAsync().ConfigureAwait(false);
            if (liveTypes is { } types)
            {
                foreach (var loc in surviving.Where(l => l.ObjType == LibObj.Program))
                {
                    if (KronosBanks.ProgramBankTypeBitIndex(loc.Bank) is not int bit || bit >= types.IsExi.Length) continue;
                    var body = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
                    if (body == null) continue;

                    bool isExi = types.IsExi[bit];
                    int expectedLen = isExi ? ProgramFormatConverter.WireSizeExi : ProgramFormatConverter.WireSizeHd1;
                    if (body.Length != expectedLen)
                        plan.Warnings.Add($"REFUSE: {loc.Label()} is a {(isExi ? "EXi" : "HD-1")} bank " +
                            $"({expectedLen}-byte Programs), but the pending write is {body.Length} bytes — wrong format for this bank.");
                }
            }
        }

        if (plan.IsRefusable) return (plan, conflicted);

        // Step 4: assemble the plan.
        foreach (var loc in surviving)
        {
            var current = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
            var baselineBody = cache.GetBaselineBody(loc.ObjType, loc.Bank, loc.Number);
            var version = cache.GetVersion(loc.ObjType, loc.Bank, loc.Number);
            if (current == null || version == null) continue;

            plan.Writes.Add(new WriteOp(loc.ObjType, loc.Bank, loc.Number, version.Value, current, loc.Label()));
            if (baselineBody != null)   // null = confirmed-empty hardware slot per the last Pull — nothing to back up
                plan.PreImages.Add(new WriteOp(loc.ObjType, loc.Bank, loc.Number, version.Value, baselineBody, "original"));
            if (!plan.Stores.Contains((loc.ObjType, loc.Bank))) plan.Stores.Add((loc.ObjType, loc.Bank));
            plan.TargetsOnSuccess.Add(loc);
        }

        if (plan.Writes.Count == 0)
            plan.Warnings.Add("CHECK: every pending change conflicted or was rejected — nothing left to push");

        return (plan, conflicted);
    }
}
