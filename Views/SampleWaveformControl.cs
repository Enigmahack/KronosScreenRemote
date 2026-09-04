using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    // Ceiling on how many min/max columns the trace is built from, regardless of how wide
    // the pane physically is - see GetOrBuildTraceGeometry for why.
    const int MaxTraceColumns = 1920;

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

    // All three marker DPs clear any live drag preview on ANY write, exactly as the two
    // selection DPs do and for the same reason: a preview must never be able to go stale
    // and shadow a later real commit arriving through some other path (RefreshDetailPanels
    // pushing a committed value onto the mirrored sibling pane being the one that actually
    // matters here - without this the sibling would stay stuck on the drag preview).
    public static readonly DependencyProperty SampleStartFrameProperty =
        DependencyProperty.Register(nameof(SampleStartFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).ClearPreviewMarkers()));

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
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).ClearPreviewMarkers()));

    // Green marker line (Kronos's own coloring), draggable independently of LoopEndFrame.
    public int LoopStartFrame
    {
        get => (int)GetValue(LoopStartFrameProperty);
        set => SetValue(LoopStartFrameProperty, value);
    }

    public static readonly DependencyProperty LoopEndFrameProperty =
        DependencyProperty.Register(nameof(LoopEndFrame), typeof(int), typeof(SampleWaveformControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SampleWaveformControl)d).ClearPreviewMarkers()));

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
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.None,
                (d, _) => ((SampleWaveformControl)d).UpdatePlayheadVisual()));

    // -1 = hidden (not currently playing). Pushed by the window's playhead pump from
    // SamplePlayback.PositionFrame - same "poll, don't marshal an event" discipline used
    // for the VU meter itself. Deliberately NOT AffectsRender: this is the one value that
    // changes every displayed frame during playback, and re-recording the entire drawing
    // (grid + loop fill + selection + trace + every marker) just to slide one vertical
    // line is what made the scan line stutter. It lives in its own DrawingVisual moved by
    // a TranslateTransform instead - see UpdatePlayheadVisual.
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

    // Live marker-drag preview, exactly parallel to _previewSelStart/_previewSelEnd above
    // and for the same reason: dragging a marker (or the whole loop region) used to write
    // SampleStartFrame/LoopStartFrame/LoopEndFrame on EVERY MouseMove, and each write
    // re-rendered this pane AND - via MarkersChanging -> MirrorMarkersPreview - the
    // sibling stereo pane too, which is what made dragging a loop point feel laggy.
    // Nothing is committed until mouse-up, so the dragged line follows the cursor without
    // any of that; the region fill, the ViewModel push and RefreshDetailPanels all happen
    // once, at the end.
    //
    // Abandoning a drag then costs nothing to undo: because the real DPs were never
    // touched, simply dropping these previews IS the revert (see OnLostMouseCapture,
    // which is where alt-tab / a focus-stealing dialog / the mouse leaving the window
    // mid-drag all land). A live-DP design would have to snapshot and restore instead.
    int? _previewSampleStart, _previewLoopStart, _previewLoopEnd;

    // What OnRender and the mirrored sibling pane should actually draw right now - the
    // live drag preview while one is in progress, the committed DP otherwise. Same shape
    // as EffectiveSelectionStart/End.
    public int EffectiveSampleStart => _previewSampleStart ?? SampleStartFrame;
    public int EffectiveLoopStart => _previewLoopStart ?? LoopStartFrame;
    public int EffectiveLoopEnd => _previewLoopEnd ?? LoopEndFrame;

    // Lets the window tell "mirror this live drag" apart from "the drag ended - drop the
    // mirrored copy too", so an abandoned drag can't leave the sibling pane stuck showing
    // a preview the dragged pane has already discarded.
    public bool HasMarkerPreview =>
        _previewSampleStart != null || _previewLoopStart != null || _previewLoopEnd != null;

    // Puppets this pane's marker preview from the sibling one mid-drag (MirrorMarkersPreview),
    // the same way SetPreviewSelection puppets its rubber band - cheaper than round-tripping
    // through this pane's own DPs, and just as revertible.
    public void SetPreviewMarkers(int sampleStart, int loopStart, int loopEnd)
    {
        _previewSampleStart = sampleStart;
        _previewLoopStart = loopStart;
        _previewLoopEnd = loopEnd;
        InvalidateVisual();
    }

    public void ClearPreviewMarkers()
    {
        if (_previewSampleStart == null && _previewLoopStart == null && _previewLoopEnd == null) return;
        _previewSampleStart = _previewLoopStart = _previewLoopEnd = null;
        InvalidateVisual();
    }

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

    // Frozen once, reused for the life of the process. Every one of these used to be a
    // fresh `new Pen(...)` built inside OnRender, i.e. re-allocated on every playhead
    // tick, every mouse-move of a drag and every zoom step - and an UNfrozen Freezable
    // handed to a DrawingContext also costs WPF a change-notification hookup each time.
    // The colours are fixed (Kronos's own marker coloring), so there is nothing here to
    // rebuild per frame.
    static readonly Pen PlayheadPen = MakeFrozenPen(Brushes.White, 1);
    static readonly Pen ScrubPen = MakeFrozenPen(Brushes.Gray, 1);

    // Freezing a Pen freezes its Brush along with it, so the theme brush is CLONED first:
    // these come from shared application resources, and freezing one in place would make
    // it immutable for every other control that resolves the same key.
    static Pen MakeFrozenPen(Brush brush, double thickness)
    {
        var own = brush.CloneCurrentValue();
        own.Freeze();
        var pen = new Pen(own, thickness);
        pen.Freeze();
        return pen;
    }

    // Theme-resource pens/brushes, resolved through FindResource ONCE on first render
    // rather than ~10 times per render: FindResource walks the logical tree up to the
    // app-level dictionaries, which is pure repeated work on a path that runs on every
    // playhead tick and every mouse-move of a drag. Safe to hold for the control's
    // lifetime - these keys are set once at startup and the app has no live theme switch.
    Pen? _gridPen, _loopStartPen, _loopEndPen, _sampleStartPen, _accentPen;
    Brush? _panelBrush, _loopSelectedBrush, _loopRegionBrush, _selectionBrush, _traceBrush;

    void EnsureRenderResources()
    {
        if (_gridPen != null) return;
        _gridPen = MakeFrozenPen((Brush)FindResource("WaveformGridLineBrush"), 1);
        _loopStartPen = MakeFrozenPen((Brush)FindResource("WaveformLoopStartBrush"), 1.5);
        _loopEndPen = MakeFrozenPen((Brush)FindResource("WaveformLoopEndBrush"), 1.5);
        _sampleStartPen = MakeFrozenPen((Brush)FindResource("WaveformSampleStartBrush"), 1.5);
        _accentPen = MakeFrozenPen((Brush)FindResource("AccentBrush"), 2);
        _traceBrush = (Brush)FindResource("WaveformTraceBrush");
        _panelBrush = (Brush)FindResource("PanelBackgroundBrush");
        _loopSelectedBrush = (Brush)FindResource("WaveformLoopSelectedBrush");
        _loopRegionBrush = (Brush)FindResource("WaveformLoopRegionBrush");
        _selectionBrush = (Brush)FindResource("WaveformSelectionBrush");
    }

    // The playhead is the only thing that moves every displayed frame, so it gets its own
    // child visual: the line is recorded once (it never changes shape - a full-height
    // vertical stroke) and each update is a single TranslateTransform write, with no
    // re-record of the trace, grid, loop fill or markers. Same "slide it with a transform
    // rather than redrawing it" reasoning _waveMoveTransform already uses for the Move
    // tool's whole-waveform drag. Visual children draw AFTER the element's own OnRender
    // content, which keeps the playhead on top exactly where it was drawn before.
    readonly DrawingVisual _playheadVisual = new();
    readonly TranslateTransform _playheadOffset = new();
    double _playheadLineHeight = -1;

    // Layering, bottom to top:
    //
    //   this element's own OnRender content  background, zoom grid, loop tint, selection
    //   _traceVisual                         the waveform envelope   <- the expensive one
    //   _overlayVisual                       marker lines, scrub line, active-channel border
    //   _playheadVisual                      playhead (moved by transform only)
    //
    // The split exists because filling the envelope is a FILL-RATE cost, not a geometry
    // one: it covers most of the pane, so on a 4K fullscreen pair it is millions of pixels
    // per repaint, which is why dragging a marker degraded with window size while the
    // sample length barely mattered. InvalidateVisual re-records only the ELEMENT's own
    // content and leaves child visuals alone, so keeping the trace in a child means a
    // drag repaints the cheap layers and the waveform is composited from what it already
    // rasterised. It is re-recorded solely when the geometry itself actually changes
    // (EnsureTraceRecorded) - i.e. on a new sample, a zoom/pan, or a resize.
    //
    // The markers have to sit in a child of their own rather than in the element content:
    // child visuals always draw ABOVE that content, so drawing them there would put them
    // under the trace instead of over it.
    readonly DrawingVisual _traceVisual = new();
    readonly DrawingVisual _overlayVisual = new();
    WriteableBitmap? _recordedTraceBitmap;
    Rect _recordedTraceRect;

    // No BitmapCache on _traceVisual. One was tried here and removed: it never measurably
    // helped, and a cached visual can composite a STALE bitmap while the fresh drawing
    // waits on the cache to regenerate - which shows up as exactly the symptom being
    // chased (the frame measures as composed in ~4ms, but what reaches the screen is the
    // previous picture). The layer split is worth keeping on its own; the caching was not.

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _traceVisual,
        1 => _overlayVisual,
        2 => _playheadVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    // The bitmap instance is reused across rebuilds (only its pixels change), so an
    // unchanged view has to be detected by the cache key rather than by reference - hence
    // the explicit flag. WritePixels updates the live bitmap in place, so a rebuild does
    // not by itself require re-recording this visual.
    void EnsureTraceRecorded(short[]? samples, int viewStart, int viewEnd, int viewLen, double w, double h)
    {
        var bitmap = samples is { Length: > 0 }
            ? GetOrBuildTraceBitmap(samples, viewStart, viewEnd, viewLen, w, h)
            : null;
        if (ReferenceEquals(bitmap, _recordedTraceBitmap) && _recordedTraceRect == new Rect(0, 0, w, h)) return;
        _recordedTraceBitmap = bitmap;
        _recordedTraceRect = new Rect(0, 0, w, h);

        using var ctx = _traceVisual.RenderOpen();
        if (bitmap != null) ctx.DrawImage(bitmap, _recordedTraceRect);
    }

    void UpdatePlayheadVisual()
    {
        double w = ActualWidth, h = ActualHeight;
        int frame = PlayheadFrame;
        // Same visibility rule OnRender used when it still drew this line itself.
        int viewStart = Math.Clamp(_viewStart, 0, ViewSpan);
        int viewEnd = Math.Clamp(ViewEndFrame, viewStart + 1, ViewSpan);
        if (w <= 0 || h <= 0 || frame < viewStart || frame > viewEnd)
        {
            _playheadVisual.Opacity = 0;
            return;
        }

        if (_playheadLineHeight != h)
        {
            using (var ctx = _playheadVisual.RenderOpen())
                ctx.DrawLine(PlayheadPen, new Point(0, 0), new Point(0, h));
            _playheadLineHeight = h;
        }
        _playheadVisual.Opacity = 1;
        _playheadOffset.X = MarkerX(frame, w);
    }

    public SampleWaveformControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _playheadVisual.Transform = _playheadOffset;
        AddVisualChild(_traceVisual);
        AddVisualChild(_overlayVisual);
        AddVisualChild(_playheadVisual);
    }

    // The playhead's pixel position depends on the view window and the control's size,
    // neither of which goes through PlayheadFrame's own callback.
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdatePlayheadVisual();
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
    // The deepest permitted zoom, one wheel step (factor 0.8) SHALLOWER than the old
    // one-frame-per-pixel floor - explicit request: at that floor every pixel column
    // bucketed exactly one sample, so there was no min/max spread left to draw and the
    // trace read as blank. Enforced here rather than only in OnMouseWheel so the toolbar
    // Zoom In button, Zoom to Selection and the scrollbar all share the same limit.
    int MinViewLen => Math.Max(1, (int)(ActualWidth * 1.25));

    public void SetView(int start, int end)
    {
        int len = Math.Clamp(end - start, Math.Min(MinViewLen, Math.Max(1, ViewSpan)), Math.Max(1, ViewSpan));
        start = Math.Clamp(start, 0, Math.Max(0, ViewSpan - len));
        // Nothing to redraw or re-mirror if this resolves to the window already shown -
        // SyncWaveformViews pushes the same view onto the sibling pane, the ruler and the
        // scrollbar on every change, so a no-op set used to cost a full re-render of both
        // panes for nothing.
        if (start == _viewStart && start + len == _viewEnd) return;
        _viewStart = start;
        _viewEnd = start + len;
        InvalidateVisual();
        UpdatePlayheadVisual();
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

        // Both marker drags below move only the PREVIEW - see _previewSampleStart's own
        // comment. The committed DPs (and everything downstream of them) are written once,
        // at mouse-up.
        if (_draggingMarker is { } marker)
        {
            int frame = PixelToFrame(e.GetPosition(this).X);
            switch (marker)
            {
                case SampleMarkerKind.SampleStart: _previewSampleStart = frame; break;
                case SampleMarkerKind.LoopStart: _previewLoopStart = Math.Min(frame, LoopEndFrame); break;
                case SampleMarkerKind.LoopEnd: _previewLoopEnd = Math.Max(frame, LoopStartFrame); break;
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
            _previewLoopStart = newStart;
            _previewLoopEnd = newStart + len;
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

        // Mouse-up is the ONLY point a marker drag commits - this is where the line
        // "drops" onto wherever the preview had reached. Clearing the drag flag BEFORE
        // releasing the capture matters: the release re-enters OnLostMouseCapture
        // synchronously, and that would otherwise discard the very preview being read here.
        if (_draggingMarker is { } marker)
        {
            _draggingMarker = null;
            ReleaseMouseCapture();
            int? dragged = marker switch
            {
                SampleMarkerKind.SampleStart => _previewSampleStart,
                SampleMarkerKind.LoopStart => _previewLoopStart,
                _ => _previewLoopEnd,
            };
            _previewSampleStart = _previewLoopStart = _previewLoopEnd = null;
            if (dragged is { } value)
            {
                switch (marker)
                {
                    case SampleMarkerKind.SampleStart: SampleStartFrame = value; break;
                    case SampleMarkerKind.LoopStart: LoopStartFrame = value; break;
                    case SampleMarkerKind.LoopEnd: LoopEndFrame = value; break;
                }
            }
            // Still reported even when the press never moved (dragged == null, nothing
            // committed): in Split L/R that is what makes clicking a marker activate its
            // channel, and SetMarker's own no-op guard means re-reporting an unchanged
            // value pushes no undo step and dirties nothing.
            InvalidateVisual();
            MarkerDragged?.Invoke(marker, marker switch
            {
                SampleMarkerKind.SampleStart => SampleStartFrame,
                SampleMarkerKind.LoopStart => LoopStartFrame,
                _ => LoopEndFrame,
            });
            return;
        }

        if (_draggingLoop)
        {
            _draggingLoop = false;
            ReleaseMouseCapture();
            int movedStart = _previewLoopStart ?? LoopStartFrame;
            int movedEnd = _previewLoopEnd ?? LoopEndFrame;
            _previewLoopStart = _previewLoopEnd = null;
            if (_loopDragMoved)
            {
                LoopStartFrame = movedStart;
                LoopEndFrame = movedEnd;
                LoopRegionChanged?.Invoke(movedStart, movedEnd);
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

    // Alt-tab, a focus-stealing dialog, the mouse leaving the window, or anything else
    // that takes the capture away mid-drag all arrive here. Every drag this control owns
    // is ABANDONED rather than committed: only a real mouse-up commits (explicit request
    // - "on window leave, alt tab, etc. the loop point should just revert to where it
    // was"). Because the marker/selection drags now only ever move a preview, dropping
    // the previews IS the revert - nothing has to be snapshotted and restored.
    //
    // Covers all five drag states, not just the markers: previously NONE of them were
    // reset here, so a capture lost mid-drag left the gesture permanently stuck armed.
    // _movingWaveform was the worst of them - it also left RenderTransform applied, which
    // stranded the whole trace visibly offset with a stale transform still on it.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Mouse-up releases the capture itself, and clears its own state before doing so -
        // so a normal end-of-drag lands here with nothing left to abandon.
        if (_draggingMarker == null && !_draggingLoop && !_movingSelection && !_movingWaveform
            && _dragAnchorFrame < 0)
            return;

        _draggingMarker = null;
        _draggingLoop = false;
        _movingSelection = false;
        _dragAnchorFrame = -1;
        _previewSampleStart = _previewLoopStart = _previewLoopEnd = null;
        _previewSelStart = _previewSelEnd = null;

        if (_movingWaveform)
        {
            _movingWaveform = false;
            _waveMoveTransform.X = 0;
            RenderTransform = Transform.Identity;
        }

        // Tells the window to drop the sibling pane's mirrored copy of the preview too -
        // by now HasMarkerPreview is false, so MirrorMarkersPreview clears rather than
        // mirrors.
        MarkersChanging?.Invoke();
        InvalidateVisual();
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
        // Counted so a wheel event that reaches this control but is turned away here can be
        // told apart from one that never arrived at all.
        if (!ScrollToZoom || FrameCount == 0)
        {
            WaveformPerfProbe.Record("wheel: REJECTED here (ScrollToZoom off / no samples)", 0);
            return;
        }
        e.Handled = true;
        using var scope = WaveformPerfProbe.Time("wheel: handler (sync work only)");

        // The pair of numbers that separates "the app is throttled" from "that is simply
        // how fast the wheel was turned". If the gap BETWEEN wheel events is ~80ms then the
        // input arrived that slowly and there is nothing to fix; if it is ~10ms while the
        // repaint gap stays ~80ms, repaints are genuinely lagging behind the input.
        // THE missing measurement. e.Timestamp is when the OS recorded the wheel movement;
        // comparing it to now gives how long the event sat in the dispatcher queue before
        // this handler got to run. Everything measured previously started HERE, so a delay
        // spent queueing was invisible to all of it - which is how the probe could report
        // 2.5ms while the interaction felt like half a second.
        WaveformPerfProbe.Record("wheel: OS -> handler queue delay", Math.Max(0, Environment.TickCount - e.Timestamp));
        WaveformPerfProbe.MeasureToNextPresent("wheel -> next composed frame");

        // How backed up the UI thread is right now: a Background-priority item runs only
        // once everything ahead of it has drained, so the delay before this fires IS the
        // congestion. Input is dispatched at a higher priority than Background but lower
        // than Render, so a large number here means something is saturating the thread.
        long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            WaveformPerfProbe.Record("dispatcher: background drain delay",
                (System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));

        long wheelNow = System.Diagnostics.Stopwatch.GetTimestamp();
        double sinceLastWheel = _lastWheelTicks == 0
            ? double.MaxValue
            : (wheelNow - _lastWheelTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (sinceLastWheel < 2000) WaveformPerfProbe.Record("wheel: gap between wheel events", sinceLastWheel);
        _lastWheelTicks = wheelNow;
        // Wheel -> pixels-on-screen latency: how long the user actually waits after a notch.
        if (_pendingWheelTicks == 0) _pendingWheelTicks = wheelNow;

        int viewLen = ViewEndFrame - _viewStart;
        int cursorFrame = PixelToFrame(e.GetPosition(this).X);

        // A flat 0.8x per notch means ~36 notches to cross the zoom range of a long sample.
        // Measurement showed each notch already repaints in ~2.5ms, so what read as "zoom
        // is slow" was never frame rate - it was having to keep wheeling, with each notch
        // moving the picture only 20%. So a fast spin compounds: keep scrolling and the
        // step grows, up to 4x the exponent (0.8^4, i.e. ~0.41x per notch), which crosses
        // the same range in under a dozen notches. A single deliberate notch, or any notch
        // after a pause, still gets the original fine 0.8x step for precise framing.
        if (sinceLastWheel < WheelAccelWindowMs) _wheelAccel = Math.Min(_wheelAccel + 0.4, 4.0);
        else _wheelAccel = 1.0;

        double factor = Math.Pow(e.Delta > 0 ? 0.8 : 1.25, _wheelAccel);
        int newLen = Math.Clamp((int)(viewLen * factor), Math.Min(MinViewLen, ViewSpan), ViewSpan);

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
    WriteableBitmap? _traceBitmap;
    byte[]? _traceBits;

    // Built once per loaded sample and reused for every zoom/pan of it. Keyed by array
    // reference, the same test the geometry cache uses - an edit that produces a new
    // buffer correctly rebuilds, a re-render of the same buffer does not.
    WaveformPyramid? _pyramid;

    WaveformPyramid GetPyramid(short[] samples)
    {
        if (_pyramid == null || !ReferenceEquals(_pyramid.Samples, samples))
            _pyramid = new WaveformPyramid(samples);
        return _pyramid;
    }

    // Renders the trace by writing PIXELS, not by handing WPF a polygon.
    //
    // The envelope is one filled vertical span per column, which as a closed polygon means
    // ~2 vertices per column and a direction change at nearly every one. Rasterising that
    // costs roughly (edges crossing each scanline) x (scanlines), so it scales with how
    // JAGGED the waveform is - which is exactly the reported behaviour: a dense passage
    // crawls, and zooming in until the trace smooths out speeds it back up, at the same
    // pixel count. Anti-aliasing multiplies the same cost again.
    //
    // Filling spans into a pixel buffer instead is O(covered pixels) of flat memory writes:
    // a dense waveform costs precisely the same as a smooth one, there are no edges to
    // anti-alias, and WPF is left with a single image blit. This is the "quantise it, it
    // needs performance not precision" trade taken to its conclusion - the output is
    // per-pixel identical to what the polygon produced, because at one column per pixel the
    // polygon had no sub-pixel detail to convey in the first place.
    WriteableBitmap? GetOrBuildTraceBitmap(short[] samples, int viewStart, int viewEnd, int viewLen, double w, double h)
    {
        if (_traceBitmap != null && ReferenceEquals(_cachedTraceSamples, samples)
            && _cachedTraceViewStart == viewStart && _cachedTraceViewEnd == viewEnd
            && _cachedTraceWidth == w && _cachedTraceHeight == h)
        {
            return _traceBitmap;
        }

        using var buildScope = WaveformPerfProbe.Time("trace: bitmap rebuild");

        double midY = h / 2;
        double yScale = h / 2 / 32768.0;
        // One min/max bucket PER PIXEL COLUMN, mapped with the exact same
        // viewStart/viewLen/width arithmetic FrameToPixel uses - not a separately
        // derived bucket count. A mismatch here (bucket count computed from control
        // width, but the loop only running for as many buckets as there are frames in
        // the view) is what caused the trace to render squeezed into a sliver at the
        // left edge once zoomed in past ~one frame per pixel; per-pixel-column mapping
        // can't drift out of sync with FrameToPixel because it's the same formula.
        // One envelope column per physical pixel is more resolution than the data (or the
        // eye) needs: on a 4K-wide pane that is a polygon of ~7,700 vertices, and the cost
        // of anti-aliasing a boundary that jagged scales with the edge count. Capped at a
        // 2K-class column count and stretched across whatever the pane actually is -
        // explicit request: accept slightly blockier steps at very high resolutions rather
        // than paying for detail that zooming in exists to provide anyway. Below the cap
        // (a smaller window) nothing changes - it stays one column per pixel.
        int pixelCount = Math.Max(1, Math.Min((int)w, MaxTraceColumns));
        double columnWidth = w / pixelCount;

        // ONE closed filled figure tracing the min/max envelope - down the top edge, back
        // along the bottom - rather than a separate stroked BeginFigure/LineTo pair per
        // pixel column. Visually identical (at one bucket per column the stroked version
        // already read as a solid filled blob), but a geometry of ~1000 individual figures
        // is expensive for WPF to RASTERISE, and that cost was being paid again on every
        // single frame: the cache below spares the PCM rescan during a marker drag, but a
        // cached geometry still gets re-rasterised each time the visual is re-rendered,
        // doubled across a stereo pair. That, not the bucketing scan, is what held marker
        // drags and zooming to single-digit FPS.
        // Which summary level (if any) can answer this zoom level - see WaveformPyramid.
        // Zoomed out, a column covers thousands of frames and reads a handful of prebuilt
        // buckets instead; zoomed in past the finest bucket, `level` is null and the raw
        // samples are read directly, which is cheap because there are few of them in view.
        var level = GetPyramid(samples).Pick((double)viewLen / pixelCount);

        // The bitmap is one pixel per envelope column and one per device row; DrawImage
        // stretches it across the pane, which at the MaxTraceColumns cap is at most a
        // marginal horizontal scale.
        int bmpH = Math.Max(1, (int)Math.Round(h));
        if (_traceBitmap == null || _traceBitmap.PixelWidth != pixelCount || _traceBitmap.PixelHeight != bmpH)
        {
            _traceBitmap = new WriteableBitmap(pixelCount, bmpH, 96, 96, PixelFormats.Bgra32, null);
            _traceBits = new byte[pixelCount * bmpH * 4];
        }
        var bits = _traceBits!;
        Array.Clear(bits);

        var traceColor = (_traceBrush as SolidColorBrush)?.Color ?? Colors.White;
        byte cb = traceColor.B, cg = traceColor.G, cr = traceColor.R, ca = traceColor.A;
        int stride = pixelCount * 4;

        for (int px = 0; px < pixelCount; px++)
        {
            int start = viewStart + (int)((long)px * viewLen / pixelCount);
            if (start >= viewEnd) break;
            int end = viewStart + (int)((long)(px + 1) * viewLen / pixelCount);
            end = Math.Clamp(end, start + 1, viewEnd);

            // viewStart/viewEnd can now extend past THIS pane's own samples.Length
            // (they're clamped to the pair-wide ViewSpan, not this buffer) - a column
            // entirely beyond real data has nothing to bucket, so it ends the envelope
            // (correctly leaving the rest blank) rather than indexing past the array.
            // start only ever increases, so the drawable columns are a contiguous run.
            if (start >= samples.Length) break;
            int readEnd = Math.Min(end, samples.Length);
            if (readEnd <= start) break;

            short min = short.MaxValue, max = short.MinValue;
            if (level is { } lv)
            {
                // Every bucket the column touches, including the two it only partly
                // overlaps at each edge. Over-including those is what keeps this exact in
                // the direction that matters - a peak is never missed, at worst it shows
                // up one column early or late, which is invisible at one bucket per pixel.
                int b0 = start / lv.Bucket;
                int b1 = Math.Min((readEnd + lv.Bucket - 1) / lv.Bucket, lv.Min.Length);
                for (int b = b0; b < b1; b++)
                {
                    if (lv.Min[b] < min) min = lv.Min[b];
                    if (lv.Max[b] > max) max = lv.Max[b];
                }
            }
            else
            {
                for (int i = start; i < readEnd; i++)
                {
                    if (samples[i] < min) min = samples[i];
                    if (samples[i] > max) max = samples[i];
                }
            }
            if (min > max) break; // past the end of the summary - ends the envelope cleanly

            double yTop = midY - max * yScale;
            double yBot = midY - min * yScale;
            // Quantise to whole rows. A silence bucket collapses to zero height, which would
            // leave the column blank - clamped to a single row so silence still reads as the
            // centre line it always did.
            int rowTop = Math.Clamp((int)(midY - max * yScale), 0, bmpH - 1);
            int rowBot = Math.Clamp((int)(midY - min * yScale), 0, bmpH - 1);
            if (rowBot < rowTop) (rowTop, rowBot) = (rowBot, rowTop);

            // The whole inner loop: a straight run down one column. No edge list, no
            // coverage computation, no anti-aliasing - just stores.
            int offset = rowTop * stride + px * 4;
            for (int row = rowTop; row <= rowBot; row++, offset += stride)
            {
                bits[offset] = cb;
                bits[offset + 1] = cg;
                bits[offset + 2] = cr;
                bits[offset + 3] = ca;
            }
        }

        _traceBitmap.WritePixels(new Int32Rect(0, 0, pixelCount, bmpH), bits, stride, 0);

        _cachedTraceSamples = samples;
        _cachedTraceViewStart = viewStart;
        _cachedTraceViewEnd = viewEnd;
        _cachedTraceWidth = w;
        _cachedTraceHeight = h;
        return _traceBitmap;
    }

    long _lastRenderTicks, _lastWheelTicks, _pendingWheelTicks;

    // Notches closer together than this are read as one continuous spin and compound the
    // zoom step; anything slower is a deliberate single adjustment and resets to the fine
    // step. Comfortably above the ~60-90ms between notches measured during a real spin.
    const double WheelAccelWindowMs = 150;
    double _wheelAccel = 1.0;

    // Temporary probe wrapper - see WaveformPerfProbe. "gap since previous" is the number
    // that actually decides this: it is the achieved repaint interval. If a zoom spin
    // shows a large gap while OnRender itself measures small, then the time is NOT in this
    // control's drawing at all and is being spent in WPF layout/composition around it.
    protected override void OnRender(DrawingContext dc)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastRenderTicks != 0)
        {
            double gap = (now - _lastRenderTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gap < 2000) WaveformPerfProbe.Record("render: gap since previous", gap);
        }
        _lastRenderTicks = now;

        // Recorded as measurements purely so the "max" column reports the real pane size
        // the slow case was actually running at.
        WaveformPerfProbe.Record("pane: width px", ActualWidth);
        WaveformPerfProbe.Record("pane: height px", ActualHeight);

        using (WaveformPerfProbe.Time("render: OnRender"))
            RenderCore(dc);

        if (_pendingWheelTicks != 0)
        {
            double latency = (System.Diagnostics.Stopwatch.GetTimestamp() - _pendingWheelTicks)
                             * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (latency < 2000) WaveformPerfProbe.Record("wheel -> render latency", latency);
            _pendingWheelTicks = 0;
        }
    }

    void RenderCore(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        EnsureRenderResources();
        dc.DrawRectangle(_panelBrush, null, new Rect(0, 0, w, h));

        var samples = Samples;
        if (samples == null || samples.Length == 0)
        {
            var text = new FormattedText("No audio data in this file",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, (Brush)FindResource("MutedTextBrush"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            // Drop the retained layers too - they are no longer re-recorded from here, so
            // without this a cleared pane would keep showing the previous sample's trace
            // and markers underneath this message.
            EnsureTraceRecorded(null, 0, 0, 0, w, h);
            using (_overlayVisual.RenderOpen()) { }
            UpdatePlayheadVisual();
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
        var gridPen = _gridPen;
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
        // Effective (preview-aware) marker positions throughout - mid-drag these follow the
        // cursor while the committed DPs stay exactly where they were.
        int effLoopStart = EffectiveLoopStart, effLoopEnd = EffectiveLoopEnd;
        bool effHasLoop = LoopEnabled && effLoopEnd > effLoopStart;
        if (effHasLoop)
        {
            double loopX0 = FrameToPixel(Math.Max(effLoopStart, viewStart));
            double loopX1 = FrameToPixel(Math.Min(effLoopEnd, viewEnd));
            if (loopX1 > loopX0)
                dc.DrawRectangle(LoopLockEnabled ? _loopSelectedBrush : _loopRegionBrush, null,
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
                dc.DrawRectangle(_selectionBrush, null,
                    new Rect(selX0, 0, selX1 - selX0, h));
        }

        EnsureTraceRecorded(samples, viewStart, viewEnd, viewLen, w, h);
        RecordOverlay(viewStart, viewEnd, w, h, effHasLoop, effLoopStart, effLoopEnd);

        // The view window/size this render just resolved is what positions the playhead,
        // so keep it in step with them.
        UpdatePlayheadVisual();

    }

    // Everything that sits ABOVE the waveform and moves while dragging. Cheap by
    // construction - at most four hairlines and a border - which is the entire point of
    // keeping it out of the layer that carries the fill.
    void RecordOverlay(int viewStart, int viewEnd, double w, double h,
                       bool effHasLoop, int effLoopStart, int effLoopEnd)
    {
        using var dc = _overlayVisual.RenderOpen();

        // Loop Start/End edge lines - Kronos's own coloring (green/blue), drawn over
        // the trace so they're always legible against it.
        if (effHasLoop)
        {
            if (effLoopStart >= viewStart && effLoopStart <= viewEnd)
                dc.DrawLine(_loopStartPen, new Point(MarkerX(effLoopStart, w), 0), new Point(MarkerX(effLoopStart, w), h));
            if (effLoopEnd >= viewStart && effLoopEnd <= viewEnd)
                dc.DrawLine(_loopEndPen, new Point(MarkerX(effLoopEnd, w), 0), new Point(MarkerX(effLoopEnd, w), h));
        }

        // Sample Start marker - Kronos's own coloring (red), on top of the loop edges
        // so it's never obscured when the two happen to coincide. >= viewStart (not >)
        // so a marker sitting exactly at frame 0/the left edge of the view still renders
        // - it was previously invisible whenever it coincided with the view's own start.
        int effSampleStart = EffectiveSampleStart;
        if (effSampleStart >= viewStart && effSampleStart <= viewEnd)
            dc.DrawLine(_sampleStartPen, new Point(MarkerX(effSampleStart, w), 0), new Point(MarkerX(effSampleStart, w), h));

        // Scrub line - grey, under the playhead (its own layer above this one) so once
        // playback starts and the white line begins moving, the grey "started here"
        // marker stays visible underneath rather than being erased.
        if (ScrubFrame >= viewStart && ScrubFrame <= viewEnd)
            dc.DrawLine(ScrubPen, new Point(MarkerX(ScrubFrame, w), 0), new Point(MarkerX(ScrubFrame, w), h));

        // Active-channel cue for Split L/R (explicit request: single click should
        // "highlight either the L or R depending on the track selected") - a plain
        // accent border, inset half its own thickness so it draws crisply inside the
        // control's own bounds rather than getting clipped/anti-aliased against the
        // edge. Absent entirely in Combine/mono (IsActiveChannel is never set there).
        if (IsActiveChannel)
            dc.DrawRectangle(null, _accentPen, new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)));
    }

    // FrameToPixel, clamped half a pen-width in from each edge so a marker sitting
    // exactly at the view's own boundary (e.g. LoopEnd == FrameCount == viewEnd, a
    // completely normal "loop runs to the end of the sample" state, not an edge case)
    // draws as a fully visible line instead of being clipped away by ClipToBounds -
    // every marker check below used to require frame < viewEnd (strict), which is
    // false exactly when a marker sits AT the view's right edge.
    double MarkerX(int frame, double w) => Math.Clamp(FrameToPixel(frame), 0.75, w - 0.75);
}
