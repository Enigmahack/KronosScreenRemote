namespace KronosScreenRemote;

using System.Windows.Media;
using System.Windows.Media.Imaging;

// Detects whether the Kronos Help overlay is showing, from the top-left 140×55
// pixel region of the raw 8bpp frame (rows 27-55 = help banner; see TopLeftOcr).
//
// ── Reference PNG ───────────────────────────────────────────────────────────
// Embedded as a WPF resource: Resources/Refs/help.png (140×55 RGBA).
//   Only rows 27–55 are used (enforced at load time) so this never overlaps the
//   mode banner (rows 0–26), which the daemon's STATE command now reports directly.
//   Transparent pixels (A=0) are ignored; non-transparent pixels define the mask.
//
// ── Matching ────────────────────────────────────────────────────────────────
// For each non-transparent reference pixel at (x, y):
//   live RGB = palette_lut[ frame8bpp[y * frameW + x] ]   (unpacked from _lut)
//   match    = |ΔR| ≤ 30 && |ΔG| ≤ 30 && |ΔB| ≤ 30
// Score = matched / total_masked. Help declared active when score ≥ 97%
// (must be fully rendered, not partial).
//
// Loading is lazy (first call) and happens once per app lifetime.
static class HelpDetector
{
    readonly struct PixelRef(int x, int y, byte r, byte g, byte b)
    {
        public readonly int  X = x, Y = y;
        public readonly byte R = r, G = g, B = b;
    }

    static PixelRef[]? _helpRef;
    static bool _loaded;

    const byte   ColorTolerance = 30;   // ±30 per channel (~12 % of 255)
    const double HelpThreshold  = 0.97; // 97 % — help must be fully rendered, not partial

    // lut[index] = (R<<16)|(G<<8)|B  (MainWindow._lut).
    public static bool IsHelpActive(byte[] frame8bpp, int frameW, int[] lut)
    {
        EnsureLoaded();
        return Score(_helpRef, frame8bpp, frameW, lut) >= HelpThreshold;
    }

    static void EnsureLoaded() { if (!_loaded) { _helpRef = TryLoad(); _loaded = true; } }

    static double Score(PixelRef[]? refs, byte[] frame8bpp, int frameW, int[] lut)
    {
        if (refs == null || refs.Length == 0) return 0.0;
        int matches = 0;
        foreach (ref readonly var p in refs.AsSpan())
        {
            int fi = p.Y * frameW + p.X;
            if ((uint)fi >= (uint)frame8bpp.Length) continue;
            int  packed = lut[frame8bpp[fi]];
            byte lR = (byte)(packed >> 16);
            byte lG = (byte)(packed >> 8);
            byte lB = (byte)packed;
            if (Math.Abs(lR - p.R) <= ColorTolerance &&
                Math.Abs(lG - p.G) <= ColorTolerance &&
                Math.Abs(lB - p.B) <= ColorTolerance)
                matches++;
        }
        return (double)matches / refs.Length;
    }

    static PixelRef[]? TryLoad()
    {
        try   { return LoadRef(); }
        catch { return null; }
    }

    static PixelRef[] LoadRef()
    {
        var uri = new Uri("pack://application:,,,/Resources/Refs/help.png");
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource   = uri;
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        src.Freeze();
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);

        var list = new List<PixelRef>(capacity: w * h / 4);
        for (int y = 27; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            if (pixels[o + 3] == 0) continue;          // transparent — skip
            list.Add(new PixelRef(x, y,
                pixels[o + 2],   // R  (BGRA32: offset +2)
                pixels[o + 1],   // G  (BGRA32: offset +1)
                pixels[o + 0])); // B  (BGRA32: offset +0)
        }
        return list.ToArray();
    }
}
