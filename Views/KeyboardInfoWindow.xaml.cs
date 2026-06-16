using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class KeyboardInfoWindow : Window
{
    const int HistLen = 60;

    readonly string         _host;
    readonly int            _port;
    readonly DispatcherTimer _timer = new();

    readonly double[] _cpuHistory = new double[HistLen];
    int               _cpuHistNext = 0;
    bool              _histFull    = false;
    bool              _polling     = false;

    long _prevRto    = 0;
    long _prevMidiRt = 0;
    bool _prevRtoSet = false;

    // Canvas shapes — created once, points updated each poll
    readonly Line     _gl25    = new();
    readonly Line     _gl50    = new();
    readonly Line     _gl75    = new();
    readonly Polygon  _cpuArea = new();
    readonly Polyline _cpuLine = new();

    static readonly string[] ModeNames =
        ["Init", "Setlist", "Combi", "Program", "Sequence", "Sampling", "Global", "Disk"];

    public KeyboardInfoWindow(string host, int ctrlPort)
    {
        _host = host;
        _port = ctrlPort;

        InitializeComponent();
        WindowTheme.ApplyDarkCaption(this);

        // ── Graph canvas setup ────────────────────────────────────────────────
        var gridStroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        foreach (var l in new[] { _gl25, _gl50, _gl75 })
        {
            l.Stroke          = gridStroke;
            l.StrokeThickness = 1;
            l.StrokeDashArray = new DoubleCollection([4, 4]);
            CpuCanvas.Children.Add(l);
        }

        _cpuArea.Fill            = new SolidColorBrush(Color.FromArgb(0x30, 0x4E, 0x9F, 0xE5));
        _cpuLine.Stroke          = new SolidColorBrush(Color.FromRgb(0x4E, 0x9F, 0xE5));
        _cpuLine.StrokeThickness = 1.5;
        CpuCanvas.Children.Add(_cpuArea);
        CpuCanvas.Children.Add(_cpuLine);

        CpuCanvas.SizeChanged += (_, _) => RedrawGraph();

        // ── Timer ─────────────────────────────────────────────────────────────
        _timer.Tick += async (_, _) => await PollAndUpdateAsync();

        Loaded  += OnLoaded;
        Closing += (_, _) => _timer.Stop();

        SetStatus(false);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInterval();
        _ = PollAndUpdateAsync();
    }

    void ApplyInterval()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(SelectedIntervalSeconds());
        _timer.Start();
    }

    int SelectedIntervalSeconds()
    {
        if (CBO_Interval.SelectedItem is ComboBoxItem { Tag: string t } &&
            int.TryParse(t, out int v)) return v;
        return 5;
    }

    void CBO_Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyInterval();

    // ── Poll ──────────────────────────────────────────────────────────────────

    async Task PollAndUpdateAsync()
    {
        if (_polling || string.IsNullOrEmpty(_host)) { SetStatus(false); return; }
        _polling = true;
        try
        {
            var raw = await CtrlClient.QueryMultiAsync(_host, _port, "SYSINFO");
            if (raw is null) { SetStatus(false); return; }

            var info = ParseSysInfo(raw);
            if (info is null) { SetStatus(false); return; }

            SetStatus(true);
            ApplyInfo(info);
        }
        finally { _polling = false; }
    }

    void SetStatus(bool ok)
    {
        TXT_Status.Text = ok ? "● Connected" : "● Not connected";
        TXT_Status.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    }

    // ── UI update ─────────────────────────────────────────────────────────────

    void ApplyInfo(SysInfo info)
    {
        // CPU graph
        _cpuHistory[_cpuHistNext] = info.CpuPct >= 0 ? info.CpuPct : 0.0;
        _cpuHistNext = (_cpuHistNext + 1) % HistLen;
        if (!_histFull && _cpuHistNext == 0) _histFull = true;

        TXT_CpuTotal.Text = info.CpuPct >= 0 ? $"{info.CpuPct}%" : "—";
        RedrawGraph();

        // Per-core bars
        SetBar(BarGrid0, TXT_Core0, info.Cores.Length > 0 ? info.Cores[0] : -1);
        SetBar(BarGrid1, TXT_Core1, info.Cores.Length > 1 ? info.Cores[1] : -1);
        SetBar(BarGrid2, TXT_Core2, info.Cores.Length > 2 ? info.Cores[2] : -1);
        SetBar(BarGrid3, TXT_Core3, info.Cores.Length > 3 ? info.Cores[3] : -1);

        // Memory
        if (info.MemTotalKb > 0)
        {
            long usedKb  = info.MemTotalKb - info.MemAvailKb;
            int  usedPct = (int)(usedKb * 100L / info.MemTotalKb);
            SetBar(MemBarGrid, null, usedPct);
            TXT_MemLabel.Text = $"{info.MemAvailKb / 1024} MB free / {info.MemTotalKb / 1024} MB";
        }

        // Uptime, mode, load
        TXT_Uptime.Text = FormatUptime(info.Uptime);
        TXT_Mode.Text   = info.Mode >= 0 && info.Mode < ModeNames.Length
            ? ModeNames[info.Mode] : "?";
        TXT_Load.Text   = string.IsNullOrWhiteSpace(info.Load) ? "—" : info.Load;

        // Audio — OA.ko USB audio bridge; show engine active/idle based on counter delta
        string audio = info.AudioSr > 0 ? $"{info.AudioSr / 1000} kHz · {info.AudioOutCh} ch" : "—";
        if (info.AudioSr > 0)
        {
            bool active = _prevRtoSet
                && (info.AudioRto > _prevRto || info.AudioMidiRt > _prevMidiRt);
            audio += active ? " · Active" : " · Idle";
        }
        _prevRto    = info.AudioRto;
        _prevMidiRt = info.AudioMidiRt;
        _prevRtoSet = true;
        TXT_Audio.Text = audio;

        // /korg/rw disk
        if (info.DiskTotalMb > 0)
        {
            int usedPct = (int)((info.DiskTotalMb - info.DiskFreeMb) * 100L / info.DiskTotalMb);
            SetBar(DiskBarGrid, null, usedPct);
            TXT_DiskLabel.Text = $"{info.DiskFreeMb / 1024} GB free / {info.DiskTotalMb / 1024} GB";
        }

        // /korg/rw2 disk (second internal SSD — section collapses when absent)
        if (info.DiskTotalRw2Mb > 0)
        {
            int usedPct = (int)((info.DiskTotalRw2Mb - info.DiskFreeRw2Mb) * 100L / info.DiskTotalRw2Mb);
            SetBar(DiskRw2BarGrid, null, usedPct);
            TXT_DiskRw2Label.Text = $"{info.DiskFreeRw2Mb / 1024} GB free / {info.DiskTotalRw2Mb / 1024} GB";
            RW2Section.Visibility = Visibility.Visible;
        }
        else
        {
            RW2Section.Visibility = Visibility.Collapsed;
        }

        // USB drives (sections collapse/expand with each poll)
        ApplyUsbSection(USB0Section, TXT_Usb0MntLabel, TXT_Usb0Space, Usb0BarGrid,
            info.UsbCount > 0, info.Usb0Mnt, info.Usb0FreeMb, info.Usb0TotalMb);
        ApplyUsbSection(USB1Section, TXT_Usb1MntLabel, TXT_Usb1Space, Usb1BarGrid,
            info.UsbCount > 1, info.Usb1Mnt, info.Usb1FreeMb, info.Usb1TotalMb);

        // Temperatures — per-sensor color coding (thresholds: 80°C=amber, 90°C=red)
        ApplyTemp(TXT_Temp1, info.Temp1);
        ApplyTemp(TXT_Temp2, info.Temp2);
        ApplyTemp(TXT_Temp3, info.Temp3);
        TXT_Fan.Text = info.Fan1Rpm.HasValue ? $"{info.Fan1Rpm.Value} RPM" : "—";
    }

    static void ApplyTemp(TextBlock txt, int degC)
    {
        if (degC <= 0) { txt.Text = ""; return; }
        txt.Text = $"{degC}°";
        txt.Foreground = new SolidColorBrush(
            degC >= 90 ? Color.FromRgb(0xF4, 0x43, 0x36) :
            degC >= 80 ? Color.FromRgb(0xFF, 0xC1, 0x07) :
                         Color.FromRgb(0xD0, 0xD0, 0xD0));
    }

    static void ApplyUsbSection(StackPanel section, TextBlock mntLabel, TextBlock spaceLabel,
        Grid barGrid, bool visible, string mnt, long freeMb, long totalMb)
    {
        if (!visible || totalMb <= 0) { section.Visibility = Visibility.Collapsed; return; }
        section.Visibility = Visibility.Visible;
        mntLabel.Text  = $"USB  {mnt}";
        spaceLabel.Text = $"{freeMb / 1024} GB free / {totalMb / 1024} GB";
        int usedPct = (int)((totalMb - freeMb) * 100L / totalMb);
        SetBar(barGrid, null, usedPct);
    }

    void RedrawGraph()
    {
        double w = CpuCanvas.ActualWidth;
        double h = CpuCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Dashed grid lines at 25 / 50 / 75 %
        foreach (var (l, yf) in new[] { (_gl75, 0.25), (_gl50, 0.50), (_gl25, 0.75) })
        {
            double y = h * yf;
            l.X1 = 0; l.X2 = w; l.Y1 = y; l.Y2 = y;
        }

        int count = _histFull ? HistLen : _cpuHistNext;
        if (count < 2) return;
        int start = _histFull ? _cpuHistNext : 0;

        var linePts = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double x = (double)i / (count - 1) * w;
            double y = h - _cpuHistory[(start + i) % HistLen] / 100.0 * h;
            linePts.Add(new System.Windows.Point(x, y));
        }
        _cpuLine.Points = linePts;

        // Filled area — close the polygon along the bottom edge
        var areaPts = new PointCollection();
        foreach (var p in linePts) areaPts.Add(p);
        areaPts.Add(new System.Windows.Point(linePts[linePts.Count - 1].X, h));
        areaPts.Add(new System.Windows.Point(linePts[0].X, h));
        _cpuArea.Points = areaPts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static void SetBar(Grid grid, TextBlock? txt, int pct)
    {
        if (txt != null) txt.Text = pct < 0 ? "—" : $"{pct}%";
        int v = pct < 0 ? 0 : Math.Clamp(pct, 0, 100);
        grid.ColumnDefinitions[0].Width = new GridLength(v,       GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(100 - v, GridUnitType.Star);
    }

    static string FormatUptime(long secs)
    {
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalDays  >= 1) return $"{(int)ts.TotalDays}d {ts.Hours:D2}h {ts.Minutes:D2}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        return $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    // ── SYSINFO parser ────────────────────────────────────────────────────────

    record SysInfo(long Uptime, string Load, long MemTotalKb, long MemFreeKb, long MemAvailKb,
                   int CpuPct, int[] Cores, int AudioSr, int AudioOutCh,
                   long AudioRto, long AudioMidiRt, int Mode,
                   long DiskFreeMb, long DiskTotalMb,
                   long DiskFreeRw2Mb, long DiskTotalRw2Mb,
                   int Temp1, int Temp2, int Temp3, int? Fan1Rpm,
                   int UsbCount,
                   string Usb0Mnt, long Usb0FreeMb, long Usb0TotalMb,
                   string Usb1Mnt, long Usb1FreeMb, long Usb1TotalMb);

    static SysInfo? ParseSysInfo(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        if (!d.TryGetValue("UPTIME", out var us) || !long.TryParse(us, out long uptime))
            return null;

        d.TryGetValue("LOAD", out var load);
        long.TryParse(d.GetValueOrDefault("MEM_TOTAL_KB"), out long memTotal);
        long.TryParse(d.GetValueOrDefault("MEM_FREE_KB"),  out long memFree);
        long.TryParse(d.GetValueOrDefault("MEM_AVAIL_KB"), out long memAvail);
        int.TryParse( d.GetValueOrDefault("CPU_PCT"),       out int  cpuPct);
        int.TryParse( d.GetValueOrDefault("AUDIO_SR"),      out int  sr);
        int.TryParse( d.GetValueOrDefault("AUDIO_OUT_CH"),  out int  ch);
        long.TryParse(d.GetValueOrDefault("AUDIO_RTO"),     out long rto);
        long.TryParse(d.GetValueOrDefault("AUDIO_MIDI_RT"), out long midiRt);
        int.TryParse( d.GetValueOrDefault("MODE"),          out int  mode);
        long.TryParse(d.GetValueOrDefault("DISK_FREE_MB"),   out long diskFree);
        long.TryParse(d.GetValueOrDefault("DISK_TOTAL_MB"),  out long diskTotal);
        long.TryParse(d.GetValueOrDefault("RW2_FREE_MB"),    out long diskFreeRw2);
        long.TryParse(d.GetValueOrDefault("RW2_TOTAL_MB"),   out long diskTotalRw2);
        int.TryParse( d.GetValueOrDefault("TEMP1"),          out int  temp1);
        int.TryParse( d.GetValueOrDefault("TEMP2"),          out int  temp2);
        int.TryParse( d.GetValueOrDefault("TEMP3"),          out int  temp3);
        int? fan1 = d.TryGetValue("FAN1_RPM", out var fanStr) && int.TryParse(fanStr, out int fv)
                    ? fv : null;
        int.TryParse( d.GetValueOrDefault("USB_COUNT"),      out int  usbCount);
        d.TryGetValue("USB0_MNT",                            out var  usb0Mnt);
        long.TryParse(d.GetValueOrDefault("USB0_FREE_MB"),   out long usb0Free);
        long.TryParse(d.GetValueOrDefault("USB0_TOTAL_MB"),  out long usb0Total);
        d.TryGetValue("USB1_MNT",                            out var  usb1Mnt);
        long.TryParse(d.GetValueOrDefault("USB1_FREE_MB"),   out long usb1Free);
        long.TryParse(d.GetValueOrDefault("USB1_TOTAL_MB"),  out long usb1Total);

        var cores = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            if (d.TryGetValue($"CPU{i}_PCT", out var cs) && int.TryParse(cs, out int cv))
                cores.Add(cv);
            else
                break;
        }

        return new SysInfo(uptime, load ?? "", memTotal, memFree, memAvail,
                           cpuPct, [.. cores], sr, ch, rto, midiRt, mode,
                           diskFree, diskTotal, diskFreeRw2, diskTotalRw2,
                           temp1, temp2, temp3, fan1,
                           usbCount,
                           usb0Mnt ?? "", usb0Free, usb0Total,
                           usb1Mnt ?? "", usb1Free, usb1Total);
    }
}
