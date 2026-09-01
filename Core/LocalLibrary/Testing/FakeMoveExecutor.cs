namespace KronosScreenRemote;

using System.IO;
using System.Security.Cryptography;

// Real in-memory ILibrarianService fake for Phase 1+ async self-tests (Pull/Push pipeline).
// Unlike Tools/UiThemeSmokeTest.cs's FakeSysExService (construction-only stubs, every
// hardware call a no-op), this one actually mutates state: WriteObjectAsync/StoreBankAsync
// land in an in-memory bank store, DumpObjectAsync reads it back, and BankDigestAsync
// computes a real SHA-1 over a bank's 128 slots - so a self-test can mutate "hardware"
// mid-pipeline and observe the pipeline react (conflict detection, staleness gates), not
// just replay canned responses.
//
// Implements only ILibrarianService - the instrument read+write slice the librarian pipelines
// actually drive - NOT the whole ISysExService. The perf-follow / MIDI-backend / raw-send
// roles it used to stub (~13 no-op members) are gone: nothing here exercises them.
sealed class FakeMoveExecutor : ILibrarianService
{
    // (Obj, Bank, Number) -> stored object. Missing = never written (DumpObjectAsync -> null).
    readonly Dictionary<(int Obj, int Bank, int Number), (byte Version, byte[] Body)> _objects = new();

    // Records which hardware-facing primitive fired, in order - lets a self-test assert
    // real call ordering (e.g. "Sync pulls before it pushes") instead of just end-state.
    public List<string> CallLog { get; } = new();

    // When true, DumpBankBulkAsync simulates a rejected/unsupported func-0x77 request
    // (returns empty, like a real USER-bank reject would look from the caller's side) -
    // lets a self-test exercise LibraryPullPipeline's per-object fallback path.
    public bool SimulateBulkDumpUnsupported { get; set; }

    // Non-zero makes WriteObjectAsync return that Reply code WITHOUT storing the body -
    // mirrors a real func-0x73 hardware reject (nothing lands on the instrument). Lets a
    // self-test exercise ApplyMoveAsync's write-reject abort (aborts before any Store).
    public int WriteRejectCode { get; set; }

    // Models a COMMITTED storage change (a front-panel Store, a PCG load): the bank's digest
    // moves immediately, which is what lets a self-test change a bank out from under an armed
    // plan. It does NOT raise a storage-change notification - use SimulatePanelStore for that.
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

    // Banks that answer NO digest at all (null), the way a real instrument stays silent for a
    // bank it doesn't recognize - as opposed to an empty bank, which still returns a real SHA-1
    // over nothing. Lets a self-test exercise LibraryPullPipeline's NoDigest sentinel.
    public HashSet<(int Obj, int Bank)> NoDigestBanks { get; } = new();

    // Fires at the top of each WriteObjectAsync, before the body lands - lets a self-test mutate
    // "hardware" DURING the write burst (see DataSafetySelfTests B1b).
    public Action? BeforeEachWrite { get; set; }

    // ── Storage-change notifications (the real service's unsolicited func-0x38 push) ─────────
    readonly Dictionary<(int Obj, int Bank), int> _storageChanges = new();

    // When true, StorageChangeCountFor answers null for every bank - the real service's "no live
    // MIDI stream, pushes cannot be observed at all" case, which ApplyMoveAsync must report as an
    // unwatched bank rather than as a quiet one.
    public bool SimulatePushesUnobservable { get; set; }

    // A front-panel Store: the committed change Seed models, PLUS the notification the instrument
    // pushes for it. Mirrors the hardware capture behind ApplyMoveAsync's step 3b - a real Store
    // pushes the same 0x38 TWICE, so this does too, and a gate that compared counts rather than
    // inequality would be wrong.
    public void SimulatePanelStore(int obj, int bank, int number, byte version, byte[] body)
    {
        Seed(obj, bank, number, version, body);
        for (int i = 0; i < 2; i++)
            _storageChanges[(obj, bank)] = _storageChanges.TryGetValue((obj, bank), out var n) ? n + 1 : 1;
    }

    public int? StorageChangeCountFor(int obj, int bank) =>
        SimulatePushesUnobservable ? null
        : _storageChanges.TryGetValue((obj, bank), out var n) ? n : 0;

    // A bank's digest is hashed over whatever the bank buffer currently holds, INCLUDING this
    // plan's own uncommitted func-0x73 writes. This fake used to shadow those writes so a bank's
    // digest stayed frozen until StoreBankAsync - modelling the instrument backwards, and the
    // only reason the removed post-write digest gate ever looked tested (commit 68da2e7c).
    // KRONOS_MIDI_SysEx.txt [38] says the digest is "generated from the bank data, in the same
    // format as is sent via func 0x73 and 0x75 dumps"; [73]'s "not committed to storage" is about
    // persistence, not about what is present to hash. Confirmed on hardware: the pre-write gate
    // passes and a post-write re-check fails with only our own writes in between.
    public Task<byte[]?> BankDigestAsync(int obj, int bank)
    {
        CallLog.Add($"Digest:{obj}:{bank}");
        if (NoDigestBanks.Contains((obj, bank))) return Task.FromResult<byte[]?>(null);
        using var sha1 = SHA1.Create();
        using var ms = new MemoryStream();
        for (int n = 0; n < 128; n++)
            if (_objects.TryGetValue((obj, bank, n), out var o)) ms.Write(o.Body, 0, o.Body.Length);
        return Task.FromResult<byte[]?>(sha1.ComputeHash(ms.ToArray()));
    }

    public Task<int> WriteObjectAsync(WriteOp op)
    {
        CallLog.Add("Write");
        BeforeEachWrite?.Invoke();
        // Simulated hardware reject: return the Reply code and leave "hardware" untouched
        // (a rejected write stores nothing), so ApplyMoveAsync must abort before any Store.
        if (WriteRejectCode != 0) return Task.FromResult(WriteRejectCode);
        // Mirrors SysExService.WriteObjectAsync's real stamping - see LibObj.
        // CurrentObjectVersion's comment - so a self-test can verify a stale/placeholder
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
    // ("reformats and erases specified bank"), clears every stored Program in that bank - so a
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

    // ── Remaining IBankDumpService members unused by Pull/Push self-tests - trivial stubs ──
    public bool CanDump => true;
    public Task<SetListData?> DumpSetListAsync(int number) => Task.FromResult<SetListData?>(null);
    public Task<SetListSyncResult> DumpAllSetListsAsync(IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct) =>
        Task.FromResult(new SetListSyncResult(new Dictionary<int, SetListData>(), Array.Empty<int>(), 0, false));
    public Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) => Task.FromResult(0);

    // Seedable by a self-test (key: func-33 type 1=program/0=combi, object bank) so the Local
    // pane's read-only GM/g rows can be exercised without a live name sweep.
    public Dictionary<(int Type, int Bank), Dictionary<int, string>> BankNames { get; } = new();
    public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank) =>
        BankNames.TryGetValue((type, objBank), out var names) ? names : new Dictionary<int, string>();
    public ObjLoc? CurrentPerformanceLoc() => null;

    // Settable by a self-test before constructing the ViewModel under test, to exercise
    // LibrarianShellViewModel.WarmProgramBankTypesAsync/BankTypeOf against a known, fake
    // "real hardware" answer - defaults to null (unreachable/unqueried), same as before.
    public ProgramBankTypes? ProgramBankTypesToReturn { get; set; }
    public Task<ProgramBankTypes?> RequestProgramBankTypesAsync() => Task.FromResult(ProgramBankTypesToReturn);
}
