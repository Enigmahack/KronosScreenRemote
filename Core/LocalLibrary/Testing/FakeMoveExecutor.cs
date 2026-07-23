namespace KronosScreenRemote;

using System.IO;
using System.Security.Cryptography;

// Real in-memory ISysExService fake for Phase 1+ async self-tests (Pull/Push pipeline).
// Unlike Tools/UiThemeSmokeTest.cs's FakeSysExService (construction-only stubs, every
// hardware call a no-op), this one actually mutates state: WriteObjectAsync/StoreBankAsync
// land in an in-memory bank store, DumpObjectAsync reads it back, and BankDigestAsync
// computes a real SHA-1 over a bank's 128 slots — so a self-test can mutate "hardware"
// mid-pipeline and observe the pipeline react (conflict detection, staleness gates), not
// just replay canned responses.
sealed class FakeMoveExecutor : ISysExService
{
    // (Obj, Bank, Number) -> stored object. Missing = never written (DumpObjectAsync -> null).
    readonly Dictionary<(int Obj, int Bank, int Number), (byte Version, byte[] Body)> _objects = new();

    // Records which hardware-facing primitive fired, in order — lets a self-test assert
    // real call ordering (e.g. "Sync pulls before it pushes") instead of just end-state.
    public List<string> CallLog { get; } = new();

    // When true, DumpBankBulkAsync simulates a rejected/unsupported func-0x77 request
    // (returns empty, like a real USER-bank reject would look from the caller's side) —
    // lets a self-test exercise LibraryPullPipeline's per-object fallback path.
    public bool SimulateBulkDumpUnsupported { get; set; }

    public void Seed(int obj, int bank, int number, byte version, byte[] body) =>
        _objects[(obj, bank, number)] = (version, body);

    public Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index)
    {
        CallLog.Add($"Dump:{obj}:{bank}:{index}");
        return Task.FromResult(_objects.TryGetValue((obj, bank, index), out var o)
            ? new ObjectDump(obj, bank, index, o.Version, o.Body) : null);
    }

    public Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count)
    {
        CallLog.Add($"BulkDump:{obj}:{bank}");
        var result = new Dictionary<int, ObjectDump>();
        if (SimulateBulkDumpUnsupported) return Task.FromResult(result);
        foreach (var (key, o) in _objects)
            if (key.Obj == obj && key.Bank == bank)
                result[key.Number] = new ObjectDump(obj, bank, key.Number, o.Version, o.Body);
        return Task.FromResult(result);
    }

    public Task<byte[]?> BankDigestAsync(int obj, int bank)
    {
        CallLog.Add($"Digest:{obj}:{bank}");
        using var sha1 = SHA1.Create();
        using var ms = new MemoryStream();
        for (int n = 0; n < 128; n++)
            if (_objects.TryGetValue((obj, bank, n), out var o))
                ms.Write(o.Body, 0, o.Body.Length);
        return Task.FromResult<byte[]?>(sha1.ComputeHash(ms.ToArray()));
    }

    public Task<int> WriteObjectAsync(WriteOp op)
    {
        CallLog.Add("Write");
        // Mirrors SysExService.WriteObjectAsync's real stamping — see LibObj.
        // CurrentObjectVersion's comment — so a self-test can verify a stale/placeholder
        // stored version never actually reaches "hardware".
        byte version = LibObj.CurrentObjectVersion(op.Obj) ?? op.Version;
        _objects[(op.Obj, op.Bank, op.Index)] = (version, (byte[])op.Body.Clone());
        return Task.FromResult(0);
    }

    public Task<int> StoreBankAsync(int obj, int bank)
    {
        CallLog.Add("Store");
        return Task.FromResult(0);
    }

    public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var op in ops) fs.Write(op.Body, 0, op.Body.Length);
        return Task.CompletedTask;
    }

    public Task SendRawAsync(byte[] data) => Task.CompletedTask;

    // ── Unused by Pull/Push self-tests — trivial stubs, same shape as
    //    Tools/UiThemeSmokeTest.cs's FakeSysExService ──
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? ValueSliderChanged;
    public event Action<SysExTrafficEntry>? SysExTraffic;
    public string PerformanceDisplay => "";
    public bool IsAvailable => true;
    public int ValueSliderCc { get; set; } = 18;
    public bool PullNamesOnChange { get; set; }
    public bool CanDump => true;
    public void Start(IKronosMidiTransport transport) { }
    public void Reset() { }
    public void RefreshNow() { }
    public void NotifyUserActivity() { }
    public Task<SetListData?> DumpSetListAsync(int number) => Task.FromResult<SetListData?>(null);
    public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) =>
        Task.FromResult(new SetListSyncResult(new Dictionary<int, SetListData>(), Array.Empty<int>(), 0, false));
    public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => Task.FromResult(0);
    public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges) { }
    public Task<bool> SendMidiAsync(string hexBytes) => Task.FromResult(false);
    public ObjLoc? CurrentPerformanceLoc() => null;

    // Settable by a self-test before constructing the ViewModel under test, to exercise
    // LibrarianShellViewModel.WarmProgramBankTypesAsync/BankTypeOf against a known, fake
    // "real hardware" answer — defaults to null (unreachable/unqueried), same as before.
    public ProgramBankTypes? ProgramBankTypesToReturn { get; set; }
    public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => Task.FromResult(ProgramBankTypesToReturn);
}
