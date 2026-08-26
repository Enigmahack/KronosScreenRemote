namespace KronosScreenRemote;

// Read-only report window: sample-rate/bit-depth normalization report across a collection.
// No commands, no editing - SampleNormalizationReport already did all the real work; this
// just displays it.
public partial class SampleNormalizationReportWindow : ThemedWindow
{
    // A UI-only display row - keeps the "flag text" formatting decision out of the
    // core SampleNormalizationEntry record, which stays a plain data structure.
    sealed record Row(string Location, string SampleName, int SampleRate, byte Bits, byte Channels, bool Flagged, string FlagText);

    public SampleNormalizationReportWindow(List<SampleNormalizationEntry> entries)
    {
        InitializeComponent();

        var rows = entries.Select(e => new Row(e.Location, e.SampleName, e.SampleRate, e.Bits, e.Channels, e.Flagged,
            e.IsHeaderOnly ? "no audio data (header-only)" : e.Flagged ? "differs from collection majority" : "")).ToList();
        Grid.ItemsSource = rows;

        int flaggedCount = rows.Count(r => r.Flagged);
        SummaryText.Text = entries.Count == 0
            ? "No samples found - open a collection first."
            : $"{entries.Count} sample(s), {flaggedCount} flagged "
                + "(sample rate/bit depth differs from the collection's own majority, or has no audio data).";
    }
}
