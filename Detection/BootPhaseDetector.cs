namespace KronosScreenRemote;

using System.Windows.Media;
using System.Windows.Media.Imaging;

// Detects the Kronos boot loading phase from a 200×30 pixel region
// at (302, 530) in the raw 800×600 8bpp frame.
// Reference images are the original 800×600 client screenshots embedded
// as WPF resources; comparison decodes palette indices via the LUT.
static class BootPhaseDetector
{
    public enum Phase { None = 0, PreloadKSC = 1, BankData = 2, Finishing = 3 }

    // Scan region in 800×600 frame space
    const int ScanX = 302, ScanY = 530, ScanW = 200, ScanH = 30;

    // >98% of scan pixels must match within ±30 per channel
    const double MatchThreshold = 0.98;
    const int    ColorTol       = 30;

    readonly record struct PixelRef(int X, int Y, byte R, byte G, byte B);

    static PixelRef[]? _preloadRef;
    static PixelRef[]? _bankDataRef;
    static PixelRef[]? _finishingRef;
    static bool _loaded;

    // Returns the highest phase whose reference matches the current frame.
    // Call only during boot phase; safe to call on every new frame (scan = 6 000 px × 3).
    public static Phase Identify(byte[] frame8bpp, int frameW, int[] lut)
    {
        EnsureLoaded();
        if (Score(_finishingRef,  frame8bpp, frameW, lut) >= MatchThreshold) return Phase.Finishing;
        if (Score(_bankDataRef,   frame8bpp, frameW, lut) >= MatchThreshold) return Phase.BankData;
        if (Score(_preloadRef,    frame8bpp, frameW, lut) >= MatchThreshold) return Phase.PreloadKSC;
        return Phase.None;
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

    static void EnsureLoaded() { if (!_loaded) { LoadAll(); _loaded = true; } }

    static void LoadAll()
    {
        _preloadRef   = TryLoad("phase_preload.png");
        _bankDataRef  = TryLoad("phase_bankdata.png");
        _finishingRef = TryLoad("phase_finishing.png");
    }

    static PixelRef[]? TryLoad(string filename)
    {
        try   { return LoadRef(filename); }
        catch { return null; }
    }

    static PixelRef[] LoadRef(string filename)
    {
        var uri = new Uri($"pack://application:,,,/Resources/BootPhase/{filename}");
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource   = uri;
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        src.Freeze();

        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;   // should be 800×600
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);

        var list = new List<PixelRef>(ScanW * ScanH);
        for (int y = ScanY; y < ScanY + ScanH && y < h; y++)
        for (int x = ScanX; x < ScanX + ScanW && x < w; x++)
        {
            int o = (y * w + x) * 4;
            list.Add(new PixelRef(x, y,
                pixels[o + 2],   // R  (BGRA32: offset +2)
                pixels[o + 1],   // G  (BGRA32: offset +1)
                pixels[o + 0])); // B  (BGRA32: offset +0)
        }
        return list.ToArray();
    }
}
