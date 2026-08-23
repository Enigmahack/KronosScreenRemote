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

    // Per-channel peaks for the stereo VU meter - MeteringSampleProvider's own
    // MaxSampleValues is already per-channel (index 0 = left, 1 = right for a
    // 2-channel stream); PeakLevel above just collapsed it with .Max(). Right mirrors
    // Left for a mono stream so a caller never has to branch on channel count.
    public volatile float PeakLevelLeft;
    public volatile float PeakLevelRight;

    // Current playhead position, in frames relative to the loaded buffer - drives the
    // waveform's playhead line. Same polling discipline as PeakLevel (read from the UI
    // thread's timer, written from the provider's own Read() on the audio thread) -
    // each concrete provider (OneShotSampleWaveProvider/LoopingSampleProvider) tracks
    // its own volatile frame counter, since "position" means something different for
    // each (a monotonic buffer offset for one-shot, a wrapping offset for looped).
    public int PositionFrame => _positionGetter?.Invoke() ?? 0;

    public float Volume
    {
        get => _pendingVolume;
        set
        {
            _pendingVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    // -12 dB, applied when the sample's own "12dB boost" flag is OFF (not, per its name,
    // added on top when it's on) - the master-volume chain runs float samples through
    // NAudio's own [-1,1] clamp on the way back to 16-bit, so pushing Volume ABOVE unity
    // for the "boosted" state would hard-clip most real content instead of previewing
    // it. Applying the same 12 dB delta downward instead gives an honest, undistorted
    // A/B of what the flag does on hardware, at the cost of the unboosted preview now
    // being quieter than plain unity - the ViewModel keeps this synced to the selected
    // sample's flag (and live during playback, for A/B toggling) via BoostEnabled.
    const float BoostOffAttenuation = 0.2511886f; // 10^(-12/20)
    bool _boostEnabled;

    public bool BoostEnabled
    {
        get => _boostEnabled;
        set { _boostEnabled = value; ApplyVolume(); }
    }

    void ApplyVolume()
    {
        if (_volumeProvider != null) _volumeProvider.Volume = _pendingVolume * (_boostEnabled ? 1f : BoostOffAttenuation);
    }

    public void Play(short[] pcm, int sampleRate)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(pcm, null, sampleRate);
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
        var provider = new OneShotSampleWaveProvider(pcm, null, sampleRate, startFrame);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    public void PlayStereoFrom(short[] left, short[] right, int sampleRate, int startFrame)
    {
        Stop();
        if (left.Length == 0 && right.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(left, right, sampleRate, startFrame);
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
        var provider = new LoopingSampleProvider(pcm, null, sampleRate, sampleStartFrame, loopStartFrame, loopEndFrame, reverse);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    // True stereo playback of a resolved L/R pair - the provider interleaves the two
    // channels itself, per audio-thread Read() call, rather than this method eagerly
    // building one combined buffer up front (see OneShotSampleWaveProvider's own
    // comment for why). It still pads whichever channel is shorter with silence rather
    // than truncating the longer one, so a mismatch never clips real audio.
    public void PlayStereo(short[] left, short[] right, int sampleRate)
    {
        Stop();
        if (left.Length == 0 && right.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(left, right, sampleRate);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    public void PlayStereoLooped(short[] left, short[] right, int sampleRate, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
    {
        Stop();
        if (left.Length == 0 && right.Length == 0) return;
        var provider = new LoopingSampleProvider(left, right, sampleRate, sampleStartFrame, loopStartFrame, loopEndFrame, reverse);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    void Start(ISampleProvider source)
    {
        _volumeProvider = new VolumeSampleProvider(source);
        ApplyVolume();

        var meter = new MeteringSampleProvider(_volumeProvider, Math.Max(1, source.WaveFormat.SampleRate / 20));
        meter.StreamVolume += (_, e) =>
        {
            var vals = e.MaxSampleValues;
            PeakLevel = vals.Length > 0 ? vals.Max() : 0f;
            PeakLevelLeft = vals.Length > 0 ? vals[0] : 0f;
            PeakLevelRight = vals.Length > 1 ? vals[1] : PeakLevelLeft;
        };

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
            PeakLevelLeft = 0f;
            PeakLevelRight = 0f;
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
        PeakLevelLeft = 0f;
        PeakLevelRight = 0f;
    }

    public void Dispose() => Stop();
}

// Host-order short[] source (mono, or a resolved stereo L/R pair) played once through,
// start to end - the one-shot counterpart to LoopingSampleProvider, tracking its own
// PositionFrame the same way for the playhead.
//
// Reads directly from the CALLER's OWN sample arrays (KsfSample's already-in-memory
// Samples()/waveform buffers) and converts short -> little-endian bytes (and, for
// stereo, interleaves L/R) lazily, a chunk at a time, inside Read() - on NAudio's own
// playback thread. This used to eagerly copy the whole buffer into a fresh byte[] (and,
// for stereo, build a fully-interleaved short[] first) in the CONSTRUCTOR, which runs
// synchronously on the UI thread from every Play/Rewind/Fast-Forward/scrub-click/
// Pause-Resume - tens of MB re-copied per click for a real multi-minute stereo sample,
// a real perf complaint (2026-08-23) once traced past the waveform-selection lag it was
// originally reported alongside. Converting only what's actually requested removes that
// UI-thread stall entirely, and a Stop() partway through never pays for the unplayed
// tail at all.
sealed class OneShotSampleWaveProvider : IWaveProvider
{
    readonly short[] _left;
    readonly short[]? _right; // null for mono
    readonly int _channels;
    readonly int _totalFrames; // pads whichever of left/right is shorter with silence, matching the old eager Interleave's own rule
    volatile int _positionFrame;

    public WaveFormat WaveFormat { get; }
    public int PositionFrame => _positionFrame;

    public OneShotSampleWaveProvider(short[] left, short[]? right, int sampleRate, int startFrame = 0)
    {
        _left = left;
        _right = right;
        _channels = right != null ? 2 : 1;
        _totalFrames = right != null ? Math.Max(left.Length, right.Length) : left.Length;
        WaveFormat = new WaveFormat(sampleRate, 16, _channels);
        _positionFrame = Math.Clamp(startFrame, 0, _totalFrames);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frameBytes = 2 * _channels;
        count -= count % frameBytes;
        int frames = Math.Max(0, Math.Min(count / frameBytes, _totalFrames - _positionFrame));

        if (_channels == 1)
        {
            Buffer.BlockCopy(_left, _positionFrame * 2, buffer, offset, frames * 2);
        }
        else
        {
            int bi = offset;
            for (int i = 0; i < frames; i++)
            {
                WriteFrame(buffer, bi, _positionFrame + i);
                bi += frameBytes;
            }
        }
        _positionFrame += frames;
        return frames * frameBytes;
    }

    void WriteFrame(byte[] buffer, int bi, int frame)
    {
        short l = frame < _left.Length ? _left[frame] : (short)0;
        buffer[bi] = (byte)l;
        buffer[bi + 1] = (byte)(l >> 8);
        short r = _right != null && frame < _right.Length ? _right[frame] : (short)0;
        buffer[bi + 2] = (byte)r;
        buffer[bi + 3] = (byte)(r >> 8);
    }
}

// Mono, or a resolved stereo L/R pair, that plays [sampleStartFrame, loopEndFrame) once
// (the sampler "attack"), then loops [loopStartFrame, loopEndFrame) indefinitely -
// forward (repeat start-to-end), or backward when reverse is true (repeat end-to-start,
// one direction, not a ping-pong) - matching a hardware sampler's own loop-preview
// behavior rather than jumping straight into the loop. IWaveProvider (not WaveStream),
// converted to an ISampleProvider via the standard .ToSampleProvider() extension so it
// can join the shared metered-playback chain in SamplePlayback.Start.
//
// Same lazy-conversion approach as OneShotSampleWaveProvider (see its own comment) -
// cursor state is tracked in FRAMES into the caller's own short[] arrays rather than
// byte offsets into a pre-materialized/pre-interleaved copy, converted to bytes only
// as each chunk is written in Read().
sealed class LoopingSampleProvider : IWaveProvider
{
    readonly short[] _left;
    readonly short[]? _right; // null for mono
    readonly int _channels;
    readonly int _loopStartFrame, _loopEndFrame;
    readonly bool _reverse;

    bool _inIntro;
    int _cursorFrame;     // forward read position (intro, and the non-reverse loop)
    int _reverseFrame;    // current frame index when reverse-looping (counts down)

    public WaveFormat WaveFormat { get; }
    public int PositionFrame => _inIntro || !_reverse ? _cursorFrame : _reverseFrame;

    public LoopingSampleProvider(short[] left, short[]? right, int sampleRate, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
    {
        _left = left;
        _right = right;
        _channels = right != null ? 2 : 1;
        WaveFormat = new WaveFormat(sampleRate, 16, _channels);
        _reverse = reverse;

        int totalFrames = right != null ? Math.Max(left.Length, right.Length) : left.Length;
        int startFrame = Math.Clamp(sampleStartFrame, 0, totalFrames);
        int loopStart = Math.Clamp(loopStartFrame, 0, totalFrames);
        int loopEnd = Math.Clamp(loopEndFrame, 0, totalFrames);
        // Degenerate loop points (end <= start, or a loop-disabled sample with both
        // still at their default 0) fall back to looping the whole buffer rather than
        // producing silence or a zero-length loop that would spin Read() forever.
        if (loopEnd <= loopStart) { loopStart = 0; loopEnd = totalFrames; }

        _loopStartFrame = loopStart;
        _loopEndFrame = loopEnd;

        _inIntro = startFrame < loopStart;
        _cursorFrame = _inIntro ? startFrame : _loopStartFrame;
        _reverseFrame = loopEnd - 1;
    }

    void WriteFrame(byte[] buffer, int bi, int frame)
    {
        short l = frame < _left.Length ? _left[frame] : (short)0;
        buffer[bi] = (byte)l;
        buffer[bi + 1] = (byte)(l >> 8);
        if (_channels == 2)
        {
            short r = _right != null && frame < _right.Length ? _right[frame] : (short)0;
            buffer[bi + 2] = (byte)r;
            buffer[bi + 3] = (byte)(r >> 8);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frameBytes = 2 * _channels;
        count -= count % frameBytes; // stay frame-aligned - simplest correctness guarantee for the reverse path
        int written = 0;

        while (written < count)
        {
            if (_inIntro)
            {
                if (_cursorFrame >= _loopEndFrame)
                {
                    _inIntro = false;
                    if (_reverse) _reverseFrame = _loopEndFrame - 1;
                    else _cursorFrame = _loopStartFrame;
                    continue;
                }
                int avail = _loopEndFrame - _cursorFrame;
                int frames = Math.Min(avail, (count - written) / frameBytes);
                if (frames <= 0) { _inIntro = false; continue; }
                for (int i = 0; i < frames; i++) WriteFrame(buffer, offset + written + i * frameBytes, _cursorFrame + i);
                _cursorFrame += frames;
                written += frames * frameBytes;
                continue;
            }

            if (!_reverse)
            {
                if (_cursorFrame >= _loopEndFrame) _cursorFrame = _loopStartFrame;
                int avail = _loopEndFrame - _cursorFrame;
                int frames = Math.Min(avail, (count - written) / frameBytes);
                if (frames <= 0) { _cursorFrame = _loopStartFrame; continue; }
                for (int i = 0; i < frames; i++) WriteFrame(buffer, offset + written + i * frameBytes, _cursorFrame + i);
                _cursorFrame += frames;
                written += frames * frameBytes;
            }
            else
            {
                if (_reverseFrame < _loopStartFrame) _reverseFrame = _loopEndFrame - 1;
                WriteFrame(buffer, offset + written, _reverseFrame);
                _reverseFrame--;
                written += frameBytes;
            }
        }
        return written;
    }
}
