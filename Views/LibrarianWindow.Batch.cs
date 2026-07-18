using System.Windows;

namespace KronosScreenRemote;

// Batch-move UI orchestration: right-click paste variants (Paste Single/Multi/All/Bank, Copy
// [Bank] to Clipboard) stage directly into _stagedPastes and mark their destination(s) red
// immediately — see PasteSingleAsync/PasteMultiAsync/PasteAllAsync/PasteBankAsync/CopyBankAsync
// below. VerifyAsync/CommitAllAsync (LibrarianWindow.xaml.cs) drive PreviewBatchAsync/
// CommitBatchAsync here exactly like the single-pair swap flow drives PreviewAsync/CommitAsync —
// both share PlanBatchMove/ApplyMoveAsync through IExecutablePlan. Multi-select itself lives in
// LibrarianWindow.ObjectBrowser.cs; the clipboard panel + paste flow in
// LibrarianWindow.Clipboard.cs.
partial class LibrarianWindow
{
    void RefreshBatchSelectionUi()
    {
        if (_batchSelection.Count == 0)
        {
            TXT_BatchSelection.Text = "(Ctrl/Shift-click Program, Combi, or Set List slots above)";
            return;
        }

        int objType = _batchSelection.First().ObjType;
        string typeName = objType switch { LibObj.Program => "Program", LibObj.Combi => "Combi", _ => "Set List" };
        TXT_BatchSelection.Text = $"{_batchSelection.Count} {typeName}{(_batchSelection.Count == 1 ? "" : "s")} selected";
    }

    bool? BankTypeOf(int bank) =>
        _programBankTypes is { } types && KronosBanks.ProgramBankTypeBitIndex(bank) is int bit && bit < types.Length
            ? types[bit]
            : null;

    // ── Batch preview ────────────────────────────────────────────────────────
    // Mirrors PreviewAsync's dump dance (freshness gate -> fresh referrer bodies -> fresh
    // destination bodies -> plan -> arm), generalized across every staged clipboard paste
    // instead of one swap pair. Purely a confirmation step — staging already happened at Paste
    // time (see the Paste*Async methods below); this just re-dumps fresh and either arms the
    // plan (non-refusable) or explains why it can't proceed. Nothing here changes what's staged.
    async Task PreviewBatchAsync()
    {
        if (_busy) return;
        if (_refIndex is not { } ri) { Log("Scan the library first (needed to find references)."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }
        if (_stagedPastes.Count == 0) { Log("Nothing staged — Copy something and Paste it below first."); return; }

        int objType = _stagedPastes[0].Entry.ObjType;
        Func<int, bool?>? bankTypeOf = objType == LibObj.Program ? BankTypeOf : null;

        // Gated on _programBankTypesLive (hardware-confirmed THIS session), not on
        // _programBankTypes == null — that field can now be non-null from a stale disk
        // cache (see LibrarianWindow.xaml.cs) via a Rescan alone, without ever fetching
        // fresh types. "Always auto-clipboard on type mismatch" can't be honored on
        // types that might be stale, so this REFUSE requires a live fetch, not just a value.
        if (objType == LibObj.Program && !_programBankTypesLive)
        {
            Log("Batch Move: program bank HD-1/EXi types haven't been fetched yet — run Scan Library first.");
            return;
        }

        _busy = true;
        _plan = null;   // ArmedPlan must never resolve to a stale plan from the other flow
        _batchPlan = null;
        _batchStagedInPlan = new();
        BRD_Warning.Visibility = Visibility.Collapsed;
        RefreshEnable();
        string typeNoun = objType switch { LibObj.Program => "Program", LibObj.Combi => "Combi", _ => "Set List" };
        Log($"\nVerifying {typeNoun} clipboard paste(s) …");
        try
        {
            var stagedPastes = _stagedPastes.Where(sp => sp.Entry.ObjType == objType).ToList();
            _batchStagedInPlan = stagedPastes;
            int otherTypePending = _stagedPastes.Count - stagedPastes.Count;
            if (otherTypePending > 0)
                Log($"  ({otherTypePending} staged paste(s) of a different type remain queued — run Verify again to include them.)");

            // Freshness gate — same property PreviewAsync enforces for the single-pair flow.
            var stale = await ri.StaleBanksAsync((o, b) => _sysEx.BankDigestAsync(o, b));
            if (stale.Count > 0)
            {
                Log("  STALE: " + string.Join(", ", stale.Select(x => Librarian.StoreLabel(x.Obj, x.Bank))) +
                    " changed since the last scan — re-scan before moving (a new referrer could be left dangling).");
                return;
            }

            var ids = new HashSet<(int Obj, int Bank, int Index)>();
            // Any provenance except DisplacedDestination leaves its Origin untouched, so its
            // referrers still point there and need repointing on paste (see NeedsOriginRepoint).
            foreach (var (entry, _) in stagedPastes)
                if (entry.Provenance.NeedsOriginRepoint())
                    ids.UnionWith(ri.ReferrerObjectIds(entry.Origin));
            var cat = new LibraryCatalog();
            foreach (var (obj, bank, index) in ids)
            {
                var dump = await _sysEx.DumpObjectAsync(obj, bank, index);
                if (dump == null) { Log($"  Failed to dump referrer obj {obj:X2} bank {bank:X2} idx {index}"); return; }
                if (obj == LibObj.Combi) cat.AddCombi(dump); else cat.AddSetlist(dump);
            }

            var placements = new List<BatchPlacement>();
            var destOccupants = new Dictionary<ObjLoc, ObjectDump>();
            foreach (var (entry, to) in stagedPastes)
            {
                var body = new ObjectDump(entry.ObjType, entry.Origin.Bank, entry.Origin.Number, entry.Version, entry.Body);
                var from = entry.Provenance.NeedsOriginRepoint() ? (ObjLoc?)entry.Origin : null;
                if (!destOccupants.ContainsKey(to))
                {
                    var occ = await _sysEx.DumpObjectAsync(objType, to.Bank, to.Number);
                    if (occ != null) destOccupants[to] = occ;
                }
                placements.Add(new BatchPlacement(from, to, body, $"clipboard: {entry.Origin.Label()}"));
            }

            bool divert = CHK_BatchDivertToClipboard.IsChecked == true;
            var plan = BatchLibrarian.PlanBatchMove(cat, objType, placements, destOccupants, divert, bankTypeOf);
            await Librarian.ArmPlanAsync(plan, _sysEx);

            _batchPlan = plan;
            foreach (var line in plan.Preview) Log("  " + line);
            foreach (var w in plan.Warnings) Log("  ! " + w);
            if (plan.DigestBaseline.Count == 0)
                Log("  ! WARNING: no bank-digest baseline captured — staleness gate will be skipped.");
            Log(plan.IsRefusable ? "  This batch is REFUSED (see above)."
                                 : "  Verify complete. Review the warnings above, then Commit.");
            ShowPlanWarningBanner(plan.Warnings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] batch preview failed: {ex.Message}");
            Log($"  Batch preview error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }

    // ── Batch commit ─────────────────────────────────────────────────────────
    // Same shared Librarian.ApplyMoveAsync as the single-pair flow — no batch-specific
    // commit logic exists, which is the whole point of IExecutablePlan.
    async Task CommitBatchAsync()
    {
        if (_busy || _batchPlan is not { } plan) return;
        if (plan.IsRefusable) { Log("Refused batch — cannot commit."); return; }
        if (!_sysEx.CanDump) { Log("Not connected."); return; }

        _busy = true;
        RefreshEnable();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Log("\nCommitting batch…");
        try
        {
            var (ok, _, aborted) = await Librarian.ApplyMoveAsync(
                plan, _sysEx, Storage.BackupDir(), stamp,
                progress: m => Dispatcher.Invoke(() => Log("  · " + m)),
                doLive: false);

            Log(ok ? "  DONE — batch committed." : $"  ABORTED — {aborted}");
            if (ok)
            {
                foreach (var entry in plan.ClipboardAdds) _batchClipboard.Entries.Add(entry);

                var pastedNow = DateTime.Now;
                foreach (var (entry, to) in _batchStagedInPlan)
                {
                    entry.PastedTo = to;
                    entry.PastedAt = pastedNow;
                    _stagedPastes.RemoveAll(sp => ReferenceEquals(sp.Entry, entry));
                    SetPasteState(to, PasteState.Committed);
                }

                if (plan.ClipboardAdds.Count > 0 || _batchStagedInPlan.Count > 0)
                {
                    await Task.Run(() => BatchLibrarian.SaveClipboard(_host, _batchClipboard));
                    if (plan.ClipboardAdds.Count > 0) Log($"  Clipboard updated: {plan.ClipboardAdds.Count} new entr{(plan.ClipboardAdds.Count == 1 ? "y" : "ies")} (displaced).");
                    if (_batchStagedInPlan.Count > 0) Log($"  {_batchStagedInPlan.Count} clipboard paste(s) finalized.");
                }

                Log("  (Re-scan recommended: references have changed.)");
                _batchPlan = null;
                _batchStagedInPlan = new();
                BRD_Warning.Visibility = Visibility.Collapsed;
                RefreshObjectTree();   // reapplies _pasteState — see its doc comment
                RefreshBatchSelectionUi();
                RefreshClipboardUi();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] batch commit failed: {ex.Message}");
            Log($"  Batch commit crashed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshEnable();
        }
    }
}
