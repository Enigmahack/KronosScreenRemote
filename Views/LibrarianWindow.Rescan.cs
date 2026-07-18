using System.Windows;

namespace KronosScreenRemote;

// Right-click "Rescan" in the object browser (TV_Objects) — a targeted, immediate
// re-fetch of exactly the node's scope (one slot, one bank, or a whole type), bypassing
// the digest-diff lazy-scan in LibraryRepository.ScanAsync entirely. Useful when the
// user knows exactly what changed (e.g. just edited one Combi on the panel) and doesn't
// want to wait for/trigger a full Scan Library.
partial class LibrarianWindow
{
    async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ObjectBrowserNode { Rescan: { } scope }) return;
        if (_busy) { Log("A scan or sync is already running."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }

        _busy = true;
        RefreshEnable();
        Log($"\nRescanning {scope.Describe()} …");
        try
        {
            await RescanScopeAsync(scope);
            Log("  Rescan complete.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] rescan failed: {ex.Message}");
            Log($"  Rescan error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshObjectTree();
            UpdateUsage();
            RefreshEnable();
        }
    }

    async Task RescanScopeAsync(RescanScope scope)
    {
        var ri = _refIndex ??= new RefIndex();
        var nameUpdates = new Dictionary<(int Type, int Bank, int Number), string>();
        var setListUpdates = new Dictionary<int, SetListData>();

        // "Start fresh" only within this Rescan's own scope — an unrelated pending/committed
        // marker elsewhere (different bank or type) has nothing to do with what's being
        // re-fetched here, so a narrow Rescan shouldn't silently wipe it.
        foreach (var loc in _pasteState.Keys
            .Where(l => l.ObjType == scope.ObjType && (scope.Bank is not int b || l.Bank == b) && (scope.Number is not int n || l.Number == n))
            .ToList())
            ClearPasteState(loc);

        IEnumerable<int> BanksFor(int objType) => scope.Bank is int b ? new[] { b }
            : objType == LibObj.Program ? ProgramMoveBanks() : CombiMoveBanks();
        IEnumerable<int> NumbersFor(int max) => scope.Number is int n ? new[] { n } : Enumerable.Range(0, max);

        if (scope.ObjType is LibObj.Program or LibObj.Combi)
        {
            foreach (var bank in BanksFor(scope.ObjType))
                foreach (var number in NumbersFor(128))
                {
                    var d = await _sysEx.DumpObjectAsync(scope.ObjType, bank, number);
                    if (d == null) continue;
                    if (scope.ObjType == LibObj.Combi) ri.AddCombi(d);
                    nameUpdates[(scope.ObjType == LibObj.Program ? 1 : 0, bank, number)] = Librarian.ReadName(d.Body);
                }

            // Refresh this scan's freshness baseline too, so the next lazy Scan Library
            // doesn't immediately treat what was JUST rescanned as still "changed".
            if (scope.ObjType == LibObj.Combi)
                foreach (var bank in BanksFor(LibObj.Combi))
                {
                    var digest = await _sysEx.BankDigestAsync(LibObj.Combi, bank);
                    if (digest != null) ri.RecordDigest(LibObj.Combi, bank, digest);
                }
        }
        else // Set List — obj 0x0D isn't bank-partitioned, digest covers all 128 at once
        {
            foreach (var number in NumbersFor(SetListData.MaxCount))
            {
                var d = await _sysEx.DumpObjectAsync(LibObj.SetList, 0, number);
                if (d != null) ri.AddSetlist(d);
                var data = await _sysEx.DumpSetListAsync(number);
                if (data != null) setListUpdates[number] = data;
            }
            var slDigest = await _sysEx.BankDigestAsync(LibObj.SetList, 0);
            if (slDigest != null) ri.RecordDigest(LibObj.SetList, 0, slDigest);
        }

        await Task.Run(() =>
        {
            if (nameUpdates.Count > 0)
            {
                var entries = Storage.LoadNames(_host);
                var byKey = entries.ToDictionary(c => (c.Type, c.Bank, c.Number));
                foreach (var (key, name) in nameUpdates)
                    byKey[key] = new CachedName(key.Type, key.Bank, key.Number, name);
                Storage.SaveNames(_host, byKey.Values.ToList());
            }
            if (setListUpdates.Count > 0)
            {
                var cache = Storage.LoadSetLists(_host);
                foreach (var (number, data) in setListUpdates) cache[number] = data;
                Storage.SaveSetLists(_host, cache);
            }
            LibraryRepository.SaveRefIndex(_host, ri);
        });

        foreach (var (key, name) in nameUpdates) _names[key] = name;
        foreach (var (number, data) in setListUpdates) _setListNames[number] = data.Name;
        _refIndex = ri;
    }
}
