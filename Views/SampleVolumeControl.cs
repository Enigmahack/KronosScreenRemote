using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

// Vertical volume control - a custom FrameworkElement instead of a retemplated native
// Slider, since the ask (bigger knob, centered, percentage printed INSIDE the knob
// itself) is easier to just draw directly than to fight Slider's default template for.
// Click-and-drag anywhere on the track jumps/drags the knob to that position, standard
// vertical-fader behavior.
sealed class SampleVolumeControl : FrameworkElement
{
    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(nameof(Volume), typeof(double), typeof(SampleVolumeControl),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1 - top of the track is 1.0 (loudest), bottom is 0.0 (silent). Purely a UI
    // value; SamplePlayback.Volume (software gain, see its own comment) is what
    // actually applies it.
    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0.0, 1.0));
    }

    public event Action<double>? VolumeChanged;

    const double KnobHeight = 30;

    public SampleVolumeControl() { Focusable = true; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        UpdateFromMouse(e.GetPosition(this).Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) UpdateFromMouse(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    void UpdateFromMouse(double y)
    {
        double h = ActualHeight;
        if (h <= 0) return;
        double frac = 1.0 - Math.Clamp(y / h, 0, 1);
        Volume = frac;
        VolumeChanged?.Invoke(Volume);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double trackW = Math.Max(2, w * 0.14); // a slim centered rail, the knob itself carries the visual weight
        double trackX = (w - trackW) / 2;
        dc.DrawRectangle((Brush)FindResource("ConsoleBackgroundBrush"), null, new Rect(trackX, 0, trackW, h));

        double knobCenterY = Math.Clamp((1.0 - Volume) * h, KnobHeight / 2, h - KnobHeight / 2);

        // Filled rail below the knob, same idea as a standard fader's fill.
        dc.DrawRectangle((Brush)FindResource("AccentBrush"), null,
            new Rect(trackX, knobCenterY, trackW, h - knobCenterY));

        // The knob - centered on the track, sized to comfortably hold the percentage
        // text, with a visible border so it reads as a distinct, grabbable control.
        double knobW = w;
        var knobRect = new Rect(0, knobCenterY - KnobHeight / 2, knobW, KnobHeight);
        dc.DrawRoundedRectangle((Brush)FindResource("PanelBackgroundBrush"), new Pen((Brush)FindResource("BorderBrush1"), 1),
            knobRect, 4, 4);

        var text = new FormattedText($"{(int)Math.Round(Volume * 100)}%", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, (Brush)FindResource("TextBrush"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(knobRect.X + (knobRect.Width - text.Width) / 2, knobRect.Y + (knobRect.Height - text.Height) / 2));
    }
}
