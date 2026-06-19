using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KronosScreenRemote;

/// <summary>
/// All overlay drawing logic (palette editor, zoom, help, calibration,
/// touch marker, hover tooltip). Called from MainWindow's OverlayElement.RenderCallback.
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

    public static void DrawHoverTooltip(DrawingContext dc, int hovIdx,
        PaletteEntry[] basePal, Dictionary<int, PaletteEntry> ov,
        System.Windows.Point mouse, Size winSize, double pixPerDip)
    {
        var e   = EffRgb(basePal, ov, hovIdx);
        var txt = $"Entry {hovIdx}  R{e.R} G{e.G} B{e.B}";
        var ft  = Fmt(txt, Colors.White, pixPerDip);
        double w = ft.Width + 4, h = ft.Height + 2;
        double tx = Math.Min(mouse.X + 14, winSize.Width  - w - 4);
        double ty = Math.Max(mouse.Y - 18, 4);
        dc.DrawRectangle(B(20, 20, 20), null, new Rect(tx - 2, ty - 1, w, h));
        dc.DrawText(ft, new Point(tx, ty));
    }

    /// <summary>
    /// Draws the palette editor panel.
    /// Returns (panelRect, gridOriginScreen, sliderTopScreen).
    /// </summary>
    public static (Rect panel, Point gridOrigin, double sliderTop) DrawEditor(
        DrawingContext dc, Size winSize,
        PaletteEntry[] basePal, Dictionary<int, PaletteEntry> ov,
        int sel, int ch, string? typed, int? hover,
        HashSet<int> locked, double pixPerDip)
    {
        double panelH = TitleH + 4 + GridPx + 4 + InfoH + 4 + 3 * ChanH + 4 + HintH + 6;
        double px = Math.Max(0, winSize.Width - EdW - 6);
        double py = 6;

        dc.DrawRectangle(B(10, 10, 10, 220), null, new Rect(px, py, EdW, panelH));

        void Txt(string s, double lx, double ly, Color c) =>
            DrawText(dc, s, new Point(px + lx, py + ly), c, pixPerDip);

        Txt("PALETTE  P=close  S=save  Del=revert", 4, 2, Color.FromRgb(170, 170, 170));

        double gy = TitleH + 4;

        for (int i = 0; i < 256; i++)
        {
            int row = i / 16, col = i % 16;
            double rx = px + 4 + col * SW;
            double ry = py + gy + row * SW;
            var e = EffRgb(basePal, ov, i);
            dc.DrawRectangle(B(e.R, e.G, e.B), null, new Rect(rx, ry, SW - 1, SW - 1));
            if (i == sel)
                dc.DrawRectangle(null, P(255, 255, 255), new Rect(rx - 1, ry - 1, SW + 1, SW + 1));
            else if (i == hover)
                dc.DrawRectangle(null, P(255, 200, 0), new Rect(rx - 1, ry - 1, SW + 1, SW + 1));
            if (ov.ContainsKey(i))
                dc.DrawEllipse(B(255, 220, 0), null,
                    new Point(rx + SW - 3, ry + 2), 2, 2);
            if (locked.Contains(i))
                dc.DrawEllipse(B(0, 220, 255), null,
                    new Point(rx + 2, ry + SW - 3), 2, 2);
        }

        double iy = gy + GridPx + 4;
        var es    = EffRgb(basePal, ov, sel);
        bool isLocked = locked.Contains(sel);
        string mod  = ov.ContainsKey(sel) ? "  [*]" : "";
        string ltag = isLocked ? "  [L]" : "";
        Color lcol  = isLocked ? Color.FromRgb(0, 220, 255) : Color.FromRgb(200, 200, 200);
        Txt($"Entry {sel,3}  R{es.R,3} G{es.G,3} B{es.B,3}{mod}{ltag}", 4, iy, lcol);
        dc.DrawRectangle(B(es.R, es.G, es.B), null,
            new Rect(px + EdW - 22, py + iy, 18, InfoH));
        dc.DrawRectangle(null, P(140, 140, 140),
            new Rect(px + EdW - 22, py + iy, 18, InfoH));

        // Channel sliders
        (byte r, byte g, byte b)[] ccols = isLocked
            ? [(80,80,80),(80,80,80),(80,80,80)]
            : [(190,50,50),(50,170,50),(50,80,200)];
        string[] cnames = ["R", "G", "B"];
        int[] vals      = [es.R, es.G, es.B];
        double sy0 = iy + InfoH + 4;

        for (int ci = 0; ci < 3; ci++)
        {
            double sy     = sy0 + ci * ChanH;
            bool   active = ci == ch;
            Color fg      = active ? Colors.White : Color.FromRgb(140, 140, 140);
            Txt(cnames[ci], 4, sy + 3, fg);

            double bx = BarX, by = sy + 5;
            dc.DrawRectangle(B(35, 35, 35), null, new Rect(px + bx, py + by, BarW, BarH));
            double fill = vals[ci] / 255.0 * BarW;
            var (cr, cg, cb) = ccols[ci];
            dc.DrawRectangle(B(cr, cg, cb), null, new Rect(px + bx, py + by, fill, BarH));
            if (active)
                dc.DrawRectangle(null, P(255, 255, 255),
                    new Rect(px + bx - 1, py + by - 1, BarW + 2, BarH + 2));

            string vs; Color vc;
            if (active && typed != null)
                { vs = typed + "_"; vc = Color.FromRgb(255, 240, 80); }
            else
                { vs = vals[ci].ToString(); vc = active ? Color.FromRgb(230,230,230) : Color.FromRgb(110,110,110); }
            Txt(vs, bx + BarW + 4, sy + 3, vc);
        }

        Txt("R/G/B=ch  wheel=±1  Shift+whl=±10  type+↵=set",
            4, sy0 + 3 * ChanH + 4, Color.FromRgb(90, 90, 90));

        var panelRect  = new Rect(px, py, EdW, panelH);
        var gridOrigin = new Point(px + 4, py + gy);
        double slTop   = py + sy0;
        return (panelRect, gridOrigin, slTop);
    }

    public static int? SwatchAt(System.Windows.Point mouse, Point gridOrigin)
    {
        int col = (int)((mouse.X - gridOrigin.X) / SW);
        int row = (int)((mouse.Y - gridOrigin.Y) / SW);
        if (col >= 0 && col < 16 && row >= 0 && row < 16)
            return row * 16 + col;
        return null;
    }

    public static (int ch, int val)? SliderHit(System.Windows.Point mouse, double panelX, double sliderTop)
    {
        double bx = panelX + BarX;
        for (int ci = 0; ci < 3; ci++)
        {
            double by = sliderTop + ci * ChanH + 5;
            if (mouse.X >= bx && mouse.X < bx + BarW && mouse.Y >= by && mouse.Y < by + BarH)
            {
                double frac = (mouse.X - bx) / BarW;
                return (ci, (int)(Math.Clamp(frac, 0, 1) * 255));
            }
        }
        return null;
    }

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
        // from the cursor — keeps the loupe away from the area under examination.
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

        // Crosshair — tracks mouse tip correctly even at frame edges
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

        // Bias dots — drawn at warped position so they move with mesh changes
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

    // ── Boot splash overlay ───────────────────────────────────────────────────
    //
    // splash is drawn scaled to frameRect regardless of its pixel dimensions.
    // fillFraction (0..1) positions the red/grey split within the bar range.
    // Bar geometry is expressed as fractions of the image so any splash resolution works.

    static readonly Brush BootBarRedBrush  = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
    static readonly Brush BootBarGreyBrush = new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0x96));

    // Bar bounds as fractions of the splash image dimensions (derived from 1600×1200 reference)
    const double BootBarFx0 = 140.0  / 1600;  // 0.0875   — left  edge of bar
    const double BootBarFx1 = 1442.0 / 1600;  // 0.90125  — right edge of bar
    const double BootBarFy0 = 859.0  / 1200;  // 0.71583  — top   of bar
    const double BootBarFy1 = 865.0  / 1200;  // 0.72083  — bottom of bar (6 px at 1200 h)

    public static void DrawBootOverlay(DrawingContext dc, Rect fr, BitmapSource splash, double fillFraction)
    {
        if (fr.Width <= 0 || fr.Height <= 0) return;

        dc.DrawImage(splash, fr);

        double ry = fr.Y + BootBarFy0 * fr.Height;
        double rh = (BootBarFy1 - BootBarFy0) * fr.Height;

        double clipped = Math.Clamp(fillFraction, 0.0, 1.0);
        double fillFx  = BootBarFx0 + clipped * (BootBarFx1 - BootBarFx0);

        if (fillFx < BootBarFx1)
        {
            double gx = fr.X + fillFx * fr.Width;
            double gw = (BootBarFx1 - fillFx) * fr.Width;
            dc.DrawRectangle(BootBarGreyBrush, null, new Rect(gx, ry, gw, rh));
        }

        if (fillFx > BootBarFx0)
        {
            double rx = fr.X + BootBarFx0 * fr.Width;
            double rw = (fillFx - BootBarFx0) * fr.Width;
            dc.DrawRectangle(BootBarRedBrush, null, new Rect(rx, ry, rw, rh));
        }
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

    public static void DrawHelp(DrawingContext dc, Size winSize, double pixPerDip,
        Func<string, string>? getKey = null)
    {
        string K(string action, string fallback) => getKey?.Invoke(action) ?? fallback;

        var helpMain = new (string key, string desc)[]
        {
            (K("Help",          "F1"), "Toggle this help"),
            (K("Zoom Window",   "Z"),  "Toggle zoom (2.5–10×)"),
            ("+  /  −",               "Zoom in / out (enables Zoom if off)"),
            (K("AspectLock",    "A"),  "Toggle aspect-ratio lock"),
            (K("Mirror",        "M"),  "Toggle VGA mirror on Kronos"),
            (K("Fullscreen",    "F"),  "Toggle fullscreen"),
            ("~ (fullscreen)",         "Show / hide menu bar"),
            (K("HideDataInput",  "—"),  "Hide/show data input"),
            (K("HideValueInput", "—"),  "Hide/show value input"),
            (K("Calibrate",     "C"),  "Calibrate  |  Click=touch  Drag node=warp  RC=add/del dot  S=save  R=reset  X=clear  C=exit"),
            ("Click (view)",           "Send touch tap to Kronos"),
            ("Scroll",                 "Turn data wheel (always active)"),
            ("Esc",                    "EXIT to Kronos  (or close overlay)"),
            ("Enter",                  "ENTER to Kronos"),
            (K("Mode Setlist",  "F2"), "Mode: Setlist"),
            (K("Mode Combi",    "F3"), "Mode: Combi"),
            (K("Mode Program",  "F4"), "Mode: Program"),
            (K("Mode Sequence", "F5"), "Mode: Sequence"),
            (K("Mode Sampling", "F6"), "Mode: Sampling"),
            (K("Mode Global",   "F7"), "Mode: Global"),
            (K("Mode Disk",     "F8"), "Mode: Disk"),
            ("Ctrl+1–5",              "Window size 75% – 200%"),
            (K("Quit",          "Q"),  "Quit  (prompts if enabled in Settings)"),
        };

        const int LineH = 16, Pad = 12, HeadH = 18;
        double keyW  = helpMain.Max(r => MeasureText(r.key).Width);
        double descW = helpMain.Max(r => MeasureText(r.desc).Width);
        const int ColGap = 14;

        double panW = Pad + keyW + ColGap + descW + Pad;
        double panH = Pad + HeadH + Pad / 2.0
                    + helpMain.Length * LineH + Pad;

        double ppx = (winSize.Width  - panW) / 2;
        double ppy = (winSize.Height - panH) / 2;

        dc.DrawRectangle(B(10, 10, 10, 210), null, new Rect(ppx, ppy, panW, panH));
        dc.DrawRectangle(null, P(80, 80, 80), new Rect(ppx, ppy, panW, panH));

        double y = ppy + Pad;

        void Row(string k, string d, double ry)
        {
            DrawText(dc, k, new Point(ppx + Pad, ry), Color.FromRgb(255, 220, 80),  pixPerDip);
            DrawText(dc, d, new Point(ppx + Pad + keyW + ColGap, ry),
                     Color.FromRgb(200, 200, 200), pixPerDip);
        }

        DrawText(dc, "MAIN CONTROLS", new Point(ppx + Pad, y),
                 Color.FromRgb(160, 200, 255), pixPerDip);
        y += HeadH + Pad / 2.0;
        foreach (var (k, d) in helpMain) { Row(k, d, y); y += LineH; }
    }
}
