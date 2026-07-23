namespace KronosScreenRemote;

// The user-facing Sync/Commit pipeline (requirement 10): "Commit Changes" and "Sync
// Library" share this one push mechanism — Sync Library = pull, then push, in one click;
// Commit Changes = push alone. Every successful push appends a PERMANENT audit-log entry
// (LocalLibraryCache.RecordPushSuccess) — it does not clear after sync, matching
// requirement 10's "persists as a permanent audit log."
static class SyncPipeline
{
    public sealed record PushResult(bool Ok, string? Error, int Written, List<ObjLoc> Conflicted);

    public static async Task<PushResult> PushAsync(
        ISysExService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
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
            cache.Save();
            return new PushResult(true, plan.Warnings.Count > 0 ? string.Join("; ", plan.Warnings) : null, 0, conflicted);
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
        cache.RecordPushSuccesses(pushed, utcNow, syncBatchId);

        // Refresh the bank-digest baseline for everything we just wrote — otherwise the
        // NEXT pull would see "hardware changed" (true, WE changed it) and waste a
        // re-sweep confirming what we already know matches.
        foreach (var (objType, bank) in plan.Stores)
        {
            var freshDigest = await sysEx.BankDigestAsync(objType, bank).ConfigureAwait(false);
            if (freshDigest != null) cache.SetBankDigestBaseline(objType, bank, Convert.ToHexString(freshDigest).ToLowerInvariant());
        }

        cache.Save();
        return new PushResult(true, null, plan.TargetsOnSuccess.Count, conflicted);
    }

    public static Task<LibraryPullPipeline.PullResult> PullAsync(
        ISysExService sysEx, LocalLibraryCache cache, bool full, Action<string>? progress = null) =>
        LibraryPullPipeline.PullAsync(sysEx, cache, full, progress);

    public static Task<PushResult> CommitChangesAsync(
        ISysExService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip, Action<string>? progress = null) =>
        PushAsync(sysEx, cache, sessionClip, progress);

    // Pull first, then push — so the push's conflict pre-scan sees the freshest possible
    // bank digests, minimizing spurious conflicts (a deliberate ordering choice: push-then-
    // pull would push against a possibly-stale baseline and then immediately re-pull over
    // data it just wrote).
    public static async Task<(LibraryPullPipeline.PullResult Pull, PushResult Push)> SyncLibraryAsync(
        ISysExService sysEx, LocalLibraryCache cache, SessionDependencyClipboard sessionClip,
        bool fullPull, Action<string>? progress = null)
    {
        var pull = await LibraryPullPipeline.PullAsync(sysEx, cache, fullPull, progress).ConfigureAwait(false);
        var push = await PushAsync(sysEx, cache, sessionClip, progress).ConfigureAwait(false);
        return (pull, push);
    }
}
