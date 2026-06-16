using System.Windows.Media.Imaging;

namespace KronosScreenRemote;

public partial class MainWindow
{
    // ── Boot splash helpers ───────────────────────────────────────────────────

    BitmapSource? GetBootSplash()
    {
        if (_bootSplash != null) return _bootSplash;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = new Uri("pack://application:,,,/Resources/Images/BootSplash.png");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _bootSplash = bmp;
        }
        catch { /* missing resource — overlay won't show */ }
        return _bootSplash;
    }

    // Returns true when more than `threshold` of frame pixels are near-black (all channels ≤20).
    // Default 0.90 (90%) is used to suppress mode detection during boot — only palette index 0
    // (0,0,2) qualifies, preventing false positives against dark reference pixels.
    // The 0.60 variant gates boot-splash display: if real UI content is visible (>40% non-black)
    // the splash is suppressed even when _bootPhase is true, avoiding false overlay during popups.
    static bool IsFrameMostlyBlack(byte[] frame, int[] lut, double threshold = 0.90)
    {
        int black = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            int rgb = lut[frame[i]];
            if (((rgb >> 16) & 0xFF) <= 20 &&
                ((rgb >>  8) & 0xFF) <= 20 &&
                ( rgb        & 0xFF) <= 20)
                black++;
        }
        return black > frame.Length * threshold;
    }

    void ResetBootState()
    {
        _bootPhase          = false;
        _detectedModeEver   = false;
        _frameIsMostlyBlack = false;
        _bootFirstFrame     = DateTime.MinValue;
        _bootPhaseStart     = DateTime.MinValue;
        _bootLoadPhase      = BootPhaseDetector.Phase.None;
        _preloadTimerStart  = DateTime.MinValue;
        _bankDataDetectedAt = DateTime.MinValue;
        _finishingFillFrac  = BootBarF_StaticEnd;
        _preloadSchedule    = null;
    }

    // Builds _preloadSchedule: 25 one-second pauses randomly distributed across 20 s of
    // active movement = 45 s total wall time.  Called once when boot phase enters.
    void BuildPreloadSchedule()
    {
        const int    pauseCount    = 25;
        const double activeTotal   = 20.0;
        const double pauseDuration = 1.0;

        // Random active-time positions (0..20 s) at which pauses are injected
        var rng = new Random();
        var pts = new double[pauseCount];
        for (int i = 0; i < pauseCount; i++) pts[i] = rng.NextDouble() * activeTotal;
        Array.Sort(pts);

        var segs  = new List<(double, double)>(pauseCount * 2 + 1);
        double wall = 0, prog = 0;

        for (int i = 0; i < pauseCount; i++)
        {
            double active = pts[i] - prog;
            if (active > 1e-9) { wall += active; prog += active; segs.Add((wall, prog)); }
            wall += pauseDuration;
            segs.Add((wall, prog));   // pause: wall advances, progress stays
        }
        double tail = activeTotal - prog;
        if (tail > 1e-9) { wall += tail; prog = activeTotal; segs.Add((wall, prog)); }

        _preloadSchedule = segs.ToArray();
    }

    // Maps wall-clock elapsed seconds to effective preload progress (0..1).
    double GetPreloadProgress(double elapsed)
    {
        if (_preloadSchedule == null) return Math.Clamp(elapsed / 20.0, 0, 1);

        double prevWall = 0, prevProg = 0;
        foreach (var (wallEnd, progEnd) in _preloadSchedule)
        {
            if (elapsed <= wallEnd)
            {
                double wallSpan = wallEnd - prevWall;
                double progSpan = progEnd - prevProg;
                if (progSpan < 1e-9 || wallSpan < 1e-9) return prevProg / 20.0; // pause
                return (prevProg + (elapsed - prevWall) / wallSpan * progSpan) / 20.0;
            }
            prevWall = wallEnd;
            prevProg = progEnd;
        }
        return 1.0; // past end of schedule
    }

    // Returns fill as a fraction (0..1) of the bar range — resolution-independent.
    // Phases are strictly forward; each segment pauses at its end until the next phase is detected.
    // Result is snapped to 1% increments so the bar advances in discrete steps.
    double ComputeBootFillFraction()
    {
        if (_bootLoadPhase == BootPhaseDetector.Phase.Finishing)
            return _finishingFillFrac;

        double raw;
        if (_bootLoadPhase == BootPhaseDetector.Phase.BankData && _bankDataDetectedAt != DateTime.MinValue)
        {
            double t = Math.Clamp((DateTime.Now - _bankDataDetectedAt).TotalSeconds / 5.0, 0, 1);
            raw = BootBarF_BankStart + (BootBarF_BankEnd - BootBarF_BankStart) * t;
        }
        else if (_preloadTimerStart != DateTime.MinValue)
        {
            double elapsed = (DateTime.Now - _preloadTimerStart).TotalSeconds;
            double t = Math.Clamp(GetPreloadProgress(elapsed), 0, 1);
            raw = BootBarF_StaticEnd + (BootBarF_PreloadEnd - BootBarF_StaticEnd) * t;
        }
        else
        {
            raw = BootBarF_StaticEnd;
        }

        // Snap to 1% steps of bar range
        double snapped = Math.Floor(raw / 0.01) * 0.01;
        return Math.Max(snapped, BootBarF_StaticEnd);
    }
}
