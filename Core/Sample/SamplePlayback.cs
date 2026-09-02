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

    // WASAPI render-endpoint id (AudioEngine.GetPlaybackDevices()) to play through; empty
    // uses the system default. Takes effect on the next Start(), same "next connect" precedent
    // as AudioEngine's own device selection - not re-routed live mid-playback.
    public string OutputDeviceId { get; set; } = "";
    NAudio.CoreAudioApi.MMDevice? _outputDevice;   // backs _output; must outlive it, disposed in Stop()

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

    // Bumped by every Stop()/Start() call (see _generation's own comment above) - a
    // caller that wants to know "did some OTHER playback start/stop happen since I
    // triggered mine" (SampleEditorViewModel.ReleasePianoKey, deciding whether a
    // mouse-up should still stop what it started) can snapshot this right after
    // starting and compare later, without this class needing to know anything about
    // who its callers are or track per-trigger identity itself.
    public int Generation => _generation;

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

    // 0..127, MIDI pan convention - 0 = full Left, 64 = Center, 127 = full Right. "This
    // app only" playback preview, same scope as Volume (not a persisted KSF field - no
    // such field exists on real hardware for this app's own auditioning). Applied live
    // via PanningSampleProvider, same "set on the live provider if one's running,
    // otherwise just remembered for the next Start()" pattern Volume/BoostEnabled use.
    int _pendingPan = 64;
    PanningSampleProvider? _panProvider;

    public int Pan
    {
        get => _pendingPan;
        set { _pendingPan = Math.Clamp(value, 0, 127); _panProvider?.SetPan(_pendingPan); }
    }

    public void Play(short[] pcm, int sampleRate)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(pcm, null, sampleRate);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    // Piano-key trigger (Sample Editor keymap): plays `pcm` as if struck at `playedKey`,
    // pitch-shifted relative to `originalKey`. A real hardware sampler transposes by
    // playing the SAME recorded audio slower/faster, not by substituting a separately
    // pitch-shifted copy - tape-style, speed and pitch move together - so this just
    // declares a DIFFERENT WaveFormat sample rate on the identical PCM bytes rather than
    // running anything through a pitch-shifting DSP path. No new resampler needed: this
    // app's own WasapiOut output already plays every sample at whatever native rate it
    // was recorded at (Play/PlayFrom above), which is exactly the "declared rate isn't
    // the device's own mix format" case being reused here, just with a rate that's been
    // deliberately shifted rather than the sample's true native one.
    public void PlayAtKey(short[] pcm, int nativeSampleRate, int originalKey, int playedKey, int startFrame = 0, bool reverse = false)
        => PlayFrom(pcm, EffectiveRate(nativeSampleRate, originalKey, playedKey), startFrame, reverse);

    // True-stereo counterpart to PlayAtKey - a resolved L/R pair plays TOGETHER through
    // one interleaved provider (matching how a real stereo instrument sounds on the
    // Kronos), not as two separate mono triggers racing each other through this class's
    // single _output slot.
    public void PlayStereoAtKey(short[] left, short[] right, int nativeSampleRate, int originalKey, int playedKey, int startFrame = 0, bool reverse = false)
        => PlayStereoFrom(left, right, EffectiveRate(nativeSampleRate, originalKey, playedKey), startFrame, reverse);

    // Loop-aware counterparts of PlayAtKey/PlayStereoAtKey - a piano-key trigger for a
    // zone whose sample has its own Loop Enabled flag on should sustain exactly like
    // PlaySelectedSample's loop branch does, not just play the one-shot attack and stop
    // (the bug this pair fixes: the keymap piano previously always went through
    // PlayFrom/PlayStereoFrom regardless of the sample's own loop/reverse flags).
    public void PlayLoopedAtKey(short[] pcm, int nativeSampleRate, int originalKey, int playedKey, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
        => PlayLooped(pcm, EffectiveRate(nativeSampleRate, originalKey, playedKey), sampleStartFrame, loopStartFrame, loopEndFrame, reverse);

    public void PlayStereoLoopedAtKey(short[] left, short[] right, int nativeSampleRate, int originalKey, int playedKey, int sampleStartFrame, int loopStartFrame, int loopEndFrame, bool reverse)
        => PlayStereoLooped(left, right, EffectiveRate(nativeSampleRate, originalKey, playedKey), sampleStartFrame, loopStartFrame, loopEndFrame, reverse);

    // Tape-style speed/pitch shift shared by every *AtKey entry point - see PlayAtKey's
    // own comment for why this is a declared WaveFormat rate change rather than a
    // pitch-shifting DSP path.
    static int EffectiveRate(int nativeSampleRate, int originalKey, int playedKey)
    {
        double ratio = Math.Pow(2.0, (playedKey - originalKey) / 12.0);
        return Math.Clamp((int)Math.Round(nativeSampleRate * ratio), 1000, 384000);
    }

    // A scrub-click's "play from here" gesture - one-shot from an arbitrary frame to
    // the end of the buffer, deliberately ignoring loop state (this is an audition
    // gesture, not a statement about how the sample plays normally - PlaySelectedSample/
    // PlayLooped remain the loop-aware entry points). `reverse` mirrors the real Kronos
    // Reverse flag (SMD1 flags bit 0x40, hardware-confirmed - "reverses playback
    // direction of the whole sample," unconditional on loop state, per the Sample
    // Editor's own tooltip) - when set, playback runs from the END of the buffer DOWN TO
    // `startFrame` instead of from `startFrame` up to the end, the same "same bounds,
    // opposite direction" relationship LoopingSampleProvider already has between its
    // forward and reverse loop modes.
    public void PlayFrom(short[] pcm, int sampleRate, int startFrame, bool reverse = false)
    {
        Stop();
        if (pcm.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(pcm, null, sampleRate, startFrame, reverse);
        _positionGetter = () => provider.PositionFrame;
        Start(provider.ToSampleProvider());
    }

    public void PlayStereoFrom(short[] left, short[] right, int sampleRate, int startFrame, bool reverse = false)
    {
        Stop();
        if (left.Length == 0 && right.Length == 0) return;
        var provider = new OneShotSampleWaveProvider(left, right, sampleRate, startFrame, reverse);
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

    // Falls back to the default-device ctor when no device is chosen, or when the chosen
    // one no longer exists (unplugged since the setting was saved) - playback must never
    // die over a stale device id.
    WasapiOut CreateOutput()
    {
        if (!string.IsNullOrEmpty(OutputDeviceId))
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                _outputDevice = enumerator.GetDevice(OutputDeviceId);
                return new WasapiOut(_outputDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            }
            catch
            {
                _outputDevice?.Dispose();
                _outputDevice = null;
            }
        }
        return new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 40);
    }

    void Start(ISampleProvider source)
    {
        _volumeProvider = new VolumeSampleProvider(source);
        ApplyVolume();

        // Pan is the FINAL software stage before metering - Volume/Boost are plain
        // per-sample scalar multiplies, so applying them before or after pan's
        // per-channel split is mathematically identical; putting pan last means
        // metering always sees the actual post-pan L/R balance, including for a mono
        // source (PanningSampleProvider upmixes it to 2 channels so pan has somewhere
        // to go - see its own comment).
        _panProvider = new PanningSampleProvider(_volumeProvider);
        _panProvider.SetPan(_pendingPan);

        var meter = new MeteringSampleProvider(_panProvider, Math.Max(1, _panProvider.WaveFormat.SampleRate / 20));
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
        //
        // useEventSync: true - the (shareMode, latency) overload this used to call defaults to FALSE, which
        // makes WasapiOut refill the buffer by Thread.Sleep(latency/2)-ing between
        // polls instead of waiting on a real WASAPI-signaled event. Windows' default
        // ~15ms Sleep timer resolution is a significant fraction of a 40ms buffer, so a
        // poll landing late (ordinary scheduler jitter, nothing exotic) can genuinely
        // underrun and produce an audible click - a known NAudio pitfall at low
        // latencies, not something extra app-level threading/async would touch: the
        // audio callback ALREADY runs on WasapiOut's own dedicated thread, never the UI
        // thread, whichever sync mode is used. Event sync instead blocks on a kernel
        // wait handle the audio engine itself signals when it actually needs more data
        // - no fixed poll interval to be late for.
        int generation = ++_generation;
        _output = CreateOutput();
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
        _outputDevice?.Dispose();
        _outputDevice = null;
        _volumeProvider = null;
        _panProvider = null;
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
// a real perf complaint once traced past the waveform-selection lag it was
// originally reported alongside. Converting only what's actually requested removes that
// UI-thread stall entirely, and a Stop() partway through never pays for the unplayed
// tail at all.
sealed class OneShotSampleWaveProvider : IWaveProvider
{
    readonly short[] _left;
    readonly short[]? _right; // null for mono
    readonly int _channels;
    readonly int _totalFrames; // pads whichever of left/right is shorter with silence, matching the old eager Interleave's own rule
    readonly bool _reverse;
    readonly int _endFrame; // forward: exclusive upper bound (_totalFrames). reverse: inclusive lower bound (the caller's startFrame).
    volatile int _positionFrame;

    public WaveFormat WaveFormat { get; }
    public int PositionFrame => _positionFrame;

    // `reverse` plays from the END of the buffer down to `startFrame` instead of from
    // `startFrame` up to the end - same bounds as forward, opposite direction, mirroring
    // the real Kronos Reverse flag (see PlayFrom's own comment for the hardware detail).
    public OneShotSampleWaveProvider(short[] left, short[]? right, int sampleRate, int startFrame = 0, bool reverse = false)
    {
        _left = left;
        _right = right;
        _channels = right != null ? 2 : 1;
        _totalFrames = right != null ? Math.Max(left.Length, right.Length) : left.Length;
        WaveFormat = new WaveFormat(sampleRate, 16, _channels);
        _reverse = reverse;
        int clampedStart = Math.Clamp(startFrame, 0, _totalFrames);
        if (reverse)
        {
            _positionFrame = Math.Max(0, _totalFrames - 1);
            _endFrame = clampedStart;
        }
        else
        {
            _positionFrame = clampedStart;
            _endFrame = _totalFrames;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frameBytes = 2 * _channels;
        count -= count % frameBytes;

        if (!_reverse && _channels == 1)
        {
            int fastFrames = Math.Max(0, Math.Min(count / frameBytes, _endFrame - _positionFrame));
            Buffer.BlockCopy(_left, _positionFrame * 2, buffer, offset, fastFrames * 2);
            _positionFrame += fastFrames;
            return fastFrames * frameBytes;
        }

        int avail = _reverse ? Math.Max(0, _positionFrame - _endFrame + 1) : Math.Max(0, _endFrame - _positionFrame);
        int frames = Math.Min(count / frameBytes, avail);
        int bi = offset;
        for (int i = 0; i < frames; i++)
        {
            WriteFrame(buffer, bi, _positionFrame);
            _positionFrame += _reverse ? -1 : 1;
            bi += frameBytes;
        }
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
    int _cursorFrame;     // intro read position (either direction) and the non-reverse loop
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

        // Reverse's intro is NOT "the same forward intro, reversed at the end" (the bug
        // this fixes - it used to always read _cursorFrame forward here regardless of
        // _reverse, so a Reverse+Loop sample played its attack forward and only started
        // reversing once it reached the loop). It's the mirror of the forward intro's
        // OWN span [sampleStartFrame, loopEnd) - reverse plays that same span backward,
        // from the buffer's true last frame down to Loop Start, exactly once, before
        // handing off to the (already-correct) backward loop-repeat below. sampleStart
        // has no reverse equivalent (there's no "where reverse audio begins" marker on
        // real hardware - only where forward playback used to begin), so it's simply not
        // used for this branch.
        if (reverse)
        {
            _inIntro = totalFrames - 1 >= loopStart;
            _cursorFrame = _inIntro ? totalFrames - 1 : loopEnd - 1;
        }
        else
        {
            _inIntro = startFrame < loopStart;
            _cursorFrame = _inIntro ? startFrame : loopStart;
        }
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
                if (_reverse)
                {
                    // Backward intro - see the constructor's own comment. Single-frame
                    // steps, matching the reverse loop-repeat branch below (not a new
                    // pattern this introduces).
                    if (_cursorFrame < _loopStartFrame)
                    {
                        _inIntro = false;
                        _reverseFrame = _loopEndFrame - 1;
                        continue;
                    }
                    WriteFrame(buffer, offset + written, _cursorFrame);
                    _cursorFrame--;
                    written += frameBytes;
                    continue;
                }

                if (_cursorFrame >= _loopEndFrame)
                {
                    _inIntro = false;
                    _cursorFrame = _loopStartFrame;
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

// Software stereo pan - the ISampleProvider stage SamplePlayback.Start inserts after
// Volume/Boost, before Metering (see Start's own comment for why order there doesn't
// matter mathematically but channel count does). Equal-power (constant-loudness) pan
// law: at center both channels are at unity gain; sweeping to either extreme brings the
// OPPOSITE channel down to silence while the SAME-side channel stays at unity, so
// perceived loudness doesn't dip passing through center the way a naive linear
// crossfade would. A MONO source is upmixed to 2 channels so pan has somewhere to go;
// a source that's ALREADY stereo (a resolved true-stereo L/R pair) keeps its own two
// channels and is balanced between them, rather than being summed to mono first and
// re-panned from scratch - true stereo content keeps its own channel separation.
sealed class PanningSampleProvider : ISampleProvider
{
    readonly ISampleProvider _source;
    readonly int _sourceChannels;
    float[] _scratch = [];
    volatile float _leftGain = 1f, _rightGain = 1f;

    public WaveFormat WaveFormat { get; }

    public PanningSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    // 0..127, MIDI pan convention - 0 = full Left, 64 = Center, 127 = full Right.
    public void SetPan(int pan)
    {
        double t = Math.Clamp(pan, 0, 127) / 127.0; // 0..1
        double angle = t * (Math.PI / 2); // 0..pi/2
        _leftGain = (float)Math.Cos(angle);
        _rightGain = (float)Math.Sin(angle);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Snapshot once per call - SetPan can be written from the UI thread while this
        // runs on the audio thread; torn reads of two independent floats would at worst
        // read a half-updated gain pair for one buffer, never anything unsafe.
        float lg = _leftGain, rg = _rightGain;

        if (_sourceChannels == 1)
        {
            int outFrames = count / 2;
            EnsureScratch(outFrames);
            int read = _source.Read(_scratch, 0, outFrames);
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i * 2] = _scratch[i] * lg;
                buffer[offset + i * 2 + 1] = _scratch[i] * rg;
            }
            return read * 2;
        }

        EnsureScratch(count);
        int readSamples = _source.Read(_scratch, 0, count);
        int frames = readSamples / 2;
        for (int i = 0; i < frames; i++)
        {
            buffer[offset + i * 2] = _scratch[i * 2] * lg;
            buffer[offset + i * 2 + 1] = _scratch[i * 2 + 1] * rg;
        }
        return frames * 2;
    }

    // Reused across Read() calls rather than allocated fresh each time - this class
    // exists in the same "no allocation in the steady-state audio callback" family as
    // OneShotSampleWaveProvider/LoopingSampleProvider (see their own comments); it only
    // grows (never shrinks) since WASAPI requests a stable buffer size per stream.
    void EnsureScratch(int minLength)
    {
        if (_scratch.Length < minLength) _scratch = new float[minLength];
    }
}
