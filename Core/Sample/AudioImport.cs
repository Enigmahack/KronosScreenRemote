namespace KronosScreenRemote;

using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Decodes an arbitrary audio file down to the Sample Editor's own native format: mono,
// 44100 Hz, 16-bit signed host-order short[] - ready to hand straight to
// KsfSample.SetSamples (the Kronos never sees anything but that). WAV goes through
// NAudio.Wave.WaveFileReader (any bit depth NAudio itself understands); everything
// else (MP3, MP4/M4A, WMA...) goes through NAudio.Wave.MediaFoundationReader, i.e.
// Windows's own built-in codecs - this app is already Windows-only, so that's zero
// extra dependency beyond the NAudio package already used for playback/capture.
static class AudioImport
{
    public const int TargetSampleRate = 44100;

    public static short[] ImportToMono44100(string path)
    {
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
        return ConvertToMono44100(reader);
    }

    // Cheap peek at the source's own channel count (opens the container/header only, no
    // decode) - lets a caller decide mono vs. stereo import BEFORE committing to either
    // decode path, rather than always downmixing.
    public static int GetSourceChannelCount(string path)
    {
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
        return reader.WaveFormat.Channels;
    }

    // Split out from ImportToMono44100 so self-tests can feed a synthetic in-memory
    // WaveStream (a real MP3/MP4 needs an actual file + Windows Media Foundation, so
    // only the WAV path is exercised off-hardware - see SampleTranscodeSelfTests's own
    // comment on that scoping).
    public static short[] ConvertToMono44100(WaveStream reader)
    {
        ISampleProvider provider = reader.ToSampleProvider(); // float, source rate/channels
        if (provider.WaveFormat.SampleRate != TargetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

        int channels = provider.WaveFormat.Channels;
        var floatBuf = new float[4096 * Math.Max(1, channels)];
        var mono = new List<short>();
        int read;
        while ((read = provider.Read(floatBuf, 0, floatBuf.Length)) > 0)
        {
            // Average every channel into one, not a left-only drop - keeps content
            // from all channels rather than silently discarding the rest. Floor by
            // channels rather than trusting `read` to always be frame-aligned.
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++) sum += floatBuf[baseIdx + c];
                mono.Add(ToShort(sum / channels));
            }
        }
        return mono.ToArray();
    }

    public static (short[] left, short[] right) ImportStereoToLR44100(string path)
    {
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
        return ConvertToStereo44100(reader);
    }

    // Split out for the same reason ConvertToMono44100 is. A mono source gets
    // duplicated into both channels - a deliberate choice (build a true stereo pair
    // from mono source material) rather than refusing. A source with more than 2
    // channels uses only the first two; anything past that is out of scope.
    public static (short[] left, short[] right) ConvertToStereo44100(WaveStream reader)
    {
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.SampleRate != TargetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

        int channels = provider.WaveFormat.Channels;
        var floatBuf = new float[4096 * Math.Max(1, channels)];
        var left = new List<short>();
        var right = new List<short>();
        int read;
        while ((read = provider.Read(floatBuf, 0, floatBuf.Length)) > 0)
        {
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                float l = floatBuf[baseIdx];
                float r = channels >= 2 ? floatBuf[baseIdx + 1] : l;
                left.Add(ToShort(l));
                right.Add(ToShort(r));
            }
        }
        return (left.ToArray(), right.ToArray());
    }

    static short ToShort(float sample) => (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue);
}
