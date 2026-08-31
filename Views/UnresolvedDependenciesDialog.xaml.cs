using System.Windows;
using System.Windows.Controls;

namespace KronosScreenRemote;

// Invoked from LibrarianShellViewModel.ConfirmContinueWithPendingDependencies. Replaces a
// plain MessageBox.Show, which grew unboundedly tall with a large number of unresolved
// dependencies (e.g. a big Set List missing many Combis/Programs) until its own Yes/No
// buttons scrolled off-screen and couldn't be clicked at all. The list here is capped
// (XAML's Border MaxHeight) and scrolls instead, with Continue/Cancel always pinned below it.
//
// Every row names the TYPE and the objects that need it, and right-clicking one searches a
// .pcg for that specific object - the same recovery the Librarian's own "Scan PCG for
// dependencies..." offers, reachable from the moment the problem is actually reported.
internal partial class UnresolvedDependenciesDialog : ThemedWindow
{
    // Set by the owner (LibrarianShellWindow): runs the file picker once, then scans the chosen
    // .pcg for EVERY address still listed - the clicked row is only what starts it. Returns the
    // addresses actually located (so their rows can go) plus the status line. Null (a headless
    // construction) disables the action.
    public Func<IReadOnlyList<ObjLoc>, (IReadOnlyCollection<ObjLoc> Found, string Status)>? ScanForDependencyRequested { get; set; }

    // How many pending ENTRIES each missing address accounts for - the heading counts references,
    // while a row group is one address, so removing a group has to subtract its own share rather
    // than one. Populated by For(); empty for the headless ctor path.
    readonly Dictionary<ObjLoc, int> _entriesPerMissing = new();
    int _remainingReferences;

    UnresolvedDependenciesDialog(string heading, IEnumerable<Row> rows)
    {
        InitializeComponent();
        TXT_Heading.Text = heading;
        foreach (var row in rows) LST_Dependencies.Items.Add(row);
    }

    // Same missing dependency needed by several different objects collapses to ONE row here
    // instead of one row per referrer/site - SessionDependencyClipboard tracks those
    // separately (it needs the per-site granularity to repatch each referrer individually; see
    // LibrarianShellViewModel.ResolvePendingDependencies), but repeating "I-A:000" a dozen
    // times in a row in THIS list would just be noise, not information. The referrers are shown
    // as indented detail lines under the row instead, capped so one heavily-shared Program can't
    // push everything else out of view.
    //
    // `nameOf` resolves a display name for an address when something is known about it (the loaded
    // PCG, or Local Library) - optional, since neither is guaranteed to be available.
    public static UnresolvedDependenciesDialog For(
        IReadOnlyList<SessionDependencyEntry> pending, Func<ObjLoc, string>? nameOf = null)
    {
        const int maxReferrersShown = 3;

        var rows = new List<Row>();
        foreach (var group in pending.GroupBy(e => e.MissingRef))
        {
            var entries = group.ToList();
            rows.Add(new Row(
                AppMessages.UnresolvedDependencies.Row(
                    ObjectTypeRegistry.Get(group.Key.ObjType).DisplayName,
                    group.Key.Label(),
                    nameOf?.Invoke(group.Key) ?? "",
                    entries.Count),
                group.Key));

            foreach (var entry in entries.Take(maxReferrersShown))
                rows.Add(new Row(
                    AppMessages.UnresolvedDependencies.RowReferrer(
                        ObjectTypeRegistry.Get(entry.RequiredBy.ObjType).DisplayName,
                        entry.RequiredBy.Label(),
                        entry.RefKind),
                    group.Key));
            if (entries.Count > maxReferrersShown)
                rows.Add(new Row(AppMessages.UnresolvedDependencies.RowReferrerMore(entries.Count - maxReferrersShown), group.Key));
        }

        var dlg = new UnresolvedDependenciesDialog(AppMessages.UnresolvedDependencies.Heading(pending.Count), rows);
        dlg._remainingReferences = pending.Count;
        foreach (var group in pending.GroupBy(e => e.MissingRef))
            dlg._entriesPerMissing[group.Key] = group.Count();
        return dlg;
    }

    // Every row - heading line or indented referrer detail - carries the missing address it belongs
    // to, so right-clicking anywhere in a group acts on that group's object.
    sealed class Row
    {
        public string Description { get; }
        public ObjLoc Missing { get; }
        public Row(string description, ObjLoc missing) { Description = description; Missing = missing; }
    }

    // Right-click selects first (Explorer convention), so the menu always acts on the row actually
    // under the cursor rather than a stale prior selection.
    void OnRowRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) item.IsSelected = true;
    }

    void OnScanForRow(object sender, RoutedEventArgs e)
    {
        if (LST_Dependencies.SelectedItem is not Row row || ScanForDependencyRequested == null) return;

        // The clicked row picks the file; the scan then covers everything still outstanding. Its
        // own address goes first so a file holding only that one still reads as answering the row
        // the user actually right-clicked.
        var targets = new List<ObjLoc> { row.Missing };
        targets.AddRange(_entriesPerMissing.Keys.Where(k => !k.Equals(row.Missing)));

        var (found, status) = ScanForDependencyRequested(targets);
        TXT_Status.Text = status;
        foreach (var located in found) DropGroup(located);
    }

    // The object turned up and is now staged in the Merge Window, so its heading row and every
    // indented referrer line under it come off the list - otherwise the user is left re-reading a
    // gap they already closed, with nothing to distinguish it from the ones still outstanding.
    // Display-only: the underlying SessionDependencyClipboard entries stay tracked (the reference
    // isn't actually repointed until the staged object is placed and the next Sync/Commit runs -
    // see LibrarianShellViewModel.ResolvePendingDependencies), so Continue still carries the same
    // meaning it always did.
    void DropGroup(ObjLoc missing)
    {
        foreach (var stale in LST_Dependencies.Items.OfType<Row>().Where(r => r.Missing.Equals(missing)).ToList())
            LST_Dependencies.Items.Remove(stale);

        if (_entriesPerMissing.Remove(missing, out int references)) _remainingReferences -= references;
        TXT_Heading.Text = _remainingReferences > 0
            ? AppMessages.UnresolvedDependencies.Heading(_remainingReferences)
            : AppMessages.UnresolvedDependencies.AllLocated;
    }

    // The whole list as text - for anyone who wants to work through the gaps outside this dialog
    // (a spreadsheet, a note, a message to someone else) rather than one right-click at a time.
    void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var lines = LST_Dependencies.Items.OfType<Row>().Select(r => r.Description);
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            TXT_Status.Text = AppMessages.UnresolvedDependencies.CopiedToClipboard;
        }
        catch (Exception ex)
        {
            // The Windows clipboard can be locked by another process - a failed copy must not take
            // down the dialog the user still has to answer.
            AppLog.Warn($"[librarian] clipboard copy failed: {ex.Message}");
            TXT_Status.Text = AppMessages.UnresolvedDependencies.ScanFailed(ex.Message);
        }
    }

    void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu }) return;
        bool hasRow = LST_Dependencies.SelectedItem is Row;
        foreach (var item in menu.Items)
            if (item is MenuItem { Name: "MI_ScanForRow" } mi)
                mi.IsEnabled = hasRow && ScanForDependencyRequested != null;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
