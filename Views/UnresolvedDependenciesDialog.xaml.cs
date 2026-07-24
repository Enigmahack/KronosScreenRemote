using System.Windows;

namespace KronosScreenRemote;

// Step 4 of the auto-heal placement pipeline's own gate (LibrarianShellViewModel.
// ConfirmContinueWithPendingDependencies) — replaces a plain MessageBox.Show, which grew
// unboundedly tall with a large number of unresolved dependencies (e.g. a big Set List
// missing many Combis/Programs) until its own Yes/No buttons scrolled off-screen and couldn't
// be clicked at all. The list here is capped (XAML's Border MaxHeight) and scrolls instead,
// with Continue/Cancel always pinned below it.
internal partial class UnresolvedDependenciesDialog : ThemedWindow
{
    UnresolvedDependenciesDialog(string heading, IEnumerable<string> descriptions)
    {
        InitializeComponent();
        TXT_Heading.Text = heading;
        foreach (var d in descriptions) LST_Dependencies.Items.Add(new Row(d));
    }

    // Same missing dependency needed by several different objects collapses to ONE row here
    // instead of one row per referrer/site — SessionDependencyClipboard tracks those
    // separately (it needs the per-site granularity to repatch each referrer individually; see
    // LibrarianShellViewModel.ResolvePendingDependencies), but repeating "I-A:000" a dozen
    // times in a row in THIS list would just be noise, not information.
    public static UnresolvedDependenciesDialog For(IReadOnlyList<SessionDependencyEntry> pending)
    {
        int count = pending.Count;
        string heading = AppMessages.UnresolvedDependencies.Heading(count);

        var grouped = pending
            .GroupBy(e => (e.MissingRef, e.ExpectedContentHash))
            .Select(g => AppMessages.UnresolvedDependencies.Row(g.Key.MissingRef.Label(), g.Count()));

        return new UnresolvedDependenciesDialog(heading, grouped);
    }

    sealed class Row
    {
        public string Description { get; }
        public Row(string description) => Description = description;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
