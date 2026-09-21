using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KronosScreenRemote;

// What a mouse-down on the overlay grabbed - see OverlayHitTest. TL/TR/BR/BL resize both
// axes from the opposite corner; N/S/E/W resize one axis only, anchored on the opposite
// edge; Move repositions without resizing.
enum OverlayDragKind { Move, TL, TR, BR, BL, N, S, E, W }

// Interactive version of nautilus_palette_selector_brief.md's manual "paired before/after
// screenshot" palette-correction method: paste a reference image (Ctrl+Shift+V) over the
// live frame, drag/resize it into alignment, then auto-assign palette indices from what's
// underneath (A). See MainWindow.Input.cs for the key/mouse wiring into this.
public partial class MainWindow
{

    void PasteOverlayFromClipboard()
    {
        if (!Clipboard.ContainsImage())
        {
            SetNotification("Clipboard has no image to paste", isError: true);
            return;
        }

        BitmapSource? src;
        try { src = Clipboard.GetImage(); }
        catch (Exception ex)
        {
            AppLog.Warn($"[palette-overlay] clipboard read failed: {ex.Message}");
            src = null;
        }
        if (src == null)
        {
            SetNotification("Could not read clipboard image", isError: true);
            return;
        }

        // Bgra32 is a fixed, known byte layout - CopyPixels below assumes it exactly, so
        // convert up front regardless of what format the clipboard image actually arrived in.
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
        if (w <= 0 || h <= 0)
        {
            SetNotification("Clipboard image is empty", isError: true);
            return;
        }
        var pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);

        _ovlBitmapSrc = converted;
        _ovlPixels    = pixels;
        _ovlPixelW    = w;
        _ovlPixelH    = h;
        _ovlStride    = stride;

        // Start centered over the frame, clamped to the frame's own size so an oversized
        // paste (e.g. a full manual-page screenshot) starts fully visible instead of
        // spilling off both edges before the user gets a chance to resize it.
        double startW = Math.Min(w, _frameRect.Width);
        double startH = Math.Min(h, _frameRect.Height);
        _ovlRect = new Rect(
            _frameRect.X + (_frameRect.Width  - startW) / 2,
            _frameRect.Y + (_frameRect.Height - startH) / 2,
            startW, startH);

        SetNotification("Overlay pasted - drag to move, corners to resize, A to apply, Esc to cancel",
            isError: false);
    }

    void ClearPaletteOverlay()
    {
        _ovlBitmapSrc = null;
        _ovlPixels    = null;
        _ovlActiveDrag = null;
        OverlayLayer.InvalidateVisual();
    }

    // Hit-tests the overlay: 4 corner handles (resize both axes), 4 edge handles (resize one
    // axis, running the full length of that edge minus the corner zones), the interior
    // (move), or null (click passes through - not on the overlay at all). Corners are
    // checked first so they win over the edge bands that would otherwise also match near a
    // corner. Tolerance (8px) matches OverlayRenderer's drawn handle size, so the hit zone
    // lines up with what's actually visible.
    OverlayDragKind? OverlayHitTest(Point p)
    {
        if (_ovlBitmapSrc == null) return null;
        const double Tol = 8;

        double left = _ovlRect.Left, right = _ovlRect.Right, top = _ovlRect.Top, bottom = _ovlRect.Bottom;
        bool nearLeft   = Math.Abs(p.X - left)   <= Tol;
        bool nearRight  = Math.Abs(p.X - right)  <= Tol;
        bool nearTop    = Math.Abs(p.Y - top)    <= Tol;
        bool nearBottom = Math.Abs(p.Y - bottom) <= Tol;

        if (nearLeft  && nearTop)    return OverlayDragKind.TL;
        if (nearRight && nearTop)    return OverlayDragKind.TR;
        if (nearRight && nearBottom) return OverlayDragKind.BR;
        if (nearLeft  && nearBottom) return OverlayDragKind.BL;

        bool withinX = p.X >= left - Tol && p.X <= right + Tol;
        bool withinY = p.Y >= top  - Tol && p.Y <= bottom + Tol;

        if (nearTop    && withinX) return OverlayDragKind.N;
        if (nearBottom && withinX) return OverlayDragKind.S;
        if (nearLeft   && withinY) return OverlayDragKind.W;
        if (nearRight  && withinY) return OverlayDragKind.E;

        return _ovlRect.Contains(p) ? OverlayDragKind.Move : null;
    }

    static System.Windows.Input.Cursor OverlayCursorFor(OverlayDragKind? kind) => kind switch
    {
        OverlayDragKind.TL or OverlayDragKind.BR => System.Windows.Input.Cursors.SizeNWSE,
        OverlayDragKind.TR or OverlayDragKind.BL => System.Windows.Input.Cursors.SizeNESW,
        OverlayDragKind.N  or OverlayDragKind.S  => System.Windows.Input.Cursors.SizeNS,
        OverlayDragKind.E  or OverlayDragKind.W  => System.Windows.Input.Cursors.SizeWE,
        OverlayDragKind.Move                     => System.Windows.Input.Cursors.SizeAll,
        _                                         => System.Windows.Input.Cursors.Arrow,
    };

    // Builds a normalized Rect from two opposite corners - used for corner-drag resize,
    // where one corner is the fixed anchor and the other follows the mouse. Independent
    // X/Y sizing (not a fixed aspect ratio) is the point: the reference image and the live
    // frame are rarely the same proportions after cropping to just the matching region.
    static Rect OverlayRectFromCorners(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));

    static string HexOf(PaletteEntry c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    // Ctrl+Click on the overlay: force-assign this ONE clicked pixel's overlay color to
    // whatever index sits under it right now, no vote/quorum/tolerance involved - an escape
    // hatch for a single mismarked entry Apply's algorithm keeps rejecting (or getting
    // "wrong" from the majority of a genuinely mixed region), where the user has visually
    // confirmed alignment is correct for that one spot and just wants it forced.
    void ForceOverlayPixelAt(Point pos)
    {
        if (_ovlPixels == null || _rawFrame == null) return;
        if (!_frameRect.Contains(pos))
        {
            SetNotification("Click a pixel over the live frame to force it", isError: true);
            return;
        }

        int? idx = RawIdxAt(pos);
        if (!idx.HasValue) return;

        if (_locked.Contains(idx.Value))
        {
            SetNotification($"Entry {idx.Value} is locked", isError: true);
            return;
        }

        int ox = Math.Clamp((int)((pos.X - _ovlRect.Left) / _ovlRect.Width  * _ovlPixelW), 0, _ovlPixelW  - 1);
        int oy = Math.Clamp((int)((pos.Y - _ovlRect.Top)  / _ovlRect.Height * _ovlPixelH), 0, _ovlPixelH - 1);
        int po = oy * _ovlStride + ox * 4;
        // Bgra32 byte order: B, G, R, A.
        var color = new PaletteEntry(_ovlPixels[po + 2], _ovlPixels[po + 1], _ovlPixels[po]);

        var old = _overrides.TryGetValue(idx.Value, out var ov) ? ov : (PaletteEntry?)null;
        if (old != color)
        {
            _overrides[idx.Value] = color;
            HistPush(idx.Value, old, color);
            RebuildLut(); ApplyLut();
        }

        _edSel = idx.Value;   // select it too, so the panel confirms what just got forced
        SetNotification($"Forced entry {idx.Value} -> {HexOf(color)}", isError: false);
        OverlayLayer.InvalidateVisual();
    }

    // Two colors "agree" for voting purposes if every channel is within this many levels of
    // each other - 10% of the 0-255 range (255 * 0.10 = 25.5, rounded up). Up to this
    // fraction of an index's total sampled pixels may disagree with the rest ("outliers")
    // without blocking the update - left untouched instead of guessing beyond this. See
    // ApplyPaletteOverlay.
    //
    // Was 0.05 (require 95% agreement); raised to 0.20 after a live diagnostic on a real
    // Korg-manual screenshot source (idx=170, anchor 93A964, real spread out to 7A8850) came
    // back at 88.2% agreement / 11.8% outliers - genuine compression/dither noise, not a bad
    // sample, and the old 5% bar was rejecting perfectly good majorities.
    const int    OverlayAgreementTolerance = 26;
    const double OverlayOutlierQuorum      = 0.20;

    void ApplyPaletteOverlay()
    {
        if (_ovlPixels == null || _rawFrame == null) return;

        var overlap = Rect.Intersect(_ovlRect, _frameRect);
        if (overlap.Width <= 0 || overlap.Height <= 0)
        {
            SetNotification("Overlay doesn't overlap the frame", isError: true);
            return;
        }

        // idx -> exact sampled color -> pixel count, evaluated per underlying raw-frame pixel
        // (not per overlay pixel - several overlay pixels can land on one raw pixel when the
        // overlay is scaled down, and vice versa scaled up), within the overlap only. No
        // minimum-sample floor: an index that's only 1-3 pixels on screen still gets assigned
        // from exactly those pixels.
        var samplesByIndex = new Dictionary<int, Dictionary<PaletteEntry, int>>();

        int fx0 = Math.Clamp((int)Math.Floor((overlap.Left   - _frameRect.X) / _frameRect.Width  * _frameW), 0, _frameW);
        int fx1 = Math.Clamp((int)Math.Ceiling((overlap.Right - _frameRect.X) / _frameRect.Width  * _frameW), 0, _frameW);
        int fy0 = Math.Clamp((int)Math.Floor((overlap.Top     - _frameRect.Y) / _frameRect.Height * _frameH), 0, _frameH);
        int fy1 = Math.Clamp((int)Math.Ceiling((overlap.Bottom - _frameRect.Y) / _frameRect.Height * _frameH), 0, _frameH);

        for (int fy = fy0; fy < fy1; fy++)
        {
            double screenY = _frameRect.Y + (fy + 0.5) * _frameRect.Height / _frameH;
            if (screenY < _ovlRect.Top || screenY >= _ovlRect.Bottom) continue;
            int oy = Math.Clamp((int)((screenY - _ovlRect.Top) / _ovlRect.Height * _ovlPixelH), 0, _ovlPixelH - 1);

            for (int fx = fx0; fx < fx1; fx++)
            {
                double screenX = _frameRect.X + (fx + 0.5) * _frameRect.Width / _frameW;
                if (screenX < _ovlRect.Left || screenX >= _ovlRect.Right) continue;
                int ox = Math.Clamp((int)((screenX - _ovlRect.Left) / _ovlRect.Width * _ovlPixelW), 0, _ovlPixelW - 1);

                int idx = _rawFrame[fy * _frameW + fx];
                if (_locked.Contains(idx)) continue;

                int po = oy * _ovlStride + ox * 4;
                // Bgra32 byte order: B, G, R, A.
                var color = new PaletteEntry(_ovlPixels[po + 2], _ovlPixels[po + 1], _ovlPixels[po]);

                if (!samplesByIndex.TryGetValue(idx, out var counts))
                    samplesByIndex[idx] = counts = new Dictionary<PaletteEntry, int>();
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        }

        int applied = 0, skippedLocked = 0, skippedQuorum = 0;
        foreach (var (idx, counts) in samplesByIndex)
        {
            // Sampling already skips locked indices (never adds them to samplesByIndex), but
            // re-checked here too, right at the write - a second, explicit gate at the point
            // of mutation rather than trusting the accumulation step alone, so this invariant
            // holds even if that loop is ever refactored.
            if (_locked.Contains(idx)) { skippedLocked++; continue; }

            // Anchor = the single most-frequent exact color (the mode) - a FIXED reference
            // point, not a running mean that drifts as members are added, so a smooth
            // gradient can never chain-merge into one blended blob the way incremental
            // clustering did.
            var anchor = counts.OrderByDescending(kv => kv.Value).First().Key;

            long agreeCount = 0, totalCount = 0, sumR = 0, sumG = 0, sumB = 0;
            foreach (var (color, count) in counts)
            {
                totalCount += count;
                bool agrees =
                    Math.Abs(color.R - anchor.R) <= OverlayAgreementTolerance &&
                    Math.Abs(color.G - anchor.G) <= OverlayAgreementTolerance &&
                    Math.Abs(color.B - anchor.B) <= OverlayAgreementTolerance;
                if (!agrees) continue;
                agreeCount += count;
                sumR += (long)color.R * count;
                sumG += (long)color.G * count;
                sumB += (long)color.B * count;
            }

            // Outliers beyond the quorum mean real disagreement (bad alignment/scaling for
            // this region), not noise - leave the index untouched rather than guess.
            double outlierFraction = (totalCount - agreeCount) / (double)totalCount;

            // Diagnostic: exactly what this index saw and why it was or wasn't updated - the
            // top 5 distinct exact colors sampled, with counts, plus the anchor/agree/outlier
            // numbers. Debug-gated (Settings > Debug Logging) rather than always-on Info -
            // this is per-index detail meant for tuning the algorithm, not everyday use.
            if (AppLog.DebugEnabled)
            {
                string top = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"{HexOf(kv.Key)}×{kv.Value}"));
                string outcome = outlierFraction > OverlayOutlierQuorum
                    ? "SKIPPED (quorum)"
                    : $"-> {HexOf(new PaletteEntry((byte)Math.Round(sumR / (double)agreeCount), (byte)Math.Round(sumG / (double)agreeCount), (byte)Math.Round(sumB / (double)agreeCount)))}";
                AppLog.Debug($"[palette-overlay] idx={idx} total={totalCount} anchor={HexOf(anchor)} " +
                    $"agree={agreeCount} ({100 - outlierFraction * 100:F1}%) outlier={totalCount - agreeCount} " +
                    $"({outlierFraction * 100:F1}%) top=[{top}] {outcome}");
            }

            if (outlierFraction > OverlayOutlierQuorum) { skippedQuorum++; continue; }

            // Weighted mean of just the agreeing pixels - outliers are dropped entirely, not
            // blended into the result the way a flat average over everyone would.
            var newColor = new PaletteEntry(
                (byte)Math.Round(sumR / (double)agreeCount),
                (byte)Math.Round(sumG / (double)agreeCount),
                (byte)Math.Round(sumB / (double)agreeCount));
            var old = _overrides.TryGetValue(idx, out var ov) ? ov : (PaletteEntry?)null;
            if (old == newColor) continue;

            _overrides[idx] = newColor;
            HistPush(idx, old, newColor);
            applied++;
        }

        RebuildLut(); ApplyLut();

        string msg = $"Overlay applied: {applied} index(es) updated";
        if (skippedQuorum > 0) msg += $", {skippedQuorum} skipped (sample disagreement)";
        if (skippedLocked > 0) msg += $", {skippedLocked} locked index(es) left untouched";
        SetNotification(msg, isError: false);
        OverlayLayer.InvalidateVisual();
    }
}
