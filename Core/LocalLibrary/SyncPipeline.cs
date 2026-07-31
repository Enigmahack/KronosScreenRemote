namespace KronosScreenRemote;

// The user-facing Sync/Commit pipeline (requirement 10): "Commit Changes" and "Sync
// Library" share this one push mechanism - Sync Library = pull, then push, in one click;
// Commit Changes = push alone. Every successful push appends a PERMANENT audit-log entry
// (LocalLibraryCache.RecordPushSuccess) - it does not clear after sync, matching
// requirement 10's "persists as a permanent audit log."
static class SyncPipeline
{
    public sealed record PushResult(bool Ok, string? Error, int Written, List<ObjLoc> Conflicted, int Deleted = 0);

    public static async Task<PushResult> PushAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        Action<string>? progress = null)
    {
        var (plan, conflicted) = await ChangesetBuilder.BuildAsync(cache, sysEx, sessionClip).ConfigureAwait(false);
        if (plan.IsRefusable)
        {
            cache.Save();   // persist the Conflicted flags ChangesetBuilder just set, even on refusal
            return new PushResult(false, string.Join("; ", plan.Warnings), 0, conflicted);
        }
        if (plan.Writes.Count == 0)
        {
            // Only local-only deletions (never on hardware, so no erase write) can reach here
            // with work to do - apply them, no instrument round-trip needed.
            var delUtc = DateTime.UtcNow;
            foreach (var loc in plan.Deletes) cache.RemoveObject(loc.ObjType, loc.Bank, loc.Number, delUtc);
            cache.Save();
            return new PushResult(true, plan.Warnings.Count > 0 ? string.Join("; ", plan.Warnings) : null, 0, conflicted, plan.Deletes.Count);
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
        return new PushResult(true, null, plan.TargetsOnSuccess.Count, conflicted, plan.Erasures.Count + plan.Deletes.Count);
    }

    public static Task<LibraryPullPipeline.PullResult> PullAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, bool full, Action<string>? progress = null) =>
        LibraryPullPipeline.PullAsync(sysEx, cache, full, progress);

    public static Task<PushResult> CommitChangesAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip, Action<string>? progress = null) =>
        PushAsync(sysEx, cache, sessionClip, progress);

    // Pull first, then push - so the push's conflict pre-scan sees the freshest possible
    // bank digests, minimizing spurious conflicts (a deliberate ordering choice: push-then-
    // pull would push against a possibly-stale baseline and then immediately re-pull over
    // data it just wrote).
    public static async Task<(LibraryPullPipeline.PullResult Pull, PushResult Push)> SyncLibraryAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        bool fullPull, Action<string>? progress = null)
    {
        var pull = await LibraryPullPipeline.PullAsync(sysEx, cache, fullPull, progress).ConfigureAwait(false);
        var push = await PushAsync(sysEx, cache, sessionClip, progress).ConfigureAwait(false);
        return (pull, push);
    }
}
