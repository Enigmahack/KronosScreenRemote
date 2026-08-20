namespace KronosScreenRemote;

using SoundTouch;

// Wraps SoundTouch.Net's SoundTouchProcessor (LGPL-2.1-or-later, dynamically linked -
// see kronosology's sample-format doc for the licensing decision) for independent
// tempo/pitch control over host-order short[] PCM. Never touches on-disk big-endian
// bytes directly - callers convert via KsfPcm at the KsfSample boundary.
//
// SoundTouchProcessor works in float samples normalized to [-1, 1], not short - the
// short<->float conversion happens once at each edge here, nowhere else.
static class TempoPitchProcessor
{
    public static short[] ChangeTempo(short[] pcm, int sampleRate, double tempoRatio) =>
        Process(pcm, sampleRate, tempo: tempoRatio, pitchSemitones: 0);

    public static short[] ChangePitchSemitones(short[] pcm, int sampleRate, double semitones) =>
        Process(pcm, sampleRate, tempo: 1.0, pitchSemitones: semitones);

    public static short[] ChangeTempoAndPitch(short[] pcm, int sampleRate, double tempoRatio, double semitones) =>
        Process(pcm, sampleRate, tempo: tempoRatio, pitchSemitones: semitones);

    static short[] Process(short[] pcm, int sampleRate, double tempo, double pitchSemitones)
    {
        if (pcm.Length == 0) return pcm;

        var proc = new SoundTouchProcessor
        {
            SampleRate = sampleRate,
            Channels = 1,   // mono throughout this format family (doc §3)
            Tempo = tempo,
            PitchSemiTones = pitchSemitones,
        };

        var inputFloat = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            inputFloat[i] = pcm[i] / 32768f;

        proc.PutSamples(inputFloat, inputFloat.Length);
        proc.Flush();

        var output = new List<float>(pcm.Length);
        Span<float> chunk = new float[4096];
        int received;
        while ((received = proc.ReceiveSamples(chunk, chunk.Length)) > 0)
            for (int i = 0; i < received; i++)
                output.Add(chunk[i]);

        var result = new short[output.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = (short)Math.Clamp(output[i] * 32768f, short.MinValue, short.MaxValue);
        return result;
    }
}
