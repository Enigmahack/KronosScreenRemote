using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace KronosScreenRemote;

sealed class SysExService : ISysExService
{

    readonly Dispatcher _dispatcher;

    // Coalescing window for perf-metadata refresh after a Program/Bank change.
    // A Bank Select is CC0 + CC32 + PC in a burst; debounce collapses them into
    // one query and makes external program changes feel instant.
    const int PerfRefreshDebounceMs = 300;

    // Settle window for the optional per-change name pull (PullNamesOnChange). A
    // wheel/INC scroll fires many program changes; only the one it lands on is
    // pulled. Short because a USB name fetch round-trips in ~ms.
    const int NamePullDebounceMs = 150;

    // Only the heavy name dump (func 0x75) is gated on interaction: skip it while
    // the user is actively driving the client (would stutter the video), show the
    // id immediately, and retry once idle. Identity (func 0x33) is never gated.
    const double PerfDumpQuietSec = 0.8;

    IKronosMidiTransport? _transport;
    SysExDumpCollector? _dump;
    // Pauses the func-33 perf-poll loop while a bulk dump/write streams (so the poll can't steal
    // one of its replies). Epoch-guarded refcount - see DumpGate for why a plain bool was racy
    // both across overlapping dumps and across a transport switch. Begin/End pair in each dump's
    // finally; NewGeneration() on every transport (re)start / reset.
    readonly DumpGate _dumpGate = new();
    CancellationTokenSource? _cts;
    CancellationTokenSource? _perfPollDelayCts;
    // These three used to be CancellationTokenSource-per-debounce, cancelling the PREVIOUS
    // pending Task.Delay whenever a new event superseded it - correct, but a genuine
    // TaskCanceledException THROW (caught, but still a real first-chance exception) on every
    // single collision. Fine for light, human-interaction-paced events; catastrophic under a
    // Sync's burst of passive stream traffic - a Full Sync's own bank-digest replies collide
    // with DeferredRefreshAsync's 300ms window on nearly every one, and every newly-learned
    // object name collides with SchedulePersist's 2s window on nearly every one, which is
    // exactly the "thousands of TaskCanceledException, roughly one per synced object" symptom
    // this was rewritten to fix (2026-08-10, diagnosed from a live repro: ~4386 exceptions
    // against 4480 synced objects). An epoch counter is the non-throwing equivalent: bump it,
    // start a plain (tokenless) Task.Delay, and after it elapses check whether a LATER call
    // already bumped the epoch past this one - if so, a newer settle is already in flight and
    // this one silently no-ops instead of doing the work AND instead of throwing to get there.
    long _refreshEpoch;
    long _namePullEpoch;
    long _persistEpoch;
    DateTime _lastUserActivity = DateTime.MinValue;
    // Stable per-Kronos key for the on-disk name / dumped-bank caches (from the
    // transport: host for TCP, "usb:<match>" for USB).
    string _cacheKey = "";

    // Perf-Id Request (func 0x32); reply is Performance Id (func 0x33).
    static readonly byte[] PerfIdRequest = { 0xF0, 0x42, 0x30, 0x68, 0x32, 0xF7 };

    public int ValueSliderCc { get; set; } = 18;

    // When true, entering a program/combi whose name isn't cached triggers a single
    // func-0x72 name fetch (debounced). Off by default - over the daemon/DIN path a
    // per-change dump was slow and popped the Kronos "Transmitting MIDI Data..." flash;
    // over USB a single name object is a ~ms round-trip, so it's now viable.
    public bool PullNamesOnChange { get; set; }

    // ── Program-change stream decode ────────────────────────────────────────────
    // Identity (bank + number) comes from PC + Bank Select on the live stream -
    // zero SysEx, so no "Transmitting MIDI Data..." flash per change. Names come
    // from a per-bank bulk dump (one flash per bank, then cached).
    int  _stateMode;             // 1..7 (probe + func 0x4E); 2=Combi, 3=Program
    int  _bankMsb, _bankLsb;     // last Bank Select MSB (CC0) / LSB (CC32)
    bool _haveBankContext;       // a Bank Select has been seen this session
    BankId? _lastBankId;         // last decoded id - lets a bare PC reuse the bank
    readonly Dictionary<(int Type, int ObjBank, int Number), string> _streamNames = new();
    readonly HashSet<(int Type, int Bank)> _dumpedBanks = new();  // name-dumps already collected (persisted)
    readonly HashSet<int> _loggedDumpObjs = new();  // log each unexpected dump obj once

    bool _midiMonitorEnabled = true;
    bool _proactivePoll       = false;
    int  _pollIntervalSec     = 60;
    bool _pollOnChanges       = true;

    string _performanceDisplay = "";
    bool _isAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? ValueSliderChanged;
    public event Action<SysExTrafficEntry>? SysExTraffic;

    public string PerformanceDisplay
    {
        get => _performanceDisplay;
        private set => SetProperty(ref _performanceDisplay, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public SysExService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start(IKronosMidiTransport transport)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        // New transport generation: a sweep orphaned by this switch must not pause the new perf
        // loop, and its later End() is now a no-op (see DumpGate). NOTE (deferred, issue 8): this
        // does not await an in-flight WriteObject/StoreBank - teardown below can still dispose the
        // old transport mid-inject. Reads fail gracefully into a timeout; a torn write can leave a
        // partial object on the Kronos. Closing that needs async-aware teardown, which couples with
        // MidiTransportCoordinator's synchronous _gate - left for a dedicated pass.
        _dumpGate.NewGeneration();

        // Supersede debounced work from the outgoing connection and flush its captured names
        // under the OLD cache key first - otherwise a still-pending PersistNames (2 s debounce)
        // could fire after _cacheKey is repointed and write the previous Kronos's names under
        // the new one's key (cross-instrument contamination). Bumping the epoch makes any
        // already-waiting settle task a no-op when it wakes, same effect the old Cancel() had.
        Interlocked.Increment(ref _refreshEpoch);
        Interlocked.Increment(ref _persistEpoch);
        Interlocked.Increment(ref _namePullEpoch);
        if (!string.IsNullOrEmpty(_cacheKey)) PersistNames();

        // Tear down any previously-running transport (e.g. switching TCP → USB).
        if (_transport != null && !ReferenceEquals(_transport, transport))
        {
            _transport.Traffic -= OnTransportTraffic;
            _transport.Stop();
            _transport.Dispose();
        }

        _transport = transport;
        _cacheKey  = transport.CacheKey;
        PerformanceDisplay = "";
        IsAvailable = false;

        // Reset program-change stream-decode state for the new connection, then
        // seed the name cache from disk so program changes are flash-free from the
        // first change (banks with persisted names are marked already-loaded).
        _stateMode = 0;
        _bankMsb = _bankLsb = 0;
        _haveBankContext = false;
        _lastBankId = null;
        var persisted = Storage.LoadNames(_cacheKey);
        lock (_streamNames)
        {
            _streamNames.Clear();
            foreach (var e in persisted) _streamNames[(e.Type, e.Bank, e.Number)] = e.Name;
        }
        lock (_dumpedBanks)
        {
            _dumpedBanks.Clear();
            foreach (var k in Storage.LoadDumpedBanks(_cacheKey)) _dumpedBanks.Add(k);
        }

        _transport.Traffic += OnTransportTraffic;
        _transport.SetStreamEnabled(_midiMonitorEnabled);
        _dump = new SysExDumpCollector(_transport);   // dumps gate on CanDump (stream active)
        _transport.Start();

        AppLog.Info($"[sysex] transport started - {_transport.Description}");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Task.Run(() => ProbeAsync(ct));
        _ = PerfMetadataLoop(ct);
    }

    public void Reset()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _dumpGate.NewGeneration();
        Interlocked.Increment(ref _refreshEpoch);
        Interlocked.Increment(ref _persistEpoch);
        Interlocked.Increment(ref _namePullEpoch);
        if (_transport != null)
        {
            _transport.Traffic -= OnTransportTraffic;
            _transport.Stop();
            _transport.Dispose();
        }
        _transport = null;
        _dump = null;
        IsAvailable = false;
        PerformanceDisplay = "";
    }

    void OnTransportTraffic(SysExTrafficEntry entry)
    {
        SysExTraffic?.Invoke(entry);

        // Only interpret messages the Kronos actually transmitted on the live
        // stream (IsMidi + not our own send + raw bytes present).
        if (entry.IsSend || !entry.IsMidi || entry.RawBytes is not { Length: > 0 } raw)
            return;

        ParseIncoming(raw);
    }

    // Decode signals the Kronos pushes unsolicited on the MIDI-out stream.
    // Every recognised message is logged so an absent signal (transmit disabled
    // in the Kronos Global/MIDI settings) is distinguishable from a parse bug.
    void ParseIncoming(byte[] raw)
    {
        byte status = raw[0];

        // Bank Digest (SysEx func 0x38): pushed whenever bank storage changes
        // (program write, PCG load, bank-type change). Invalidate just the named
        // bank so the rest of the persisted cache survives. Layout:
        //   F0 42 3g 68 38 obj bank <23-byte digest> F7
        if (KronosSysEx.HasKorgHeaderAt(raw, 0, 0x38) && raw.Length >= 7)
        {
            int digObj  = raw[5];   // 0x00 = Program, 0x01 = Combi, ...
            int digBank = raw[6];   // object bank number

            if (digObj is 0x00 or 0x01)
            {
                int t = digObj == 0x00 ? 1 : 0;     // program = 1, combi = 0
                lock (_streamNames)
                    foreach (var k in _streamNames.Keys.Where(k => k.Type == t && k.ObjBank == digBank).ToList())
                        _streamNames.Remove(k);
                // Also clear the dumped-ledger entry so a changed bank is re-dumped
                // on the next Sync (its old names/blank status are now stale).
                bool wasDumped;
                lock (_dumpedBanks) wasDumped = _dumpedBanks.Remove((t, digBank));
                if (wasDumped) Storage.SaveDumpedBanks(_cacheKey, SnapshotDumped());
                PersistNames();
                AppLog.Debug($"[sysex] bank digest 0x38 obj={digObj:X2} bank={digBank:X2} - bank invalidated");
            }

            _ = DeferredRefreshAsync();
            return;
        }

        // Object Dump (SysEx func 0x73): passively capture program/combi names
        // from ANY dump - a front-panel Global dump or our own Sync Names sweep.
        // The Name is offset 0 (24 bytes) of every object, full or name-only.
        //   F0 42 3g 68 73 obj bank idH idL version <data> F7
        if (KronosSysEx.HasKorgHeaderAt(raw, 0, 0x73) && raw.Length >= 11)
        {
            int obj = raw[5];
            // 0x00 Program / 0x13 Program Name → program; 0x01 Combi / 0x12 Combi Name → combi.
            if (obj is 0x00 or 0x13 or 0x01 or 0x12)
            {
                int type    = obj is 0x00 or 0x13 ? 1 : 0;
                int objBank = raw[6];
                var (idx, nm) = KronosSysEx.ParseNameObjectDump(raw);
                AppLog.Debug($"[sysex] capture obj={obj:X2} bank={objBank:X2} idx={idx} len={raw.Length} name=\"{nm}\"");
                if (idx >= 0 && nm.Length > 0)
                {
                    bool added;
                    lock (_streamNames)
                        added = !_streamNames.TryGetValue((type, objBank, idx), out var old) || old != nm;
                    if (added)
                    {
                        lock (_streamNames) _streamNames[(type, objBank, idx)] = nm;
                        SchedulePersist();
                        // Live-fill the footer if this is the current program.
                        if (_lastBankId is { } cur && cur.Type == type && cur.ObjBank == objBank && cur.Number == idx)
                            SetStreamPerfDisplay(cur, nm);
                    }
                }
            }
            else if (_loggedDumpObjs.Add(obj))
            {
                AppLog.Info($"[sysex] dump obj=0x{obj:X2} seen on stream but not a program/combi name object");
            }
            return;
        }

        // Mode Change (SysEx func 0x4E): F0 42 3g 68 4E 0m F7 - passive live-stream
        // signal. The UI no longer treats this as a mode source (the daemon's STATE
        // command is authoritative there - see ScreenSession's STATE polling),
        // but it's still the freshest available seed for _stateMode, which
        // Program-Change stream decode below needs to resolve the right bank.
        if (KronosSysEx.HasKorgHeaderAt(raw, 0, 0x4E) && raw.Length >= 7)
        {
            var md = new SysExModeData(raw[5] & 0x0F, 0, 0, 0);
            int stateMode = md.ToStateMode();
            if (stateMode > 0)
            {
                _stateMode = stateMode;   // for program-change stream decode
                AppLog.Info($"[sysex] mode-change 0x4E -> {md.ModeName} (state={stateMode})");
            }
            return;
        }

        // Channel messages.
        int hi = status & 0xF0;

        // Control Change: value-slider follow (CC# = ValueSliderCc) and
        // Program/Bank-change perf refresh (Bank Select CC0/CC32).
        if (hi == 0xB0 && raw.Length >= 3)
        {
            int cc  = raw[1] & 0x7F;
            int val = raw[2] & 0x7F;

            // Bank Select (MSB/LSB) always takes priority - a misconfigured
            // ValueSliderCc must never shadow program-change follow. Record it for
            // the next Program Change's stream decode (no SysEx query here).
            if (cc == 0)  { _bankMsb = val; _haveBankContext = true; return; }
            if (cc == 32) { _bankLsb = val; _haveBankContext = true; return; }

            if (cc == ValueSliderCc)
            {
                AppLog.Debug($"[sysex] value-slider CC#{cc} = {val}");   // Debug: fires rapidly on a sweep
                _dispatcher.InvokeAsync(() => ValueSliderChanged?.Invoke(val));
            }
            return;
        }

        // Program Change: resolve the new performance from the stream (Bank Select
        // + PC) with zero SysEx, so no flash. Fall back to a func 0x33 query only
        // when we can't decode (no bank context yet, or a non-Program/Combi mode).
        if (hi == 0xC0)
        {
            int pc = raw[1] & 0x7F;

            BankId? id = _haveBankContext ? KronosBanks.Decode(_stateMode, _bankMsb, _bankLsb, pc) : null;

            // Bare PC (no Bank Select this change) - reuse the last decoded bank
            // with the new program number, as long as we're still in its mode.
            if (id == null && _lastBankId is { } last &&
                (_stateMode == 2 || _stateMode == 3) &&
                last.Type == (_stateMode == 2 ? 0 : 1))
                id = last with { Number = pc };

            if (id is { } resolved)
            {
                _lastBankId = resolved;
                OnStreamProgramChange(resolved);
                return;
            }
            if (_pollOnChanges) _ = DeferredRefreshAsync();
        }
    }

    // A Program Change decoded straight from the stream. Zero SysEx: show
    // bank:number and the name IF we have it cached (from a Sync Names dump or a
    // captured front-panel dump). Never queries the Kronos on a program change.
    void OnStreamProgramChange(BankId id)
    {
        string? name;
        lock (_streamNames) _streamNames.TryGetValue((id.Type, id.ObjBank, id.Number), out name);
        SetStreamPerfDisplay(id, name);

        // Optional per-change name pull: if we don't have this object's name, fetch
        // just its name object. ParseIncoming captures the 0x73 reply and live-fills
        // the footer. Debounced so a fast scroll pulls only where it settles.
        if (PullNamesOnChange && string.IsNullOrWhiteSpace(name))
            ScheduleNamePull(id);
    }

    // Debounced single-object name fetch for the current program/combi (func 0x72).
    void ScheduleNamePull(BankId id)
    {
        long epoch = Interlocked.Increment(ref _namePullEpoch);
        _ = NamePullAfterSettleAsync(id, epoch);
    }

    async Task NamePullAfterSettleAsync(BankId id, long epoch)
    {
        await Task.Delay(NamePullDebounceMs).ConfigureAwait(false);
        if (Interlocked.Read(ref _namePullEpoch) != epoch) return;   // a later call superseded this one

        // Never inject a single-name request into a bulk sweep's 0x73 stream.
        if (_dumpGate.Active) return;
        var transport = _transport;
        if (transport?.CanStream != true) return;

        // A passive dump may have filled the cache during the settle window.
        lock (_streamNames)
            if (_streamNames.ContainsKey((id.Type, id.ObjBank, id.Number))) return;

        byte nameObj = (byte)KronosBanks.NameObject(id.Type);
        var req = MidiHex.ToBytes(SysExDumpCollector.ObjectDumpRequest(nameObj, id.ObjBank, id.Number));
        if (req == null) return;
        AppLog.Debug($"[sysex] name-pull func72 obj={nameObj:X2} bank={id.ObjBank:X2} idx={id.Number}");
        await transport.SendAsync(req).ConfigureAwait(false);
    }

    void SetStreamPerfDisplay(BankId id, string? name)
    {
        var display = string.IsNullOrWhiteSpace(name) ? id.Display : $"{id.Display} {name}";
        if (display != PerformanceDisplay)
            AppLog.Info($"[sysex] program change (stream): {display}");
        PerformanceDisplay = display;   // setter marshals PropertyChanged to the UI thread
    }

    void PersistNames()
    {
        List<CachedName> snapshot;
        lock (_streamNames)
            snapshot = _streamNames
                .Select(kv => new CachedName(kv.Key.Type, kv.Key.ObjBank, kv.Key.Number, kv.Value))
                .ToList();
        Storage.SaveNames(_cacheKey, snapshot);
    }

    // Passive capture can add hundreds of names in a burst (a Full Sync can mean thousands);
    // debounce the disk write so a full dump persists once when it settles, not per object.
    void SchedulePersist()
    {
        long epoch = Interlocked.Increment(ref _persistEpoch);
        _ = PersistAfterSettleAsync(epoch);
    }

    async Task PersistAfterSettleAsync(long epoch)
    {
        await Task.Delay(2000).ConfigureAwait(false);
        if (Interlocked.Read(ref _persistEpoch) != epoch) return;   // a later call superseded this one
        PersistNames();
    }

    int CurrentNameCount() { lock (_streamNames) return _streamNames.Count; }

    HashSet<(int Type, int Bank)> SnapshotDumped()
    {
        lock (_dumpedBanks) return new HashSet<(int, int)>(_dumpedBanks);
    }

    // User-triggered: request every program/combi bank's names (func 0x77) and let
    // passive capture (ParseIncoming func 0x73) fill the cache. Robust to whether
    // the Kronos answers with name-only or full objects.
    //
    // SERIALIZE - one bank in flight at a time. Firing all ~45 requests back-to-back
    // on a timer overruns the Kronos MIDI-in and it silently drops most. CollectAsync
    // paces us per bank; ParseIncoming captures names off the same stream (keyed by
    // the bank byte), so a front-panel dump also populates names.
    //
    // TWO DUMP PATHS, one per bank kind (see KronosBanks / the loop below):
    //   • PRESET banks (INT, GM)  - func-0x77 whole-bank name ENUM. One request,
    //     ~20 ms for 128 names. This enum is firmware-limited to preset banks.
    //   • WRITABLE banks (USER)   - the func-0x77 enum REJECTS them (Reply code 4),
    //     so pull each slot with a paced func-0x72 fetch (SysExDumpCollector
    //     .CollectPerObjectNamesAsync). Confirmed on HW at 128/128 for user banks.
    // There is NO per-object session throttle: the old "~13 banks/session then it
    // rejects everything" reading was a misdiagnosis - presets dumped and USER banks
    // rejected the *enum*, not a session cap. So a SINGLE Sync now pulls every bank.
    // The persisted `_dumpedBanks` ledger still usefully skips banks already done
    // across runs; a bank that doesn't complete is left un-dumped (retryable) - never
    // guessed "absent" and marked done, which once corrupted the ledger.
    public async Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return CurrentNameCount();

        var all   = KronosBanks.AllNameBanks().ToList();   // program banks first, then combi
        int total = all.Count;
        List<(int Type, int ObjBank)> todo;
        lock (_dumpedBanks) todo = all.Where(b => !_dumpedBanks.Contains((b.Type, b.ObjBank))).ToList();

        if (todo.Count == 0)
        {
            AppLog.Info($"[sync] all {total} banks already dumped - nothing to do");
            return CurrentNameCount();
        }

        int gateEpoch = _dumpGate.Begin();   // pause the func-33 perf loop for the whole sweep
        bool ledgerDirty = false;
        try
        {
            int gotThisRun = 0;

            foreach (var (type, objBank) in todo)
            {
                if (ct.IsCancellationRequested) break;
                byte nameObj = (byte)KronosBanks.NameObject(type);
                string label = type == 1 ? "prog" : "combi";
                bool done;

                if (objBank < 0x40)
                {
                    // PRESET bank (INT, GM): the firmware's func-0x77 whole-bank name
                    // ENUM works and streams all 128 names in ~20 ms - one request.
                    // Blocks until its replies go idle, the Kronos rejects (func 0x24 →
                    // fast exit), or it stays silent.
                    var req  = SysExDumpCollector.DumpBankRequest(nameObj, objBank);
                    var (msgs, _) = await dump.CollectAsync(req, nameObj, expectedCount: null,
                                            idleMs: 600, noResponseMs: 1200,
                                            stallMs: 3000, overallMs: 30000)
                              .ConfigureAwait(false);
                    AppLog.Info($"[sync] {label} bank=0x{objBank:X2} (0x77 enum) collected {msgs.Count} name(s)");
                    // A send failure here just leaves the bank out of _dumpedBanks below (not
                    // "done"), so an unlucky transient failure self-heals on the next Sync Names
                    // run - unlike the object-body pull path (DumpObjectAsync/DumpBankBulkAsync),
                    // nothing here writes a false "confirmed empty" into the local cache, so no
                    // retry is needed for correctness.
                    done = msgs.Count > 0;
                }
                else
                {
                    // WRITABLE bank (USER): the firmware REJECTS the func-0x77 name enum
                    // (preset-only), so pull each slot with a paced func-0x72 fetch -
                    // works for every bank (128/128 on HW), no throttle. ParseIncoming
                    // caches the names; mark done only when the pull converges (complete).
                    var perObj = new Progress<int>(_ =>
                    {
                        int nd; lock (_dumpedBanks) nd = _dumpedBanks.Count;
                        progress?.Report((nd, total, CurrentNameCount()));
                    });
                    var (replied, converged) = await dump.CollectPerObjectNamesAsync(
                            nameObj, objBank, 128, perObj, ct).ConfigureAwait(false);
                    AppLog.Info($"[sync] {label} bank=0x{objBank:X2} (0x72 per-object) got " +
                                $"{replied.Count}/128 (converged={converged})");
                    done = converged && replied.Count > 0;
                }

                if (done)
                {
                    lock (_dumpedBanks) _dumpedBanks.Add((type, objBank));
                    ledgerDirty = true;
                    gotThisRun++;
                }
                // A bank that didn't complete is left un-dumped (retryable next session)
                // - never guess "absent" and mark it done; that once corrupted the ledger
                // by skipping real USER banks forever.

                int nowDone; lock (_dumpedBanks) nowDone = _dumpedBanks.Count;
                progress?.Report((nowDone, total, CurrentNameCount()));
            }

            // Report. GM/g banks that stay empty are usually just not present on this
            // unit (g7-gd), so if ONLY GM/g remain, treat it as effectively complete.
            List<(int Type, int ObjBank)> left;
            lock (_dumpedBanks) left = all.Where(b => !_dumpedBanks.Contains((b.Type, b.ObjBank))).ToList();
            bool onlyGmLeft = left.All(b => b.Type == 1 && b.ObjBank >= 0x10 && b.ObjBank <= 0x1A);
            if (left.Count == 0 || onlyGmLeft)
                AppLog.Info($"[sync] complete - {total - left.Count}/{total} banks dumped" +
                            (left.Count > 0 ? $" ({left.Count} GM-variation bank(s) not present - normal)" : ""));
            else
                AppLog.Info($"[sync] round done: +{gotThisRun} new; {left.Count}/{total} banks still incomplete " +
                            "(retryable next Sync - a partial/rejected bank is never marked done).");
        }
        finally
        {
            _dumpGate.End(gateEpoch);
            if (ledgerDirty) Storage.SaveDumpedBanks(_cacheKey, SnapshotDumped());
            PersistNames();
        }
        return CurrentNameCount();
    }

    public void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges)
    {
        _proactivePoll   = proactivePoll;
        _pollIntervalSec = pollIntervalSec;
        _pollOnChanges   = pollOnChanges;

        _midiMonitorEnabled = midiMonitorEnabled;

        // The transport owns the live inbound stream. For TCP this connects/
        // disconnects the port-9875 monitor (a bandwidth optimisation); for USB
        // it's a no-op (the device connection inherently carries the stream).
        _transport?.SetStreamEnabled(midiMonitorEnabled);

        // Wake up the polling loop if proactive was just enabled
        if (proactivePoll)
        {
            try { _perfPollDelayCts?.Cancel(); } catch { }
        }
    }

    public void RefreshNow()
    {
        _ = DeferredRefreshAsync();
    }

    // ── Bulk dumps (collected off the live stream; callers cache the result) ─────

    public bool CanDump => _dump != null && _transport?.CanStream == true;

    // Dump one Set List (obj 0x0D) by number. One request → one ~79 KB object
    // carrying the name + all 128 slots. Returns null if the monitor is disabled
    // or the Kronos doesn't answer (SysEx transmit off).
    public async Task<SetListData?> DumpSetListAsync(int number)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true)
        { AppLog.Warn("[sysex] set-list dump needs the live MIDI stream enabled"); return null; }

        // Pause perf polling so its func 0x33 (daemon SYSEX path) can't capture
        // one of this dump's 0x73 replies by mistake.
        int gateEpoch = _dumpGate.Begin();
        try { return await DumpOneSetListAsync(dump, number).ConfigureAwait(false); }
        finally
        {
            _dumpGate.End(gateEpoch);
            RefreshNow();   // resync the perf display after the dump window
        }
    }

    // Sweep every set list (0..127) for "Sync All". A single DumpGate scope spans
    // the whole pass (not 128 on/off cycles), and DumpOneSetListAsync touches the
    // gate not at all, so the two wrappers own it cleanly. Cancellable between set
    // lists; whatever completed before a cancel is returned. A no-response set list
    // is reported as neither Found nor ConfirmedEmpty, so the caller never deletes a
    // good cached entry over one transient miss.
    public async Task<SetListSyncResult> DumpAllSetListsAsync(
        IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct)
    {
        var found = new Dictionary<int, SetListData>();
        var empty = new List<int>();

        var dump = _dump;
        if (dump == null || _transport?.CanStream != true)
        {
            AppLog.Warn("[sysex] set-list sweep needs the live MIDI stream enabled");
            return new SetListSyncResult(found, empty, 0, ct.IsCancellationRequested);
        }

        const int total = SetListData.MaxCount;
        int attempted = 0;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            for (int n = 0; n < total; n++)
            {
                if (ct.IsCancellationRequested) break;
                attempted++;

                SetListData? data = null;
                try { data = await DumpOneSetListAsync(dump, n).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Warn($"[sysex] set-list {n} sweep dump failed: {ex.Message}"); }

                if (data != null)
                {
                    if (data.IsEmpty) empty.Add(n);       // responded blank → caller drops stale entry
                    else              found[n] = data;     // has content → caller caches
                }
                // data == null: no response - leave the caller's cache untouched.

                progress?.Report((n + 1, total, found.Count));
            }
        }
        finally
        {
            _dumpGate.End(gateEpoch);
            RefreshNow();   // resync the perf display once after the whole sweep
        }

        AppLog.Info($"[sysex] set-list sweep: {found.Count} with content, {empty.Count} empty, " +
                    $"{attempted}/{total} attempted (cancelled={ct.IsCancellationRequested})");
        return new SetListSyncResult(found, empty, attempted, ct.IsCancellationRequested);
    }

    // Core one-set-list collection, shared by the single dump and the sweep. Does NOT
    // touch the DumpGate - the caller owns that so the sweep can hold it across all 128.
    // A Set List is a single ~79 KB object; the Kronos can take several seconds to
    // serialize it before the first byte streams, so the "no activity at all" window
    // gets generous headroom (the 4 s default would give up before transmission even
    // starts). Once bytes flow, the per-chunk activity keepalive carries the transfer.
    async Task<SetListData?> DumpOneSetListAsync(SysExDumpCollector dump, int number)
    {
        var req  = SysExDumpCollector.ObjectDumpRequest(0x0D, 0, number);
        var msgs = await CollectRetryingSendAsync(dump, req, 0x0D, expectedCount: 1, noResponseMs: 10000)
            .ConfigureAwait(false);
        var msg  = msgs.Count > 0 ? msgs[0] : null;
        return msg != null ? SetListData.FromObjectDump(msg) : null;
    }

    // Retries a CollectAsync call ONCE, but ONLY when the request never reached the wire at all
    // (SendFailed - see SysExDumpCollector.CollectAsync's own comment), never on a genuine
    // empty/no-reply/rejected result. A transient ctrl-port send timeout under heavy Sync load
    // (CtrlQuery's own comment) is normally gone within milliseconds, so this turns what would
    // otherwise silently read as "the Kronos confirmed this slot is empty" into a real answer
    // the large majority of the time - closing the gap where LibraryPullPipeline could record a
    // populated hardware slot as empty just because its dump request's SEND happened to time out.
    async Task<List<byte[]>> CollectRetryingSendAsync(
        SysExDumpCollector dump, string requestHex, byte expectObj, int? expectedCount,
        int idleMs = 600, int noResponseMs = 4000, int stallMs = 4000, int overallMs = 60000)
    {
        var (msgs, sendFailed) = await dump.CollectAsync(requestHex, expectObj, expectedCount, idleMs, noResponseMs, stallMs, overallMs)
            .ConfigureAwait(false);
        if (!sendFailed) return msgs;

        AppLog.Debug($"[sysex-dump] retrying after send failure: {requestHex}");
        await Task.Delay(200).ConfigureAwait(false);
        var (retryMsgs, retrySendFailed) = await dump.CollectAsync(requestHex, expectObj, expectedCount, idleMs, noResponseMs, stallMs, overallMs)
            .ConfigureAwait(false);
        if (retrySendFailed)
            AppLog.Warn($"[sysex-dump] send failed twice in a row - giving up; this will read as an empty/no reply: {requestHex}");
        return retryMsgs;
    }

    // Coalescing debounce: each Program/Bank message restarts a short timer, so
    // a CC0+CC32+PC burst fires a single refresh ~PerfRefreshDebounceMs later.
    // The user-activity guard still skips refreshes during active app-driven
    // interaction (which would otherwise freeze the video stream mid-drag);
    // external changes on the Kronos have no app activity, so they follow fast.
    async Task DeferredRefreshAsync()
    {
        long epoch = Interlocked.Increment(ref _refreshEpoch);
        await Task.Delay(PerfRefreshDebounceMs).ConfigureAwait(false);
        if (Interlocked.Read(ref _refreshEpoch) != epoch) return;   // a later call superseded this one

        // Wake the perf loop. It resolves identity + a cached name (cheap, no
        // freeze); only an uncached name triggers a dump, and that dump is what
        // defers to a quiet moment - see PerfMetadataLoop.
        try { _perfPollDelayCts?.Cancel(); } catch { }
    }

    public void NotifyUserActivity()
    {
        _lastUserActivity = DateTime.Now;
    }

    async Task ProbeAsync(CancellationToken ct)
    {
        var transport = _transport;
        if (transport == null) return;

        try
        {
            bool capable = await transport.ProbeAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            if (capable)
            {
                var md = transport.LastModeData;
                if (md.HasValue)
                {
                    int stateMode = md.Value.ToStateMode();
                    if (stateMode > 0)
                        _stateMode = stateMode;   // seed for program-change stream decode
                }
            }

            IsAvailable = capable;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[sysex-service] probe exception: {ex.Message}");
            IsAvailable = false;
        }
    }

    async Task PerfMetadataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isAvailable)
        {
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        while (!ct.IsCancellationRequested)
        {
            var transport = _transport;
            if (transport != null && _isAvailable && !_dumpGate.Active)
            {
                try
                {
                    var resp = await transport.QueryAsync(PerfIdRequest, 0x33, 1200)
                        .ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    var info = resp != null ? KronosSysEx.ParsePerformanceId(resp) : null;

                    if (info != null)
                    {
                        // Seed the stream-decode bank context from the identity, and
                        // resolve the name from the shared cache (no func 0x75 - names
                        // come only from a Sync Names / captured dump). Format through
                        // BankId so mode-change and program-change displays match
                        // exactly (incl. GM/g 1-based numbering).
                        var bid = KronosBanks.FromFunc33(info.Value.Type, info.Value.Bank, info.Value.Number);
                        string display;
                        if (bid is { } b)
                        {
                            _lastBankId = b;
                            string? name;
                            lock (_streamNames) _streamNames.TryGetValue((b.Type, b.ObjBank, b.Number), out name);
                            display = string.IsNullOrWhiteSpace(name) ? b.Display : $"{b.Display} {name}";
                        }
                        else
                        {
                            display = info.Value.ToDisplayString();   // song / unknown
                        }

                        if (display != PerformanceDisplay)
                            AppLog.Info($"[sysex] current performance: {display}");
                        PerformanceDisplay = display;
                    }
                    else
                    {
                        PerformanceDisplay = "";
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"[sysex-service] perf metadata poll error: {ex.Message}");
                }
            }

            // Proactive: repeat on fixed interval; otherwise park until DeferredRefreshAsync wakes us.
            int delayMs = _proactivePoll ? _pollIntervalSec * 1000 : Timeout.Infinite;
            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _perfPollDelayCts = delayCts;
                await Task.Delay(delayMs, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
            }
            finally
            {
                _perfPollDelayCts = null;
            }
        }
    }

    public async Task<bool> SendMidiAsync(string hexBytes)
    {
        var decoded = MidiStreamMonitor.DecodeHex(hexBytes);
        SysExTraffic?.Invoke(new SysExTrafficEntry(DateTime.Now, true, decoded, IsMidi: true));

        var transport = _transport;
        var bytes     = MidiHex.ToBytes(hexBytes);
        bool ok = transport != null && bytes != null &&
                  await transport.SendAsync(bytes).ConfigureAwait(false);
        if (!ok)
            SysExTraffic?.Invoke(new SysExTrafficEntry(DateTime.Now, false, "ERR", IsMidi: true));
        return ok;
    }

    void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _dispatcher.InvokeAsync(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }

    // ── Librarian primitives (ISysExService + IMoveExecutor) ────────────────────
    // Bulk read/write over the live stream; each pauses the func-33 perf loop
    // (via the DumpGate) so it can't steal one of our 0x73/0x24 replies, mirroring the
    // existing DumpSetListAsync / WriteSetListSlotAsync pattern.

    public async Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return null;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            var req  = SysExDumpCollector.ObjectDumpRequest(obj, bank, index);
            // Set Lists (~79 KB) and Global (~24 KB) are the big, slow-to-serialize objects -
            // give their "no activity" window headroom rather than timing out on a reply that
            // was on its way.
            var msgs = await CollectRetryingSendAsync(dump, req, (byte)obj, expectedCount: 1,
                                    noResponseMs: obj is 0x0D or LibObj.Global ? 10000 : 6000).ConfigureAwait(false);
            var msg = msgs.Count > 0 ? msgs[0] : null;
            return msg != null ? KronosSysEx.ParseObjectDump(msg) : null;
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    public async Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count)
    {
        var result = new Dictionary<int, ObjectDump>();
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return result;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            var req = SysExDumpCollector.DumpBankRequest(obj, bank);
            // Generously sized for the largest case this can be asked to cover - a
            // Set List bulk-bank request is up to 128 x ~79 KB (~10 MB total); a full
            // Combi bank is ~1.1 MB. Far larger than the Name-enum's tuning (SysExService's
            // SyncNamesAsync path), which only ever moves ~128 short names. A rejected or
            // genuinely-empty bank still exits promptly via CollectAsync's idle/reject
            // fast-paths, so the generous cap only matters when data is actually flowing.
            var msgs = await CollectRetryingSendAsync(dump, req, (byte)obj, expectedCount: count,
                idleMs: 2000, noResponseMs: 3000, stallMs: 15000, overallMs: 300000).ConfigureAwait(false);
            foreach (var m in msgs)
            {
                var parsed = KronosSysEx.ParseObjectDump(m);
                if (parsed != null) result[parsed.Index] = parsed;
            }
        }
        finally { _dumpGate.End(gateEpoch); }
        return result;
    }

    public async Task<int> WriteObjectAsync(WriteOp op)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return -1;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            // Stamp the CURRENT, correct object-version byte at the moment of the actual
            // hardware write - never trust whatever's stored on op.Version. This is the one
            // choke point every push goes through, so it retroactively heals any object
            // already sitting in Local Library or the Merge Window with a stale/placeholder
            // version (e.g. every PCG-imported Program used to carry 0 instead of 5) without
            // needing to re-place or re-pull anything. See LibObj.CurrentObjectVersion.
            byte version = LibObj.CurrentObjectVersion(op.Obj) ?? op.Version;
            var msg  = KronosSysEx.BuildObjectDumpMessage(op.Obj, op.Bank, op.Index, version, op.Body);
            AppLog.Debug($"[sysex] WriteObjectAsync obj=0x{op.Obj:X2} bank=0x{op.Bank:X2} idx={op.Index} version={version} bodyLen={op.Body.Length}");
            var code = await dump.SendLargeObjectDumpAndAwaitReplyAsync(msg).ConfigureAwait(false);
            return code ?? -1;
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    public async Task<int> StoreBankAsync(int obj, int bank)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return -1;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            var code = await dump.SendStoreBankRequestAsync(obj, bank, timeoutMs: 20000).ConfigureAwait(false);
            return code ?? -1;
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    public async Task<int> ChangeProgramBankTypeAsync(int bank, bool isExi)
    {
        var dump = _dump;
        if (dump == null || _transport?.CanStream != true) return -1;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            AppLog.Debug($"[sysex] ChangeProgramBankTypeAsync bank=0x{bank:X2} -> {(isExi ? "EXi" : "HD-1")} (reformats+erases the bank)");
            var code = await dump.SendChangeProgramBankTypeAsync(bank, isExi).ConfigureAwait(false);
            return code ?? -1;
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    public async Task<byte[]?> BankDigestAsync(int obj, int bank)
    {
        var transport = _transport;
        if (transport?.CanStream != true) return null;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            return await transport.AwaitReplyAsync<byte[]>(
                () => transport.SendAsync(KronosSysEx.BuildBankDigestRequest(obj, bank)),
                m => KronosSysEx.ParseBankDigest(m) is { } bd && bd.Obj == obj && bd.Bank == bank ? bd.Sha1 : null,
                5000).ConfigureAwait(false);
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    // Snapshot of one bank's known slot names, taken under the same lock every other
    // _streamNames reader uses. A copy, not a view: callers hold it across a tree rebuild.
    public IReadOnlyDictionary<int, string> CachedBankNames(int type, int objBank)
    {
        var result = new Dictionary<int, string>();
        lock (_streamNames)
            foreach (var (key, name) in _streamNames)
                if (key.Type == type && key.ObjBank == objBank) result[key.Number] = name;
        return result;
    }

    public async Task<ProgramBankTypes?> RequestProgramBankTypesAsync()
    {
        var transport = _transport;
        if (transport?.CanStream != true) return null;
        int gateEpoch = _dumpGate.Begin();
        try
        {
            return await transport.AwaitReplyAsync<ProgramBankTypes>(
                () => transport.SendAsync(KronosSysEx.BuildProgramBankTypesRequest()),
                KronosSysEx.ParseProgramBankTypes,
                5000).ConfigureAwait(false);
        }
        finally { _dumpGate.End(gateEpoch); }
    }

    public async Task BackupObjectsAsync(IReadOnlyList<WriteOp> ops, string path)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var op in ops)
        {
            // Same version stamping as WriteObjectAsync (see its comment) - a backup
            // should be restorable byte-for-byte, which means carrying the CURRENT
            // object version, not a stale placeholder stored on the op.
            byte version = LibObj.CurrentObjectVersion(op.Obj) ?? op.Version;
            var m = KronosSysEx.BuildObjectDumpMessage(op.Obj, op.Bank, op.Index, version, op.Body);
            await fs.WriteAsync(m).ConfigureAwait(false);
        }
    }

    public async Task SendRawAsync(byte[] data)
    {
        var transport = _transport;
        if (transport != null) await transport.SendAsync(data).ConfigureAwait(false);
    }

    public ObjLoc? CurrentPerformanceLoc()
    {
        if (_lastBankId is not { } b) return null;
        int objType = b.Type == 1 ? LibObj.Program : LibObj.Combi;
        return new ObjLoc(objType, b.ObjBank, b.Number);
    }
}
