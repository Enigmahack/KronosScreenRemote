namespace KronosScreenRemote;

using System.IO;
using NAudio.Wave;

// WASAPI playback for a loaded KsfSample's PCM - the DSP-editing counterpart to
// Audio/AudioEngine.cs's capture. Works on host-order short[] throughout, NOT KsfPcm's
// big-endian on-disk bytes (KsfPcm is specifically the KSF-file boundary; NAudio wants
// its own little-endian format, a separate, standard conversion done here).
sealed class SamplePlayback : IDisposable
{
    WasapiOut? _output;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public event Action? PlaybackStopped;

    public void Play(short[] pcm, int sampleRate)
    {
        Stop();
        if (pcm.Length == 0) return;

        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        var format = new WaveFormat(sampleRate, 16, 1);
        var stream = new RawSourceWaveStream(new MemoryStream(bytes), format);

        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
        _output.Init(stream);
        _output.PlaybackStopped += (_, _) => PlaybackStopped?.Invoke();
        _output.Play();
    }

    // Loop-preview (Phase 5): plays [loopStartFrame, loopEndFrame) on repeat, forever,
    // until Stop() is called - PlaybackStopped only fires on an explicit Stop, never on
    // its own, since a looping provider never runs out of data. Falls back to looping
    // the whole buffer if the loop points are degenerate (end <= start), so this is
    // always safe to call even against a sample whose loop is currently disabled/unset.
    public void PlayLooped(short[] pcm, int sampleRate, int loopStartFrame, int loopEndFrame)
    {
        Stop();
        if (pcm.Length == 0) return;

        var provider = new LoopingSampleProvider(pcm, sampleRate, loopStartFrame, loopEndFrame);
        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
        _output.Init(provider);
        _output.PlaybackStopped += (_, _) => PlaybackStopped?.Invoke();
        _output.Play();
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();
}

// Mono, 16-bit host-order short[] source that loops [loopStartFrame, loopEndFrame)
// indefinitely - IWaveProvider (not WaveStream), the shape WasapiOut.Init actually
// wants, and simpler than adapting a Stream for something that never really "ends."
sealed class LoopingSampleProvider : IWaveProvider
{
    readonly byte[] _pcm; // little-endian 16-bit, matching NAudio's own convention
    readonly int _loopStartByte, _loopEndByte;
    int _position;

    public WaveFormat WaveFormat { get; }

    public LoopingSampleProvider(short[] samples, int sampleRate, int loopStartFrame, int loopEndFrame)
    {
        _pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, _pcm, 0, _pcm.Length);
        WaveFormat = new WaveFormat(sampleRate, 16, 1);

        int startFrame = Math.Clamp(loopStartFrame, 0, samples.Length);
        int endFrame = Math.Clamp(loopEndFrame, 0, samples.Length);
        _loopStartByte = startFrame * 2;
        _loopEndByte = endFrame * 2;
        // Degenerate loop points (end <= start, or a loop-disabled sample with both
        // still at their default 0) fall back to looping the whole buffer rather than
        // producing silence or a zero-length loop that would spin Read() forever.
        if (_loopEndByte <= _loopStartByte) { _loopStartByte = 0; _loopEndByte = _pcm.Length; }
        _position = _loopStartByte;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_position >= _loopEndByte) _position = _loopStartByte;
            int available = _loopEndByte - _position;
            int toCopy = Math.Min(available, count - written);
            Array.Copy(_pcm, _position, buffer, offset + written, toCopy);
            _position += toCopy;
            written += toCopy;
        }
        return written;
    }
}
