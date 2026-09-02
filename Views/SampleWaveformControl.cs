using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

// Min/max-bucketed waveform trace for a loaded KsfSample - a custom FrameworkElement
// (OnRender), not a Canvas full of Line shapes: a multi-hundred-thousand-sample real
// .KSF makes that a real perf problem, not just a style preference. Click-drag
// selection (feeds Crop/Cut/Copy/fade-selection), mouse-wheel zoom, a light vertical
// grid so the zoom level itself is legible, draggable Sample Start/Loop Start/Loop End
// markers (Kronos's own coloring - red/green/blue), and a playhead line during
// playback. This control owns the horizontal view window (ViewStartFrame/
// ViewEndFrame); the ruler footer and horizontal scrollbar next to it in
// SampleEditorWindow.xaml are separate elements kept in sync via ViewChanged/SetView,
// not merged into this one - each is independently simple to get right; a single
// control trying to own the trace, the ruler, and the scrollbar would not be.
public sealed class SampleWaveformControl : FrameworkElement
{
    const double HitTestPixels = 5;

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
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).ClearPreviewSelection()));

    public int SelectionStartFrame
    {
        get => (int)GetValue(SelectionStartFrameProperty);
        set => SetValue(SelectionStartFrameProperty, value);
    }

    public static readonly DependencyProperty SelectionEndFrameProperty =
        DependencyProperty.Register(nameof(SelectionEndFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).ClearPreviewSelection()));

    public int SelectionEndFrame
    {
        get => (int)GetValue(SelectionEndFrameProperty);
        set => SetValue(SelectionEndFrameProperty, value);
    }

    public static readonly DependencyProperty SampleStartFrameProperty =
        DependencyProperty.Register(nameof(SampleStartFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Red marker line (Kronos's own coloring) - the KsfSample.SampleStart field (where
    // real playback begins, skipping any leading silence/pre-roll left in the raw PCM).
    // Draggable: grab within HitTestPixels of the line to move it.
    public int SampleStartFrame
    {
        get => (int)GetValue(SampleStartFrameProperty);
        set => SetValue(SampleStartFrameProperty, value);
    }

    public static readonly DependencyProperty LoopStartFrameProperty =
        DependencyProperty.Register(nameof(LoopStartFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Green marker line (Kronos's own coloring), draggable independently of LoopEndFrame.
    public int LoopStartFrame
    {
        get => (int)GetValue(LoopStartFrameProperty);
        set => SetValue(LoopStartFrameProperty, value);
    }

    public static readonly DependencyProperty LoopEndFrameProperty =
        DependencyProperty.Register(nameof(LoopEndFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Blue marker line (Kronos's own coloring), draggable independently of LoopStartFrame.
    // The region [LoopStartFrame, LoopEndFrame) itself is still shown as a faint fill
    // whenever LoopEndFrame > LoopStartFrame, regardless of the Kronos-side loop-enable
    // flag - this is "where the loop points currently are," not "whether it's active."
    public int LoopEndFrame
    {
        get => (int)GetValue(LoopEndFrameProperty);
        set => SetValue(LoopEndFrameProperty, value);
    }

    public static readonly DependencyProperty PlayheadFrameProperty =
        DependencyProperty.Register(nameof(PlayheadFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    // -1 = hidden (not currently playing). Polled/pushed by the window's VU-meter timer
    // from SamplePlayback.PositionFrame - same "poll, don't marshal an event" discipline
    // used for the VU meter itself.
    public int PlayheadFrame
    {
        get => (int)GetValue(PlayheadFrameProperty);
        set => SetValue(PlayheadFrameProperty, value);
    }

    // Fires once per drag, on mouse-up - code-behind reads SelectionStartFrame/
    // SelectionEndFrame and pushes them into the ViewModel then, not on every
    // intermediate MouseMove.
    public event Action? SelectionChanged;

    // Fires on every intermediate MouseMove during a plain crop-selection drag (not the
    // loop region or a marker edge) - lets the window mirror the live highlight onto the
    // sibling stereo pane AS THE DRAG HAPPENS, not just once at mouse-up. Deliberately
    // separate from SelectionChanged: that one pushes into the ViewModel (which drives
    // RefreshDetailPanels/undo-adjacent bookkeeping) and firing that on every pixel of
    // mouse movement would be wasteful; this one is a pure DP-to-DP mirror the window
    // performs directly on the sibling control.
    public event Action? SelectionPreviewChanged;

    // Fires whenever the zoom/pan view window changes (wheel zoom, double-click reset,
    // or an external SetView call) - lets the ruler footer and horizontal scrollbar
    // stay in sync without polling every frame.
    public event Action? ViewChanged;

    // Fires once, after the loop region is actually MOVED as a whole (a real drag
    // starting INSIDE the region while Loop Lock is on, or an arrow-key nudge while
    // Loop Lock is on) - (newLoopStart, newLoopEnd), both shifted by the same delta. A
    // plain click with no movement does NOT fire this - see OnMouseLeftButtonDown/Up's
    // own comments. Dragging one EDGE independently (not the body) fires MarkerDragged
    // instead - see below.
    public event Action<int, int>? LoopRegionChanged;

    // Fires once, on mouse-up, after dragging the Sample Start line or either loop edge
    // independently - the ViewModel's SetMarker is the single choke point this (and
    // every typed-field edit) routes through, applying Use Zero snapping, Loop Lock
    // length preservation, and the "Loop Start can never precede Sample Start" ordering
    // invariant uniformly regardless of how the edit originated.
    public event Action<SampleMarkerKind, int>? MarkerDragged;

    // Fires on every intermediate MouseMove while dragging the Sample Start line, a
    // single loop edge, or the WHOLE loop region - lets the window mirror the live
    // marker position(s) onto the sibling stereo pane AS THE DRAG HAPPENS, not just
    // once at mouse-up (MarkerDragged/LoopRegionChanged above). Same "live during drag"
    // treatment SelectionPreviewChanged already gives crop-selection dragging - no
    // per-kind variant, since a caller can just re-read SampleStartFrame/LoopStartFrame/
    // LoopEndFrame directly (all three are visible DPs already).
    public event Action? MarkersChanging;

    public static readonly DependencyProperty LoopEnabledProperty =
        DependencyProperty.Register(nameof(LoopEnabled), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // Mirrors the sample's own "Loop Enabled" flag (the Kronos one-shot/loop-off bit) -
    // ALL loop-region interactivity (the fill, the green/blue edge lines, click-to-
    // select, whole-region drag, independent edge drag, arrow-key nudge) is gated on
    // this being true. Most real samples don't loop, and the loop region defaults to
    // spanning [0, frameCount) - with no gate, that swallowed almost every click as "a
    // click inside the loop," making plain crop-selection (drag to select) impossible
    // and the "selected" green fill impossible to clear (nowhere outside the loop to
    // click). When false, the waveform behaves as pure crop-selection only; Sample
    // Start (unrelated to looping) stays visible/draggable either way.
    public bool LoopEnabled
    {
        get => (bool)GetValue(LoopEnabledProperty);
        set => SetValue(LoopEnabledProperty, value);
    }

    public static readonly DependencyProperty LoopLockEnabledProperty =
        DependencyProperty.Register(nameof(LoopLockEnabled), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false));

    // Mirrors the sample's own "Loop Lock" checkbox. Whole-region drag (click inside the
    // loop body, not on an edge, to move BOTH Loop Start and Loop End together) is gated
    // on this being true - with Loop Lock off, a click/drag starting inside the loop
    // falls straight through to a normal crop-selection drag instead, same as clicking
    // anywhere else on the waveform. Not needed for dragging a single edge independently
    // (SampleMarkerKind.LoopStart/LoopEnd) - those stay available either way.
    public bool LoopLockEnabled
    {
        get => (bool)GetValue(LoopLockEnabledProperty);
        set => SetValue(LoopLockEnabledProperty, value);
    }

    public static readonly DependencyProperty MoveToolActiveProperty =
        DependencyProperty.Register(nameof(MoveToolActive), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false));

    // Local Edits' Select/Move toggle (SampleEditorWindow.xaml). Select (default,
    // false): a plain click-drag on empty waveform starts a new crop selection - today's
    // only behavior, entirely unchanged. Move (true): a plain click-drag instead
    // relocates whatever it starts on - an existing selection, the loop region (without
    // needing LoopLockEnabled - Move IS the "reposition the loop" gesture now, LoopLock
    // keeps its own separate "preserve loop length while dragging an EDGE" meaning), or
    // failing either of those, the whole waveform itself (only when CanMoveWaveform - see
    // its own comment). Marker-edge dragging (Sample Start/Loop Start/Loop End lines) is
    // unaffected by this - grabbing a line always resizes/repositions that one marker
    // regardless of mode, same as before modes existed.
    public bool MoveToolActive
    {
        get => (bool)GetValue(MoveToolActiveProperty);
        set => SetValue(MoveToolActiveProperty, value);
    }

    public static readonly DependencyProperty ScrollToZoomProperty =
        DependencyProperty.Register(nameof(ScrollToZoom), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(true));

    // The window's own "Scroll to Zoom" checkbox (SampleEditorWindow.xaml, checked by
    // default - today's only behavior). Unchecked: OnMouseWheel below leaves the event
    // unhandled instead of zooming, so it bubbles up exactly like scrolling anywhere
    // OUTSIDE this control already does - a plain vertical scroll of the outer pane, not
    // a zoom or a horizontal pan of the waveform's own view.
    public bool ScrollToZoom
    {
        get => (bool)GetValue(ScrollToZoomProperty);
        set => SetValue(ScrollToZoomProperty, value);
    }

    public static readonly DependencyProperty CanMoveWaveformProperty =
        DependencyProperty.Register(nameof(CanMoveWaveform), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false));

    // Whole-waveform dragging only means anything with a stereo partner to offset
    // against (SampleEditorViewModel.ApplyChannelMove requires HasStereoPair && SplitLR
    // and no-ops otherwise) - the window sets this to HasStereoPair && SplitLR. When
    // false, a Move-mode drag that starts on bare waveform (no selection, no loop under
    // the cursor) falls through to an ordinary crop-selection drag instead of doing
    // nothing - Move's toggle state shouldn't make the waveform stop responding to clicks
    // just because there's nothing for IT specifically to move right now.
    public bool CanMoveWaveform
    {
        get => (bool)GetValue(CanMoveWaveformProperty);
        set => SetValue(CanMoveWaveformProperty, value);
    }

    // Fires once, on mouse-up, after a whole-waveform Move drag actually moved
    // (deltaFrames, +later/-earlier) - see SampleEditorViewModel.ApplyChannelMove for
    // what this does to the underlying PCM. Never fires for a plain click (no movement)
    // or when CanMoveWaveform is false.
    public event Action<int>? WaveformMoved;

    public static readonly DependencyProperty IsSplitChannelPaneProperty =
        DependencyProperty.Register(nameof(IsSplitChannelPane), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false));

    // Set by the window (RefreshDetailPanels) to HasStereoPair && SplitLR - true whenever
    // this pane represents one half of an actively Split-edited stereo pair. Gates
    // ChannelDoubleClicked below: a double-click outside the loop region only means
    // "pick a channel" in that context - for a mono sample, or in Combine (where both
    // channels always mirror together and there's nothing to pick), double-click keeps
    // its ordinary "reset zoom to fit" meaning.
    public bool IsSplitChannelPane
    {
        get => (bool)GetValue(IsSplitChannelPaneProperty);
        set => SetValue(IsSplitChannelPaneProperty, value);
    }

    public static readonly DependencyProperty IsActiveChannelProperty =
        DependencyProperty.Register(nameof(IsActiveChannel), typeof(bool), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // Set by the window (RefreshDetailPanels) - true when THIS pane is (one of) the
    // channel(s) edits currently target: the sole active side, or either side while Both
    // is active. Purely a rendering cue (see OnRender's accent border) - has no bearing
    // on hit-testing/gestures, only on what the user sees.
    public bool IsActiveChannel
    {
        get => (bool)GetValue(IsActiveChannelProperty);
        set => SetValue(IsActiveChannelProperty, value);
    }

    // Fires on a double-click outside the loop region (that still takes priority - see
    // OnMouseLeftButtonDown) while IsSplitChannelPane is true - the window
    // (OnWaveformLeftChannelDoubleClicked/OnWaveformRightChannelDoubleClicked) owns what
    // "select a channel" actually means (ActivateSplitChannel/ActivateBothSplitChannels),
    // this control only reports the gesture.
    public event Action? ChannelDoubleClicked;


    public static readonly DependencyProperty ScrubFrameProperty =
        DependencyProperty.Register(nameof(ScrubFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    // -1 = hidden. A plain click (no drag) anywhere on the waveform sets this to the
    // clicked frame and fires ScrubRequested - a simple grey line marking "play starts
    // here," separate from PlayheadFrame (the white line tracking the LIVE position
    // once playback is actually running - the two coincide right as playback starts and
    // then diverge as the white line moves on).
    public int ScrubFrame
    {
        get => (int)GetValue(ScrubFrameProperty);
        set => SetValue(ScrubFrameProperty, value);
    }

    // Fires once, on mouse-up, after a plain click (no movement) anywhere on the
    // waveform OUTSIDE a marker/loop-region hit - (frame). Playback and the grey line
    // itself are the window's responsibility (ScrubFrame is set directly, mirrored to
    // the sibling stereo pane the same way other markers are); this event exists so the
    // window can also kick off SamplePlayback.PlayFrom at that frame.
    public event Action<int>? ScrubRequested;

    // The visible frame window - ViewEndFrame == 0 means "not zoomed, show everything"
    // (can't use -1-as-sentinel with an int DP's 0 default doing double duty, so 0 end
    // specifically means unset here; a real 0-length zoom window is meaningless anyway).
    int _viewStart, _viewEnd;
    int _dragAnchorFrame = -1;
    int _lastFrameCount = -1;
    bool _dragMoved;

    // Live crop-selection drag preview - separate from the committed SelectionStartFrame/
    // SelectionEndFrame DPs, which are now written ONCE at mouse-up instead of on every
    // MouseMove (continuous DP writes - doubled in stereo Combine mode, which mirrors
    // them onto the sibling pane too - made drag-selecting feel laggy on a real
    // multi-minute sample). null means "no drag in progress, draw
    // the committed selection"; OnRender and the mirrored sibling both read through
    // EffectiveSelectionStart/End rather than caring which one is live. Both DPs' own
    // registration clears this on ANY write, so a preview can never go stale and shadow
    // a later real commit made through some other path (e.g. Select All, Zoom, a reload).
    int? _previewSelStart, _previewSelEnd;

    // Loop-region (whole-region) drag state - set when a mouse-down lands inside
    // [LoopStartFrame, LoopEndFrame) but not on either edge - separate from
    // _dragAnchorFrame's own crop-selection drag and _draggingMarker's own edge drag so
    // the three interactions can never be confused with each other.
    bool _draggingLoop;
    bool _loopDragMoved;
    int _loopDragAnchorFrame, _loopDragStartAtAnchor, _loopDragEndAtAnchor;

    // Single-marker (Sample Start line, or one loop edge independently) drag state.
    SampleMarkerKind? _draggingMarker;

    // Move tool's "drag an existing selection" state - same anchor/at-anchor shape as
    // the loop whole-region drag above, kept separate so the two can't be confused with
    // each other (a selection and a loop region can overlap on screen). Live feedback
    // reuses SetPreviewSelection/_previewSelStart-End (the same preview channel a normal
    // crop-selection drag uses) rather than writing the committed DPs on every
    // MouseMove, for the same perf reason SelectionPreviewChanged's own comment gives.
    bool _movingSelection;
    bool _selMoveMoved;
    int _selMoveAnchorFrame, _selMoveStartAtAnchor, _selMoveEndAtAnchor;

    // Move tool's "drag the whole waveform" state (CanMoveWaveform only). Live feedback
    // is a pure RenderTransform translate - correct for free (everything currently drawn,
    // trace and markers alike, slides together exactly as it will once the real PCM
    // shift + marker shift commit) and O(1) per MouseMove, unlike the trace geometry
    // itself which is O(view length) to rebuild. Committed by converting the total pixel
    // delta back to frames on mouse-up and firing WaveformMoved once.
    bool _movingWaveform;
    // Local coordinates, captured with the transform at X=0 - see the mouse-down/
    // mouse-move sites' own comments for how this stays stable once the transform
    // starts moving (PointToScreen was tried and reverted: it returns DEVICE pixels
    // while _waveMoveTransform.X is DIPs, so at any display scaling other than 100%
    // the waveform would slide faster/slower than the cursor).
    double _waveMoveAnchorX;
    readonly TranslateTransform _waveMoveTransform = new();

    // External pair-timeline override for the VIEW WINDOW only (SetView/wheel-zoom/
    // double-click-reset bounds) - NOT for rendering or hit-testing, which still use
    // this pane's own real FrameCount. The window sets this to max(left, right) frame
    // count on BOTH panes whenever they're part of a stereo pair (SampleEditorWindow.
    // xaml.cs's RefreshDetailPanels), because Split L/R's Move tool lets the two
    // channels' buffers end up different lengths. Without this, SyncWaveformViews'
    // shared numeric view window (both panes forced to the SAME [ViewStart,ViewEnd)
    // frame numbers, so an offset between them renders as a real pixel offset) gets
    // clamped down to whichever pane is SHORTER on every SetView call - the two panes
    // would silently end up at different zoom levels instead of showing the offset at
    // all. 0 (default) means "no override - just use this pane's own FrameCount", so a
    // mono pane, or one not part of a pair, behaves exactly as before.
    public int ViewFrameCount { get; set; }

    int ViewSpan => Math.Max(FrameCount, ViewFrameCount);

    // Read by SyncWaveformViews for the shared horizontal scrollbar's range - must span
    // the LONGER pane or zoomed scrolling can never reach the end of it.
    public int ViewSpanFrameCount => ViewSpan;

    public int ViewStartFrame => _viewStart;
    public int ViewEndFrame => _viewEnd == 0 ? ViewSpan : _viewEnd;

    public SampleWaveformControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // KsfSample.Samples() decodes a brand-new array from the underlying big-endian
    // bytes on EVERY call - so any field-only edit (a marker drag, a loop-region move,
    // toggling Loop Enabled) that re-reads and reassigns this DP produces a new array
    // reference even though the PCM content/length is completely unchanged. Resetting
    // pan/zoom on every such reassignment (the old behavior) meant dragging the loop
    // region snapped the view back to "show everything" on every single move - only
    // reset when the frame count actually changed, which is what genuinely
    // distinguishes "a different sample got loaded" (or a length-changing edit like
    // crop) from "the same sample got re-decoded."
    void OnSamplesChanged()
    {
        int newCount = Samples?.Length ?? 0;
        if (newCount != _lastFrameCount)
        {
            _viewStart = 0;
            _viewEnd = ViewSpan; // full PAIR timeline, not just this pane's own length - see ViewFrameCount's own comment
            ScrubFrame = -1;
        }
        else
        {
            _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, ViewSpan - 1));
            _viewEnd = Math.Clamp(_viewEnd, _viewStart + 1, ViewSpan);
        }
        _lastFrameCount = newCount;
        ViewChanged?.Invoke();
    }

    int FrameCount => Samples?.Length ?? 0;

    // External callers (the horizontal scrollbar) drive the view window through here,
    // rather than reaching into private zoom state directly.
    public void SetView(int start, int end)
    {
        int len = Math.Clamp(end - start, 1, Math.Max(1, ViewSpan));
        start = Math.Clamp(start, 0, Math.Max(0, ViewSpan - len));
        _viewStart = start;
        _viewEnd = start + len;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    int PixelToFrame(double x)
    {
        var w = ActualWidth;
        if (w <= 0 || FrameCount == 0) return 0;
        int viewLen = Math.Max(1, ViewEndFrame - _viewStart);
        int frame = _viewStart + (int)(x / w * viewLen);
        return Math.Clamp(frame, 0, FrameCount);
    }

    double FrameToPixel(int frame)
    {
        var w = ActualWidth;
        int viewLen = Math.Max(1, ViewEndFrame - _viewStart);
        return (frame - _viewStart) * w / viewLen;
    }

    bool InLoopRegion(int frame) => HasLoop && frame >= LoopStartFrame && frame < LoopEndFrame;
    // Loop-region interactivity (fill, edges, drag, click-select, nudge) all key off
    // this - see LoopEnabled's own comment for why. LoopEndFrame > LoopStartFrame alone
    // isn't enough; the sample's own Loop Enabled flag must also be on.
    bool HasLoop => LoopEnabled && LoopEndFrame > LoopStartFrame;

    bool NearPixel(double x, int frame) => Math.Abs(x - FrameToPixel(frame)) <= HitTestPixels;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (FrameCount == 0) return;
        Focus();

        double x = e.GetPosition(this).X;
        int clickFrame = PixelToFrame(x);

        // Double-click resets to the full-sample view - the only "un-zoom" affordance;
        // deliberately not a button, matching how the equivalent gesture works in most
        // waveform editors. FrameworkElement has no OnMouseDoubleClick override (that's
        // Control-only), so ClickCount is checked here instead. Double-clicking INSIDE
        // the loop region is the one exception - explicit request: it selects the loop
        // (matching Loop Selected Area's own [LoopStart, LoopEnd) range) instead,
        // regardless of Select/Move tool - unlike the single-click "grab the whole loop
        // to drag" gesture below (Move tool only), this is a selection, not a move, so
        // there's no reason to gate it on which tool is active.
        if (e.ClickCount == 2)
        {
            if (HasLoop && InLoopRegion(clickFrame))
            {
                ClearPreviewSelection();
                SelectionStartFrame = LoopStartFrame;
                SelectionEndFrame = LoopEndFrame;
                SelectionChanged?.Invoke();
                return;
            }

            // Split L/R: outside the loop region, double-click picks a channel instead
            // of resetting zoom - explicit request. Click 1 of this same gesture already
            // ran through the window's ordinary single-click channel-activation
            // (OnWaveformPaneScrubRequested), so by now this pane is already the sole
            // active channel; ChannelDoubleClicked's only remaining job (window-side) is
            // deciding what a SECOND click on the already-active pane means.
            if (IsSplitChannelPane)
            {
                ChannelDoubleClicked?.Invoke();
                return;
            }

            _viewStart = 0;
            _viewEnd = ViewSpan;
            InvalidateVisual();
            ViewChanged?.Invoke();
            return;
        }

        // Marker edges take priority over the loop-region body / crop-selection -
        // checked in the same visual order they're drawn (Sample Start on top, then the
        // loop edges), so overlapping hits resolve the same way the eye reads them.
        if (NearPixel(x, SampleStartFrame))
        {
            _draggingMarker = SampleMarkerKind.SampleStart;
            CaptureMouse();
            InvalidateVisual();
            return;
        }
        if (HasLoop && NearPixel(x, LoopStartFrame))
        {
            _draggingMarker = SampleMarkerKind.LoopStart;
            CaptureMouse();
            InvalidateVisual();
            return;
        }
        if (HasLoop && NearPixel(x, LoopEndFrame))
        {
            _draggingMarker = SampleMarkerKind.LoopEnd;
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        // A click starting INSIDE the current loop region (but not on either edge) grabs
        // the WHOLE region for dragging instead of starting a new crop selection there -
        // Move mode ONLY (explicit feedback: Select mode must never move anything, only
        // ever highlight/select - Loop Lock no longer doubles as an implicit "move the
        // loop" trigger the way it used to; Loop Lock's own meaning is unchanged
        // elsewhere, it still links Start/End length when dragging a single EDGE). Also
        // excluded when the loop already spans the entire sample (LoopEndFrame -
        // LoopStartFrame >= FrameCount): the drag-to-move below clamps newStart to
        // [0, FrameCount - len], which is always exactly 0 in that case - the region can
        // never actually move, so a click there could never do anything useful anyway.
        // Either way, falling through to the normal crop-selection start below.
        bool loopMovable = MoveToolActive && InLoopRegion(clickFrame) && LoopEndFrame - LoopStartFrame < FrameCount;
        if (loopMovable)
        {
            _draggingLoop = true;
            _loopDragMoved = false;
            _loopDragAnchorFrame = clickFrame;
            _loopDragStartAtAnchor = LoopStartFrame;
            _loopDragEndAtAnchor = LoopEndFrame;
            CaptureMouse();
            return;
        }

        if (MoveToolActive)
        {
            // A click starting inside the CURRENT selection grabs the whole highlight to
            // relocate it - selection only, the underlying waveform is untouched (see
            // ApplyChannelMove's own comment for why that's a separate gesture below).
            bool hasSelection = SelectionEndFrame > SelectionStartFrame;
            if (hasSelection && clickFrame >= SelectionStartFrame && clickFrame < SelectionEndFrame)
            {
                _movingSelection = true;
                _selMoveMoved = false;
                _selMoveAnchorFrame = clickFrame;
                _selMoveStartAtAnchor = SelectionStartFrame;
                _selMoveEndAtAnchor = SelectionEndFrame;
                CaptureMouse();
                return;
            }

            if (CanMoveWaveform && FrameCount > 0)
            {
                _movingWaveform = true;
                // Transform is reset to 0 BEFORE the anchor is read, so x here is a
                // genuine untransformed local position - see OnMouseMove for how every
                // later reading stays comparable to this one even once the transform
                // moves away from 0.
                _waveMoveTransform.X = 0;
                _waveMoveAnchorX = x;
                RenderTransform = _waveMoveTransform;
                CaptureMouse();
                return;
            }

            // Move mode but nothing here to move (no selection under the cursor, and
            // either not split or already dragged past what CanMoveWaveform allows) -
            // fall through to an ordinary crop-selection drag rather than absorbing the
            // click and doing nothing.
        }

        _dragAnchorFrame = clickFrame;
        _dragMoved = false;
        SetPreviewSelection(_dragAnchorFrame, _dragAnchorFrame);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_draggingMarker is { } marker)
        {
            int frame = PixelToFrame(e.GetPosition(this).X);
            switch (marker)
            {
                case SampleMarkerKind.SampleStart: SampleStartFrame = frame; break;
                case SampleMarkerKind.LoopStart: LoopStartFrame = Math.Min(frame, LoopEndFrame); break;
                case SampleMarkerKind.LoopEnd: LoopEndFrame = Math.Max(frame, LoopStartFrame); break;
            }
            InvalidateVisual();
            MarkersChanging?.Invoke();
            return;
        }

        if (_draggingLoop)
        {
            int frame = PixelToFrame(e.GetPosition(this).X);
            int delta = frame - _loopDragAnchorFrame;
            if (delta != 0) _loopDragMoved = true;
            int len = _loopDragEndAtAnchor - _loopDragStartAtAnchor;
            int newStart = Math.Clamp(_loopDragStartAtAnchor + delta, 0, Math.Max(0, FrameCount - len));
            LoopStartFrame = newStart;
            LoopEndFrame = newStart + len;
            InvalidateVisual();
            MarkersChanging?.Invoke();
            return;
        }

        if (_movingSelection)
        {
            int frame = PixelToFrame(e.GetPosition(this).X);
            int delta = frame - _selMoveAnchorFrame;
            if (delta != 0) _selMoveMoved = true;
            int len = _selMoveEndAtAnchor - _selMoveStartAtAnchor;
            int newStart = Math.Clamp(_selMoveStartAtAnchor + delta, 0, Math.Max(0, FrameCount - len));
            SetPreviewSelection(newStart, newStart + len);
            SelectionPreviewChanged?.Invoke();
            return;
        }

        if (_movingWaveform)
        {
            // e.GetPosition(this) is relative to this control's OWN post-transform
            // layout, so as _waveMoveTransform.X moves, the raw reading here silently
            // becomes "true position minus the transform we're also writing" - adding
            // the transform's CURRENT value back cancels that out and recovers a
            // stable untransformed X directly comparable to the anchor, with no
            // feedback loop and no coordinate-space mismatch (still DIPs throughout,
            // unlike the PointToScreen version this replaced, which mixed device
            // pixels into a DIP-valued transform and drifted at non-100% scaling).
            double stableLocalX = e.GetPosition(this).X + _waveMoveTransform.X;
            _waveMoveTransform.X = stableLocalX - _waveMoveAnchorX;
            return;
        }

        if (_dragAnchorFrame < 0)
        {
            // Hover-only: a resize cursor over any marker line, a grab hand over the
            // loop region body (or, in Move mode, the current selection, or the bare
            // waveform when CanMoveWaveform) - matching whichever drag interaction is
            // actually available at that point (see OnMouseLeftButtonDown's own comment
            // for the same conditions).
            double xx = e.GetPosition(this).X;
            int hoverFrame = PixelToFrame(xx);
            bool loopGrabbable = MoveToolActive && InLoopRegion(hoverFrame) && LoopEndFrame - LoopStartFrame < FrameCount;
            bool selectionGrabbable = MoveToolActive && SelectionEndFrame > SelectionStartFrame
                && hoverFrame >= SelectionStartFrame && hoverFrame < SelectionEndFrame;
            if (NearPixel(xx, SampleStartFrame) || (HasLoop && (NearPixel(xx, LoopStartFrame) || NearPixel(xx, LoopEndFrame))))
                Cursor = Cursors.SizeWE;
            // SizeAll (4 small arrows, one per cardinal direction) for every Move-mode
            // grab (loop region, selection, or the bare waveform) - explicit request:
            // Hand read as a generic "clickable" cursor, not specifically "this can be
            // relocated," and previously only the bare-waveform drag used SizeAll.
            else if (loopGrabbable || selectionGrabbable || (MoveToolActive && CanMoveWaveform))
                Cursor = Cursors.SizeAll;
            else
                Cursor = null;
            return;
        }

        int selFrame = PixelToFrame(e.GetPosition(this).X);
        if (selFrame != _dragAnchorFrame) _dragMoved = true;
        SetPreviewSelection(Math.Min(_dragAnchorFrame, selFrame), Math.Max(_dragAnchorFrame, selFrame));
        SelectionPreviewChanged?.Invoke();
    }

    // Sets the live drag-preview rectangle without touching the committed
    // SelectionStartFrame/SelectionEndFrame DPs - see _previewSelStart's own comment for
    // why. Used both for this control's own drag and (via MirrorSelectionPreview in
    // SampleEditorWindow) to puppet the sibling stereo pane's rubber band from here,
    // cheaper than round-tripping through that pane's own DPs.
    public void SetPreviewSelection(int start, int end)
    {
        _previewSelStart = start;
        _previewSelEnd = end;
        InvalidateVisual();
    }

    public void ClearPreviewSelection()
    {
        if (_previewSelStart == null) return;
        _previewSelStart = null;
        _previewSelEnd = null;
        InvalidateVisual();
    }

    // What OnRender and MirrorSelectionPreview should actually draw/mirror right now -
    // the live preview while a drag is in progress, the committed selection otherwise.
    public int EffectiveSelectionStart => _previewSelStart ?? SelectionStartFrame;
    public int EffectiveSelectionEnd => _previewSelEnd ?? SelectionEndFrame;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_draggingMarker is { } marker)
        {
            _draggingMarker = null;
            ReleaseMouseCapture();
            int value = marker switch
            {
                SampleMarkerKind.SampleStart => SampleStartFrame,
                SampleMarkerKind.LoopStart => LoopStartFrame,
                _ => LoopEndFrame,
            };
            MarkerDragged?.Invoke(marker, value);
            return;
        }

        if (_draggingLoop)
        {
            _draggingLoop = false;
            ReleaseMouseCapture();
            if (_loopDragMoved)
            {
                LoopRegionChanged?.Invoke(LoopStartFrame, LoopEndFrame);
            }
            else
            {
                // A plain click (no movement) inside a Loop-Locked region used to toggle
                // a separate green "selected" highlight - removed as unnecessary now
                // that plain crop-selection dragging works everywhere, loop region
                // included (see OnMouseLeftButtonDown). Falls back to the same "play
                // from here" scrub-click every other plain click on the waveform does.
                ScrubFrame = _loopDragAnchorFrame;
                ScrubRequested?.Invoke(_loopDragAnchorFrame);
            }
            InvalidateVisual();
            return;
        }

        if (_movingSelection)
        {
            _movingSelection = false;
            ReleaseMouseCapture();
            if (_selMoveMoved)
            {
                int movedStart = _previewSelStart!.Value;
                int movedEnd = _previewSelEnd!.Value;
                SelectionStartFrame = movedStart;
                SelectionEndFrame = movedEnd;
                SelectionChanged?.Invoke();
            }
            else
            {
                ClearPreviewSelection();
                ScrubFrame = _selMoveAnchorFrame;
                ScrubRequested?.Invoke(_selMoveAnchorFrame);
            }
            InvalidateVisual();
            return;
        }

        if (_movingWaveform)
        {
            _movingWaveform = false;
            ReleaseMouseCapture();
            double pixelDelta = _waveMoveTransform.X;
            _waveMoveTransform.X = 0;
            RenderTransform = Transform.Identity;
            if (pixelDelta != 0)
            {
                int viewLen = Math.Max(1, ViewEndFrame - _viewStart);
                int frameDelta = (int)Math.Round(pixelDelta * viewLen / Math.Max(1, ActualWidth));
                if (frameDelta != 0) WaveformMoved?.Invoke(frameDelta);
            }
            InvalidateVisual();
            return;
        }

        if (_dragAnchorFrame < 0) return;
        int clickedFrame = _dragAnchorFrame;
        _dragAnchorFrame = -1;
        ReleaseMouseCapture();

        if (!_dragMoved)
        {
            // A plain click, not a drag - drops any EXISTING highlight (a prior drag's
            // committed selection) rather than leaving it stuck on screen, then treats
            // the click as "play from here" the same as before. SelectionEndFrame >
            // SelectionStartFrame is this control's own established "there is a real
            // selection" convention (Zoom to Selection/Loop Selected Area/the info text
            // all key off it) - collapsing both to the same frame reads as "nothing
            // selected" everywhere that already checks it, with no separate "cleared"
            // state needed. Only fires SelectionChanged when there WAS something to
            // clear, so a click with no prior selection doesn't push a no-op update.
            ClearPreviewSelection();
            bool hadSelection = SelectionEndFrame > SelectionStartFrame;
            SelectionStartFrame = clickedFrame;
            SelectionEndFrame = clickedFrame;
            if (hadSelection) SelectionChanged?.Invoke();
            ScrubFrame = clickedFrame;
            ScrubRequested?.Invoke(clickedFrame);
            return;
        }

        // Commit once, at mouse-up - this is the one point the real DPs (and everything
        // downstream: the VM push on SelectionChanged, the sibling pane's own commit)
        // change for a crop-selection drag. Snapshot BOTH values before writing either
        // DP - writing the first one fires its PropertyChangedCallback, which clears
        // BOTH preview fields (see their own registration), so reading _previewSelEnd
        // AFTER already having written SelectionStartFrame would see it already nulled.
        int newStart = _previewSelStart!.Value;
        int newEnd = _previewSelEnd!.Value;
        SelectionStartFrame = newStart;
        SelectionEndFrame = newEnd;
        SelectionChanged?.Invoke();
    }

    // Left/Right nudge the loop region by one frame - gated on LoopLockEnabled, same
    // condition the mouse whole-region drag itself uses (nudging and dragging are the
    // same underlying action - repositioning the loop - so they share one gate).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+A is handled window-wide (SampleEditorWindow.OnWindowPreviewKeyDown),
        // not here - this control only has keyboard focus after a click lands on IT
        // specifically, so a control-local handler silently did nothing whenever focus
        // was anywhere else (a button, the tree, ...) when Ctrl+A was pressed, exactly
        // like Ctrl+Z/Ctrl+Y already needed to be window-level rather than per-control.

        if (!LoopLockEnabled || !HasLoop) return;
        if (e.Key != Key.Left && e.Key != Key.Right) return;

        int delta = e.Key == Key.Right ? 1 : -1;
        int len = LoopEndFrame - LoopStartFrame;
        int newStart = Math.Clamp(LoopStartFrame + delta, 0, Math.Max(0, FrameCount - len));
        if (newStart == LoopStartFrame) return; // already at an edge
        LoopStartFrame = newStart;
        LoopEndFrame = newStart + len;
        InvalidateVisual();
        LoopRegionChanged?.Invoke(LoopStartFrame, LoopEndFrame);
        e.Handled = true;
    }

    // A right-click on/near the current selection re-purposes the click as "keep this
    // selection, just open the context menu" rather than collapsing it to a new
    // zero-width selection at the click point - the context menu's Cut/Copy/Fade/Loop
    // items all act on the selection, so losing it on right-click would be actively
    // hostile to the exact workflow the menu exists for. A right-click OUTSIDE the
    // current selection still moves it (to a zero-width point at the click), matching
    // how right-click behaves in most editors when nothing is selected there yet.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (FrameCount == 0) return;
        int frame = PixelToFrame(e.GetPosition(this).X);
        if (frame >= SelectionStartFrame && frame < SelectionEndFrame) return;
        SelectionStartFrame = frame;
        SelectionEndFrame = frame;
        SelectionChanged?.Invoke();
    }

    // Zooms toward whatever frame is under the cursor, clamped to [0, FrameCount] and
    // to a minimum visible window of one frame per pixel - zooming in further than that
    // can't show any more detail (and the trace's own per-pixel bucketing in OnRender
    // stops being meaningful below it), so this is the hard floor, not an arbitrary
    // frame count.
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!ScrollToZoom || FrameCount == 0) return;
        e.Handled = true;

        int viewLen = ViewEndFrame - _viewStart;
        int cursorFrame = PixelToFrame(e.GetPosition(this).X);
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        int minLen = Math.Max(1, (int)ActualWidth);
        int newLen = Math.Clamp((int)(viewLen * factor), Math.Min(minLen, ViewSpan), ViewSpan);

        double t = viewLen == 0 ? 0.5 : (double)(cursorFrame - _viewStart) / viewLen;
        int newStart = Math.Clamp(cursorFrame - (int)(newLen * t), 0, ViewSpan - newLen);
        _viewStart = newStart;
        _viewEnd = newStart + newLen;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    // Picks a "nice" frame interval (1/2/5 * 10^n) for vertical gridlines that lands
    // roughly `targetPixels` apart on screen at the current zoom - the standard graph-
    // paper-axis algorithm, not anything specific to audio.
    static int NiceInterval(double framesPerPixel, double targetPixels)
    {
        double rough = framesPerPixel * targetPixels;
        if (rough < 1) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double residual = rough / magnitude;
        double niceResidual = residual < 1.5 ? 1 : residual < 3.5 ? 2 : residual < 7.5 ? 5 : 10;
        return Math.Max(1, (int)(niceResidual * magnitude));
    }

    // Cache for the trace's min/max-per-pixel-column geometry - the one genuinely
    // O(viewLen) piece of OnRender (every other draw call here is O(width) or O(1)).
    // PlayheadFrame updates 25x/sec during
    // playback (the VU-meter timer, SampleEditorWindow.xaml.cs) via a DP with
    // AffectsRender, so OnRender was re-running this full bucketing pass over the
    // ENTIRE visible sample on every tick even though only the playhead line itself
    // moved - for a real multi-minute 44.1kHz sample (millions of frames) at 25fps,
    // that's tens of millions of redundant array reads per second. Rebuilt only when
    // the inputs that actually change the trace's SHAPE change (sample content
    // reference, view window, or control size) - a pure marker/playhead move reuses
    // the cached geometry untouched.
    short[]? _cachedTraceSamples;
    int _cachedTraceViewStart = -1, _cachedTraceViewEnd = -1;
    double _cachedTraceWidth = -1, _cachedTraceHeight = -1;
    StreamGeometry? _cachedTraceGeometry;

    StreamGeometry GetOrBuildTraceGeometry(short[] samples, int viewStart, int viewEnd, int viewLen, double w, double h)
    {
        if (_cachedTraceGeometry != null && ReferenceEquals(_cachedTraceSamples, samples)
            && _cachedTraceViewStart == viewStart && _cachedTraceViewEnd == viewEnd
            && _cachedTraceWidth == w && _cachedTraceHeight == h)
        {
            return _cachedTraceGeometry;
        }

        double midY = h / 2;
        double yScale = h / 2 / 32768.0;
        // One min/max bucket PER PIXEL COLUMN, mapped with the exact same
        // viewStart/viewLen/width arithmetic FrameToPixel uses - not a separately
        // derived bucket count. A mismatch here (bucket count computed from control
        // width, but the loop only running for as many buckets as there are frames in
        // the view) is what caused the trace to render squeezed into a sliver at the
        // left edge once zoomed in past ~one frame per pixel; per-pixel-column mapping
        // can't drift out of sync with FrameToPixel because it's the same formula.
        int pixelCount = Math.Max(1, (int)w);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int px = 0; px < pixelCount; px++)
            {
                int start = viewStart + (int)((long)px * viewLen / pixelCount);
                if (start >= viewEnd) break;
                int end = viewStart + (int)((long)(px + 1) * viewLen / pixelCount);
                end = Math.Clamp(end, start + 1, viewEnd);

                // viewStart/viewEnd can now extend past THIS pane's own samples.Length
                // (they're clamped to the pair-wide ViewSpan, not this buffer) - a
                // column entirely beyond real data has nothing to bucket, so it's left
                // undrawn (correctly blank) rather than indexing past the array.
                if (start >= samples.Length) continue;
                int readEnd = Math.Min(end, samples.Length);
                if (readEnd <= start) continue;

                short min = short.MaxValue, max = short.MinValue;
                for (int i = start; i < readEnd; i++)
                {
                    if (samples[i] < min) min = samples[i];
                    if (samples[i] > max) max = samples[i];
                }

                double yTop = midY - max * yScale;
                double yBot = midY - min * yScale;
                ctx.BeginFigure(new Point(px, yTop), false, false);
                ctx.LineTo(new Point(px, yBot), true, false);
            }
        }
        geometry.Freeze();

        _cachedTraceSamples = samples;
        _cachedTraceViewStart = viewStart;
        _cachedTraceViewEnd = viewEnd;
        _cachedTraceWidth = w;
        _cachedTraceHeight = h;
        _cachedTraceGeometry = geometry;
        return geometry;
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

        // Clamped against ViewSpan (the pair-wide timeline), NOT samples.Length (this
        // pane's own buffer) - matching FrameToPixel's own math exactly. Clamping to
        // samples.Length here made the untouched pane in a Split pair compute its grid
        // interval/spacing from ITS OWN shorter length after the other channel grew via
        // a Move, while every marker/grid line was still POSITIONED with FrameToPixel's
        // full-pair math - two different notions of "how many frames across this view"
        // in the same frame, which is what left the untouched pane's grid stale/wrong
        // instead of resizing to match. GetOrBuildTraceGeometry is told separately (via
        // samples.Length inside it) where THIS pane's real data actually ends, so it
        // still never reads past its own buffer.
        int viewStart = Math.Clamp(_viewStart, 0, ViewSpan);
        int viewEnd = Math.Clamp(ViewEndFrame, viewStart + 1, ViewSpan);
        int viewLen = viewEnd - viewStart;

        // Vertical zoom grid, under everything else.
        var gridPen = new Pen((Brush)FindResource("WaveformGridLineBrush"), 1);
        int interval = NiceInterval((double)viewLen / w, 90);
        int firstGridFrame = ((viewStart + interval - 1) / interval) * interval;
        for (int f = firstGridFrame; f < viewEnd; f += interval)
        {
            double x = FrameToPixel(f);
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }

        // Loop region fill, under the trace and the selection - green while Loop Lock is
        // on (meaning the region is draggable as a whole right now), faint blue
        // otherwise informational-only.
        if (HasLoop)
        {
            double loopX0 = FrameToPixel(Math.Max(LoopStartFrame, viewStart));
            double loopX1 = FrameToPixel(Math.Min(LoopEndFrame, viewEnd));
            if (loopX1 > loopX0)
                dc.DrawRectangle((Brush)FindResource(LoopLockEnabled ? "WaveformLoopSelectedBrush" : "WaveformLoopRegionBrush"), null,
                    new Rect(loopX0, 0, loopX1 - loopX0, h));
        }

        // Selection highlight, under the trace but over the loop region (the active
        // editing selection should read as "on top of" the informational loop tint).
        int effSelStart = EffectiveSelectionStart, effSelEnd = EffectiveSelectionEnd;
        if (effSelEnd > effSelStart)
        {
            double selX0 = FrameToPixel(Math.Max(effSelStart, viewStart));
            double selX1 = FrameToPixel(Math.Min(effSelEnd, viewEnd));
            if (selX1 > selX0)
                dc.DrawRectangle((Brush)FindResource("WaveformSelectionBrush"), null,
                    new Rect(selX0, 0, selX1 - selX0, h));
        }

        var pen = new Pen((Brush)FindResource("WaveformTraceBrush"), 1);
        dc.DrawGeometry(null, pen, GetOrBuildTraceGeometry(samples, viewStart, viewEnd, viewLen, w, h));

        // Loop Start/End edge lines - Kronos's own coloring (green/blue), drawn over
        // the trace so they're always legible against it.
        if (HasLoop)
        {
            if (LoopStartFrame >= viewStart && LoopStartFrame <= viewEnd)
                dc.DrawLine(new Pen((Brush)FindResource("WaveformLoopStartBrush"), 1.5), new Point(MarkerX(LoopStartFrame, w), 0), new Point(MarkerX(LoopStartFrame, w), h));
            if (LoopEndFrame >= viewStart && LoopEndFrame <= viewEnd)
                dc.DrawLine(new Pen((Brush)FindResource("WaveformLoopEndBrush"), 1.5), new Point(MarkerX(LoopEndFrame, w), 0), new Point(MarkerX(LoopEndFrame, w), h));
        }

        // Sample Start marker - Kronos's own coloring (red), on top of the loop edges
        // so it's never obscured when the two happen to coincide. >= viewStart (not >)
        // so a marker sitting exactly at frame 0/the left edge of the view still renders
        // - it was previously invisible whenever it coincided with the view's own start.
        if (SampleStartFrame >= viewStart && SampleStartFrame <= viewEnd)
            dc.DrawLine(new Pen((Brush)FindResource("WaveformSampleStartBrush"), 1.5), new Point(MarkerX(SampleStartFrame, w), 0), new Point(MarkerX(SampleStartFrame, w), h));

        // Scrub line - grey, under the playhead (drawn just before it) so once playback
        // actually starts and the white line begins moving, the grey "started here"
        // marker stays visible underneath rather than being erased.
        if (ScrubFrame >= viewStart && ScrubFrame <= viewEnd)
            dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(MarkerX(ScrubFrame, w), 0), new Point(MarkerX(ScrubFrame, w), h));

        // Playhead, always on top - a thin white line so it reads clearly against any
        // of the above.
        if (PlayheadFrame >= viewStart && PlayheadFrame <= viewEnd)
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(MarkerX(PlayheadFrame, w), 0), new Point(MarkerX(PlayheadFrame, w), h));

        // Active-channel cue for Split L/R (explicit request: single click should
        // "highlight either the L or R depending on the track selected") - a plain
        // accent border, inset half its own thickness so it draws crisply inside the
        // control's own bounds rather than getting clipped/anti-aliased against the
        // edge. Absent entirely in Combine/mono (IsActiveChannel is never set there).
        if (IsActiveChannel)
        {
            var accentPen = new Pen((Brush)FindResource("AccentBrush"), 2);
            dc.DrawRectangle(null, accentPen, new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)));
        }
    }

    // FrameToPixel, clamped half a pen-width in from each edge so a marker sitting
    // exactly at the view's own boundary (e.g. LoopEnd == FrameCount == viewEnd, a
    // completely normal "loop runs to the end of the sample" state, not an edge case)
    // draws as a fully visible line instead of being clipped away by ClipToBounds -
    // every marker check below used to require frame < viewEnd (strict), which is
    // false exactly when a marker sits AT the view's right edge.
    double MarkerX(int frame, double w) => Math.Clamp(FrameToPixel(frame), 0.75, w - 0.75);
}
