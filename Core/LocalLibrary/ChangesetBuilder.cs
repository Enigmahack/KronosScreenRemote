namespace KronosScreenRemote;

// Builds a ChangesetPlan from whatever's currently dirty in the local cache - the Push
// half of the Sync/Commit pipeline. In order:
//  1. Dependency-completeness gate: REFUSE outright if the session clipboard still has
//     unresolved placements.
//  2. Conflict pre-scan: re-check each distinct (type,bank) containing a dirty object
//     against hardware's CURRENT digest. A mismatch excludes every dirty object in that
//     bank from this push (flagged Conflicted, same marker Pull uses) instead of silently
//     overwriting a possible concurrent front-panel edit. Skipped entirely when
//     `forceDestructiveWrite` says the keyboard library is the source of truth.
//  3. Defense-in-depth referential check: any surviving dirty Combi/Set List whose
//     references point at a target with NO local body at all (never pulled/placed) is
//     REFUSEd - catches an edit+discard interaction leaving a referrer pointing at
//     nothing. (Presence only, not content-coherence - a discard reverting a target to
//     stale-but-present baseline content is a known, narrower residual gap; the real
//     hardware-corruption guard remains ApplyMoveAsync's pre-write backup + staleness gate.)
//  4. Assemble Writes/PreImages from the surviving dirty set.
static class ChangesetBuilder
{
    // forceDestructiveWrite (Settings > Librarian, AppSettings.LibrarianForceDestructiveWrite):
    // the keyboard library is authoritative, so step 2 below is skipped outright rather than run and
    // ignored - its digest round trip per dirty bank exists only to decide what to exclude, and a
    // MarkConflicted here would flag objects this very push is about to write anyway. The gates
    // that survive it (steps 3 and 3.5, and ApplyMoveAsync's own staleness gate) guard against
    // writes the Kronos would MANGLE, which is a different question from who wins a disagreement.
    public static async Task<(ChangesetPlan Plan, List<ObjLoc> Conflicted)> BuildAsync(
        LocalLibraryCache cache, ILibrarianService sysEx, SessionDependencyClipboard sessionClip,
        bool forceDestructiveWrite = false)
    {
        var plan = new ChangesetPlan();
        var conflicted = new List<ObjLoc>();

        if (sessionClip.Pending.Count > 0)
        {
            plan.Warnings.Add(AppMessages.Librarian.Sync.RefusePendingDependencies(sessionClip.Pending.Count));
            return (plan, conflicted);
        }

        // Delete supersedes edit: an object marked for deletion is erased and removed, never
        // written as a normal edit - so drop any pending-delete from the dirty set before
        // assembling writes.
        var pendingDeletes = cache.PendingDeleteObjects().ToList();
        var pendingDeleteSet = new HashSet<ObjLoc>(pendingDeletes);
        var dirty = cache.DirtyObjects().Where(d => !pendingDeleteSet.Contains(d)).ToList();
        if (dirty.Count == 0 && pendingDeletes.Count == 0)
        {
            plan.Warnings.Add(AppMessages.Librarian.Sync.CheckNothingToPush);
            return (plan, conflicted);
        }

        // Step 2: conflict pre-scan, one BankDigestAsync per distinct (type,bank) - over the
        // union of edited and to-be-deleted banks (an erase write is conflict-gated exactly like
        // an edit: a bank changed on hardware since baseline must not be silently clobbered).
        var excludedBanks = new HashSet<(int, int)>();
        if (!forceDestructiveWrite)
        {
            var baseline = cache.BankDigestBaselineHex();
            foreach (var (objType, bank) in dirty.Concat(pendingDeletes).Select(loc => (loc.ObjType, loc.Bank)).Distinct())
            {
                var fresh = await sysEx.BankDigestAsync(objType, bank).ConfigureAwait(false);
                string freshHex = fresh != null ? Convert.ToHexString(fresh).ToLowerInvariant() : "";
                bool changed = !baseline.TryGetValue((objType, bank), out var baseHex) || freshHex != baseHex;
                if (changed) excludedBanks.Add((objType, bank));
            }
        }
        else
        {
            // A Conflicted flag left over from an earlier pull/push would otherwise outlive this
            // push forever: nothing downstream clears it now that the pre-scan no longer runs, and
            // the object it marks is in `surviving` and about to be written. Clearing it here keeps
            // the conflict banner honest instead of showing resolved history.
            foreach (var loc in dirty.Concat(pendingDeletes))
                cache.ClearConflict(loc.ObjType, loc.Bank, loc.Number);
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

        var survivingDeletes = new List<ObjLoc>();
        foreach (var loc in pendingDeletes)
        {
            if (excludedBanks.Contains((loc.ObjType, loc.Bank)))
            {
                cache.MarkConflicted(loc.ObjType, loc.Bank, loc.Number);
                conflicted.Add(loc);
            }
            else survivingDeletes.Add(loc);
        }

        // Step 3: defense-in-depth referential check over the surviving set. The slots step 4b
        // is about to blank count as missing here: they still read back normally until the push
        // actually runs (PendingDelete only flips a flag - both the index entry and the blob
        // stay), so without naming them the gate is blind to the one case it exists to catch -
        // a dirty Combi pushed in the same changeset that erases a Program it points at.
        var beingErased = survivingDeletes.ToHashSet();
        foreach (var loc in surviving)
        {
            if (loc.ObjType != LibObj.Combi && loc.ObjType != LibObj.SetList) continue;
            var body = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
            if (body == null) continue;
            foreach (var (missingRef, kind) in DependencyScanner.ScanForPush(cache, loc.ObjType, body, beingErased))
                plan.Warnings.Add(AppMessages.Librarian.Sync.RefuseMissingReference(loc.Label(), missingRef.Label(), kind));
        }

        // Whole-bank type changes: a Program bank the user staged an HD-1/EXi change for, AND
        // which we're actually writing this push, gets a func 0x7C emitted
        // before its writes. Filtered to banks with surviving writes so a bank we're NOT
        // rewriting is never erased. Safe even when the live type query below is unavailable -
        // 0x7C is a no-op on the instrument if the bank is already that type.
        foreach (var bank in surviving.Where(l => l.ObjType == LibObj.Program).Select(l => l.Bank).Distinct())
            if (cache.PendingBankTypeChange(bank) is bool targetIsExi)
                plan.AddBankTypeChange(bank, targetIsExi);

        // Step 3.5: Program EXi/HD-1 bank-type re-verification - a single FRESH func 0x61
        // query, right before ArmPlanAsync/ApplyMoveAsync ever touches hardware. This is
        // deliberately independent of LibrarianShellViewModel's own placement-time check
        // (BatchMoveModel.cs's PlanBatchMove, via BankTypeOf): that one runs against a
        // background-warmed cache that can still be null the instant after the Librarian
        // window opens (a real race the very first placement can lose), silently degrading
        // to a non-blocking CHECK and letting a genuine mismatch reach hardware as a Reply
        // Code 3 ("short or otherwise mangled message"). Querying fresh here - the one place
        // guaranteed to run right before the write - closes that gap regardless of timing.
        // ONE REFUSE per bank, not per Program: every program in a mismatched bank is the same
        // wrong format, so 128 identical REFUSE lines would just be noise. A bank whose type
        // change was intentionally staged (in plan.BankTypeChanges above) is skipped - the 0x7C
        // reformats it first.
        if (surviving.Any(l => l.ObjType == LibObj.Program))
        {
            var liveTypes = await sysEx.RequestProgramBankTypesAsync().ConfigureAwait(false);
            if (liveTypes is { } types)
            {
                foreach (var bank in surviving.Where(l => l.ObjType == LibObj.Program).Select(l => l.Bank).Distinct())
                {
                    if (plan.BankTypeChanges.Any(x => x.Bank == bank)) continue;   // intentional reformat staged
                    if (KronosBanks.ProgramBankTypeBitIndex(bank) is not int bit || bit >= types.IsExi.Length) continue;
                    bool isExi = types.IsExi[bit];

                    bool anyMismatch = surviving
                        .Where(l => l.ObjType == LibObj.Program && l.Bank == bank)
                        .Select(l => cache.GetCurrentBody(l.ObjType, l.Bank, l.Number))
                        .Any(b => b != null && (b.Length == ProgramFormatConverter.WireSizeExi) != isExi);
                    if (!anyMismatch) continue;

                    plan.Warnings.Add(AppMessages.Librarian.Sync.RefuseBankTypeMismatch(KronosBanks.ProgramLabel(bank), isExi ? "EXi" : "HD-1"));
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
            if (baselineBody != null)   // null = confirmed-empty hardware slot per the last Pull - nothing to back up
                plan.PreImages.Add(new WriteOp(loc.ObjType, loc.Bank, loc.Number, version.Value, baselineBody, "original"));
            if (!plan.Stores.Contains((loc.ObjType, loc.Bank))) plan.Stores.Add((loc.ObjType, loc.Bank));
            plan.TargetsOnSuccess.Add(loc);
        }

        // Step 4b: committed deletions. An object that exists on hardware (has a baseline) is
        // overwritten with a blank/INIT body (EraseBody) + Stored, then removed locally on
        // success; a local-only object (never pushed - no baseline) is just removed
        // locally, with no hardware write. Either way its loc goes in Deletes, never
        // TargetsOnSuccess - a deleted object's baseline is dropped, not advanced.
        var blankTemplates = new BlankTemplateStore(cache.Root);
        foreach (var loc in survivingDeletes)
        {
            var baselineBody = cache.GetBaselineBody(loc.ObjType, loc.Bank, loc.Number);
            if (baselineBody == null)
            {
                plan.Deletes.Add(loc);   // local-only: nothing on the instrument to erase
                continue;
            }
            var version = cache.GetVersion(loc.ObjType, loc.Bank, loc.Number);
            var current = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number) ?? baselineBody;
            if (version == null) continue;

            // Prefer a REAL captured blank body - the exact bytes the instrument uses for a blank
            // object of this kind (BlankTemplates captures it once from a currently-blank slot and
            // reuses it forever). Fall back to EraseBody's derived name-blank only when no template
            // is available (offline AND never captured).
            bool isExi = cache.IsExi(loc.ObjType, loc.Bank, loc.Number);
            var eraseBody = await BlankTemplates.EnsureAsync(sysEx, cache, blankTemplates, loc.ObjType, isExi).ConfigureAwait(false)
                ?? EraseBody.Build(loc.ObjType, current);

            // A Set List's default name encodes its own slot number ("Set List 006"), but the blank
            // template is captured ONCE from a single donor slot (Set List 127) and reused for every
            // erase - so writing it verbatim stamps the donor's "Set List 127" onto whatever slot
            // we're erasing (a real bug: it renamed live hardware set-list slots to "Set List 127").
            // Re-stamp THIS slot's own default name so the reverted slot reads as itself - "revert
            // to init, but with the name of the slot it occupies". Programs/Combis have no
            // slot-numbered default, so their INIT name from the template/EraseBody stands.
            if (loc.ObjType == LibObj.SetList)
                eraseBody = SetListBody.WriteName(eraseBody, SetListData.DefaultName(loc.Number));

            plan.Writes.Add(new WriteOp(loc.ObjType, loc.Bank, loc.Number, version.Value, eraseBody, $"erase {loc.Label()}"));
            plan.PreImages.Add(new WriteOp(loc.ObjType, loc.Bank, loc.Number, version.Value, baselineBody, "original"));
            if (!plan.Stores.Contains((loc.ObjType, loc.Bank))) plan.Stores.Add((loc.ObjType, loc.Bank));
            // Revert-to-blank, NOT remove: on success the local slot advances to this same blank
            // body so it stays in the tree as the init object at its address.
            plan.Erasures.Add((loc, version.Value, eraseBody));
        }

        if (plan.Writes.Count == 0 && plan.Deletes.Count == 0)
            plan.Warnings.Add(AppMessages.Librarian.Sync.CheckEveryChangeConflicted);

        return (plan, conflicted);
    }
}
