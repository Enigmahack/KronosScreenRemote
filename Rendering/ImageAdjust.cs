namespace KronosScreenRemote;

// How the 800×600 frame is resampled up to the display size.  Maps directly onto WPF's
// BitmapScalingMode on FrameImage — see MainWindow.ApplyScalingMode().
public enum ScalingQuality
{
    Sharp,        // NearestNeighbor — crisp, blocky pixels; no interpolation
    Smooth,       // LowQuality (bilinear) — soft, cheap
    HighQuality,  // Fant — best resample quality (default; matches the prior fixed behaviour)
}

// Pure, stateless image-adjustment math shared by the render pipeline.
//
// Tone (brightness / contrast / gamma) collapses into a single 256-entry per-channel curve, so the
// whole frame is adjusted for free inside the existing palette LUT — no per-pixel cost beyond the
// LUT lookup already done.  Saturation mixes the three curve-adjusted channels toward luma.  Sharpen
// is the only spatial operation: a 3×3 unsharp mask over the packed-BGR32 frame, run once per frame.
//
// All packed integers use the WriteableBitmap Bgr32 layout: 0x00RRGGBB (byte order B,G,R,X in memory).
static class ImageAdjust
{
    // Unsharp-mask strength at the top of the 0..100 sharpen slider.
    public const double MaxSharpen = 1.25;

    public static bool ToneIsIdentity(int brightness, int contrast, double gamma)
        => brightness == 0 && contrast == 0 && System.Math.Abs(gamma - 1.0) < 1e-6;

    public static bool IsIdentity(int brightness, int contrast, double gamma, int saturation)
        => ToneIsIdentity(brightness, contrast, gamma) && saturation == 0;

    // Build a 256-entry per-channel tone curve: contrast (about mid-grey) → brightness offset → gamma.
    // brightness/contrast are in [-100,100]; gamma > 0 (1.0 = linear).
    public static byte[] BuildToneCurve(int brightness, int contrast, double gamma)
    {
        var curve = new byte[256];
        double cf   = 1.0 + System.Math.Clamp(contrast, -100, 100) / 100.0;   // 0..2 contrast gain
        double bo   = System.Math.Clamp(brightness, -100, 100) / 100.0 * 0.5; // ±0.5 lightness offset
        double invG = 1.0 / System.Math.Clamp(gamma, 0.1, 10.0);
        for (int i = 0; i < 256; i++)
        {
            double n = i / 255.0;
            n = (n - 0.5) * cf + 0.5 + bo;
            if (n < 0.0) n = 0.0; else if (n > 1.0) n = 1.0;
            n = System.Math.Pow(n, invG);
            int v = (int)(n * 255.0 + 0.5);
            curve[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return curve;
    }

    // saturation in [-100,100] → factor 0..2 (1.0 = unchanged, 0 = greyscale, 2 = double vividness).
    public static double SaturationFactor(int saturation)
        => 1.0 + System.Math.Clamp(saturation, -100, 100) / 100.0;

    // Apply the tone curve, then saturation, to one (r,g,b) and pack to 0x00RRGGBB.
    public static int ApplyToChannel(byte r, byte g, byte b, byte[] curve, double satFactor)
    {
        int rr = curve[r], gg = curve[g], bb = curve[b];
        if (satFactor != 1.0)
        {
            double luma = 0.299 * rr + 0.587 * gg + 0.114 * bb;
            rr = Clamp8(luma + (rr - luma) * satFactor);
            gg = Clamp8(luma + (gg - luma) * satFactor);
            bb = Clamp8(luma + (bb - luma) * satFactor);
        }
        return (rr << 16) | (gg << 8) | bb;
    }

    static int Clamp8(double v)
    {
        int i = (int)(v + 0.5);
        return i < 0 ? 0 : i > 255 ? 255 : i;
    }

    // 3×3 unsharp mask over a packed 0x00RRGGBB frame:  dst = src + amount * (src − boxblur3(src)).
    // src and dst must be distinct w*h buffers.  Edges use clamped (replicated) sampling so the
    // border is sharpened consistently rather than darkened.
    public static unsafe void UnsharpMask(int* src, int* dst, int w, int h, double amount)
    {
        if (w <= 0 || h <= 0) return;
        for (int y = 0; y < h; y++)
        {
            int* rowUp   = src + (y > 0     ? y - 1 : 0)     * w;
            int* rowMid  = src +  y                          * w;
            int* rowDown = src + (y < h - 1 ? y + 1 : h - 1) * w;
            int* d       = dst +  y                          * w;
            for (int x = 0; x < w; x++)
            {
                int xl = x > 0     ? x - 1 : 0;
                int xr = x < w - 1 ? x + 1 : w - 1;

                int p0 = rowUp[xl],   p1 = rowUp[x],   p2 = rowUp[xr];
                int p3 = rowMid[xl],  p4 = rowMid[x],  p5 = rowMid[xr];
                int p6 = rowDown[xl], p7 = rowDown[x], p8 = rowDown[xr];

                int sumR = ((p0 >> 16) & 0xFF) + ((p1 >> 16) & 0xFF) + ((p2 >> 16) & 0xFF)
                         + ((p3 >> 16) & 0xFF) + ((p4 >> 16) & 0xFF) + ((p5 >> 16) & 0xFF)
                         + ((p6 >> 16) & 0xFF) + ((p7 >> 16) & 0xFF) + ((p8 >> 16) & 0xFF);
                int sumG = ((p0 >> 8) & 0xFF) + ((p1 >> 8) & 0xFF) + ((p2 >> 8) & 0xFF)
                         + ((p3 >> 8) & 0xFF) + ((p4 >> 8) & 0xFF) + ((p5 >> 8) & 0xFF)
                         + ((p6 >> 8) & 0xFF) + ((p7 >> 8) & 0xFF) + ((p8 >> 8) & 0xFF);
                int sumB = (p0 & 0xFF) + (p1 & 0xFF) + (p2 & 0xFF)
                         + (p3 & 0xFF) + (p4 & 0xFF) + (p5 & 0xFF)
                         + (p6 & 0xFF) + (p7 & 0xFF) + (p8 & 0xFF);

                int cr = (p4 >> 16) & 0xFF, cg = (p4 >> 8) & 0xFF, cb = p4 & 0xFF;
                double br = sumR / 9.0, bg = sumG / 9.0, bb2 = sumB / 9.0;

                int outR = (int)(cr + amount * (cr - br)  + 0.5);
                int outG = (int)(cg + amount * (cg - bg)  + 0.5);
                int outB = (int)(cb + amount * (cb - bb2) + 0.5);

                outR = outR < 0 ? 0 : outR > 255 ? 255 : outR;
                outG = outG < 0 ? 0 : outG > 255 ? 255 : outG;
                outB = outB < 0 ? 0 : outB > 255 ? 255 : outB;

                d[x] = (outR << 16) | (outG << 8) | outB;
            }
        }
    }
}
