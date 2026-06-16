using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KronosScreenRemote;

public record AudioDevice(string Id, string Name);

public sealed class AudioEngine : IDisposable
{
    const double MinDb = -80.0;

    WasapiCapture? _capture;
    readonly object _lock = new();
    double _levelL = MinDb;
    double _levelR = MinDb;

    public static IReadOnlyList<AudioDevice> GetDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName))
                .ToList();
        }
        catch { return []; }
    }

    public void Start(string deviceId)
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            _capture = new WasapiCapture(device);
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
        lock (_lock) { _levelL = MinDb; _levelR = MinDb; }
    }

    public (double L, double R) GetLevels() { lock (_lock) return (_levelL, _levelR); }
    public bool IsRunning => _capture != null;

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var fmt = _capture!.WaveFormat;
        int ch = fmt.Channels;

        // Track per-sample peak amplitude so the VU meter reaches amber/red for
        // transient-heavy audio (RMS is typically 6-20 dB below peak, which kept
        // the bar permanently below the -12 dBFS amber threshold).
        double peakL = 0, peakR = 0;
        int frames;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            frames = e.BytesRecorded / (ch * 4);
            var buf = e.Buffer;
            for (int i = 0; i < frames; i++)
            {
                double s0 = Math.Abs((double)BitConverter.ToSingle(buf, i * ch * 4));
                double s1 = ch > 1 ? Math.Abs((double)BitConverter.ToSingle(buf, (i * ch + 1) * 4)) : s0;
                if (s0 > peakL) peakL = s0;
                if (s1 > peakR) peakR = s1;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            frames = e.BytesRecorded / (ch * 2);
            var buf = e.Buffer;
            const double scale = 1.0 / 32768.0;
            for (int i = 0; i < frames; i++)
            {
                double s0 = Math.Abs(BitConverter.ToInt16(buf, i * ch * 2) * scale);
                double s1 = ch > 1 ? Math.Abs(BitConverter.ToInt16(buf, (i * ch + 1) * 2) * scale) : s0;
                if (s0 > peakL) peakL = s0;
                if (s1 > peakR) peakR = s1;
            }
        }
        else return;

        if (frames > 0)
        {
            double l = ToDb(peakL);
            double r = ToDb(peakR);
            lock (_lock) { _levelL = l; _levelR = r; }
        }
    }

    static double ToDb(double rms)
        => rms < 1e-10 ? MinDb : Math.Max(MinDb, 20.0 * Math.Log10(rms));

    public void Dispose() => Stop();
}
