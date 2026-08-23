using System.Globalization;
using System.Windows;

namespace KronosScreenRemote;

// Frames and Seconds are two entry points onto the SAME value - Core/Sample/Dsp's
// InsertSilenceEffect only understands frames (matching every other position field in
// this window), so Seconds is purely a convenience that computes frames and writes them
// into FramesBox; nothing downstream of this dialog ever sees a seconds value.
public partial class InsertSilenceDialog : ThemedWindow
{
    readonly int _sampleRate;
    bool _syncing; // reentrancy guard - TextChanged on the box THIS method just wrote would otherwise recompute the other box from a rounded value and drift

    public int Frames { get; private set; }

    public InsertSilenceDialog(int sampleRate, int initialFrames)
    {
        InitializeComponent();
        _sampleRate = Math.Max(1, sampleRate);
        FramesBox.Text = initialFrames.ToString(CultureInfo.InvariantCulture);
        SecondsBox.Text = FormatSeconds(initialFrames / (double)_sampleRate);
        Loaded += (_, _) => { FramesBox.SelectAll(); FramesBox.Focus(); };
    }

    static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    void OnFramesChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!int.TryParse(FramesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames)) return;

        _syncing = true;
        SecondsBox.Text = FormatSeconds(frames / (double)_sampleRate);
        _syncing = false;
    }

    void OnSecondsChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!double.TryParse(SecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0) return;

        _syncing = true;
        // Round rather than truncate - "0.5s at 44100Hz" should land on the frame count
        // that round-trips back to "0.5", not one a hair short of it.
        FramesBox.Text = ((int)Math.Round(seconds * _sampleRate)).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FramesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) || frames <= 0)
        {
            PromptLabel.Text = "Enter a positive whole number of frames.";
            PromptLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerTextBrush");
            return;
        }
        Frames = frames;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
