namespace KronosScreenRemote;

using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// WASAPI playback for a loaded KsfSample's PCM - the DSP-editing counterpart to
// Audio/AudioEngine.cs's capture. Works on host-order short[] throughout, NOT KsfPcm's
// big-endian on-disk bytes (KsfPcm is specifically the KSF-file boundary; NAudio wants
// its own little-endian format, a separate, standard conversion done here).
//
// Every playback path routes through a shared ISampleProvider chain (Start) so Volume
// and the VU meter work identically for one-shot, looped, and stereo playback - all
// were separate ad-hoc IWaveProvider paths before; unifying them is what let Volume/
// metering/position-tracking slot in once instead of many times.
//
// Volume is a pure in-process software gain (NAudio's own VolumeSampleProvider - a
// per-sample multiply, nothing more), deliberately NOT WasapiOut.Volume. That property
// controls the SHARED-MODE AUDIO SESSION's volume, which Windows surfaces in the
// per-app Volume Mixer and applies its own click-prevention ramp to when changed - the
// "audio fades in" symptom this was replaced to fix. A software multiply has no such
// ramp (it's just arithmetic) and can never reach past this app's own output stage to
// touch anything system- or device-level, satisfying "0..1 relative, never maxing out
// past whatever the system's already at" by construction rather than by convention.
sealed class SamplePlayback : IDisposable
{
    WasapiOut? _output;
    VolumeSampleProvider? _volumeProvider;
    float _pendingVolume = 1f;
    Func<int>? _positionGetter;

    // Which Start() call an in-flight PlaybackStopped notification belongs to. Every
    // Play* entry point calls Stop() first, and WasapiOut.Stop() raises PlaybackStopped
    // - on NAudio's own thread, and the UI-side handler adds another Dispatcher hop on
    // top of that, so the notification for the OLD output routinely lands AFTER the new
    // one has already started and set IsPlaying = true. The UI then believed playback
    // had stopped while audio was still running: the Play/Stop button reverted to Play,
    // Pause greyed out, and the playhead line vanished (it's gated on IsPlaying) - most
    // visibly on Rewind/Fast-Forward mid-playback, which restart via PlayFrom. Stop()
    // bumps this, and each output's handler only forwards the event if its own captured
    // generation is still current, so a stop-for-restart is silent while a genuine
    // end-of-buffer stop (no Stop() call, generation unchanged) still fires.
    int _generation;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public event Action? PlaybackStopped;

    // Peak level (0..1) from the most recent metering notification - updated on
    // NAudio's own playback thread. The UI polls this via a timer rather than
    // reacting to the StreamVolume event directly, so no Dispatcher marshaling is
    // needed for a value that changes many times a second. Metered AFTER the volume
    // stage (see Start), so the VU meter reflects what's actually being sent to the
    // speakers, not the raw pre-volume signal.
    public volatile float PeakLevel;

    // Current playhead position, in frames relative to the loaded buffer - drives the
    // waveform's playhead line. Same polling discipline as PeakLevel (read from the UI
    // thread's timer, written from the provider's own Read() on the audio thread) -
    // each concrete provider (OneShotSampleWaveProvider/LoopingSampleProvider) tracks
    // its own volatile frame counter, since "position" means something different for
    // each (a monotonic buffer offset for one-shot, a wrapping offset for looped).
    public int PositionFrame => _positionGetter?.Invoke() ?? 0;

    public float Volume
    {
        get => _volumeProvider?.Volume ?? _pendingVolume;
        set
        {
            _pendingVolume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null) _volumeProvider.Volume = _pendingVolume;
        }
    }

    public void Play(short[] pcm, int sampleRate)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(pcm, sampleRate, 1);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    // A scrub-click's "play from here" gesture - one-shot from an arbitrary frame to
    // the end of the buffer, deliberately ignoring loop state (this is an audition
    // gesture, not a statement about how the sample plays normally - PlaySelectedSample/
    // PlayLooped remain the loop-aware entry points).
    public void PlayFrom(short[] pcm, int sampleRate, int startFrame)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(pcm, sampleRate, 1, startFrame);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    public void PlayStereoFrom(short[] left, short[] right, int sampleRate, int startFrame)
    {
        Stop();
        var interleaved = Interleave(left, right);
        if (interleaved.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(interleaved, sampleRate, 2, startFrame);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    // Loop-preview: plays sampleStartFrame -> loopEndFrame once (the normal sampler
    // "attack" portion), then repeats [loopStartFrame, loopEndFrame) forever - forward,
    // or backward (end to start, repeatedly) when reverse is true - until Stop() is
    // called. PlaybackStopped only fires on an explicit Stop, never on its own, since a
    // looping provider never runs out of data. Falls back to looping the whole buffer
    // if the loop points are degenerate (end <= start), so this is always safe to call
    // even against a sample whose loop is currently disabled/unset.
    public void PlayLooped(short[] pcm, int sampleRate, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new LoopingSampleProvider(pcm, sampleRate, 1, sampleStartFrame, loopStartFrame, loopEndFrame, reverse);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    // True stereo playback of a resolved L/R pair - interleaves the two channels into
    // one 2-channel buffer (Interleave pads whichever channel is shorter with silence
    // rather than truncating the longer one, so a mismatch never clips real audio).
    public void PlayStereo(short[] left, short[] right, int sampleRate)
    {
        Stop();
        var interleaved = Interleave(left, right);
        if (interleaved.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(interleaved, sampleRate, 2);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    public void PlayStereoLooped(short[] left, short[] right, int sampleRate, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
    {
        Stop();
        var interleaved = Interleave(left, right);
        if (interleaved.Length == 0) return;
        var provider = new LoopingSampleProvider(interleaved, sampleRate, 2, sampleStartFrame, loopStartFrame, loopEndFrame, reverse);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    static short[] Interleave(short[] left, short[] right)
    {
        int len = Math.Max(left.Length, right.Length);
        var result = new short[len * 2];
        for (int i = 0; i < len; i++)
        {
            result[i * 2] = i < left.Length ? left[i] : (short)0;
            result[i * 2 + 1] = i < right.Length ? right[i] : (short)0;
        }
        return result;
    }

    void Start(ISampleProvider source)
    {
        _volumeProvider = new VolumeSampleProvider(source) { Volume = _pendingVolume };

        var meter = new MeteringSampleProvider(_volumeProvider, Math.Max(1, source.WaveFormat.SampleRate / 20));
        meter.StreamVolume += (_, e) => PeakLevel = e.MaxSampleValues.Length > 0 ? e.MaxSampleValues.Max() : 0f;

        // Latency (ms) is how much audio WASAPI keeps pre-buffered - Stop() doesn't
        // flush it, so whatever's already queued keeps playing out for up to this long
        // after Stop is called (audible as a short glitch/tail, worse for a looping
        // source since the queued tail can span a loop wrap). 40ms - matched to the VU
        // timer's own polling interval - trades a little more CPU/dropout risk for a
        // much shorter, still-inaudible-in-practice stop tail versus the previous 100ms.
        int generation = ++_generation;
        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 40);
        _output.Init(meter.ToWaveProvider());
        _output.PlaybackStopped += (_, _) =>
        {
            PeakLevel = 0f;
            if (generation == _generation) PlaybackStopped?.Invoke();
        };
        _output.Play();
    }

    public void Stop()
    {
        _generation++; // retires whatever's currently playing - see _generation's own comment
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _volumeProvider = null;
        _positionGetter = null;
        PeakLevel = 0f;
    }

    public void Dispose() => Stop();
}

// Host-order short[] source (mono or interleaved N-channel) played once through, start
// to end - the one-shot counterpart to LoopingSampleProvider, tracking its own
// PositionFrame the same way for the playhead.
sealed class OneShotSampleWaveProvider : IWaveProvider
{
    readonly byte[] _pcm;
    readonly int _frameBytes;
    volatile int _position; // byte offset into _pcm

    public WaveFormat WaveFormat { get; }
    public int PositionFrame => _position / _frameBytes;

    public OneShotSampleWaveProvider(short[] samples, int sampleRate, int channels, int startFrame = 0)
    {
        _pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, _pcm, 0, _pcm.Length);
        channels = Math.Max(1, channels);
        _frameBytes = 2 * channels;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
        _position = Math.Clamp(startFrame * _frameBytes, 0, _pcm.Length);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int avail = _pcm.Length - _position;
        int toCopy = Math.Max(0, Math.Min(avail, count));
        if (toCopy > 0)
        {
            Array.Copy(_pcm, _position, buffer, offset, toCopy);
            _position += toCopy;
        }
        return toCopy;
    }
}

// Mono or interleaved N-channel host-order short[] source that plays [sampleStartFrame,
// loopEndFrame) once (the sampler "attack"), then loops [loopStartFrame, loopEndFrame)
// indefinitely - forward (repeat start-to-end), or backward when reverse is true
// (repeat end-to-start, one direction, not a ping-pong) - matching a hardware sampler's
// own loop-preview behavior rather than jumping straight into the loop. IWaveProvider
// (not WaveStream), converted to an ISampleProvider via the standard .ToSampleProvider()
// extension so it can join the shared metered-playback chain in SamplePlayback.Start.
sealed class LoopingSampleProvider : IWaveProvider
{
    readonly byte[] _pcm; // little-endian 16-bit, matching NAudio's own convention
    readonly int _frameBytes;
    readonly int _loopStartByte, _loopEndByte;
    readonly bool _reverse;

    bool _inIntro;
    int _cursorByte;      // forward read position (intro, and the non-reverse loop)
    int _reverseFrame;    // current frame index when reverse-looping (counts down)

    public WaveFormat WaveFormat { get; }
    public int PositionFrame => _inIntro || !_reverse ? _cursorByte / _frameBytes : _reverseFrame;

    public LoopingSampleProvider(short[] samples, int sampleRate, int channels, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
    {
        _pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, _pcm, 0, _pcm.Length);
        channels = Math.Max(1, channels);
        _frameBytes = 2 * channels;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
        _reverse = reverse;

        int totalFrames = _pcm.Length / _frameBytes;
        int startFrame = Math.Clamp(sampleStartFrame, 0, totalFrames);
        int loopStart = Math.Clamp(loopStartFrame, 0, totalFrames);
        int loopEnd = Math.Clamp(loopEndFrame, 0, totalFrames);
        // Degenerate loop points (end <= start, or a loop-disabled sample with both
        // still at their default 0) fall back to looping the whole buffer rather than
        // producing silence or a zero-length loop that would spin Read() forever.
        if (loopEnd <= loopStart) { loopStart = 0; loopEnd = totalFrames; }

        _loopStartByte = loopStart * _frameBytes;
        _loopEndByte = loopEnd * _frameBytes;

        _inIntro = startFrame < loopStart;
        _cursorByte = _inIntro ? startFrame * _frameBytes : _loopStartByte;
        _reverseFrame = loopEnd - 1;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        count -= count % _frameBytes; // stay frame-aligned - simplest correctness guarantee for the reverse path
        int written = 0;

        while (written < count)
        {
            if (_inIntro)
            {
                if (_cursorByte >= _loopEndByte)
                {
                    _inIntro = false;
                    if (_reverse) _reverseFrame = _loopEndByte / _frameBytes - 1;
                    else _cursorByte = _loopStartByte;
                    continue;
                }
                int avail = _loopEndByte - _cursorByte;
                int toCopy = Math.Min(avail, count - written);
                toCopy -= toCopy % _frameBytes;
                if (toCopy <= 0) { _inIntro = false; continue; }
                Array.Copy(_pcm, _cursorByte, buffer, offset + written, toCopy);
                _cursorByte += toCopy;
                written += toCopy;
                continue;
            }

            if (!_reverse)
            {
                if (_cursorByte >= _loopEndByte) _cursorByte = _loopStartByte;
                int avail = _loopEndByte - _cursorByte;
                int toCopy = Math.Min(avail, count - written);
                toCopy -= toCopy % _frameBytes;
                if (toCopy <= 0) { _cursorByte = _loopStartByte; continue; }
                Array.Copy(_pcm, _cursorByte, buffer, offset + written, toCopy);
                _cursorByte += toCopy;
                written += toCopy;
            }
            else
            {
                if (_reverseFrame * _frameBytes < _loopStartByte) _reverseFrame = _loopEndByte / _frameBytes - 1;
                Array.Copy(_pcm, _reverseFrame * _frameBytes, buffer, offset + written, _frameBytes);
                _reverseFrame--;
                written += _frameBytes;
            }
        }
        return written;
    }
}
