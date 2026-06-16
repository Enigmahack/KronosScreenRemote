namespace KronosScreenRemote;

using System.Windows.Media;
using System.Windows.Media.Imaging;

// Identifies the active Kronos mode and help-screen state from the top-left
// 140×55 pixel region of the raw 8bpp frame.
//
// ── Reference PNGs ──────────────────────────────────────────────────────────
// Embedded as WPF resources: Resources/Refs/mode_1.png … mode_7.png + help.png
//   (Setlist=1, Combi=2, Program=3, Sequence=4, Sampling=5, Global=6, Disk=7)
//
// Format   : 140×55 RGBA PNG, Kronos native pixel coordinates (top-left origin).
//   Transparent pixels (A=0) are ignored.
//   Non-transparent pixels define the comparison mask.
//
// Row bands are enforced at load time — pixels outside the band are discarded
// even if non-transparent, so the two detectors are completely independent:
//   mode_N.png : only rows  0–26 are used  (mode banner)
//   help.png   : only rows 27–55 are used  (help banner)
//
// ── Matching ────────────────────────────────────────────────────────────────
// For each non-transparent reference pixel at (x, y):
//   live RGB = palette_lut[ frame8bpp[y * frameW + x] ]   (unpacked from _lut)
//   match    = |ΔR| ≤ 30 && |ΔG| ≤ 30 && |ΔB| ≤ 30
// Score = matched / total_masked.  Mode / help declared when score ≥ 85 %.
// Tolerates minor palette variation across firmware versions or units.
//
// Loading is lazy (first call) and happens once per app lifetime.
static class ModeDetector
{
    readonly struct PixelRef(int x, int y, byte r, byte g, byte b)
    {
        public readonly int  X = x, Y = y;
        public readonly byte R = r, G = g, B = b;
    }

    static readonly PixelRef[]?[] _modeRefs = new PixelRef[]?[8]; // indices 1–7
    static PixelRef[]? _helpRef;
    static bool _loaded = false;

    const byte   ColorTolerance    = 30;   // ±30 per channel (~12 % of 255)
    const double ModeThreshold     = 0.85; // 85 % of masked pixels must match
    const double HelpThreshold     = 0.97; // 97 % — help must be fully rendered, not partial

    // lut[index] = (R<<16)|(G<<8)|B  (MainWindow._lut).
    public static int Identify(byte[] frame8bpp, int frameW, int[] lut)
    {
        EnsureLoaded();
        int    bestMode  = 0;
        double bestScore = ModeThreshold - double.Epsilon;
        for (int m = 1; m <= 7; m++)
        {
            double s = Score(_modeRefs[m], frame8bpp, frameW, lut);
            if (s > bestScore) { bestScore = s; bestMode = m; }
        }
        return bestMode;
    }

    // Only pixels in rows 27–55 of help.png are compared (enforced at load time).
    public static bool IsHelpActive(byte[] frame8bpp, int frameW, int[] lut)
    {
        EnsureLoaded();
        return Score(_helpRef, frame8bpp, frameW, lut) >= HelpThreshold;
    }

    // True if at least one mode reference PNG loaded successfully.
    public static bool HasAny()
    {
        if (!_loaded) return false;
        for (int m = 1; m <= 7; m++) if (_modeRefs[m] != null) return true;
        return false;
    }

    static void EnsureLoaded() { if (!_loaded) { LoadAll(); _loaded = true; } }

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

    static void LoadAll()
    {
        for (int m = 1; m <= 7; m++)
            _modeRefs[m] = TryLoad($"mode_{m}.png", yMin: 0,  yMax: 26);
        _helpRef        = TryLoad("help.png",        yMin: 27, yMax: 55);
    }

    static PixelRef[]? TryLoad(string filename, int yMin, int yMax)
    {
        try   { return LoadRef(filename, yMin, yMax); }
        catch { return null; }
    }

    static PixelRef[] LoadRef(string filename, int yMin, int yMax)
    {
        var uri = new Uri($"pack://application:,,,/Resources/Refs/{filename}");
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource    = uri;
        src.CacheOption  = BitmapCacheOption.OnLoad;
        src.EndInit();
        src.Freeze();
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);

        var list = new List<PixelRef>(capacity: w * h / 4);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (y < yMin || y > yMax) continue;        // outside this ref's row band
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
