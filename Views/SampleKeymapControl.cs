using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

// Piano-strip keymap for a multisample: an actual piano (white/black keys, full 0-127
// MIDI range) with a zone-assignment bar above it. Each boundary BETWEEN two adjacent
// zones is a draggable handle - dragging it changes where zone[i] ends / zone[i+1]
// begins (KmpZone.TopKey), never the first zone's own low edge (always 0/C-1, per
// KmpZone's own doc comment: "range runs from previous zone's TopKey+1"), since there's
// no zone below the first one to trade keys with - the same reasoning that means there's
// no handle above the LAST zone's top edge either. Click a key (not a handle) to select
// that zone, same as clicking it in the tree.
//
// Layout: WHITE-KEY-PROPORTIONAL, not a uniform 128-equal-slot chromatic ruler (the
// earlier version). A real piano's 7 white keys per octave are evenly spaced and span
// the full octave width; black keys are narrower and sit centered on the boundary
// between the two white keys they visually fall between (C# centers on the C/D
// boundary, etc.) - a uniform per-semitone grid gets this visibly wrong (black keys
// evenly spaced under every key instead of clustered in their real 2-and-3 groups).
// BuildLayout() computes both, once per render/hit-test pass (128 keys - trivial cost).
sealed class SampleKeymapControl : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty =
        DependencyProperty.Register(nameof(Zones), typeof(List<KmpZone>), typeof(SampleKeymapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleKeymapControl)d).OnZonesChanged()));

    public List<KmpZone>? Zones
    {
        get => (List<KmpZone>?)GetValue(ZonesProperty);
        set => SetValue(ZonesProperty, value);
    }

    public static readonly DependencyProperty SelectedZoneProperty =
        DependencyProperty.Register(nameof(SelectedZone), typeof(KmpZone), typeof(SampleKeymapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public KmpZone? SelectedZone
    {
        get => (KmpZone?)GetValue(SelectedZoneProperty);
        set => SetValue(SelectedZoneProperty, value);
    }

    public event Action<KmpZone>? ZoneClicked;

    // Fires once, on mouse-up, after a boundary drag - (leftZone, newTopKey). The
    // control doesn't mutate the zone itself; the caller (SampleEditorViewModel) owns
    // deciding what a TopKey change means for dirty-tracking/persistence.
    public event Action<KmpZone, int>? BoundaryMoved;

    // Fires once, on mouse-up, after dragging one zone's bar segment onto a DIFFERENT
    // zone's segment - (dragged, dropTarget). Only zone-bar drags (not piano-key clicks)
    // can start a reorder; a drag that ends back on its own origin zone (or off the bar
    // entirely) is treated as a plain click instead (see ZoneClicked). The control
    // itself doesn't reorder anything - SampleEditorViewModel.ReorderZone owns the list
    // mutation and the "each zone keeps its own key-range WIDTH, only its position in
    // the sequence changes" rule.
    public event Action<KmpZone, KmpZone>? ZoneReordered;

    const double ZoneBarHeight = 16;
    const double HitTestPixels = 5;

    int _hoverBoundary = -1;
    int _dragBoundary = -1;
    int _dragPendingKey = -1;

    // Zone-bar drag-to-reorder state - separate from the boundary-drag state above
    // (mutually exclusive: a boundary hit is checked first and, if hit, owns the drag).
    KmpZone? _dragZoneOrigin;
    KmpZone? _dragZoneHover;

    public SampleKeymapControl() { Focusable = true; }

    void OnZonesChanged() { _hoverBoundary = _dragBoundary = -1; _dragZoneOrigin = _dragZoneHover = null; }

    static bool IsBlackKey(int midi) => (midi % 12) is 1 or 3 or 6 or 8 or 10;

    // Precomputes each of the 128 keys' left/right pixel edges for the current
    // ActualWidth: white keys get equal-width slots (ActualWidth / total-white-count);
    // black keys are centered on the boundary between the white keys straddling them,
    // width = 60% of a white key's width - the standard simplified-piano-strip
    // convention (real black keys aren't exactly centered on that boundary, but this is
    // close enough to read correctly at a glance and is simple to hit-test exactly).
    (double whiteWidth, double[] leftX, double[] rightX) BuildLayout()
    {
        var leftX = new double[128];
        var rightX = new double[128];
        var whiteIndexOf = new int[128];
        int whiteCount = 0;
        for (int k = 0; k <= 127; k++)
        {
            whiteIndexOf[k] = whiteCount;
            if (!IsBlackKey(k)) whiteCount++;
        }

        double whiteWidth = Math.Max(0.01, ActualWidth / Math.Max(1, whiteCount));
        for (int k = 0; k <= 127; k++)
        {
            if (IsBlackKey(k))
            {
                double center = whiteIndexOf[k] * whiteWidth;
                double bw = whiteWidth * 0.6;
                leftX[k] = center - bw / 2;
                rightX[k] = center + bw / 2;
            }
            else
            {
                leftX[k] = whiteIndexOf[k] * whiteWidth;
                rightX[k] = (whiteIndexOf[k] + 1) * whiteWidth;
            }
        }
        return (whiteWidth, leftX, rightX);
    }

    // Each zone's trigger range: (previous zone's TopKey + 1) through its own TopKey -
    // computed once per render/hit-test rather than duplicated between them.
    (KmpZone Zone, int LowKey, int HighKey)[] ComputeRanges()
    {
        var zones = Zones;
        if (zones == null || zones.Count == 0) return [];
        var ranges = new (KmpZone, int, int)[zones.Count];
        int prevTop = -1;
        for (int i = 0; i < zones.Count; i++)
        {
            int low = Math.Clamp(prevTop + 1, 0, 127);
            int high = Math.Clamp(zones[i].TopKey, low, 127);
            ranges[i] = (zones[i], low, high);
            prevTop = zones[i].TopKey;
        }
        return ranges;
    }

    // Boundary i sits at the RIGHT edge of ranges[i].HighKey's own rendered rectangle -
    // deliberately rightX[topKey], NOT leftX[topKey+1] (the previous formula). Those two
    // are only the same key's edge for WHITE top keys (rightX[white] == leftX[next]);
    // for a BLACK top key they're NOT - a black key is drawn at only 60% width,
    // CENTERED on the white-key grid boundary (see BuildLayout), so leftX[topKey+1]
    // lands at that black key's own CENTER, not its edge. That inconsistency (edge for
    // white top keys, center for black ones) is exactly what made the boundary line
    // hard to read: "sitting in the middle of the key" only for black-key boundaries.
    // rightX[topKey] is well-defined for every key 0..127 (including 127 itself), so
    // this also drops the old leftX[topKey+1]-out-of-range special case entirely.
    double BoundaryX(double[] rightX, (KmpZone, int, int)[] ranges, int i) => rightX[ranges[i].Item3];

    int HitTestBoundary(double[] rightX, (KmpZone, int, int)[] ranges, double x)
    {
        for (int i = 0; i < ranges.Length - 1; i++)
            if (Math.Abs(x - BoundaryX(rightX, ranges, i)) <= HitTestPixels) return i;
        return -1;
    }

    // Nearest-boundary-position scan, NOT `x / whiteWidth`. The piano layout isn't
    // linear in MIDI key number (black keys are interspersed at 60% width, so a white
    // key's pixel index and its actual MIDI number diverge more the further right you
    // go - by the top of the keyboard a "white-key index" is barely half the real MIDI
    // number). Dividing raw pixel x by whiteWidth produces that white-key index, then
    // comparing/clamping it against real MIDI key numbers (minKey/maxKey) silently
    // capped the drag well short of the right side of the control and could desync the
    // direction of travel from the mouse - this scans the actual candidate boundary
    // positions (same coordinate space BoundaryX/rendering use) and picks whichever is
    // physically closest to the cursor, so the yellow line tracks the mouse exactly
    // across the FULL key range in both directions.
    static int PixelToBoundaryKey(double x, double[] rightX, int minKey, int maxKey)
    {
        int best = minKey;
        double bestDist = double.MaxValue;
        for (int k = minKey; k <= maxKey; k++)
        {
            double boundaryX = rightX[k];
            double dist = Math.Abs(x - boundaryX);
            if (dist < bestDist) { bestDist = dist; best = k; }
        }
        return best;
    }

    // Black keys are hit-tested first (they visually sit on top of the white keys near
    // a boundary), falling back to whichever white key's slot contains x.
    int PixelToKey(double x, double[] leftX, double[] rightX)
    {
        for (int k = 0; k <= 127; k++)
            if (IsBlackKey(k) && x >= leftX[k] && x < rightX[k]) return k;
        for (int k = 0; k <= 127; k++)
            if (!IsBlackKey(k) && x >= leftX[k] && x < rightX[k]) return k;
        return x < 0 ? 0 : 127;
    }

    // The zone whose trigger range [LowKey, HighKey] contains key `k`, or null if k
    // falls outside every range (shouldn't happen for 0..127 with a well-formed zone
    // list, but a drag can end past the control's own edge).
    static KmpZone? ZoneAt(int key, (KmpZone Zone, int LowKey, int HighKey)[] ranges)
    {
        foreach (var (zone, low, high) in ranges)
            if (key >= low && key <= high) return zone;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (whiteWidth, leftX, rightX) = BuildLayout();
        var ranges = ComputeRanges();
        var pos = e.GetPosition(this);
        var x = pos.X;

        if (_dragBoundary >= 0)
        {
            int minKey = _dragBoundary > 0 ? ranges[_dragBoundary - 1].Item3 + 1 : 0;
            int maxKey = _dragBoundary + 1 < ranges.Length ? ranges[_dragBoundary + 1].Item3 - 1 : 126;
            _dragPendingKey = PixelToBoundaryKey(x, rightX, minKey, maxKey);
            InvalidateVisual();
            return;
        }

        if (_dragZoneOrigin != null)
        {
            int key = PixelToKey(x, leftX, rightX);
            var hover = ZoneAt(key, ranges);
            if (!ReferenceEquals(hover, _dragZoneHover))
            {
                _dragZoneHover = hover;
                InvalidateVisual();
            }
            Cursor = Cursors.Hand;
            return;
        }

        // The resize (SizeWE) cursor - and boundary-hover highlighting - only applies
        // in the header/zone-bar strip. Over the piano itself, a boundary's x-position
        // can sit directly under a piano key the user wants to CLICK to select its zone
        // (see OnMouseLeftButtonDown) - showing a resize cursor there, and letting a
        // click start a boundary drag instead of selecting the zone, was exactly the
        // "awkward" selection behavior this fixes: the cursor now only ever changes
        // while actually over the header, and a piano-key click always selects.
        int hover2 = pos.Y < ZoneBarHeight ? HitTestBoundary(rightX, ranges, x) : -1;
        if (hover2 != _hoverBoundary)
        {
            _hoverBoundary = hover2;
            Cursor = hover2 >= 0 ? Cursors.SizeWE : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var (whiteWidth, leftX, rightX) = BuildLayout();
        var ranges = ComputeRanges();
        var pos = e.GetPosition(this);
        var x = pos.X;

        // Boundary drag can only START from the header/zone-bar strip - same "piano
        // clicks always select" reasoning as OnMouseMove above; a piano-key click never
        // starts a resize drag even when x happens to line up with a boundary.
        if (pos.Y < ZoneBarHeight)
        {
            int hit = HitTestBoundary(rightX, ranges, x);
            if (hit >= 0)
            {
                _dragBoundary = hit;
                _dragPendingKey = ranges[hit].Item3;
                CaptureMouse();
                InvalidateVisual();
                return;
            }
        }

        int key = PixelToKey(x, leftX, rightX);
        var hitZone = ZoneAt(key, ranges);
        if (hitZone == null) return;

        // The zone bar (the label strip above the piano) starts a POTENTIAL reorder
        // drag - whether it turns into a reorder or a plain select is decided on
        // mouse-up (see below), by comparing where the drag ENDED against where it
        // started. Piano-key clicks (y >= ZoneBarHeight) still select immediately,
        // unchanged - only the zone bar itself is a drag surface.
        if (pos.Y < ZoneBarHeight)
        {
            _dragZoneOrigin = hitZone;
            _dragZoneHover = hitZone;
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        ZoneClicked?.Invoke(hitZone);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragBoundary >= 0 && _dragPendingKey >= 0)
        {
            var ranges = ComputeRanges();
            var zone = ranges[_dragBoundary].Item1;
            if (_dragPendingKey != zone.TopKey) BoundaryMoved?.Invoke(zone, _dragPendingKey);
        }
        _dragBoundary = -1;
        _dragPendingKey = -1;

        if (_dragZoneOrigin != null)
        {
            var (_, leftX, rightX) = BuildLayout();
            var ranges = ComputeRanges();
            int key = PixelToKey(e.GetPosition(this).X, leftX, rightX);
            var dropZone = ZoneAt(key, ranges);

            // Ended on a DIFFERENT zone than where the drag started -> reorder. Ended
            // back on its own origin (or off the strip entirely) -> treat as the plain
            // click it visually was, same as clicking any other zone.
            if (dropZone != null && !ReferenceEquals(dropZone, _dragZoneOrigin))
                ZoneReordered?.Invoke(_dragZoneOrigin, dropZone);
            else
                ZoneClicked?.Invoke(_dragZoneOrigin);

            _dragZoneOrigin = null;
            _dragZoneHover = null;
        }

        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle((Brush)FindResource("PanelBackgroundBrush"), null, new Rect(0, 0, w, h));

        var (whiteWidth, leftX, rightX) = BuildLayout();
        var ranges = ComputeRanges();
        double pianoTop = ZoneBarHeight;
        double pianoHeight = Math.Max(1, h - ZoneBarHeight);

        // ── Zone assignment bar ──
        if (ranges.Length == 0)
        {
            var text = new FormattedText("No zones", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, (Brush)FindResource("MutedTextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(4, 2));
        }
        else
        {
            var typeface = new Typeface("Segoe UI");
            var textBrush = (Brush)FindResource("TextBrush");
            var skippedBrush = (Brush)FindResource("DisabledTextBrush");
            var selectedBrush = (Brush)FindResource("WaveformSelectionBrush");
            var zoneEvenBrush = (Brush)FindResource("HoverBrush");
            var barBorder = new Pen((Brush)FindResource("DividerBrush"), 1);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int i = 0; i < ranges.Length; i++)
            {
                var (zone, low, high) = ranges[i];
                // x1 = rightX[high], matching BoundaryX exactly - the zone-bar segment's
                // own right edge must line up with the yellow boundary line drawn below
                // it (over the piano), or the two would visibly disagree about where a
                // zone actually ends.
                double x0 = leftX[low], x1 = rightX[high];
                var rect = new Rect(x0, 0, Math.Max(1, x1 - x0), ZoneBarHeight);

                Brush fill = zone.IsSkipped ? skippedBrush : i % 2 == 0 ? zoneEvenBrush : (Brush)FindResource("PanelBackgroundBrush");
                dc.DrawRectangle(fill, barBorder, rect);
                if (ReferenceEquals(zone, SelectedZone))
                    dc.DrawRectangle(selectedBrush, barBorder, rect);

                // Zone-bar reorder-drag feedback: the origin stays outlined for the
                // whole drag (so it's clear what's being moved), and whichever zone the
                // cursor is currently over gets a thicker yellow outline (matching the
                // boundary-drag's own "active = yellow" convention) as the drop target.
                if (_dragZoneOrigin != null && ReferenceEquals(zone, _dragZoneOrigin))
                    dc.DrawRectangle(null, new Pen(Brushes.Yellow, 1), rect);
                if (_dragZoneHover != null && ReferenceEquals(zone, _dragZoneHover) && !ReferenceEquals(zone, _dragZoneOrigin))
                    dc.DrawRectangle(null, new Pen(Brushes.Yellow, 2), rect);

                if (rect.Width > 12)
                {
                    var label = zone.IsSkipped ? "-" : zone.Filename;
                    var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 9, textBrush, dpi) { MaxTextWidth = Math.Max(1, rect.Width - 2), MaxTextHeight = ZoneBarHeight };
                    dc.DrawText(text, new Point(rect.X + 1, 1));
                }
            }
        }

        // ── Piano: white keys first (full height), black keys drawn on top, narrower
        //    and centered on the white-key boundary they fall between ──
        var whiteFill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        var blackFill = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
        var keyBorder = new Pen(Brushes.Black, 0.5);
        whiteFill.Freeze(); blackFill.Freeze(); keyBorder.Freeze();

        for (int key = 0; key <= 127; key++)
        {
            if (IsBlackKey(key)) continue;
            dc.DrawRectangle(whiteFill, keyBorder, new Rect(leftX[key], pianoTop, rightX[key] - leftX[key], pianoHeight));
        }
        double blackHeight = pianoHeight * 0.6;
        for (int key = 0; key <= 127; key++)
        {
            if (!IsBlackKey(key)) continue;
            dc.DrawRectangle(blackFill, null, new Rect(leftX[key], pianoTop, rightX[key] - leftX[key], blackHeight));
        }

        // Faint greyscale highlight over the SELECTED zone's own key range - replaces
        // relying on the permanent boundary lines to show "which keys are in this
        // zone" (those read as clutter running through the whole piano for every
        // zone boundary; a direct fill over the actual keys is much more legible at a
        // glance). The zone bar's own selection highlight above is untouched.
        //
        // Darker/more opaque than the original Color.FromArgb(70, 200, 200, 200) - that
        // combination barely moved a white key's own near-white fill (0xE8E8E8), only
        // about 9 of 255 luminance levels, so it read as basically invisible over white
        // keys specifically (still visible enough over the much-darker black keys,
        // which is why the mismatch wasn't obvious everywhere). This alpha/grey pairing
        // shifts a white key noticeably (~35-40 levels) while still reading as a subtle
        // tint rather than a solid block over the darker black keys.
        if (SelectedZone != null)
        {
            var selectedRange = ranges.FirstOrDefault(r => ReferenceEquals(r.Zone, SelectedZone));
            if (selectedRange.Zone != null)
            {
                double hx0 = leftX[selectedRange.LowKey], hx1 = rightX[selectedRange.HighKey];
                var keyHighlight = new SolidColorBrush(Color.FromArgb(100, 140, 140, 140));
                keyHighlight.Freeze();
                dc.DrawRectangle(keyHighlight, null, new Rect(hx0, pianoTop, Math.Max(1, hx1 - hx0), pianoHeight));
            }
        }

        // C octave labels along the bottom of the white keys.
        if (pianoHeight > 14)
        {
            var typeface = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int key = 0; key <= 127; key += 12)
            {
                var text = new FormattedText(MidiNoteName.ToName(key), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 8, Brushes.Black, dpi);
                double x = leftX[key] + 1;
                if (x + text.Width <= w)
                    dc.DrawText(text, new Point(x, pianoTop + pianoHeight - text.Height - 1));
            }
        }

        // ── Boundary handles - only drawn while active (hover/drag), not as permanent
        //    lines - the always-visible grey divider running the full height of the
        //    piano for EVERY zone boundary was the "confusing border lines" the key
        //    highlight above now replaces for "where does a zone's range fall." The
        //    line itself is still the actual drag handle (hit-tested in HitTestBoundary
        //    regardless of whether it's currently drawn), so resizing still works
        //    exactly the same - only the constant-clutter rest state is gone. ──
        for (int i = 0; i < ranges.Length - 1; i++)
        {
            bool active = _dragBoundary == i || _hoverBoundary == i;
            if (!active) continue;

            double x;
            if (_dragBoundary == i)
            {
                int dragKey = Math.Clamp(_dragPendingKey, 0, 127);
                x = rightX[dragKey];
            }
            else
            {
                x = BoundaryX(rightX, ranges, i);
            }
            dc.DrawLine(new Pen(Brushes.Yellow, 2), new Point(x, 0), new Point(x, h));
        }
    }
}
