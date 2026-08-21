using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

// Time ruler footer for SampleWaveformControl - a thin strip of tick marks + time
// labels along the X axis that scales with whatever view window the waveform control
// is currently showing. Deliberately a separate control (kept in sync via
// SampleEditorWindow.xaml.cs reading SampleWaveformControl.ViewChanged) rather than
// folded into the waveform control itself - each stays simple to get right on its own.
public sealed class SampleWaveformRulerControl : FrameworkElement
{
    public static readonly DependencyProperty ViewStartFrameProperty =
        DependencyProperty.Register(nameof(ViewStartFrame), typeof(int), typeof(SampleWaveformRulerControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int ViewStartFrame { get => (int)GetValue(ViewStartFrameProperty); set => SetValue(ViewStartFrameProperty, value); }

    public static readonly DependencyProperty ViewEndFrameProperty =
        DependencyProperty.Register(nameof(ViewEndFrame), typeof(int), typeof(SampleWaveformRulerControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int ViewEndFrame { get => (int)GetValue(ViewEndFrameProperty); set => SetValue(ViewEndFrameProperty, value); }

    public static readonly DependencyProperty SampleRateProperty =
        DependencyProperty.Register(nameof(SampleRate), typeof(int), typeof(SampleWaveformRulerControl),
            new FrameworkPropertyMetadata(44100, FrameworkPropertyMetadataOptions.AffectsRender));
    public int SampleRate { get => (int)GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }

    static readonly double[] NiceSeconds =
        [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];

    static double NiceTimeInterval(double roughSeconds)
    {
        foreach (var s in NiceSeconds)
            if (roughSeconds <= s) return s;
        return NiceSeconds[^1];
    }

    static string FormatTime(double seconds, double interval)
    {
        if (interval < 1) return seconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
        long totalSec = (long)Math.Round(seconds);
        long h = totalSec / 3600, m = totalSec / 60 % 60, s = totalSec % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle((Brush)FindResource("PanelBackgroundBrush"), null, new Rect(0, 0, w, h));

        int sampleRate = SampleRate;
        int viewStart = ViewStartFrame, viewEnd = ViewEndFrame;
        if (sampleRate <= 0 || viewEnd <= viewStart) return;

        double startSeconds = (double)viewStart / sampleRate;
        double viewLenSeconds = (double)(viewEnd - viewStart) / sampleRate;
        double interval = NiceTimeInterval(viewLenSeconds * 80 / w);
        double firstTick = Math.Ceiling(startSeconds / interval) * interval;

        var pen = new Pen((Brush)FindResource("WaveformGridLineBrush"), 1);
        var textBrush = (Brush)FindResource("MutedTextBrush");
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (double t = firstTick; t <= startSeconds + viewLenSeconds; t += interval)
        {
            double x = (t - startSeconds) / viewLenSeconds * w;
            if (x < 0 || x > w) continue;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, 5));

            var text = new FormattedText(FormatTime(t, interval), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);
            double labelX = Math.Min(Math.Max(0, x + 2), Math.Max(0, w - text.Width));
            dc.DrawText(text, new Point(labelX, 6));
        }
    }
}
