using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

/// <summary>
/// All overlay drawing logic (palette editor, zoom, calibration, touch marker,
/// hover tooltip). Called from MainWindow's OverlayElement.RenderCallback.
/// Methods take the full application state as a parameter record to keep them pure.
/// </summary>
static class OverlayRenderer
{
    static readonly Typeface Mono = new("Consolas");
    const double Em = 11.0;

    static FormattedText Fmt(string text, Color color, double pixPerDip = 1.0) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Mono, Em, new SolidColorBrush(color), pixPerDip);

    static void DrawText(DrawingContext dc, string text, Point pos, Color color,
                         double pixPerDip = 1.0)
        => dc.DrawText(Fmt(text, color, pixPerDip), pos);

    public static Size MeasureText(string text)
    {
        var ft = Fmt(text, Colors.White);
        return new Size(ft.Width, ft.Height);
    }

    static Brush B(byte r, byte g, byte b, byte a = 255) =>
        new SolidColorBrush(Color.FromArgb(a, r, g, b));

    static Pen P(byte r, byte g, byte b, double thick = 1) =>
        new(B(r, g, b), thick);

    // ── Layout constants (mirror Python) ──────────────────────────────────────

    const int SW      = 12;
    const int GridPx  = SW * 16;     // 192
    const int EdW     = GridPx + 24; // 216
    const int BarX    = 16;
    const int BarW    = GridPx - 30; // 162
    const int BarH    = 11;
    const int ChanH   = 22;
    const int TitleH  = 15;
    const int InfoH   = 15;
    const int HintH   = 12;

    public static PaletteEntry EffRgb(PaletteEntry[] base_, Dictionary<int, PaletteEntry> ov, int i)
        => ov.TryGetValue(i, out var e) ? e : base_[i];

    const double ZoomMin  = 2.5;
    const double ZoomMax  = 10.0;
    const double ZoomStep = 0.5;
    static readonly Size LoupeOut = new(200, 150);
    static int _lastLoupeIdx = 0;

    public static int? DrawZoomLoupe(DrawingContext dc, ImageSource frameSrc,
        byte[]? rawFrame, int frameW, int frameH, Rect frameRect,
        System.Windows.Point mouse,
        PaletteEntry[] basePal, Dictionary<int, PaletteEntry> ov,
        double zoom, double loupeScale, Size winSize, double pixPerDip)
    {
        double fx = frameRect.X, fy = frameRect.Y,
               fw = frameRect.Width, fh = frameRect.Height;
        if (fw <= 0 || fh <= 0 ||
            mouse.X < fx || mouse.X >= fx + fw ||
            mouse.Y < fy || mouse.Y >= fy + fh)
            return null;

        double outW = LoupeOut.Width  * loupeScale;
        double outH = LoupeOut.Height * loupeScale;
        double srcW = Math.Max(1, outW / zoom);
        double srcH = Math.Max(1, outH / zoom);

        int npx = Math.Clamp((int)((mouse.X - fx) / fw * frameW), 0, frameW - 1);
        int npy = Math.Clamp((int)((mouse.Y - fy) / fh * frameH), 0, frameH - 1);

        double sx = Math.Clamp(npx - srcW / 2, 0, frameW - srcW);
        double sy = Math.Clamp(npy - srcH / 2, 0, frameH - srcH);

        // Pick the placement (among 4 cursor offsets) whose clamped center is farthest
        // from the cursor - keeps the loupe away from the area under examination.
        double maxLx = Math.Max(4, winSize.Width  - outW - 4);
        double maxLy = Math.Max(4, winSize.Height - outH - 4);
        const double Pad = 20;
        (double x, double y)[] placements =
        [
            (mouse.X + Pad,        mouse.Y - outH / 2),   // right
            (mouse.X - outW - Pad, mouse.Y - outH / 2),   // left
            (mouse.X - outW / 2,   mouse.Y - outH - Pad), // above
            (mouse.X - outW / 2,   mouse.Y + Pad),        // below
        ];
        double lx = 4, ly = 4, bestDist = -1;
        // Hysteresis: require a 30-px-equivalent improvement before switching sides,
        // so a 1-pixel mouse move at the placement threshold does not cause rapid flipping.
        const double LoupeHysteresis = 900;
        int winnerIdx = _lastLoupeIdx;
        for (int pi = 0; pi < placements.Length; pi++)
        {
            var (px, py) = placements[pi];
            double qx = Math.Clamp(px, 4, maxLx);
            double qy = Math.Clamp(py, 4, maxLy);
            double dx = mouse.X - (qx + outW / 2);
            double dy = mouse.Y - (qy + outH / 2);
            double dist = dx * dx + dy * dy + (pi == _lastLoupeIdx ? LoupeHysteresis : 0);
            if (dist > bestDist) { bestDist = dist; lx = qx; ly = qy; winnerIdx = pi; }
        }
        _lastLoupeIdx = winnerIdx;

        dc.DrawRectangle(B(15, 15, 15), null, new Rect(lx - 2, ly - 2, outW + 4, outH + 4));
        dc.DrawRectangle(null, P(140, 140, 140), new Rect(lx - 1, ly - 1, outW + 2, outH + 2));

        dc.PushClip(new RectangleGeometry(new Rect(lx, ly, outW, outH)));
        double scale = zoom;
        dc.DrawImage(frameSrc,
            new Rect(lx - sx * scale, ly - sy * scale, frameW * scale, frameH * scale));
        dc.Pop();

        DrawText(dc, $"{zoom:F1}×",
            new Point(lx + outW - MeasureText($"{zoom:F1}×").Width - 4, ly + 3),
            Color.FromRgb(200, 200, 200), pixPerDip);

        // Crosshair - tracks mouse tip correctly even at frame edges
        // The frame is drawn at position lx + (px - sx)*zoom, so (npx,npy) maps to:
        double cx = lx + (npx - sx) * zoom;
        double cy = ly + (npy - sy) * zoom;
        int gap = Math.Max(2, (int)zoom);
        var pen = P(255, 60, 60);
        dc.PushClip(new RectangleGeometry(new Rect(lx, ly, outW, outH)));
        dc.DrawLine(pen, new Point(cx - 14, cy), new Point(cx - gap, cy));
        dc.DrawLine(pen, new Point(cx + gap, cy), new Point(cx + 14, cy));
        dc.DrawLine(pen, new Point(cx, cy - 14), new Point(cx, cy - gap));
        dc.DrawLine(pen, new Point(cx, cy + gap), new Point(cx, cy + 14));
        dc.Pop();

        int? palIdx = null;
        if (rawFrame != null)
        {
            palIdx = rawFrame[npy * frameW + npx];
            var e   = EffRgb(basePal, ov, palIdx.Value);
            var lbl = Fmt($"Entry {palIdx}  R{e.R} G{e.G} B{e.B}", Colors.White, pixPerDip);
            double lblX = lx, lblY = ly + outH + 3;
            if (lblY + lbl.Height > winSize.Height) lblY = ly - lbl.Height - 3;
            dc.DrawRectangle(B(20, 20, 20), null,
                new Rect(lblX - 2, lblY - 1, lbl.Width + 4, lbl.Height + 2));
            dc.DrawText(lbl, new Point(lblX, lblY));
        }
        return palIdx;
    }

    // ── Calibration overlay ───────────────────────────────────────────────────

    public static void DrawCalOverlay(DrawingContext dc, CalMesh mesh, List<CalBiasDot> biasDots,
        (int col, int row)? hoverNode, (int col, int row)? draggingNode,
        bool dirty,
        Rect frameRect, int kronW, int kronH, Size winSize, double pixPerDip)
    {
        double fx = frameRect.X, fy = frameRect.Y,
               fw = frameRect.Width, fh = frameRect.Height;

        dc.DrawRectangle(B(0, 0, 0, 80), null, frameRect);

        Point ToScr((int x, int y) p) =>
            new(fx + p.x * fw / kronW, fy + p.y * fh / kronH);

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 60, 120, 200)), 1.0);

        for (int r = 0; r < mesh.Rows; r++)
            for (int c = 0; c < mesh.Cols - 1; c++)
                dc.DrawLine(gridPen,
                    ToScr(mesh.NodeDst(c,     r, kronW, kronH)),
                    ToScr(mesh.NodeDst(c + 1, r, kronW, kronH)));

        for (int c = 0; c < mesh.Cols; c++)
            for (int r = 0; r < mesh.Rows - 1; r++)
                dc.DrawLine(gridPen,
                    ToScr(mesh.NodeDst(c, r,     kronW, kronH)),
                    ToScr(mesh.NodeDst(c, r + 1, kronW, kronH)));

        for (int c = 0; c < mesh.Cols; c++)
        {
            for (int r = 0; r < mesh.Rows; r++)
            {
                var sp      = ToScr(mesh.NodeDst(c, r, kronW, kronH));
                bool isHov  = hoverNode    == (c, r);
                bool isDrag = draggingNode == (c, r);
                var fill    = isDrag ? B(255, 200, 50)
                            : isHov  ? B(100, 200, 255)
                                     : B(60, 140, 220, 255);
                dc.DrawEllipse(fill, null, sp, 5, 5);
                if (isHov || isDrag)
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White), 1.2), sp, 10, 10);
            }
        }

        // Bias dots - drawn at warped position so they move with mesh changes
        foreach (var dot in biasDots)
        {
            var sp = ToScr(mesh.Apply(dot.Nx, dot.Ny, kronW, kronH));
            dc.DrawEllipse(B(220, 60, 60), null, sp, 3, 3);
        }

        int n = biasDots.Count;
        string saveTag;
        Color barColor;
        if (dirty) { saveTag = "  [UNSAVED]"; barColor = Color.FromRgb(220, 80, 60); }
        else        { saveTag = "  [SAVED]";   barColor = Color.FromRgb(80, 210, 80); }

        string msg = $"CALIBRATE{saveTag}  |  Click=touch  Drag node=warp  RC=add/del dot  S=save  R=reset  X=clear  C=exit  |  {n} dot{(n != 1 ? "s" : "")}";
        var ft  = Fmt(msg, barColor, pixPerDip);
        double bw = ft.Width + 8, bh = ft.Height + 4;
        double bx_ = Math.Max(0, (winSize.Width - bw) / 2);
        double by_ = winSize.Height - bh - 4;
        dc.DrawRectangle(B(0, 0, 0, 180), null, new Rect(bx_, by_, bw, bh));
        dc.DrawText(ft, new Point(bx_ + 4, by_ + 2));
    }

    // t: 0.0 = fully visible (fresh tap / active drag), 1.0 = fully gone (expired)
    public static void DrawTouchMarker(DrawingContext dc, System.Windows.Point pos, double t = 0.0)
    {
        double r     = 10.0 + t * 8.0;                 // 10 → 18 as it fades
        byte   fillA = (byte)(int)(130 * (1.0 - t));
        byte   ringA = (byte)(int)(220 * (1.0 - t));
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(fillA, 195, 195, 195)),
            new Pen(new SolidColorBrush(Color.FromArgb(ringA, 65, 65, 65)), 1.5),
            pos, r, r);
    }

    static readonly Typeface MonoLg = new("Consolas");

    public static void DrawDisconnectedOverlay(DrawingContext dc, Rect fr, double pixPerDip)
    {
        if (fr.Width <= 0 || fr.Height <= 0) return;
        dc.DrawRectangle(Brushes.Black, null, fr);
        var ft = new System.Windows.Media.FormattedText(
            "Disconnected...",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoLg, 18.0,
            new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            pixPerDip);
        dc.DrawText(ft, new Point(
            fr.X + (fr.Width  - ft.Width)  / 2,
            fr.Y + (fr.Height - ft.Height) / 2));
    }

}
