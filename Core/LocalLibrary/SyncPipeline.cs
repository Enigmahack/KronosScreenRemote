namespace KronosScreenRemote;

// The user-facing Sync/Commit pipeline (requirement 10): "Commit Changes" and "Sync
// Library" share this one push mechanism - Sync Library = pull, then push, in one click;
// Commit Changes = push alone. Every successful push appends a PERMANENT audit-log entry
// (LocalLibraryCache.RecordPushSuccess) - it does not clear after sync, matching
// requirement 10's "persists as a permanent audit log."
static class SyncPipeline
{
    public sealed record PushResult(bool Ok, string? Error, int Written, List<ObjLoc> Conflicted, int Deleted = 0);

    // "Combi U-C, Set Lists" - the distinct banks a set of excluded objects sits in, so the
    // conflict message names WHERE to look instead of just how many.
    static string DescribeBanks(IEnumerable<ObjLoc> locs) =>
        string.Join(", ", locs.Select(l => (l.ObjType, l.Bank)).Distinct()
                              .Select(b => Librarian.StoreLabel(b.ObjType, b.Bank)));

    // forceDestructiveWrite: the keyboard library wins outright - see ChangesetBuilder.BuildAsync's
    // own comment for exactly which gate that skips and which ones deliberately still run.
    public static async Task<PushResult> PushAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        Action<string>? progress = null, bool forceDestructiveWrite = false)
    {
        var (plan, conflicted) = await ChangesetBuilder.BuildAsync(cache, sysEx, sessionClip, forceDestructiveWrite).ConfigureAwait(false);
        if (plan.IsRefusable)
        {
            cache.Save();   // persist the Conflicted flags ChangesetBuilder just set, even on refusal
            return new PushResult(false, plan.Warnings.Join(), 0, conflicted);
        }
        if (plan.Writes.Count == 0)
        {
            // Only local-only deletions (never on hardware, so no erase write) can reach here
            // with work to do - apply them, no instrument round-trip needed.
            var delUtc = DateTime.UtcNow;
            foreach (var loc in plan.Deletes) cache.RemoveObject(loc.ObjType, loc.Bank, loc.Number, delUtc);
            cache.Save();
            return new PushResult(true, plan.Warnings.Count > 0 ? plan.Warnings.Join() : null, 0, conflicted, plan.Deletes.Count);
        }

        await Librarian.ArmPlanAsync(plan, sysEx).ConfigureAwait(false);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var (ok, steps, aborted) = await Librarian.ApplyMoveAsync(
            plan, sysEx, Storage.BackupDir(), stamp, progress, doLive: false).ConfigureAwait(false);
        foreach (var s in steps) progress?.Invoke(s);

        if (!ok)
        {
            cache.Save();
            return new PushResult(false, aborted, 0, conflicted);
        }

        var utcNow = DateTime.UtcNow;
        var syncBatchId = Guid.NewGuid();
        var pushed = new List<(int ObjType, int Bank, int Number, byte Version, byte[] Body)>();
        foreach (var loc in plan.TargetsOnSuccess)
        {
            var body = cache.GetCurrentBody(loc.ObjType, loc.Bank, loc.Number);
            var version = cache.GetVersion(loc.ObjType, loc.Bank, loc.Number);
            if (body == null || version == null) continue;
            pushed.Add((loc.ObjType, loc.Bank, loc.Number, version.Value, body));
        }
        // Committed deletions of hardware objects: the slot was blanked on the instrument, so the
        // LOCAL slot advances to that same blank body - it stays in the tree as the init object at
        // its address (requirement 2), clean and with its pending-delete flag cleared (which
        // RecordPushSuccesses does by rebuilding the entry). NOT removed.
        foreach (var (loc, version, blank) in plan.Erasures)
            pushed.Add((loc.ObjType, loc.Bank, loc.Number, version, blank));
        cache.RecordPushSuccesses(pushed, utcNow, syncBatchId);

        // Local-only deletions (never on hardware): undo the placement - the slot is genuinely
        // empty on the instrument, so the local entry is removed rather than shown as a blank.
        foreach (var loc in plan.Deletes) cache.RemoveObject(loc.ObjType, loc.Bank, loc.Number, utcNow);

        // Committed whole-bank type changes are now realized on hardware - clear the intent so a
        // later, unrelated push to the same bank doesn't re-issue the (erasing) func 0x7C.
        foreach (var (bank, _) in plan.BankTypeChanges) cache.ClearPendingBankTypeChange(bank);

        // Refresh the bank-digest baseline for everything we just wrote - otherwise the
        // NEXT pull would see "hardware changed" (true, WE changed it) and waste a
        // re-sweep confirming what we already know matches.
        foreach (var (objType, bank) in plan.Stores)
        {
            var freshDigest = await sysEx.BankDigestAsync(objType, bank).ConfigureAwait(false);
            if (freshDigest != null) cache.SetBankDigestBaseline(objType, bank, Convert.ToHexString(freshDigest).ToLowerInvariant());
        }

        cache.Save();
        // A partial push is NOT a clean success. Anything the conflict pre-scan excluded is
        // reported here or nowhere: `conflicted` never reaches plan.Writes, so every downstream
        // count (Written, Deleted) is silent about it, and a push that dropped 50 objects while
        // writing 99 otherwise renders as an unqualified "Pushed 99 object(s)."
        string? error = conflicted.Count > 0
            ? AppMessages.Librarian.Sync.CheckConflictedNotPushed(conflicted.Count, DescribeBanks(conflicted)).ToString()
            : null;
        return new PushResult(true, error, plan.TargetsOnSuccess.Count, conflicted, plan.Erasures.Count + plan.Deletes.Count);
    }

    // Pull with no push - the launch action behind Settings > Librarian > "Full sync on launch"
    // (LibrarianShellViewModel.LaunchPullAsync). `ct` bounds it the same way SyncLibraryAsync's
    // does, because the window closing is exactly what has to stop an unattended sweep.
    public static Task<LibraryPullPipeline.PullResult> PullAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, bool full, Action<string>? progress = null,
        CancellationToken ct = default) =>
        LibraryPullPipeline.PullAsync(sysEx, cache, full, progress, ct);

    public static Task<PushResult> CommitChangesAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        Action<string>? progress = null, bool forceDestructiveWrite = false) =>
        PushAsync(sysEx, cache, sessionClip, progress, forceDestructiveWrite);

    // Pull first, then push - so the push's conflict pre-scan sees the freshest possible
    // bank digests, minimizing spurious conflicts (a deliberate ordering choice: push-then-
    // pull would push against a possibly-stale baseline and then immediately re-pull over
    // data it just wrote).
    // `ct` only ever bounds the PULL half (see LibraryPullPipeline.PullAsync's own checks) -
    // deliberately NOT threaded into PushAsync/ArmPlanAsync/ApplyMoveAsync below. Once a hardware
    // write has started, aborting partway is worse than letting it finish: a half-applied
    // changeset (some objects written, some not, mid-bank) is a real corruption risk a slow
    // window close never is. If `ct` is already cancelled by the time the pull returns (a window
    // closed mid-pull), skip the push entirely rather than starting new hardware writes on behalf
    // of a session that's already gone - PushAsync hasn't touched anything yet at that point.
    public static async Task<(LibraryPullPipeline.PullResult Pull, PushResult Push)> SyncLibraryAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        bool fullPull, Action<string>? progress = null, CancellationToken ct = default,
        bool forceDestructiveWrite = false)
    {
        var pull = await LibraryPullPipeline.PullAsync(sysEx, cache, fullPull, progress, ct).ConfigureAwait(false);
        // The pull gave up because the instrument answered nothing (SysEx off, or unplugged).
        // Skipping the push is not just an optimisation: every write would time out the same way,
        // and a push that "wrote 0 objects" against a silent instrument reads like success.
        if (pull.Aborted is { } pullAborted)
            return (pull, new PushResult(false, pullAborted.ToString(), 0, new List<ObjLoc>()));
        if (ct.IsCancellationRequested)
            return (pull, new PushResult(false, AppMessages.Librarian.Sync.CheckSyncCancelled.ToString(), 0, new List<ObjLoc>()));
        var push = await PushAsync(sysEx, cache, sessionClip, progress, forceDestructiveWrite).ConfigureAwait(false);
        return (pull, push);
    }
}
