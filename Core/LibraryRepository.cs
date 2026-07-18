namespace KronosScreenRemote;

// The Librarian's single data-layer entry point. Turns the persisted reference-graph cache
// (Storage.LoadRefGraph/SaveRefGraph) into a live RefIndex (Core/LibrarianModel.cs), decides
// what a "lazy" scan actually needs to re-fetch by diffing per-bank digests, and wraps
// ISysExService's Sync Names / Sync All so every caller shares one code path instead of
// separate flows hitting different subsets of Storage's per-host JSON caches.
//
// Split like Librarian (Core/LibrarianModel.cs): PlanScan is PURE and off-hardware
// testable (SelfTest exercises it); only ScanAsync/SyncNamesAsync/SyncAllAsync talk to the
// instrument, through ISysExService.
static class LibraryRepository
{
    // Combi banks a move can ever reference: 7 INT (I-A..I-G) + 7 USER (U-A..U-G). Programs
    // are never referrers (see LibrarianModel.cs's referrer scope), so program banks are
    // never part of this graph — only combi bodies + set lists get scanned/persisted here,
    // matching what the Librarian's scan has always actually fetched.
    public static IEnumerable<int> CombiRefBanks() => Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7));

    // ── RefIndex <-> persisted RefGraph ──────────────────────────────────────

    static Storage.RefGraph ToRefGraph(RefIndex ri) => new(
        ri.CombiRefs.Select(kv => new Storage.RefGraphCombiEntry(
            kv.Key.Bank, kv.Key.Index, kv.Value.Select(r => new[] { r.Bank, r.Number }).ToList())).ToList(),
        ri.SetlistRefs.Select(kv => new Storage.RefGraphSetlistEntry(
            kv.Key, kv.Value.Select(r => new[] { r.Slot, r.Type, r.Bank, r.Index }).ToList())).ToList(),
        ri.ScanDigests.Select(kv => new Storage.RefGraphDigest(
            kv.Key.Obj, kv.Key.Bank, Convert.ToHexString(kv.Value))).ToList());

    static RefIndex FromRefGraph(Storage.RefGraph g)
    {
        var ri = new RefIndex();
        foreach (var c in g.Combis)
            ri.CombiRefs[(c.Bank, c.Index)] = c.Refs.Select(r => (r[0], r[1])).ToList();
        foreach (var s in g.Setlists)
            ri.SetlistRefs[s.Number] = s.Refs.Select(r => (r[0], r[1], r[2], r[3])).ToList();
        foreach (var d in g.Digests)
            ri.ScanDigests[(d.Obj, d.Bank)] = Convert.FromHexString(d.Hex);
        return ri;
    }

    public static RefIndex LoadRefIndex(string host) => FromRefGraph(Storage.LoadRefGraph(host));
    public static void SaveRefIndex(string host, RefIndex ri) => Storage.SaveRefGraph(host, ToRefGraph(ri));

    // ── Pure scan planning ───────────────────────────────────────────────────
    // Decides which combi banks and whether set lists need re-fetching. A bank/set-list with
    // no persisted digest (never scanned, or scanned before this cache existed) is always
    // treated as changed — same "unknown = needs work" convention Storage's dumped-bank
    // ledger already uses. The set-list digest is all-or-nothing across all 128 set lists
    // (obj 0x0D isn't bank-partitioned on the wire), so any set-list change re-fetches all of
    // them; there's no finer-grained hardware signal to diff against.
    public sealed record ScanPlan(List<int> CombiBanksToFetch, bool FetchSetLists, bool FirstRun);

    public static ScanPlan PlanScan(
        IReadOnlyDictionary<(int Obj, int Bank), byte[]> persistedDigests,
        IReadOnlyDictionary<(int Obj, int Bank), byte[]> freshDigests,
        bool full)
    {
        bool firstRun = persistedDigests.Count == 0;
        var combiBanks = CombiRefBanks().ToList();
        if (full) return new ScanPlan(combiBanks, true, firstRun);

        bool Changed(int obj, int bank) =>
            !persistedDigests.TryGetValue((obj, bank), out var baseline) ||
            !freshDigests.TryGetValue((obj, bank), out var cur) ||
            !cur.AsSpan().SequenceEqual(baseline);

        var toFetch = combiBanks.Where(b => Changed(LibObj.Combi, b)).ToList();
        bool fetchSetLists = Changed(LibObj.SetList, 0);
        return new ScanPlan(toFetch, fetchSetLists, firstRun);
    }

    // ── Async scan orchestration ─────────────────────────────────────────────
    // full=false (lazy): re-read every combi bank's + the set-list digest, diff against the
    // persisted baseline, and only re-sweep what changed (a changed bank means a full 128-slot
    // re-sweep of THAT bank — a bank digest can't say which slot changed, only that one did).
    // full=true: re-sweep everything regardless of digests. Either way, every bank's fresh
    // digest is recorded before saving, so next time's lazy diff is against what's true now.
    public static async Task<(RefIndex Index, ScanPlan Plan)> ScanAsync(
        ISysExService sysEx, string host, bool full,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // LoadRefIndex/SaveRefIndex do synchronous JSON (de)serialization + file I/O — big
        // enough (up to ~28k combi-timbre + ~16k set-list-slot tuples) to visibly stall the
        // UI thread if run inline, so both ends of this method push it to a background thread.
        var ri = full ? new RefIndex() : await Task.Run(() => LoadRefIndex(host));
        var persistedDigests = new Dictionary<(int, int), byte[]>(ri.ScanDigests);

        var fresh = new Dictionary<(int, int), byte[]>();
        foreach (var bank in CombiRefBanks())
        {
            var d = await sysEx.BankDigestAsync(LibObj.Combi, bank);
            if (d != null) fresh[(LibObj.Combi, bank)] = d;
        }
        var slDigest = await sysEx.BankDigestAsync(LibObj.SetList, 0);
        if (slDigest != null) fresh[(LibObj.SetList, 0)] = slDigest;

        var plan = PlanScan(persistedDigests, fresh, full);

        int total = plan.CombiBanksToFetch.Count * 128 + (plan.FetchSetLists ? SetListData.MaxCount : 0);
        int done = 0;
        foreach (var bank in plan.CombiBanksToFetch)
        {
            for (int number = 0; number < 128; number++)
            {
                if (ct.IsCancellationRequested) break;
                var d = await sysEx.DumpObjectAsync(LibObj.Combi, bank, number);
                // One malformed/short object must not abort the whole scan — skip and log it.
                try { if (d != null) ri.AddCombi(d); }
                catch (Exception ex) { AppLog.Warn($"[librarian] skipped malformed combi {KronosBanks.CombiLabel(bank)}:{number:D3}: {ex.Message}"); }
                done++;
                progress?.Invoke($"Scanning {done}/{total} — combi {KronosBanks.CombiLabel(bank)}:{number:D3}");
            }
        }
        if (plan.FetchSetLists)
        {
            for (int number = 0; number < SetListData.MaxCount; number++)
            {
                if (ct.IsCancellationRequested) break;
                var d = await sysEx.DumpObjectAsync(LibObj.SetList, 0, number);
                try { if (d != null) ri.AddSetlist(d); }
                catch (Exception ex) { AppLog.Warn($"[librarian] skipped malformed set list {number:D3}: {ex.Message}"); }
                done++;
                progress?.Invoke($"Scanning {done}/{total} — set list {number:D3}");
            }
        }

        foreach (var ((obj, bank), digest) in fresh)
            ri.RecordDigest(obj, bank, digest);

        if (!ct.IsCancellationRequested) await Task.Run(() => SaveRefIndex(host, ri));
        return (ri, plan);
    }

    // ── Sync Names / Sync All ────────────────────────────────────────────────
    // Thin wrappers — the actual sync logic already lives in ISysExService (never
    // MainWindow-coupled); this just gives every caller (now: only LibrarianWindow) one
    // shared entry point instead of duplicating the confirm/progress/cache-merge dance.
    public static Task<int> SyncNamesAsync(
        ISysExService sysEx, IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct) =>
        sysEx.SyncNamesAsync(progress, ct);

    public static async Task<(int Names, SetListSyncResult SetLists)> SyncAllAsync(
        ISysExService sysEx, string host,
        IProgress<(int Done, int Total, int Names)>? nameProgress,
        IProgress<(int Done, int Total, int Found)>? listProgress,
        CancellationToken ct)
    {
        int names = await sysEx.SyncNamesAsync(nameProgress, ct);
        var result = await sysEx.DumpAllSetListsAsync(listProgress, ct);

        if (result.Found.Count > 0 || result.ConfirmedEmpty.Count > 0)
        {
            // Off the UI thread — each Set List is ~79 KB of decoded data, so (de)serializing
            // a populated cache inline froze the window (the same reason MainWindow's original
            // Sync All wrapped this in Task.Run before this logic moved here).
            await Task.Run(() =>
            {
                var cache = Storage.LoadSetLists(host);
                foreach (var kv in result.Found) cache[kv.Key] = kv.Value;
                foreach (var n in result.ConfirmedEmpty) cache.Remove(n);
                Storage.SaveSetLists(host, cache);
            });
        }
        return (names, result);
    }

    // ── Off-hardware self-test (invoked at diagnostic startup via App, alongside
    //    Librarian.SelfTest()). Returns the list of failing check names; empty = all passed.
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var banks = CombiRefBanks().ToList();

        // 1. No persisted baseline at all -> first run, everything needs fetching.
        var empty = new Dictionary<(int, int), byte[]>();
        var freshAllPresent = banks.ToDictionary(b => (LibObj.Combi, b), b => new byte[] { (byte)b });
        freshAllPresent[(LibObj.SetList, 0)] = new byte[] { 0xAA };
        var p1 = PlanScan(empty, freshAllPresent, full: false);
        Check("firstrun-is-full", p1.FirstRun && p1.CombiBanksToFetch.Count == banks.Count && p1.FetchSetLists);

        // 2. Matching digests everywhere -> nothing to fetch.
        var p2 = PlanScan(freshAllPresent, freshAllPresent, full: false);
        Check("no-change-fetches-nothing", !p2.FirstRun && p2.CombiBanksToFetch.Count == 0 && !p2.FetchSetLists);

        // 3. One combi bank's digest changed -> only that bank is flagged.
        int oneBank = banks[0];
        var freshOneChanged = new Dictionary<(int, int), byte[]>(freshAllPresent) { [(LibObj.Combi, oneBank)] = new byte[] { 0xFF } };
        var p3 = PlanScan(freshAllPresent, freshOneChanged, full: false);
        Check("single-bank-change-detected", p3.CombiBanksToFetch.Count == 1 && p3.CombiBanksToFetch[0] == oneBank && !p3.FetchSetLists);

        // 4. Set-list digest changed -> FetchSetLists true, no combi banks flagged.
        var freshSlChanged = new Dictionary<(int, int), byte[]>(freshAllPresent) { [(LibObj.SetList, 0)] = new byte[] { 0xBB } };
        var p4 = PlanScan(freshAllPresent, freshSlChanged, full: false);
        Check("setlist-change-detected", p4.CombiBanksToFetch.Count == 0 && p4.FetchSetLists);

        // 5. full:true always requests everything regardless of digests matching.
        var p5 = PlanScan(freshAllPresent, freshAllPresent, full: true);
        Check("full-scan-ignores-digests", p5.CombiBanksToFetch.Count == banks.Count && p5.FetchSetLists);

        // 6. RefIndex <-> RefGraph round-trip (pure in-memory — no disk I/O in a self-test).
        var ri = new RefIndex();
        var cbody = new byte[7810];
        LibRefs.SetCombiTimbreRef(cbody, 0, 5, 42);
        ri.AddCombi(new ObjectDump(LibObj.Combi, 0x00, 3, 3, cbody));
        var slbody = new byte[69416];
        LibRefs.SetSetListSlotRef(slbody, 1, 19, 7, type: 1);
        ri.AddSetlist(new ObjectDump(LibObj.SetList, 0, 9, 0, slbody));
        ri.RecordDigest(LibObj.Combi, 0x00, new byte[] { 1, 2, 3 });
        ri.RecordDigest(LibObj.SetList, 0, new byte[] { 4, 5 });

        var ri2 = FromRefGraph(ToRefGraph(ri));
        Check("refgraph-roundtrip-combi", ri2.CombiRefs[(0x00, 3)].SequenceEqual(ri.CombiRefs[(0x00, 3)]));
        Check("refgraph-roundtrip-setlist", ri2.SetlistRefs[9].SequenceEqual(ri.SetlistRefs[9]));
        Check("refgraph-roundtrip-digest", ri2.ScanDigests[(LibObj.Combi, 0x00)].SequenceEqual(ri.ScanDigests[(LibObj.Combi, 0x00)]));
        Check("refgraph-roundtrip-usage", ri2.UsageCount(new ObjLoc(LibObj.Program, 5, 42)) == ri.UsageCount(new ObjLoc(LibObj.Program, 5, 42)));

        return fails;
    }
}
