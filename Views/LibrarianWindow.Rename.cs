namespace KronosScreenRemote;

// Double-click an object in the browser (TV_Objects) to rename it. Program/Combi: a
// live name-only rewrite (dump -> patch name field -> write -> Store), same backup
// discipline as a move (Librarian.ApplyMoveAsync) — this is still a real write to the
// instrument, not a local-only edit. Set List: opens the Set List editor instead,
// since a Set List's own "name" isn't a simple field patch the way Program/Combi is.
partial class LibrarianWindow
{
    async Task OnObjectDoubleClickAsync()
    {
        if (TV_Objects.SelectedItem is not ObjectBrowserNode { Loc: { } loc }) return;

        if (loc.ObjType == LibObj.SetList) { OpenSetListEditor(loc.Number); return; }
        await RenameObjectAsync(loc);
    }

    void OpenSetListEditor(int number)
    {
        var win = new SetListWindow(_sysEx, _host, initialNumber: number) { Owner = this };
        win.Show();
    }

    async Task RenameObjectAsync(ObjLoc loc)
    {
        if (_busy) { Log("A scan or sync is already running."); return; }
        if (!_sysEx.CanDump) { Log("Not connected / MIDI monitoring off."); return; }

        var dlg = new PromptDialog($"Rename {loc.Label()}", NameOf(loc)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string newName = dlg.Result ?? "";
        if (newName == NameOf(loc)) return;

        _busy = true;
        RefreshEnable();
        Log($"\nRenaming {loc.Label()} -> \"{newName}\" …");
        try
        {
            var dump = await _sysEx.DumpObjectAsync(loc.ObjType, loc.Bank, loc.Number);
            if (dump == null) { Log("  Failed to dump current object — nothing changed."); return; }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var safeLabel = loc.Label().Replace(":", "").Replace(" ", "");
            var backupPath = System.IO.Path.Combine(Storage.BackupDir(), $"{stamp}_rename_{safeLabel}.syx");
            await _sysEx.BackupObjectsAsync(
                new[] { new WriteOp(loc.ObjType, loc.Bank, loc.Number, dump.Version, dump.Body, "pre-rename") },
                backupPath);
            Log($"  backed up original -> {backupPath}");

            var renamedBody = Librarian.BuildRenamedBody(dump.Body, newName);
            int writeRc = await _sysEx.WriteObjectAsync(new WriteOp(loc.ObjType, loc.Bank, loc.Number, dump.Version, renamedBody, "rename"));
            if (writeRc != 0) { Log($"  Write rejected (Reply {writeRc}) — nothing Stored."); return; }

            int storeRc = await _sysEx.StoreBankAsync(loc.ObjType, loc.Bank);
            if (storeRc != 0) { Log($"  Store rejected (Reply {storeRc}) — object was written but not committed; replay the backup to be safe."); return; }

            int nameType = loc.ObjType == LibObj.Program ? 1 : 0;
            _names[(nameType, loc.Bank, loc.Number)] = newName;
            await Task.Run(() =>
            {
                var entries = Storage.LoadNames(_host);
                entries.RemoveAll(c => c.Type == nameType && c.Bank == loc.Bank && c.Number == loc.Number);
                entries.Add(new CachedName(nameType, loc.Bank, loc.Number, newName));
                Storage.SaveNames(_host, entries);
            });

            Log("  DONE — renamed.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[librarian] rename failed: {ex.Message}");
            Log($"  Rename error: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshObjectTree();
            RefreshEnable();
        }
    }
}
