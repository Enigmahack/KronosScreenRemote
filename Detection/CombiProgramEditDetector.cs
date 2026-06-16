namespace KronosScreenRemote;

using System.Windows.Media;
using System.Windows.Media.Imaging;

// Detects the "program-edit-from-Combi" indicator that appears at frame
// coordinates (696, 39) when editing a Program while inside a Combi.
//
// Reference PNG: Resources/Refs/program_edit_from_combi.png (70×18, RGBA).
//   Pixel (x, y) in the PNG maps to frame position (ScanX+x, ScanY+y).
//   Transparent pixels (A=0) are skipped — the PNG acts as a mask.
//
// Match: ≥98% of masked pixels within ±30 per channel.
static class CombiProgramEditDetector
{
    const int ScanX = 696, ScanY = 39;

    const double MatchThreshold = 0.98;
    const int    ColorTol       = 30;

    readonly record struct PixelRef(int X, int Y, byte R, byte G, byte B);

    static PixelRef[]? _ref;
    static bool _loaded;

    public static bool IsActive(byte[] frame8bpp, int frameW, int[] lut)
    {
        EnsureLoaded();
        return Score(_ref, frame8bpp, frameW, lut) >= MatchThreshold;
    }

    static double Score(PixelRef[]? refs, byte[] frame8bpp, int frameW, int[] lut)
    {
        if (refs == null || refs.Length == 0) return 0.0;
        int matches = 0;
        foreach (var p in refs)
        {
            int fi = p.Y * frameW + p.X;
            if ((uint)fi >= (uint)frame8bpp.Length) continue;
            int  packed = lut[frame8bpp[fi]];
            byte lR = (byte)(packed >> 16);
            byte lG = (byte)(packed >>  8);
            byte lB = (byte) packed;
            if (Math.Abs(lR - p.R) <= ColorTol &&
                Math.Abs(lG - p.G) <= ColorTol &&
                Math.Abs(lB - p.B) <= ColorTol)
                matches++;
        }
        return (double)matches / refs.Length;
    }

    static void EnsureLoaded() { if (!_loaded) { _ref = TryLoad(); _loaded = true; } }

    static PixelRef[]? TryLoad()
    {
        try   { return LoadRef(); }
        catch { return null; }
    }

    static PixelRef[] LoadRef()
    {
        var uri = new Uri("pack://application:,,,/Resources/Refs/program_edit_from_combi.png");
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

        var list = new List<PixelRef>(w * h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            if (pixels[o + 3] == 0) continue;          // transparent — skip
            list.Add(new PixelRef(ScanX + x, ScanY + y,
                pixels[o + 2],   // R  (BGRA32: offset +2)
                pixels[o + 1],   // G  (BGRA32: offset +1)
                pixels[o + 0])); // B  (BGRA32: offset +0)
        }
        return list.ToArray();
    }
}
