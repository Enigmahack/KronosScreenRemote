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

    // What's in the loaded file, by type, plus how many of its objects reference something
    // absent from both this file AND Keyboard Library - mirrors MergePaneViewModel's own tally,
    // shown the same way in LibrarianShellWindow.xaml. Rebuilt by RefreshTally on every load.
    [ObservableProperty] string tallyText = "";
    [ObservableProperty] int missingDependencyCount;

    // Drives the tally's red styling in LibrarianShellWindow.xaml, same as MergePaneViewModel's.
    public bool HasMissingDependencies => MissingDependencyCount > 0;

    partial void OnMissingDependencyCountChanged(int value) => OnPropertyChanged(nameof(HasMissingDependencies));

    // Set by LibrarianShellViewModel (same "Func injected post-construction" pattern as
    // GetCategoryNames): whether Keyboard Library already covers a reference this file doesn't -
    // an address absent from the PCG isn't a real gap when Keyboard Library resolves it (same rule
    // ShowPcgObjectDependencies' own DescribeGapOrLocal applies). Null in a headless self-test
    // just means every PCG-external reference counts as missing.
    public Func<ObjLoc, bool>? IsAvailableLocally { get; set; }

    // Live filter text for the top-right search box (LibrarianShellWindow.xaml, above the "Loaded
    // PCG File" pane). Set on every keystroke (UpdateSourceTrigger=PropertyChanged) - ApplyFilter
    // below is a single pass over an already-loaded tree (no disk/network I/O), so this stays cheap
    // even on a large .pcg file.
    [ObservableProperty] string searchText = "";

    // Set by LibrarianShellViewModel (same "Func injected post-construction" pattern as LocalPane.
    // BankTypeOf/ReadOnlyBankNames) so this pane can search Category/Sub-Category names without
    // owning a CategoryNames of its own - that instance is shell-owned, seeded from disk and warmed
    // from a live Global dump (LibrarianShellViewModel.WarmCategoryNamesAsync), and stays current
    // automatically since this is read fresh on every tree rebuild rather than captured once.
    public Func<CategoryNames>? GetCategoryNames { get; set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // Re-applied after every RefreshTree() (a fresh load, or the tree rebuilding for any other
    // reason) so a search typed before a reload - or a reload triggered while one is active -
    // still filters the new tree instead of silently reverting to "show everything."
    void ApplyFilter()
    {
        string query = SearchText.Trim();
        foreach (var root in Roots) FilterNode(root, query);
    }

    // Leaf: visible iff its own SearchText contains the query (or the query is empty). Bank/
    // type-root: visible iff any descendant is, matching the user's own framing ("searching I-A
    // would start to show everything in the I-A banks") for free, since every leaf's SearchText
    // already embeds its own bank label (see BuildSearchText). A group that gains a match is
    // force-EXPANDED so the live result is never hidden behind a closed branch - but never force-
    // COLLAPSED when it stops matching, so clearing the search doesn't fight a manual expand/
    // collapse the user made along the way.
    static bool FilterNode(ObjectTreeNode node, string query)
    {
        if (node.Children.Count == 0)
        {
            node.IsVisible = query.Length == 0 || node.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
            return node.IsVisible;
        }
        bool anyVisible = false;
        foreach (var child in node.Children)
            if (FilterNode(child, query)) anyVisible = true;
        node.IsVisible = anyVisible;
        if (anyVisible && query.Length > 0) node.IsExpanded = true;
        return anyVisible;
    }

    // A real Kronos .pcg tops out well under this; the picker's own "All Files" option means
    // the byte[] here can otherwise be any file the user happens to select, read whole into
    // memory before anything gets a chance to reject it.
    public const long MaxPcgBytes = 256L * 1024 * 1024;

    // Bumped once each load has something real to commit (a picked file, about to be read/
    // parsed) - NOT on dialog open, so a cancelled second pick can't invalidate a still-in-
    // flight first one. Load()/ClearLoaded() below only apply if no OTHER attempt has bumped
    // this since, so whichever attempt reaches ITS OWN commit point last is what's shown - see
    // Load's own "most recently ATTEMPTED load always wins" comment. That's exactly true for
    // two picks through the SAME method (LoadFromComputerAsync's modal dialog serializes pick
    // order = commit order); across DIFFERENT methods (Computer vs. Kronos) it's commit order,
    // not true selection order - a Kronos pick selected first but stuck behind a slow FTP
    // download can still supersede a Computer pick made and read after it. Narrower than the
    // stated invariant, but closes the concrete, easily-hit race (same method, back-to-back).
    int _loadEpoch;

    public void LoadFromComputer(Window owner) => _ = LoadFromComputerAsync(owner);

    public async Task LoadFromComputerAsync(Window owner)
    {
        var dlg = new OpenFileDialog { Title = "Load PCG... From Computer", Filter = "Korg PCG Files|*.pcg|All Files|*.*" };
        if (dlg.ShowDialog(owner) != true) return;

        int epoch = ++_loadEpoch;
        var path = dlg.FileName;
        try
        {
            var size = new FileInfo(path).Length;
            if (size > MaxPcgBytes)
            {
                if (epoch == _loadEpoch)
                    ClearLoaded(AppMessages.Librarian.Pcg.LoadFailed(
                        $"file is {size / (1024 * 1024)} MB, larger than the {MaxPcgBytes / (1024 * 1024)} MB limit"));
                return;
            }

            StatusText = AppMessages.Librarian.Pcg.Loading(Path.GetFileName(path));
            // Read AND parse off the WPF thread. Both used to run inline in this handler, so
            // picking a large file froze the whole window until PcgFile.Open finished. Only the
            // tree build below needs the UI thread, and it gets it by virtue of the await
            // resuming here.
            var bytes = await Task.Run(() => File.ReadAllBytes(path)).ConfigureAwait(true);
            // A second load (Computer or Kronos) may have started AND finished while this one
            // was off doing file I/O - fire-and-forget from LoadFromComputer has no other way
            // to know. Discard rather than clobber the newer, already-displayed result.
            if (epoch != _loadEpoch) return;
            Load(bytes, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from computer failed: {ex}");
            if (epoch == _loadEpoch)
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

        int epoch = ++_loadEpoch;
        try
        {
            Load(file.Bytes, file.FileName);
        }
        catch (Exception ex)
        {
            AppLog.Error($"PCG load from Kronos failed: {ex}");
            if (epoch == _loadEpoch)
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
        // A leftover query from browsing the PREVIOUS file must not silently carry over and
        // filter the new one - and just as important perf-wise, RefreshTree()'s own ApplyFilter()
        // call would otherwise run a full non-empty-query filter pass (touching every leaf's
        // IsVisible, not just building - see ApplyFilter's own comment) as part of EVERY load
        // from here on, not only the load that's actually followed by a real search.
        SearchText = "";
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
        RefreshTally();

        StatusText = AppMessages.Librarian.Pcg.Loaded(fileName, file.Objects.Count);
        if (file.RejectedBanks.Count > 0)
        {
            StatusText += AppMessages.Librarian.Pcg.RejectedBanksSuffix(file.RejectedBanks.Count);
            AppLog.Warn($"PCG load '{fileName}': {file.RejectedBanks.Count} candidate bank chunk(s) rejected:");
            foreach (var r in file.RejectedBanks)
                AppLog.Warn($"  {r.Tag} @0x{r.Offset:X} count={r.Count} itemSize={r.ItemSize} bankId=0x{r.BankIdRaw:X} - {r.Reason}");
        }
        if (file.ChecksumWarnings.Count > 0)
        {
            // Advisory only (PcgChecksumWarning) - the bank still loaded and is still usable,
            // this just means its on-disk bytes no longer match what the Kronos itself wrote
            // (truncated download, hand-edited file, a tool that doesn't recompute checksums).
            StatusText += AppMessages.Librarian.Pcg.ChecksumWarningsSuffix(file.ChecksumWarnings.Count);
            AppLog.Warn($"PCG load '{fileName}': {file.ChecksumWarnings.Count} bank chunk(s) failed their checksum (loaded anyway):");
            foreach (var w in file.ChecksumWarnings)
                AppLog.Warn($"  {w.Tag} @0x{w.Offset:X} expected=0x{w.Expected:X2} actual=0x{w.Actual:X2}");
        }
    }

    void ClearLoaded(string statusText)
    {
        _view = null;
        LoadedFileName = null;
        Roots.Clear();
        StatusText = statusText;
        RefreshTally();
        TreeRefreshed?.Invoke();
    }

    // Whole-file counts by type, and how many objects have at least one reference this file
    // doesn't satisfy and Keyboard Library doesn't either. A direct (one-hop) check per object,
    // not MergePaneViewModel's transitive walk - there's no per-reference-site cache to drive
    // it here, and re-deriving one on every load, over what can be a 5,000+ object file, is the
    // exact cost RefreshTree's own leaf-building already accepts once per leaf (see
    // PcgPaneViewModel's header comment on why a decode-per-leaf is fine with the whole file
    // already resident in memory) - this is simply a second such pass.
    void RefreshTally()
    {
        if (_view == null) { TallyText = ""; MissingDependencyCount = 0; return; }

        var all = _view.AllObjects.ToList();
        int Count(int objType) => all.Count(l => l.ObjType == objType);
        TallyText = $"Programs: {Count(LibObj.Program)}   Combis: {Count(LibObj.Combi)}   " +
                    $"Drum Kits: {Count(LibObj.DrumKit)}   Wave Seq: {Count(LibObj.WaveSequence)}   " +
                    $"Set Lists: {Count(LibObj.SetList)}";

        int missing = 0;
        foreach (var loc in all)
        {
            var entry = _view.Get(loc);
            if (entry == null) continue;
            byte[]? body = ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
            if (body == null) continue;
            bool hasGap = ObjectReferenceWalker.WalkResolvable(loc.ObjType, body)
                .Any(r => _view.Get(r.Ref) == null && IsAvailableLocally?.Invoke(r.Ref) != true);
            if (hasGap) missing++;
        }
        MissingDependencyCount = missing;
    }

    // Testing-only entry point: LoadFromComputer/LoadFromKronosAsync both need a real Window
    // (a file dialog / FTP picker), so self-tests inject a pre-built view directly instead.
    internal void LoadForTesting(PcgLibraryView view)
    {
        _view = view;
        RefreshTree();
        RefreshTally();
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
        ApplyFilter();
        TreeRefreshed?.Invoke();
    }

    // The populated banks of one Program/Combi object type from the loaded view, banks in numeric
    // order and each bank's leaves in slot order - matching what the same objects look like once
    // placed into Keyboard Library.
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
        // Unlike the Local pane (LocalLibraryCache.HasSampleDependency, a cached write-time bit -
        // see its own comment for why), the whole .pcg file is already fully in memory here, so
        // there's no blob-read cost to computing this fresh per leaf.
        var entry = _view.Get(loc);
        byte[]? body = entry == null ? null : ProgramFormatConverter.WireBodyFromPcgEntry(loc.ObjType, entry);
        // Walked ONCE and shared with BuildSearchText below - this used to run SampleReferenceWalker.
        // Walk twice per leaf (once here, once again inside BuildSearchText), doubling a walk that
        // itself allocates a Dictionary + row list, across every Program/DrumKit/WaveSequence in the
        // file on every load.
        var sampleRows = body == null ? Array.Empty<SampleReferenceWalker.SampleDependencyRow>() : SampleReferenceWalker.Walk(loc.ObjType, body);
        // Built eagerly, right here - see ObjectTreeNode.SearchText's own comment for why (the
        // lazy version this replaced was measured at ~120ms total against a real 5,343-object
        // file, and didn't fix the reported freeze anyway).
        string searchText = BuildSearchText(loc, name, entry, body, sampleRows);
        return new ObjectTreeNode(label, loc, hasSampleDependency: sampleRows.Count > 0, searchText: searchText);
    }

    // Everything searchable about one leaf (LibrarianShellWindow.xaml's PCG-pane search box),
    // joined into one haystack: label already covers Name + Bank type (loc.Label() embeds the bank,
    // e.g. "I-A:000", so "searching I-A" matches for free); Category/Sub-Category names when a
    // CategoryNames source is wired up; the EXi engine name for an EXi Program (so "AL-1" matches
    // both a NAME containing "AL-1" and a Program that IS one); and this object's own direct
    // (one-hop, not transitive) dependencies - object references plus sample-bank references, the
    // same two walkers the Object Dependencies panel itself uses. Deliberately excludes anything
    // never shown in the Kronos UI (UUIDs, content hashes) - the user can't act on those anyway.
    string BuildSearchText(ObjLoc loc, string name, PcgObjectEntry? entry, byte[]? body, IReadOnlyList<SampleReferenceWalker.SampleDependencyRow> sampleRows)
    {
        var parts = new List<string> { loc.Label(), name, ObjectTypeRegistry.Get(loc.ObjType).DisplayName };
        if (body == null) return string.Join(" | ", parts);

        if (loc.ObjType is LibObj.Program or LibObj.Combi && GetCategoryNames?.Invoke() is { } categoryNames)
        {
            var (category, sub) = loc.ObjType == LibObj.Program ? ProgramBody.ReadCategory(body) : CombiBody.ReadCategory(body);
            parts.Add(categoryNames.CategoryLabel(loc.ObjType, category));
            parts.Add(categoryNames.SubCategoryLabel(loc.ObjType, category, sub));
        }

        if (loc.ObjType == LibObj.Program && entry!.IsExi && LibRefs.ProgramEngineName(body) is { } engine)
            parts.Add(engine);

        // Deduped, same discipline as LibrarianShellViewModel.CollectPcgDeps's own `seen` set - a
        // Combi has 16 timbres, and a Set List up to 128 slots; without this, every one of them
        // pointing at the same Program/Combi (a common, even deliberate, real-world pattern) added
        // its label+name again per SLOT, ballooning that leaf's haystack - and therefore every
        // keystroke's Contains cost against it - by up to 16-128x for no additional search value.
        var seenRefs = new HashSet<ObjLoc>();
        foreach (var (_, _, refLoc) in ObjectReferenceWalker.Walk(loc.ObjType, body))
        {
            if (!seenRefs.Add(refLoc)) continue;
            parts.Add(refLoc.Label());
            if (_view!.GetName(refLoc) is { Length: > 0 } refName) parts.Add(refName);
        }
        foreach (var row in sampleRows)
            parts.Add(row.Description);

        return string.Join(" | ", parts);
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
