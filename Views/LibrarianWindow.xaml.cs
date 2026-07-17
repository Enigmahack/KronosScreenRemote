using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace KronosScreenRemote;

// Move Programs/Combis between slots while keeping every Combi timbre and Set List
// slot that references them coherent, live over SysEx. Orchestration + safety UI;
// the risky logic is in LibrarianModel / KronosSysEx (verified off-hardware via
// Librarian.SelfTest). Nothing is written until Commit, and Commit is gated behind
// an explicit "Store-Bank spike verified" checkbox.
partial class LibrarianWindow : Window
{
    readonly ISysExService _sysEx;
    readonly string _host;
    readonly Dictionary<(int Type, int Bank, int Number), string> _names;

    RefIndex? _refIndex;
    MovePlan? _plan;
    bool _busy;
    CancellationTokenSource? _scanCts;
    int[] _curBanks = Array.Empty<int>();

    public LibrarianWindow(ISysExService sysEx, string host)
    {
        _sysEx = sysEx;
        _host  = host;
        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        _names = new Dictionary<(int, int, int), string>();
        foreach (var n in Storage.LoadNames(host))
            _names[(n.Type, n.Bank, n.Number)] = n.Name;

        RB_Prog.Checked  += (_, _) => OnTypeChanged();
        RB_Combi.Checked += (_, _) => OnTypeChanged();
        CMB_SrcBank.SelectionChanged += (_, _) => UpdateUsage();
        CMB_DstBank.SelectionChanged += (_, _) => UpdateUsage();
        TXT_SrcNum.TextChanged += (_, _) => UpdateUsage();
        TXT_DstNum.TextChanged += (_, _) => UpdateUsage();
        CHK_Spike.Checked   += (_, _) => RefreshEnable();
        CHK_Spike.Unchecked += (_, _) => RefreshEnable();

        BTN_Scan.Click       += async (_, _) => await ScanAsync();
        BTN_ScanCancel.Click += (_, _) => _scanCts?.Cancel();
        BTN_Preview.Click    += async (_, _) => await PreviewAsync();
        BTN_Commit.Click     += async (_, _) => await CommitAsync();

        TXT_BackupHint.Text = "Backups: " + BackupDir();
        OnTypeChanged();
        RefreshEnable();
    }

    // ── Banks / type ─────────────────────────────────────────────────────────

    static int[] ProgramMoveBanks() =>   // exclude read-only GM/g (0x10-0x1A)
        Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 14)).ToArray();
    static int[] CombiMoveBanks() =>
        Enumerable.Range(0x00, 7).Concat(Enumerable.Range(0x40, 7)).ToArray();

    int CurType() => RB_Prog.IsChecked == true ? LibObj.Program : LibObj.Combi;

    void OnTypeChanged()
    {
        _curBanks = CurType() == LibObj.Program ? ProgramMoveBanks() : CombiMoveBanks();
        Func<int, string> label = CurType() == LibObj.Program ? KronosBanks.ProgramLabel : KronosBanks.CombiLabel;
        foreach (var combo in new[] { CMB_SrcBank, CMB_DstBank })
        {
            combo.Items.Clear();
            foreach (var ob in _curBanks) combo.Items.Add(label(ob));
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }
        UpdateUsage();
    }

    static int ParseNum(TextBox t) =>
        int.TryParse(t.Text, out var n) ? Math.Clamp(n, 0, 127) : 0;

    ObjLoc? SelLoc(ComboBox bank, TextBox num)
    {
        int i = bank.SelectedIndex;
        if (i < 0 || i >= _curBanks.Length) return null;
        return new ObjLoc(CurType(), _curBanks[i], ParseNum(num));
    }

    string NameOf(ObjLoc loc)
    {
        int t = loc.ObjType == LibObj.Program ? 1 : 0;
        return _names.TryGetValue((t, loc.Bank, loc.Number), out var nm) ? nm : "";
    }

    void UpdateUsage()
    {
        void One(ComboBox bank, TextBox num, TextBlock lbl)
        {
            var loc = SelLoc(bank, num);
            if (loc is not { } l) { lbl.Text = ""; return; }
            string name = NameOf(l);
            string namePart = string.IsNullOrEmpty(name) ? "(name unknown — Sync Names)" : $"'{name}'";
            lbl.Text = _refIndex != null
                ? $"{namePart}   used by {_refIndex.UsageCount(l)} ref(s)"
                : $"{namePart}   (scan for usage)";
        }
        One(CMB_SrcBank, TXT_SrcNum, TXT_SrcUsage);
        One(CMB_DstBank, TXT_DstNum, TXT_DstUsage);
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    async Task ScanAsync()
    {
        if (_busy || !_sysEx.CanDump) { Log("Cannot scan — MIDI monitoring off or not connected."); return; }
        _busy = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        BTN_Scan.IsEnabled = false;
        BTN_ScanCancel.IsEnabled = true;
        TXT_ScanStatus.Text = "Scanning…";
        RefreshEnable();

        bool full = CHK_Full.IsChecked == true;
        var ri = new RefIndex();

        var combiIds = full
            ? CombiMoveBanks().SelectMany(b => Enumerable.Range(0, 128).Select(n => (Bank: b, Number: n))).ToList()
            : Storage.LoadNames(_host).Where(n => n.Type == 0).Select(n => (Bank: n.Bank, Number: n.Number)).ToList();
        var cachedSl = Storage.LoadSetLists(_host);
        var setlistIds = full || cachedSl.Count == 0
            ? Enumerable.Range(0, SetListData.MaxCount).ToList()
            : cachedSl.Keys.OrderBy(k => k).ToList();

        int total = combiIds.Count + setlistIds.Count, done = 0;
        try
        {
            foreach (var (bank, number) in combiIds)
            {
                if (ct.IsCancellationRequested) break;
                var d = await _sysEx.DumpObjectAsync(LibObj.Combi, bank, number);
                if (d != null) ri.AddCombi(d);
                done++;
                TXT_ScanStatus.Text = $"Scanning {done}/{total} — combi {KronosBanks.CombiLabel(bank)}:{number:D3}";
            }
            foreach (var number in setlistIds)
            {
                if (ct.IsCancellationRequested) break;
                var d = await _sysEx.DumpObjectAsync(LibObj.SetList, 0, number);
                if (d != null) ri.AddSetlist(d);
                done++;
                TXT_ScanStatus.Text = $"Scanning {done}/{total} — set list {number:D3}";
            }
            // Capture scan-time digests of every swept bank for the freshness gate.
            if (!ct.IsCancellationRequested)
            {
                foreach (var bank in combiIds.Select(c => c.Bank).Distinct().OrderBy(b => b))
                    ri.RecordDigest(LibObj.Combi, bank, await _sysEx.BankDigestAsync(LibObj.Combi, bank));
                ri.RecordDigest(LibObj.SetList, 0, await _sysEx.BankDigestAsync(LibObj.SetList, 0));
            }

            _refIndex = ri;
            TXT_ScanStatus.Text = ct.IsCancellationRequested
                ? "Scan cancelled"
                : $"Indexed {ri.CombiRefs.Count} combis, {ri.SetlistRefs.Count} set lists";
            Log(TXT_ScanStatus.Text);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] scan failed: {ex.Message}");
            TXT_ScanStatus.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            _busy = false;
            BTN_Scan.IsEnabled = true;
            BTN_ScanCancel.IsEnabled = false;
            UpdateUsage();
            RefreshEnable();
        }
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    async Task PreviewAsync()
    {
        if (_busy) return;
        if (_refIndex is not { } ri) { Log("Scan the library first (needed to find references)."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }
        var src = SelLoc(CMB_SrcBank, TXT_SrcNum);
        var dst = SelLoc(CMB_DstBank, TXT_DstNum);
        if (src is not { } s || dst is not { } d) return;

        _busy = true;
        _plan = null;
        BTN_Preview.IsEnabled = false;
        RefreshEnable();
        Log($"\nPreviewing  {s.Label()}  <->  {d.Label()} …");
        try
        {
            // Freshness gate: if a swept bank changed since the scan, refuse (a new
            // referrer could have appeared and been missed).
            var stale = await ri.StaleBanksAsync((o, b) => _sysEx.BankDigestAsync(o, b));
            if (stale.Count > 0)
            {
                Log("  STALE: " + string.Join(", ", stale.Select(x => Librarian.StoreLabel(x.Obj, x.Bank))) +
                    " changed since the last scan — re-scan before moving (a new referrer could be left dangling).");
                return;
            }

            var ids = ri.ReferrerObjectIds(s);
            ids.UnionWith(ri.ReferrerObjectIds(d));
            var cat = new LibraryCatalog();
            foreach (var (obj, bank, index) in ids)
            {
                var dump = await _sysEx.DumpObjectAsync(obj, bank, index);
                if (dump == null) { Log($"  Failed to dump referrer obj {obj:X2} bank {bank:X2} idx {index}"); return; }
                if (obj == LibObj.Combi) cat.AddCombi(dump); else cat.AddSetlist(dump);
            }
            var srcDump = await _sysEx.DumpObjectAsync(s.ObjType, s.Bank, s.Number);
            var dstDump = await _sysEx.DumpObjectAsync(d.ObjType, d.Bank, d.Number);
            if (srcDump == null || dstDump == null) { Log("  Failed to dump source/destination object."); return; }

            var active = _sysEx.CurrentPerformanceLoc();
            var plan = Librarian.PlanMove(cat, s, srcDump, d, dstDump, active);
            await Librarian.ArmPlanAsync(plan, _sysEx);   // capture digest baseline now

            _plan = plan;
            foreach (var line in plan.Preview) Log("  " + line);
            foreach (var w in plan.Warnings) Log("  ! " + w);
            if (plan.DigestBaseline.Count == 0)
                Log("  ! WARNING: no bank-digest baseline captured — staleness gate will be skipped.");
            Log(plan.IsRefusable ? "  This move is REFUSED (see above)."
                                 : "  Preview complete. Review, tick the spike box, then Commit.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] preview failed: {ex.Message}");
            Log($"  Preview error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            BTN_Preview.IsEnabled = true;
            RefreshEnable();
        }
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    async Task CommitAsync()
    {
        if (_busy || _plan is not { } plan) return;
        if (CHK_Spike.IsChecked != true) { Log("Tick 'Store-Bank spike verified' first."); return; }
        if (plan.IsRefusable) { Log("Refused plan — cannot commit."); return; }
        if (!_sysEx.CanDump) { Log("Not connected."); return; }

        _busy = true;
        BTN_Commit.IsEnabled = false;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Log("\nCommitting…");
        try
        {
            // ApplyMoveAsync awaits with ConfigureAwait(false) internally, so its
            // progress callback fires off the UI thread — marshal each line back.
            var (ok, _, aborted) = await Librarian.ApplyMoveAsync(
                plan, _sysEx, BackupDir(), stamp,
                progress: m => Dispatcher.Invoke(() => Log("  · " + m)),
                doLive: CHK_Live.IsChecked == true);

            Log(ok ? "  DONE — move committed." : $"  ABORTED — {aborted}");
            Log("  (Re-scan recommended: references have changed.)");
            _plan = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] commit failed: {ex.Message}");
            Log($"  Commit crashed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    void RefreshEnable()
    {
        bool connected = _sysEx.CanDump;
        BTN_Scan.IsEnabled    = connected && !_busy;
        BTN_Preview.IsEnabled = connected && !_busy && _refIndex != null;
        BTN_Commit.IsEnabled  = connected && !_busy && _plan is { IsRefusable: false } && CHK_Spike.IsChecked == true;
    }

    void Log(string text)
    {
        TXT_Log.AppendText(text + "\n");
        TXT_Log.ScrollToEnd();
    }

    static string BackupDir()
    {
        var d = Path.Combine(Storage.DataDir, "librarian_backups");
        Directory.CreateDirectory(d);
        return d;
    }
}
