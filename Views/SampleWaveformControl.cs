using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

// Min/max-bucketed waveform trace for a loaded KsfSample - a custom FrameworkElement
// with OnRender, not a Canvas full of Line shapes (the Python POC's approach): a
// multi-hundred-thousand-sample real .KSF makes that a real perf problem, not just a
// style preference. Phase 3 adds click-drag selection (feeds CropEffect) and
// mouse-wheel zoom; this control never talks to the ViewModel directly - code-behind
// reads SelectionStartFrame/SelectionEndFrame after SelectionChanged fires, matching
// the rest of this window's direct-control-manipulation style rather than binding.
public sealed class SampleWaveformControl : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(nameof(Samples), typeof(short[]), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).OnSamplesChanged()));

    public short[]? Samples
    {
        get => (short[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public static readonly DependencyProperty SelectionStartFrameProperty =
        DependencyProperty.Register(nameof(SelectionStartFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int SelectionStartFrame
    {
        get => (int)GetValue(SelectionStartFrameProperty);
        set => SetValue(SelectionStartFrameProperty, value);
    }

    public static readonly DependencyProperty SelectionEndFrameProperty =
        DependencyProperty.Register(nameof(SelectionEndFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int SelectionEndFrame
    {
        get => (int)GetValue(SelectionEndFrameProperty);
        set => SetValue(SelectionEndFrameProperty, value);
    }

    // Fires once per drag, on mouse-up - code-behind reads the two properties above
    // and pushes them into the ViewModel then, not on every intermediate MouseMove.
    public event Action? SelectionChanged;

    // The visible frame window - ZoomEndFrame == 0 means "not zoomed, show everything"
    // (can't use -1-as-sentinel with an int DP's 0 default doing double duty, so 0 end
    // specifically means unset here; a real 0-length zoom window is meaningless anyway).
    int _viewStart, _viewEnd;
    int _dragAnchorFrame = -1;

    public SampleWaveformControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    void OnSamplesChanged()
    {
        _viewStart = 0;
        _viewEnd = Samples?.Length ?? 0;
    }

    int FrameCount => Samples?.Length ?? 0;

    int PixelToFrame(double x)
    {
        var w = ActualWidth;
        if (w <= 0 || FrameCount == 0) return 0;
        int viewLen = Math.Max(1, (_viewEnd == 0 ? FrameCount : _viewEnd) - _viewStart);
        int frame = _viewStart + (int)(x / w * viewLen);
        return Math.Clamp(frame, 0, FrameCount);
    }

    double FrameToPixel(int frame)
    {
        var w = ActualWidth;
        int viewLen = Math.Max(1, (_viewEnd == 0 ? FrameCount : _viewEnd) - _viewStart);
        return (frame - _viewStart) * w / viewLen;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (FrameCount == 0) return;
        Focus();

        // Double-click resets to the full-sample view - the only "un-zoom" affordance;
        // deliberately not a button, matching how the equivalent gesture works in most
        // waveform editors. FrameworkElement has no OnMouseDoubleClick override (that's
        // Control-only), so ClickCount is checked here instead.
        if (e.ClickCount == 2)
        {
            _viewStart = 0;
            _viewEnd = FrameCount;
            InvalidateVisual();
            return;
        }

        _dragAnchorFrame = PixelToFrame(e.GetPosition(this).X);
        SelectionStartFrame = _dragAnchorFrame;
        SelectionEndFrame = _dragAnchorFrame;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragAnchorFrame < 0) return;
        int frame = PixelToFrame(e.GetPosition(this).X);
        SelectionStartFrame = Math.Min(_dragAnchorFrame, frame);
        SelectionEndFrame = Math.Max(_dragAnchorFrame, frame);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragAnchorFrame < 0) return;
        _dragAnchorFrame = -1;
        ReleaseMouseCapture();
        SelectionChanged?.Invoke();
    }

    // Zooms toward whatever frame is under the cursor, clamped to [0, FrameCount] and
    // to a minimum visible window of 32 frames (zooming to literally 0-width would
    // divide by zero elsewhere and serves no purpose).
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (FrameCount == 0) return;
        e.Handled = true;

        int viewLen = (_viewEnd == 0 ? FrameCount : _viewEnd) - _viewStart;
        int cursorFrame = PixelToFrame(e.GetPosition(this).X);
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        int newLen = Math.Clamp((int)(viewLen * factor), 32, FrameCount);

        double t = viewLen == 0 ? 0.5 : (double)(cursorFrame - _viewStart) / viewLen;
        int newStart = Math.Clamp(cursorFrame - (int)(newLen * t), 0, FrameCount - newLen);
        _viewStart = newStart;
        _viewEnd = newStart + newLen;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle((Brush)FindResource("PanelBackgroundBrush"), null, new Rect(0, 0, w, h));

        var samples = Samples;
        if (samples == null || samples.Length == 0)
        {
            var text = new FormattedText("No audio data in this file",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, (Brush)FindResource("MutedTextBrush"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            return;
        }

        int viewStart = Math.Clamp(_viewStart, 0, samples.Length);
        int viewEnd = Math.Clamp(_viewEnd == 0 ? samples.Length : _viewEnd, viewStart + 1, samples.Length);

        // Selection highlight drawn first, under the trace.
        if (SelectionEndFrame > SelectionStartFrame)
        {
            double selX0 = FrameToPixel(Math.Max(SelectionStartFrame, viewStart));
            double selX1 = FrameToPixel(Math.Min(SelectionEndFrame, viewEnd));
            if (selX1 > selX0)
                dc.DrawRectangle((Brush)FindResource("SelectionHighlightBrush"), null,
                    new Rect(selX0, 0, selX1 - selX0, h));
        }

        var pen = new Pen((Brush)FindResource("AccentBrush"), 1);
        double midY = h / 2;
        double yScale = h / 2 / 32768.0;
        int viewLen = viewEnd - viewStart;
        int bucketCount = Math.Max(1, (int)w);
        int samplesPerBucket = Math.Max(1, viewLen / bucketCount);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int b = 0; b < bucketCount; b++)
            {
                int start = viewStart + b * samplesPerBucket;
                if (start >= viewEnd) break;
                int end = Math.Min(start + samplesPerBucket, viewEnd);
                short min = short.MaxValue, max = short.MinValue;
                for (int i = start; i < end; i++)
                {
                    if (samples[i] < min) min = samples[i];
                    if (samples[i] > max) max = samples[i];
                }
                double x = b * w / bucketCount;
                double yTop = midY - max * yScale;
                double yBot = midY - min * yScale;
                ctx.BeginFigure(new Point(x, yTop), false, false);
                ctx.LineTo(new Point(x, yBot), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
