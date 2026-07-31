using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace KronosScreenRemote.ViewModels;

// The right ("loaded .pcg file") pane's view-state. Strictly READ-ONLY per requirement 11:
// nothing here ever writes back into a .pcg file - the tree it builds is a drag SOURCE only
// (enforced at the View layer, which never wires a drop target onto this pane).
partial class PcgPaneViewModel : ObservableObject
{
    PcgLibraryView? _view;

    // Read-only access for the Merge Window's own pull (LibrarianShellViewModel.
    // PullIntoMerge) - MergeCache.PullFromPcg needs the same PcgLibraryView.Get/AllObjects
    // surface PlaceFromPcg/BatchPlaceFromPcg already use via PcgPane.Get(loc).
    public PcgLibraryView? View => _view;

    public ObservableCollection<ObjectTreeNode> Roots { get; } = new();

    // Raised at the end of RefreshTree() - see LocalLibraryPaneViewModel's own TreeRefreshed for
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
            ClearLoaded(AppMessages.Librarian.Pcg.LoadFailed(ex.Message));
        }
    }

    // The remote (FTP) load's login, browse, and download all live behind IRemotePcgSource -
    // the one librarian branch the self-tests otherwise couldn't reach, since it constructs a
    // Window and talks to the Kronos's FTP server inline. The production source
    // (KronosRemotePcgSource) owns those; a self-test injects an in-memory fake. A cancelled/
    // failed pick just sets the status and leaves any previously loaded file untouched.
    public async Task LoadFromKronosAsync(IRemotePcgSource source)
    {
        var pick = await source.PickAsync();
        if (pick.File is not { } file)
        {
            StatusText = pick.StatusMessage;
            return;
        }

        try
        {
            Load(file.Bytes, file.FileName);
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from Kronos failed: {ex}");
            ClearLoaded(AppMessages.Librarian.Pcg.LoadFailed(ex.Message));
        }
    }

    // The most recently ATTEMPTED load always wins, whether it succeeds or fails - a failed
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
            ClearLoaded(AppMessages.Librarian.Pcg.NotRecognizedPcg(fileName));
            return;
        }
        _view = new PcgLibraryView(file);
        LoadedFileName = fileName;
        RefreshTree();

        StatusText = AppMessages.Librarian.Pcg.Loaded(fileName, file.Objects.Count);
        if (file.RejectedBanks.Count > 0)
        {
            StatusText += AppMessages.Librarian.Pcg.RejectedBanksSuffix(file.RejectedBanks.Count);
            AppLog.Warn($"PCG load '{fileName}': {file.RejectedBanks.Count} candidate bank chunk(s) rejected:");
            foreach (var r in file.RejectedBanks)
                AppLog.Warn($"  {r.Tag} @0x{r.Offset:X} count={r.Count} itemSize={r.ItemSize} bankId=0x{r.BankIdRaw:X} - {r.Reason}");
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

    // Testing-only entry point for the "last attempted load wins" self-test - drives the
    // exact same Load() a real "from computer" or "from Kronos" load ends up calling, without
    // needing a real file dialog/FTP picker in between.
    internal void LoadBytesForTesting(byte[] bytes, string fileName) => Load(bytes, fileName);

    // The Programs/Combis/Set Lists tree SHAPE is shared with the Local pane (ObjectTreeScaffold),
    // including the Set-List "no inner bank node" rule that once regressed into a redundant nested
    // "Set Lists" node. This pane supplies only what's PCG-specific: the objects come from the
    // loaded view (grouped by bank), each leaf is a plain name label, and keepEmptyRoots: false
    // hides a type root with nothing loaded under it (unlike Local, which always shows all three).
    void RefreshTree()
    {
        if (_view == null)
        {
            Roots.Clear();
            TreeRefreshed?.Invoke();
            return;
        }

        var byType = _view.AllObjects.GroupBy(l => l.ObjType).ToDictionary(g => g.Key, g => g.ToList());
        ObjectTreeScaffold.Rebuild(
            Roots,
            banksFor: objType => PcgBanksFor(objType, byType),
            setListLocs: byType.TryGetValue(LibObj.SetList, out var sl) ? sl.OrderBy(l => l.Number).ToList() : Array.Empty<ObjLoc>(),
            makeLeaf: MakeLeafNode,
            bankLabel: BankNodeLabel,
            keepEmptyRoots: false);
        TreeRefreshed?.Invoke();
    }

    // The populated banks of one Program/Combi object type from the loaded view, banks in numeric
    // order and each bank's leaves in slot order - matching what the same objects look like once
    // placed into Local Library.
    IReadOnlyList<ObjectTreeScaffold.Bank> PcgBanksFor(int objType, Dictionary<int, List<ObjLoc>> byType) =>
        byType.TryGetValue(objType, out var locs)
            ? locs.GroupBy(l => l.Bank).OrderBy(g => g.Key)
                  .Select(g => new ObjectTreeScaffold.Bank(g.Key, g.OrderBy(l => l.Number).ToList()))
                  .ToList()
            : Array.Empty<ObjectTreeScaffold.Bank>();

    ObjectTreeNode MakeLeafNode(ObjLoc loc)
    {
        string name = _view!.GetName(loc) ?? "";
        string label = string.IsNullOrEmpty(name) ? loc.Label() : $"{loc.Label()}  {name}";
        return new ObjectTreeNode(label, loc);
    }

    // Program banks are stored in a .pcg file as one whole-bank chunk tagged either MBK1
    // (EXi) or PBK1 (HD-1) - see PcgObjectExtractor/ProgramFormatConverter, since which one
    // a bank is also determines whether its bodies need truncating for the wire format.
    // Labeling it here surfaces that at a glance instead of it being an invisible internal
    // detail (requested explicitly - the same info matters when dragging a Program out). Only
    // called for a populated bank (the scaffold skips empty ones), so bank.Locs[0] is safe.
    string BankNodeLabel(int objType, ObjectTreeScaffold.Bank bank)
    {
        string label = ObjectTypeRegistry.Get(objType).BankLabel(bank.Number);
        if (objType == LibObj.Program && _view!.Get(bank.Locs[0]) is { } entry)
            label += entry.IsExi ? " (EXi)" : " (HD-1)";
        return label;
    }

    public PcgObjectEntry? Get(ObjLoc loc) => _view?.Get(loc);
}
