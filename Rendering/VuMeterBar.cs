using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KronosScreenRemote;

public sealed class VuMeterBar : FrameworkElement
{
    const double MinDb    = -60.0;   // signals below -60 dBFS are floor noise
    const double MaxDb    =   0.0;
    const double YellowDb = -12.0;
    const double RedDb    =  -6.0;
    const double BarGap   =   2.0;
    const double DecayRate =  22.0;
    const double PeakHold  =   1.5;
    const double PeakDecay =   3.5;
    // WASAPI shared-mode capture path applies Windows mixer headroom reduction,
    // typically 6–12 dB below true 0 dBFS. This trim shifts all levels up so
    // that the captured ceiling maps to 0 dBFS on the meter. Adjust if the
    // clipping peak still doesn't reach the top (increase) or overshoots (decrease).
    const double InputTrimDb = 7.5;

    static readonly Brush BgBrush     = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x12, 0x05)));
    static readonly Brush GreenBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xE0, 0x40)));
    static readonly Brush YellowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0xC8, 0x00)));
    static readonly Brush RedBrush    = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x20, 0x00)));
    static readonly Brush PeakBrush   = Freeze(new SolidColorBrush(Colors.White));

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    readonly double[] _levelDb = { MinDb, MinDb };
    readonly double[] _peakDb  = { MinDb, MinDb };
    readonly double[] _holdT   = { 0.0,   0.0   };
    readonly bool[]   _clip    = { false, false  };

    // Two-segment scale: MinDb..YellowDb fills the bottom 60%, YellowDb..MaxDb fills the top 40%.
    // This gives the -12..0 dBFS danger zone 2.7× more visual space per dB than the quiet range,
    // making amber/red clearly readable and spreading out the near-clip region.
    static double DbToFrac(double db)
    {
        if (db <= MinDb) return 0.0;
        if (db >= MaxDb) return 1.0;
        if (db <= YellowDb)
            return 0.6 * (db - MinDb) / (YellowDb - MinDb);
        return 0.6 + 0.4 * (db - YellowDb) / (MaxDb - YellowDb);
    }

    public void Update(double rawL, double rawR, double dt)
    {
        Advance(0, rawL, dt);
        Advance(1, rawR, dt);
        InvalidateVisual();
    }

    void Advance(int ch, double raw, double dt)
    {
        raw += InputTrimDb;
        if (raw >= _levelDb[ch])
            _levelDb[ch] = raw;
        else
            _levelDb[ch] = Math.Max(raw, _levelDb[ch] - DecayRate * dt);

        if (raw >= _peakDb[ch])
        {
            _peakDb[ch] = raw;
            _holdT[ch]  = PeakHold;
        }
        else
        {
            _holdT[ch] -= dt;
            if (_holdT[ch] <= 0)
            {
                _peakDb[ch] -= PeakDecay * dt;
                if (_peakDb[ch] < _levelDb[ch]) _peakDb[ch] = _levelDb[ch];
            }
        }

        if (raw >= 0.0) _clip[ch] = true;
    }

    public void ResetClip()
    {
        _clip[0] = _clip[1] = false;
        InvalidateVisual();
    }

    public void Reset()
    {
        _levelDb[0] = _levelDb[1] = MinDb;
        _peakDb[0]  = _peakDb[1]  = MinDb;
        _holdT[0]   = _holdT[1]   = 0.0;
        _clip[0]    = _clip[1]    = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ResetClip();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        double barH = Math.Max(1.0, (h - BarGap) / 2.0);

        for (int ch = 0; ch < 2; ch++)
        {
            double y = ch == 0 ? 0 : barH + BarGap;
            DrawBar(dc, w, y, barH, _levelDb[ch], _peakDb[ch], _clip[ch]);
        }
    }

    void DrawBar(DrawingContext dc, double w, double y, double h,
                 double levelDb, double peakDb, bool clip)
    {
        dc.DrawRectangle(BgBrush, null, new Rect(0, y, w, h));

        double fillX   = DbToFrac(levelDb) * w;
        double yellowX = DbToFrac(YellowDb) * w;
        double redX    = DbToFrac(RedDb)    * w;

        if (fillX > 0)
        {
            double g = Math.Min(fillX, yellowX);
            if (g > 0)
                dc.DrawRectangle(GreenBrush, null, new Rect(0, y, g, h));

            if (fillX > yellowX)
            {
                double yl = Math.Min(fillX, redX) - yellowX;
                if (yl > 0)
                    dc.DrawRectangle(YellowBrush, null, new Rect(yellowX, y, yl, h));
            }
            if (fillX > redX)
                dc.DrawRectangle(RedBrush, null, new Rect(redX, y, fillX - redX, h));
        }

        double peakX = DbToFrac(peakDb) * w;
        if (peakX > 1.5)
            dc.DrawRectangle(PeakBrush, null, new Rect(peakX - 1, y, 1.5, h));

        if (clip)
            dc.DrawRectangle(RedBrush, null, new Rect(w - 2, y, 2, h));
    }
}
