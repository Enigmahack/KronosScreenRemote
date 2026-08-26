using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

// Horizontal pan control - same "custom FrameworkElement instead of a retemplated
// native Slider" reasoning as SampleVolumeControl (its own comment), rotated 90 degrees:
// a horizontal track/knob instead of vertical, with the value printed INSIDE the knob.
// MIDI pan convention throughout: 0..127, 64 = center (0 = full Left, 127 = full Right).
sealed class SamplePanControl : FrameworkElement
{
    public static readonly DependencyProperty PanProperty =
        DependencyProperty.Register(nameof(Pan), typeof(int), typeof(SamplePanControl),
            new FrameworkPropertyMetadata(64, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Pan
    {
        get => (int)GetValue(PanProperty);
        set => SetValue(PanProperty, Math.Clamp(value, 0, 127));
    }

    public event Action<int>? PanChanged;

    const double KnobWidth = 30;
    const int Center = 64;

    public SamplePanControl() { Focusable = true; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        // Double-click re-centers, same gesture SampleWaveformControl already uses for
        // its own "reset" affordance (double-click there un-zooms) - no drag involved,
        // so this returns before CaptureMouse rather than also dragging from the click.
        if (e.ClickCount == 2) { SetPan(Center); return; }
        CaptureMouse();
        UpdateFromMouse(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) UpdateFromMouse(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    void UpdateFromMouse(double x)
    {
        double w = ActualWidth;
        if (w <= 0) return;
        double frac = Math.Clamp(x / w, 0, 1);
        SetPan((int)Math.Round(frac * 127));
    }

    void SetPan(int value)
    {
        Pan = value;
        PanChanged?.Invoke(Pan);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double trackH = Math.Max(2, h * 0.3);
        double trackY = (h - trackH) / 2;
        dc.DrawRectangle((Brush)FindResource("ConsoleBackgroundBrush"), null, new Rect(0, trackY, w, trackH));

        double knobCenterX = Math.Clamp(Pan / 127.0 * w, KnobWidth / 2, w - KnobWidth / 2);

        // Filled rail from CENTER out to the knob, not from an edge - pan has no
        // natural "silent" end the way Volume's bottom is 0%, so the fill anchors at
        // 64 (center) and grows toward whichever side the knob is on, same idea as a
        // standard pan-position meter.
        double centerX = w / 2;
        double fillLeft = Math.Min(centerX, knobCenterX);
        double fillRight = Math.Max(centerX, knobCenterX);
        dc.DrawRectangle((Brush)FindResource("AccentBrush"), null, new Rect(fillLeft, trackY, fillRight - fillLeft, trackH));

        // A thin center tick, always visible even when the knob sits on it - the one
        // fixed reference point Pan has that Volume's own track doesn't need.
        dc.DrawLine(new Pen((Brush)FindResource("BorderBrush1"), 1), new Point(centerX, 0), new Point(centerX, h));

        var knobRect = new Rect(knobCenterX - KnobWidth / 2, 0, KnobWidth, h);
        dc.DrawRoundedRectangle((Brush)FindResource("PanelBackgroundBrush"), new Pen((Brush)FindResource("BorderBrush1"), 1),
            knobRect, 4, 4);

        // The three named positions (0=L, 64=Center, 127=R) read as letters, matching
        // real hardware pan displays - every other value is just its raw MIDI number,
        // same as before.
        string label = Pan switch { 0 => "L", Center => "C", 127 => "R", _ => Pan.ToString(CultureInfo.InvariantCulture) };
        var text = new FormattedText(label, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, (Brush)FindResource("TextBrush"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(knobRect.X + (knobRect.Width - text.Width) / 2, knobRect.Y + (knobRect.Height - text.Height) / 2));
    }
}
