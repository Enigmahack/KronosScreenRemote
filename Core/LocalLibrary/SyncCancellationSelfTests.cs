namespace KronosScreenRemote;

using System.IO;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for the Sync cancellation plumbing added to fix a real leak: closing
// the Librarian window mid-Sync used to orphan LibraryPullPipeline.PullAsync's loop over every
// registry bank (a Force Full Sync can mean minutes of it) with nothing ever cancelling or
// awaiting it - _cache/_sysEx are MainWindow-owned, app-lifetime singletons the orphaned Task
// kept referencing, and repeating "open, start a Full Sync, close before it finishes" piled up
// concurrent orphaned pulls, each still allocating, all serializing through the same
// SysExDumpCollector gate. See LibrarianShellViewModel's own comment on _syncCts.
//
// Two things need covering: (1) LibraryPullPipeline.PullAsync's ct checks actually stop the
// digest sweep AND the bank-fetch loop early, not just at the very top before anything ran; (2)
// LibrarianShellViewModel wires this so Dispose() cancels a still-running sync, and - just as
// important - does NOT throw when Cancel() would land on a CTS a completed sync already disposed
// (the ObjectDisposedException trap; see SyncLibraryAsync's own comment on nulling _syncCts).
static class SyncCancellationSelfTests
{
    public static async Task<List<string>> SelfTestAsync()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        string root = Path.Combine(Path.GetTempPath(), "kronos_selftest_sync_cancellation");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        try
        {
            // ── 1. LibraryPullPipeline itself: cancelling partway through the digest sweep
            // must stop BEFORE every registry bank was asked, not run to completion regardless.
            var cache1 = new LocalLibraryCache(root);
            var inner = new FakeMoveExecutor();
            int totalBanks = LibraryPullPlanner.AllBanks().Count();
            Check("fixture-has-enough-banks-to-prove-early-stop", totalBanks > 10);

            var cts = new CancellationTokenSource();
            const int cancelAfter = 5;
            var exec = new CancelAfterNDigestsExecutor(inner, cts, cancelAfter);
            var result = await LibraryPullPipeline.PullAsync(exec, cache1, full: true, ct: cts.Token);

            int digestCalls = inner.CallLog.Count(c => c.StartsWith("Digest:", StringComparison.Ordinal));
            Check("cancellation-observed-quickly", cts.IsCancellationRequested);
            // Exactly cancelAfter+1: the Nth call trips Cancel(), but that call itself already
            // went out before the loop's own ct-check (at the TOP of the next iteration) sees it.
            Check("digest-sweep-stopped-early", digestCalls <= cancelAfter + 1 && digestCalls < totalBanks);
            // The bank-fetch loop must never have started at all - cancellation happened during
            // the digest sweep, well before plan.BanksToFetch's own loop is reached.
            Check("no-bulk-dump-calls-after-cancel-during-digest-sweep",
                !inner.CallLog.Any(c => c.StartsWith("BulkDump:", StringComparison.Ordinal)));
            Check("nothing-fetched", result.ObjectsFetched == 0);

            // ── 2. Same idea, but cancel during the BANK-FETCH loop instead of the digest sweep -
            // proves the second ct-check (LibraryPullPipeline.cs's `foreach (var bankRef in
            // plan.BanksToFetch)` loop) also stops promptly, not just the digest sweep's own.
            var cache2 = new LocalLibraryCache(Path.Combine(root, "phase2"));
            var inner2 = new FakeMoveExecutor();
            // Seed a few banks with real content so PlanPull has more than one bank to fetch.
            for (int b = 0; b < 5; b++)
                inner2.Seed(LibObj.Program, 0x40 + b, 0, 5, new byte[ProgramFormatConverter.PcgSlotSize]);
            var cts2 = new CancellationTokenSource();
            var exec2 = new CancelAfterNBulkDumpsExecutor(inner2, cts2, cancelAfterCalls: 1);
            var result2 = await LibraryPullPipeline.PullAsync(exec2, cache2, full: true, ct: cts2.Token);
            int bulkCalls = inner2.CallLog.Count(c => c.StartsWith("BulkDump:", StringComparison.Ordinal));
            Check("bank-fetch-loop-stopped-early", bulkCalls <= 2 && bulkCalls < 5);

            // ── 3. ViewModel wiring: Dispose() while a sync is genuinely in flight must cancel
            // it without throwing, and must not resurrect a stale WarningText the disposed window
            // can no longer show anyone.
            var cache3 = new LocalLibraryCache(Path.Combine(root, "phase3"));
            var inner3 = new FakeMoveExecutor();
            var cts3ForVm = new CancellationTokenSource();   // never cancelled by the test itself
            var slowExec = new CancelAfterNDigestsExecutor(inner3, cts3ForVm, cancelAfter: int.MaxValue);
            var vm = new LibrarianShellViewModel(slowExec, cache3, new AppSettings(), "selftest-sync-cancel-host");
            var syncTask = vm.SyncLibraryCommand.ExecuteAsync(null);
            // FakeMoveExecutor resolves every call synchronously, so by the time ExecuteAsync
            // returns a Task at all, the whole pull has very likely already run to completion on
            // this thread - there is no real async gap to race against without a slower fake.
            // What's actually under test here is narrower and just as important: Dispose() must
            // be safe to call regardless of whether a sync is mid-flight or already finished (the
            // ObjectDisposedException trap on a `using`-disposed CTS).
            await syncTask;
            bool disposeThrew = false;
            try { vm.Dispose(); } catch { disposeThrew = true; }
            Check("dispose-after-completed-sync-does-not-throw", !disposeThrew);

            // ── 4. A VM that never ran a sync at all - Dispose() must tolerate a null _syncCts.
            var cache4 = new LocalLibraryCache(Path.Combine(root, "phase4"));
            var vmNeverSynced = new LibrarianShellViewModel(new FakeMoveExecutor(), cache4, new AppSettings(), "selftest-sync-cancel-host2");
            bool neverSyncedDisposeThrew = false;
            try { vmNeverSynced.Dispose(); } catch { neverSyncedDisposeThrew = true; }
            Check("dispose-without-ever-syncing-does-not-throw", !neverSyncedDisposeThrew);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        return fails;
    }

    // Forwards every ILibrarianService call to `inner`, but cancels `cts` once BankDigestAsync
    // has been called `cancelAfter` times - simulates a window closing partway through the
    // digest sweep without needing a real async gap (FakeMoveExecutor resolves synchronously).
    sealed class CancelAfterNDigestsExecutor(FakeMoveExecutor inner, CancellationTokenSource cts, int cancelAfter) : ILibrarianService
    {
        int _digestCalls;

        public async Task<byte[]?> BankDigestAsync(int obj, int bank)
        {
            var result = await inner.BankDigestAsync(obj, bank);
            if (++_digestCalls >= cancelAfter) cts.Cancel();
            return result;
        }

        public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path) => inner.BackupObjectsAsync(ops, path);
        public Task<int> WriteObjectAsync(WriteOp op) => inner.WriteObjectAsync(op);
        public Task<int> StoreBankAsync(int obj, int bank) => inner.StoreBankAsync(obj, bank);
        public Task SendRawAsync(byte[] data) => inner.SendRawAsync(data);
        public Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi) => inner.ChangeProgramBankTypeAsync(bank, isExi);
        public int? StorageChangeCountFor(int obj, int bank) => inner.StorageChangeCountFor(obj, bank);
        public Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index) => inner.DumpObjectAsync(obj, bank, index);
        public Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count) => inner.DumpBankBulkAsync(obj, bank, count);
        public ObjLoc? CurrentPerformanceLoc() => inner.CurrentPerformanceLoc();
        public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => inner.RequestProgramBankTypesAsync();
        public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank) => inner.CachedBankNames(type, objBank);
        public bool CanDump => inner.CanDump;
        public Task<SetListData?> DumpSetListAsync(int number) => inner.DumpSetListAsync(number);
        public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) => inner.DumpAllSetListsAsync(progress, ct);
        public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => inner.SyncNamesAsync(progress, ct);
    }

    // Same idea, but trips on DumpBankBulkAsync instead - simulates a window closing partway
    // through the bank-FETCH loop (after the digest sweep already finished) rather than during it.
    sealed class CancelAfterNBulkDumpsExecutor(FakeMoveExecutor inner, CancellationTokenSource cts, int cancelAfterCalls) : ILibrarianService
    {
        int _bulkCalls;

        public async Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count)
        {
            var result = await inner.DumpBankBulkAsync(obj, bank, count);
            if (++_bulkCalls >= cancelAfterCalls) cts.Cancel();
            return result;
        }

        public Task<byte[]?> BankDigestAsync(int obj, int bank) => inner.BankDigestAsync(obj, bank);
        public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path) => inner.BackupObjectsAsync(ops, path);
        public Task<int> WriteObjectAsync(WriteOp op) => inner.WriteObjectAsync(op);
        public Task<int> StoreBankAsync(int obj, int bank) => inner.StoreBankAsync(obj, bank);
        public Task SendRawAsync(byte[] data) => inner.SendRawAsync(data);
        public Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi) => inner.ChangeProgramBankTypeAsync(bank, isExi);
        public int? StorageChangeCountFor(int obj, int bank) => inner.StorageChangeCountFor(obj, bank);
        public Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index) => inner.DumpObjectAsync(obj, bank, index);
        public ObjLoc? CurrentPerformanceLoc() => inner.CurrentPerformanceLoc();
        public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => inner.RequestProgramBankTypesAsync();
        public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank) => inner.CachedBankNames(type, objBank);
        public bool CanDump => inner.CanDump;
        public Task<SetListData?> DumpSetListAsync(int number) => inner.DumpSetListAsync(number);
        public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) => inner.DumpAllSetListsAsync(progress, ct);
        public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => inner.SyncNamesAsync(progress, ct);
    }
}
