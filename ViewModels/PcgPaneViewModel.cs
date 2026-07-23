using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace KronosScreenRemote.ViewModels;

// The right ("loaded .pcg file") pane's view-state. Strictly READ-ONLY per requirement 11:
// nothing here ever writes back into a .pcg file — the tree it builds is a drag SOURCE only
// (enforced at the View layer, which never wires a drop target onto this pane).
partial class PcgPaneViewModel : ObservableObject
{
    PcgLibraryView? _view;

    // Read-only access for the Merge Window's own pull (LibrarianShellViewModel.
    // PullIntoMerge) — MergeCache.PullFromPcg needs the same PcgLibraryView.Get/AllObjects
    // surface PlaceFromPcg/BatchPlaceFromPcg already use via PcgPane.Get(loc).
    public PcgLibraryView? View => _view;

    public ObservableCollection<ObjectTreeNode> Roots { get; } = new();

    // Raised at the end of RefreshTree() — see LocalLibraryPaneViewModel's own TreeRefreshed for
    // why: a fresh Load() rebuilds Roots from scratch, so code-behind's selection tracking
    // (keyed by node reference) needs to re-bind to the new node instances by identity.
    public event Action? TreeRefreshed;

    [ObservableProperty] string statusText = "";
    [ObservableProperty] string? loadedFileName;

    public void LoadFromComputer(Window owner)
    {
        var dlg = new OpenFileDialog { Title = "Load PCG... From Computer", Filter = "Korg PCG Files|*.pcg|All Files|*.*" };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            Load(bytes, Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from computer failed: {ex}");
            ClearLoaded($"Load failed: {ex.Message}");
        }
    }

    public async Task LoadFromKronosAsync(Window owner, AppSettings settings, string host)
    {
        if (!await KronosFtpSession.EnsureLoginAsync(owner, settings, host))
        {
            StatusText = "FTP login failed or was cancelled.";
            return;
        }

        // The picker downloads the selected file itself, over the one connection it opened
        // to browse — no second connection here. Opening a second one right after the first
        // closes risked hanging: the Kronos's FTP server appears to hold a session open
        // until its own timeout unless sent a clean QUIT (see RemoteFilePickerDialog's own
        // comment), so a second connect could be left waiting for a session slot.
        var picker = new RemoteFilePickerDialog(host, settings.FtpPort, settings.FtpUsername, settings.FtpPassword, ".pcg") { Owner = owner };
        if (picker.ShowDialog() != true || picker.DownloadedTempPath == null)
        {
            StatusText = "Load from Kronos cancelled — the previously loaded file (if any) is unchanged.";
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(picker.DownloadedTempPath);
            Load(bytes, Path.GetFileName(picker.DownloadedTempPath));
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from Kronos failed: {ex}");
            ClearLoaded($"Load failed: {ex.Message}");
        }
    }

    // The most recently ATTEMPTED load always wins, whether it succeeds or fails — a failed
    // second load (wrong file, corrupt download, login hiccup) must never leave the previous,
    // unrelated file's tree sitting there looking current. This matters even more here than
    // it would elsewhere: the status bar explaining WHY a load failed isn't reliably visible
    // in the current window layout, so a stale tree with no visible error reads as "loading
    // from Kronos silently does nothing" rather than "that specific attempt failed, see log."
    void Load(byte[] bytes, string fileName)
    {
        var file = PcgFile.Open(bytes);
        if (file == null)
        {
            AppLog.Warn($"PCG load '{fileName}' failed: not a recognizable Kronos .pcg file.");
            ClearLoaded($"{fileName} is not a recognizable Kronos .pcg file.");
            return;
        }
        _view = new PcgLibraryView(file);
        LoadedFileName = fileName;
        RefreshTree();

        StatusText = $"Loaded {fileName} — {file.Objects.Count} object(s).";
        if (file.RejectedBanks.Count > 0)
        {
            StatusText += $" ({file.RejectedBanks.Count} bank chunk(s) couldn't be read — see log)";
            AppLog.Warn($"PCG load '{fileName}': {file.RejectedBanks.Count} candidate bank chunk(s) rejected:");
            foreach (var r in file.RejectedBanks)
                AppLog.Warn($"  {r.Tag} @0x{r.Offset:X} count={r.Count} itemSize={r.ItemSize} bankId=0x{r.BankIdRaw:X} — {r.Reason}");
        }
    }

    void ClearLoaded(string statusText)
    {
        _view = null;
        LoadedFileName = null;
        Roots.Clear();
        StatusText = statusText;
        TreeRefreshed?.Invoke();
    }

    // Testing-only entry point: LoadFromComputer/LoadFromKronosAsync both need a real Window
    // (a file dialog / FTP picker), so self-tests inject a pre-built view directly instead.
    internal void LoadForTesting(PcgLibraryView view)
    {
        _view = view;
        RefreshTree();
    }

    // Testing-only entry point for the "last attempted load wins" self-test — drives the
    // exact same Load() a real "from computer" or "from Kronos" load ends up calling, without
    // needing a real file dialog/FTP picker in between.
    internal void LoadBytesForTesting(byte[] bytes, string fileName) => Load(bytes, fileName);

    void RefreshTree()
    {
        var expandedKeys = ObjectTreeNode.CollectExpandedKeys(Roots);
        Roots.Clear();
        if (_view == null) { TreeRefreshed?.Invoke(); return; }

        var programsRoot = new ObjectTreeNode("Programs");
        var combisRoot = new ObjectTreeNode("Combis");
        // Set Lists have no bank concept (a flat, single group, all bank 0) — unlike
        // BuildTypeSubtree's Program/Combi banks, so the type root itself carries the bankRef
        // identity and Set List objects nest directly underneath it, matching
        // LocalLibraryPaneViewModel's own convention (see that file's BuildSetListSubtree)
        // instead of routing through BuildTypeSubtree, which used to produce a redundant inner
        // "Set Lists" bank node repeating the same label for no reason.
        var setListsRoot = new ObjectTreeNode("Set Lists", bankRef: (LibObj.SetList, 0));

        var byType = _view.AllObjects.GroupBy(l => l.ObjType).ToDictionary(g => g.Key, g => g.ToList());

        BuildTypeSubtree(programsRoot, LibObj.Program, byType);
        BuildTypeSubtree(combisRoot, LibObj.Combi, byType);
        BuildSetListSubtree(setListsRoot, byType);

        if (programsRoot.Children.Count > 0) Roots.Add(programsRoot);
        if (combisRoot.Children.Count > 0) Roots.Add(combisRoot);
        if (setListsRoot.Children.Count > 0) Roots.Add(setListsRoot);
        ObjectTreeNode.RestoreExpandedKeys(Roots, expandedKeys);
        TreeRefreshed?.Invoke();
    }

    void BuildTypeSubtree(ObjectTreeNode typeRoot, int objType, Dictionary<int, List<ObjLoc>> byType)
    {
        if (!byType.TryGetValue(objType, out var locs)) return;
        var descriptor = ObjectTypeRegistry.Get(objType);

        foreach (var bankGroup in locs.GroupBy(l => l.Bank).OrderBy(g => g.Key))
        {
            // bankRef makes this bank a selectable unit (LibrarianShellWindow.xaml.cs's
            // PaneSelection) — same identity shape LocalLibraryPaneViewModel's own
            // BuildTypeSubtree already gives its bank nodes.
            var bankNode = new ObjectTreeNode(BankNodeLabel(objType, descriptor, bankGroup), bankRef: (objType, bankGroup.Key));
            foreach (var loc in bankGroup.OrderBy(l => l.Number))
            {
                string name = _view!.GetName(loc) ?? "";
                string label = string.IsNullOrEmpty(name) ? loc.Label() : $"{loc.Label()}  {name}";
                bankNode.Children.Add(new ObjectTreeNode(label, loc));
            }
            typeRoot.Children.Add(bankNode);
        }
    }

    // Mirrors LocalLibraryPaneViewModel.BuildSetListSubtree — Set Lists have no bank concept
    // (a flat 128 numbered slots, all bank 0), so leaves nest directly under the type root
    // instead of through an inner bank-grouping node.
    void BuildSetListSubtree(ObjectTreeNode setListsRoot, Dictionary<int, List<ObjLoc>> byType)
    {
        if (!byType.TryGetValue(LibObj.SetList, out var locs)) return;
        foreach (var loc in locs.OrderBy(l => l.Number))
        {
            string name = _view!.GetName(loc) ?? "";
            string label = string.IsNullOrEmpty(name) ? loc.Label() : $"{loc.Label()}  {name}";
            setListsRoot.Children.Add(new ObjectTreeNode(label, loc));
        }
    }

    // Program banks are stored in a .pcg file as one whole-bank chunk tagged either MBK1
    // (EXi) or PBK1 (HD-1) — see PcgObjectExtractor/ProgramFormatConverter, since which one
    // a bank is also determines whether its bodies need truncating for the wire format.
    // Labeling it here surfaces that at a glance instead of it being an invisible internal
    // detail (requested explicitly — the same info matters when dragging a Program out).
    string BankNodeLabel(int objType, IObjectTypeDescriptor descriptor, IGrouping<int, ObjLoc> bankGroup)
    {
        string label = descriptor.BankLabel(bankGroup.Key);
        if (objType == LibObj.Program && _view!.Get(bankGroup.First()) is { } entry)
            label += entry.IsExi ? " (EXi)" : " (HD-1)";
        return label;
    }

    public PcgObjectEntry? Get(ObjLoc loc) => _view?.Get(loc);
}
