namespace KronosScreenRemote;

using System.IO;
using System.Security.Cryptography;

// Real in-memory ILibrarianService fake for Phase 1+ async self-tests (Pull/Push pipeline).
// Unlike Tools/UiThemeSmokeTest.cs's FakeSysExService (construction-only stubs, every
// hardware call a no-op), this one actually mutates state: WriteObjectAsync/StoreBankAsync
// land in an in-memory bank store, DumpObjectAsync reads it back, and BankDigestAsync
// computes a real SHA-1 over a bank's 128 slots — so a self-test can mutate "hardware"
// mid-pipeline and observe the pipeline react (conflict detection, staleness gates), not
// just replay canned responses.
//
// Implements only ILibrarianService — the instrument read+write slice the librarian pipelines
// actually drive — NOT the whole ISysExService. The perf-follow / MIDI-backend / raw-send
// roles it used to stub (~13 no-op members) are gone: nothing here exercises them.
sealed class FakeMoveExecutor : ILibrarianService
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

    // Non-zero makes WriteObjectAsync return that Reply code WITHOUT storing the body —
    // mirrors a real func-0x73 hardware reject (nothing lands on the instrument). Lets a
    // self-test exercise ApplyMoveAsync's write-reject abort (aborts before any Store).
    public int WriteRejectCode { get; set; }

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
        // Simulated hardware reject: return the Reply code and leave "hardware" untouched
        // (a rejected write stores nothing), so ApplyMoveAsync must abort before any Store.
        if (WriteRejectCode != 0) return Task.FromResult(WriteRejectCode);
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

    // Records each program bank's requested HD-1/EXi type and, mirroring the real func 0x7C
    // ("reformats and erases specified bank"), clears every stored Program in that bank — so a
    // self-test can assert both that the type change fired and that the whole-bank rewrite that
    // follows lands on a freshly-erased bank.
    public Dictionary<int, bool> BankTypeChanges { get; } = new();
    public Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi)
    {
        CallLog.Add($"ChangeBankType:{bank}:{(isExi ? "EXi" : "HD-1")}");
        BankTypeChanges[bank] = isExi;
        foreach (var key in _objects.Keys.Where(k => k.Obj == LibObj.Program && k.Bank == bank).ToList())
            _objects.Remove(key);
        return Task.FromResult(0);
    }

    public Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path)
    {
        CallLog.Add("Backup");   // lets a self-test assert the pre-image backup fired BEFORE any Write
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var op in ops) fs.Write(op.Body, 0, op.Body.Length);
        return Task.CompletedTask;
    }

    public Task SendRawAsync(byte[] data) => Task.CompletedTask;

    // ── Remaining IBankDumpService members unused by Pull/Push self-tests — trivial stubs ──
    public bool CanDump => true;
    public Task<SetListData?> DumpSetListAsync(int number) => Task.FromResult<SetListData?>(null);
    public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) =>
        Task.FromResult(new SetListSyncResult(new Dictionary<int, SetListData>(), Array.Empty<int>(), 0, false));
    public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => Task.FromResult(0);
    public ObjLoc? CurrentPerformanceLoc() => null;

    // Settable by a self-test before constructing the ViewModel under test, to exercise
    // LibrarianShellViewModel.WarmProgramBankTypesAsync/BankTypeOf against a known, fake
    // "real hardware" answer — defaults to null (unreachable/unqueried), same as before.
    public ProgramBankTypes? ProgramBankTypesToReturn { get; set; }
    public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => Task.FromResult(ProgramBankTypesToReturn);
}
