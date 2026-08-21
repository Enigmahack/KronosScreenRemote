using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

// Vertical VU meter with a -90dB..0dB scale (0 = loudest/full scale, at the top) - a
// custom FrameworkElement rather than trying to retrofit tick labels onto a plain
// Rectangle fill, since the labels need to land at exact dB-proportional Y positions
// that only this control's own OnRender knows precisely.
sealed class SampleVuMeterControl : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(SampleVuMeterControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Linear peak amplitude, 0..1 (as SamplePlayback.PeakLevel reports) - converted to
    // dB internally so the caller never has to.
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    static readonly double[] TickDb = [0, -6, -12, -20, -40, -60, -90];
    const double MinDb = -90.0;

    static double ToDb(double linear) => linear <= 0.0000316 ? MinDb : Math.Clamp(20.0 * Math.Log10(linear), MinDb, 0.0);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double trackWidth = Math.Max(4, w - 20);
        var trackRect = new Rect(0, 0, trackWidth, h);
        dc.DrawRectangle((Brush)FindResource("ConsoleBackgroundBrush"), null, trackRect);

        double db = ToDb(Level);
        double frac = (db - MinDb) / -MinDb; // 0 at -90dB, 1 at 0dB
        double fillHeight = frac * h;
        var fillBrush = (Brush)FindResource(db > -6 ? "DangerTextBrush" : db > -20 ? "AccentBrush" : "SuccessBrush");
        if (fillHeight > 0)
            dc.DrawRectangle(fillBrush, null, new Rect(0, h - fillHeight, trackWidth, fillHeight));

        var textBrush = (Brush)FindResource("MutedTextBrush");
        var tickPen = new Pen(textBrush, 1);
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var tickDb in TickDb)
        {
            double y = h - (tickDb - MinDb) / -MinDb * h;
            y = Math.Clamp(y, 0, h);
            dc.DrawLine(tickPen, new Point(trackWidth, y), new Point(trackWidth + 3, y));
            var text = new FormattedText(tickDb.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 8, textBrush, dpi);
            dc.DrawText(text, new Point(trackWidth + 5, Math.Clamp(y - text.Height / 2, 0, h - text.Height)));
        }
    }
}
