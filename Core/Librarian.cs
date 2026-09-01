namespace KronosScreenRemote;

static partial class Librarian
{
    static readonly HashSet<int> ReadOnlyProgramBanks =
        new(new[] { 0x10 }.Concat(Enumerable.Range(0x11, 0x0A)));   // GM, g(1)..g(d)

    public static string StoreLabel(int obj, int bank) => obj switch
    {
        LibObj.Program      => $"Prog {KronosBanks.ProgramLabel(bank)}",
        LibObj.Combi        => $"Combi {KronosBanks.CombiLabel(bank)}",
        LibObj.SetList      => "Set Lists",
        LibObj.DrumKit      => $"Drum Kit {KronosBanks.DrumKitLabel(bank)}",
        LibObj.WaveSequence => $"Wave Seq {KronosBanks.WaveSeqLabel(bank)}",
        _ => $"obj{obj:X2}:bank{bank:X2}",
    };

    // ── Name field helpers (Program/Combi bodies: 24-byte ASCII name at offset 0) ──
    // Used by both a single-slot rename write (LibrarianWindow's double-click-to-rename)
    // and anywhere else that needs to read or patch just the name without touching the
    // rest of an object's body.

    public static byte[] PadAscii(string s, int len)
    {
        var data = new byte[len];
        Array.Fill(data, (byte)0x20);
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, data, Math.Min(bytes.Length, len));
        return data;
    }

    public static string ReadName(byte[] body) =>
        System.Text.Encoding.ASCII.GetString(body, 0, Math.Min(24, body.Length)).TrimEnd('\0', ' ');

    // Same bytes as `original`, only the first 24 (the name field) replaced - every other
    // byte of the object (parameters, timbres, whatever) is preserved exactly.
    public static byte[] BuildRenamedBody(byte[] original, string newName)
    {
        var body = (byte[])original.Clone();
        var padded = PadAscii(newName, 24);
        Array.Copy(padded, body, Math.Min(24, body.Length));
        return body;
    }

    // Compute a coherent swap(src, dst). PURE - no hardware access. srcDump/dstDump
    // are the freshly dumped bodies of the two objects being swapped.
    public static MovePlan PlanMove(LibraryCatalog cat, ObjLoc src, ObjectDump srcDump,
                                    ObjLoc dst, ObjectDump dstDump, ObjLoc? active = null)
    {
        var plan = new MovePlan { Src = src, Dst = dst };

        if (src.ObjType != dst.ObjType)
            plan.Warnings.Add(AppMessages.Librarian.Move.CannotMoveBetweenTypes);
        if (src.ObjType == LibObj.Program && ReadOnlyProgramBanks.Contains(dst.Bank))
            plan.Warnings.Add(AppMessages.Librarian.Move.DestinationReadOnlyBank(dst.Label()));
        if (src.Equals(dst))
            plan.Warnings.Add(AppMessages.Librarian.Move.SameLocation);

        // func33-encoded, only for step (4)'s live-preview use below - combi_timbre referrers
        // only ever exist when src/dst are Programs (combi timbres always reference Programs),
        // so refType is always the Program table there regardless of what other referrer kinds
        // this swap also turned up.
        int dstFunc33 = KronosBanks.ObjBankToFunc33(1, dst.Bank);
        int srcFunc33 = KronosBanks.ObjBankToFunc33(1, src.Bank);

        var srcReferrers = cat.ReferrersOf(src);   // → point to dst
        var dstReferrers = cat.ReferrersOf(dst);   // → point back to src
        plan.Referrers.AddRange(srcReferrers);
        plan.Referrers.AddRange(dstReferrers);

        // Group site patches by referring object so each object is written once. Raw bank/number
        // (not pre-encoded) - LibRefs.ApplyResolvedRef below encodes per-Kind (func33 for a
        // Combi/Program target, linear for a Drum Kit/Wave Sequence one), and src/dst can be
        // either depending on what's being swapped.
        var grouped = new Dictionary<(int, int, int), List<(int Site, RefKind Kind, int NewBank, int NewNumber)>>();
        void AddPatch(ReferrerSite r, int newBank, int newNumber)
        {
            var key = (r.RefObj, r.RefBank, r.RefIndex);
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = new();
            list.Add((r.Site, r.Kind, newBank, newNumber));
        }
        foreach (var r in srcReferrers) AddPatch(r, dst.Bank, dst.Number);
        foreach (var r in dstReferrers) AddPatch(r, src.Bank, src.Number);

        // (1) The two swapped objects (patched write to the OTHER location; pre-image
        //     records each at its ORIGINAL location for restore).
        plan.Writes.Add(new WriteOp(src.ObjType, dst.Bank, dst.Number, srcDump.Version, srcDump.Body, $"{src.Label()} -> {dst.Label()}"));
        plan.Writes.Add(new WriteOp(dst.ObjType, src.Bank, src.Number, dstDump.Version, dstDump.Body, $"{dst.Label()} -> {src.Label()}"));
        plan.PreImages.Add(new WriteOp(src.ObjType, src.Bank, src.Number, srcDump.Version, srcDump.Body, "original"));
        plan.PreImages.Add(new WriteOp(dst.ObjType, dst.Bank, dst.Number, dstDump.Version, dstDump.Body, "original"));

        // (2) Patched referrer objects (pre-image = the unpatched original body).
        foreach (var ((refObj, refBank, refIndex), patches) in grouped)
        {
            ObjectDump? baseDump = refObj switch
            {
                LibObj.Combi   => cat.Combis.TryGetValue((refBank, refIndex), out var c) ? c : null,
                LibObj.Program => cat.Programs.TryGetValue((refBank, refIndex), out var p) ? p : null,
                _              => cat.Setlists.TryGetValue(refIndex, out var s) ? s : null,
            };
            if (baseDump == null)
            {
                plan.Warnings.Add(AppMessages.Librarian.Move.ReferringObjectMissing(refObj, refBank, refIndex));
                continue;
            }
            plan.PreImages.Add(new WriteOp(refObj, refBank, refIndex, baseDump.Version, baseDump.Body, "original"));

            var body = (byte[])baseDump.Body.Clone();
            foreach (var (site, kind, newBank, newNumber) in patches)
                LibRefs.ApplyResolvedRef(body, kind, site, src.ObjType, newBank, newNumber);
            plan.Writes.Add(new WriteOp(refObj, refBank, refIndex, baseDump.Version, body, $"fix {patches.Count} ref(s)"));
        }

        // (3) Banks to Store (deduped). Set lists all live under obj 0x0D bank 0.
        foreach (var w in plan.Writes)
            if (!plan.Stores.Contains((w.Obj, w.Bank))) plan.Stores.Add((w.Obj, w.Bank));

        // (4) Optional live dual-write: if the current performance is a combi we are
        //     patching, mirror the change into its edit buffer with 0x43 (audible now).
        if (active is { } act && act.ObjType == LibObj.Combi)
            foreach (var r in plan.Referrers)
                if (r.Kind == RefKind.CombiTimbre && r.RefObj == LibObj.Combi &&
                    r.RefBank == act.Bank && r.RefIndex == act.Number)
                {
                    bool isSrc = r.CurBank == srcFunc33 && r.CurNumber == src.Number;
                    int newBank = isSrc ? dstFunc33 : srcFunc33;
                    int newNum  = isSrc ? dst.Number : src.Number;
                    plan.LivePc.Add(KronosSysEx.BuildParamChange(4, r.Site, 0, 8, 0, newBank));   // pid 8 = bank
                    plan.LivePc.Add(KronosSysEx.BuildParamChange(4, r.Site, 0, 9, 0, newNum));    // pid 9 = number
                }

        // (5) Program bank-type reminder (HD-1/EXi) - enforced at apply via Reply 64.
        if (src.ObjType == LibObj.Program && src.Bank != dst.Bank)
            plan.Warnings.Add(AppMessages.Librarian.Move.CheckProgramMoveAcrossBanks);

        plan.Preview.Add($"SWAP  {src.Label()}  <->  {dst.Label()}  ({(src.ObjType == LibObj.Program ? "programs" : "combis")})");
        plan.Preview.Add($"  references to rewrite: {plan.Referrers.Count}");
        foreach (var r in srcReferrers) plan.Preview.Add($"    - {r.Describe()}  ->  {dst.Label()}");
        foreach (var r in dstReferrers) plan.Preview.Add($"    - {r.Describe()}  ->  {src.Label()}");
        plan.Preview.Add($"  objects to write (0x73): {plan.Writes.Count}");
        plan.Preview.Add("  banks to Store (0x76): " + string.Join(", ", plan.Stores.Select(s => StoreLabel(s.Obj, s.Bank))));
        if (plan.LivePc.Count > 0)
            plan.Preview.Add($"  live edit-buffer preview (0x43): {plan.LivePc.Count} message(s) to active combi");

        return plan;
    }

    // Capture both staleness baselines of every affected bank: the storage digest the pre-write
    // gate compares, and the unsolicited-0x38 push count step 3b compares after the write burst.
    // A bank the instrument gives neither for is deliberately NOT recorded; both apply-side gates
    // walk plan.Stores rather than just the recorded baselines, so a missing entry is reported as
    // an unprotected/unwatched bank instead of silently counting as a pass.
    public static async Task ArmPlanAsync(IExecutablePlan plan, IMoveExecutor ex)
    {
        foreach (var (obj, bank) in plan.Stores)
        {
            var d = await ex.BankDigestAsync(obj, bank).ConfigureAwait(false);
            if (d != null) plan.DigestBaseline[(obj, bank)] = d;
            // Read AFTER the digest, in the same iteration: a Store landing between the two is
            // then already in the digest but not yet in the baseline count, so the pre-write gate
            // catches it. The other order would leave it in neither.
            if (ex.StorageChangeCountFor(obj, bank) is { } pushes)
                plan.StorageChangeBaseline[(obj, bank)] = pushes;
        }
    }

    // Execute a coherent move. Order: backup pre-images -> staleness gate -> writes ->
    // staleness gate again -> Store -> optional live 0x43. Aborts (before any Store) on a
    // stale digest or a rejected write. `stamp` is an externally supplied timestamp.
    public static async Task<(bool Ok, List<string> Steps, string? Aborted)> ApplyMoveAsync(
        IExecutablePlan plan, IMoveExecutor ex, string backupDir, string stamp,
        Action<string>? progress = null, bool doLive = true)
    {
        var steps = new List<string>();
        void Note(string m) { steps.Add(m); progress?.Invoke(m); }

        if (plan.IsRefusable)
            return (false, steps, plan.Warnings.Join());

        // 1. Backup the pre-image of every object we overwrite (restore = replay + Store).
        var safe = $"{stamp}_{plan.BackupLabel}";
        var backupPath = System.IO.Path.Combine(backupDir, safe + ".syx");
        Note($"backup {plan.PreImages.Count} pre-image object(s) -> {backupPath}");
        await ex.BackupObjectsAsync(plan.PreImages, backupPath).ConfigureAwait(false);

        // The staleness gate, run ONCE, before any write. It compares each affected bank's
        // digest against the arm-time baseline, so it is only meaningful while this plan has
        // written nothing: a 0x73 write lands in the bank buffer the digest is hashed over
        // (KRONOS_MIDI_SysEx.txt [38] - "generated from the bank data, in the same format as is
        // sent via func 0x73 and 0x75 dumps"), so after the burst every affected bank mismatches
        // by construction. See step 3b's note for why a post-write re-check cannot use digests.
        async Task<string?> StalenessAbortAsync()
        {
            var unprotected = new List<string>();
            foreach (var (obj, bank) in plan.Stores)
            {
                var cur = await ex.BankDigestAsync(obj, bank).ConfigureAwait(false);
                if (cur == null || !plan.DigestBaseline.TryGetValue((obj, bank), out var baseline))
                {
                    // The read-only GM banks have no digest by design ("Digests are not available
                    // for the GM program and drum kit banks" - both halves, hence the per-type
                    // predicate) and reject a Store anyway. Anywhere else a null means arm or this
                    // re-check got no answer, and a timed-out digest would otherwise look exactly
                    // like a passed gate - so say it out loud.
                    if (!ObjectTypeRegistry.Get(obj).IsReadOnlyBank(bank))
                        unprotected.Add(StoreLabel(obj, bank));
                    continue;
                }
                if (!cur.AsSpan().SequenceEqual(baseline))
                    return $"ABORT: {StoreLabel(obj, bank)} changed since preview (edited at the panel?) - nothing was Stored";
            }
            if (unprotected.Count > 0)
                Note($"WARNING: no digest for {string.Join(", ", unprotected)} - staleness gate cannot protect {(unprotected.Count == 1 ? "that bank" : "those banks")}");
            return null;
        }

        // 2. Staleness gate - abort if any affected bank changed since arm.
        if (await StalenessAbortAsync().ConfigureAwait(false) is { } stale)
            return (false, steps, stale);
        Note("staleness gate passed");

        // 2b. Program bank-type changes (func 0x7C) - REFORMATS AND ERASES each bank to the
        // requested EXi/HD-1 type. After the staleness gate (an external change is still caught
        // first) but before the writes, because 0x7C erases the bank; the changeset guarantees
        // every slot of that bank is in Writes so the whole bank is rebuilt immediately after.
        foreach (var (bank, isExi) in plan.BankTypeChanges)
        {
            int rc = await ex.ChangeProgramBankTypeAsync(bank, isExi).ConfigureAwait(false);
            Note($"change bank type {KronosBanks.ProgramLabel(bank)} -> {(isExi ? "EXi" : "HD-1")} -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: bank-type change rejected (Reply {rc}) for {KronosBanks.ProgramLabel(bank)} - nothing Stored");
        }

        // 3. Send all object writes (volatile).
        foreach (var w in plan.Writes)
        {
            int rc = await ex.WriteObjectAsync(w).ConfigureAwait(false);
            Note($"write 0x73 {StoreLabel(w.Obj, w.Bank)} idx {w.Index} ({w.Note}) -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: write rejected (Reply {rc}) for {StoreLabel(w.Obj, w.Bank)} idx {w.Index} - nothing Stored; replay backups to be safe");
        }

        // 3b. Post-write gate - catches a front-panel Store or PCG load that landed DURING the
        // write burst above, which the pre-write gate structurally cannot see.
        //
        // NOT a digest re-check. That is what this used to be, and it aborted EVERY commit: a
        // 0x73 write lands in the bank buffer the digest is hashed over ([38] "generated from the
        // bank data, in the same format as is sent via func 0x73 and 0x75 dumps"), so after the
        // burst every affected bank mismatches its arm-time baseline by construction. [73]'s "not
        // committed to storage until a Store Bank Request" is about persistence, not about what
        // is present to hash. Observed directly: gate 2 passes, a post-write digest re-check
        // fails, and the only thing in between is our own writes.
        //
        // The detector that does work is the unsolicited 0x38 the instrument PUSHES for exactly
        // this class of event and explicitly not for our 0x73 writes ([38]). Hardware-confirmed:
        // a panel Store pushes obj/bank in bytes 5-6, only on a real change (a no-diff Store
        // pushes nothing, so no false aborts), twice ~100 ms apart - which is why the baseline is
        // compared for inequality rather than counted.
        //
        // UNVERIFIED, and the reason the pass note below says only what was OBSERVED: whether an
        // instrument that answers request/reply SysEx necessarily also transmits unsolicited
        // pushes is not established. If some Global/MIDI setting can enable the first and not the
        // second, this gate goes quiet without going null, and a quiet gate would be a fail-open
        // on exactly the hazard it exists for. Never reword the note into a claim of safety.
        {
            var unwatched = new List<string>();
            foreach (var (obj, bank) in plan.Stores)
            {
                int? pushes = ex.StorageChangeCountFor(obj, bank);
                if (pushes == null || !plan.StorageChangeBaseline.TryGetValue((obj, bank), out var armed))
                {
                    // Same policy as the digest gate's `unprotected`: a bank whose pushes could
                    // not be watched must never look like a bank that was watched and stayed
                    // quiet. Say it out loud instead - shipping a silent pass here would be the
                    // same fail-open class of bug as the digest gate this replaces.
                    unwatched.Add(StoreLabel(obj, bank));
                    continue;
                }
                if (pushes != armed)
                    return (false, steps, $"ABORT: {StoreLabel(obj, bank)} was Stored on the instrument during this commit's writes - nothing was Stored by us"
                                        + "; this plan's 0x73 writes ARE already in the instrument's volatile bank buffer, and a Store from the panel would commit them - replay backups to be safe");
            }
            if (unwatched.Count > 0)
                Note($"WARNING: no storage-change notifications for {string.Join(", ", unwatched)} - a Store made at the panel during this commit's writes would not have been caught");
            else
                Note("no panel-Store notification seen during the write burst");
        }

        // 4. Commit each affected bank.
        foreach (var (obj, bank) in plan.Stores)
        {
            int rc = await ex.StoreBankAsync(obj, bank).ConfigureAwait(false);
            Note($"Store 0x76 {StoreLabel(obj, bank)} -> Reply {rc}");
            if (rc != 0)
                return (false, steps, $"ABORT: Store rejected (Reply {rc}) for {StoreLabel(obj, bank)} - replay backups");
        }

        // 5. Optional live edit-buffer dual-write (audible-now, non-persisting).
        if (doLive && plan.LivePc.Count > 0)
        {
            foreach (var msg in plan.LivePc) await ex.SendRawAsync(msg).ConfigureAwait(false);
            Note($"live 0x43 x{plan.LivePc.Count} to active combi");
        }

        return (true, steps, null);
    }
}
