namespace KronosScreenRemote;

// Multi-resolution min/max summary of one sample buffer - the standard waveform "mipmap",
// and what stops zooming from having to touch every frame in view. Built once per loaded
// sample (SampleWaveformControl caches it by array reference); every later zoom/pan reads
// the summary instead of the PCM.
//
// Deliberately NOT decimation ("keep every Nth sample"). Dropping samples drops PEAKS, so
// the drawn envelope would shrink, change shape at every zoom level, and swallow short
// transients entirely - a click a few frames wide would simply stop existing once zoomed
// out. Min/max buckets are lossless for this purpose instead: the min/max OF a set of
// min/max pairs is exactly the min/max of the samples underneath them, so the envelope
// drawn from any level is the same envelope the raw PCM would have drawn, differing only
// in which bucket a column boundary happens to land in.
//
// Cost: one pass over the samples for the first level, and each level after that is built
// from the level below rather than from the PCM again, so the whole pyramid is ~1.33
// passes total. Memory is ~1/64th of the sample buffer across all levels combined.
sealed class WaveformPyramid
{
    // The finest summary covers 256 frames; each level above it is 4x coarser again.
    const int BaseBucket = 256;
    const int LevelFactor = 4;

    internal readonly record struct Level(short[] Min, short[] Max, int Bucket);

    readonly List<Level> _levels = [];

    public short[] Samples { get; }

    public WaveformPyramid(short[] samples)
    {
        Samples = samples;
        short[]? prevMin = null, prevMax = null;
        int prevBucket = 1;

        for (int bucket = BaseBucket; bucket <= samples.Length; bucket *= LevelFactor)
        {
            int count = (samples.Length + bucket - 1) / bucket;
            var min = new short[count];
            var max = new short[count];

            if (prevMin == null || prevMax == null)
            {
                for (int i = 0; i < count; i++)
                {
                    int s = i * bucket, e = Math.Min(s + bucket, samples.Length);
                    short lo = short.MaxValue, hi = short.MinValue;
                    for (int j = s; j < e; j++)
                    {
                        if (samples[j] < lo) lo = samples[j];
                        if (samples[j] > hi) hi = samples[j];
                    }
                    min[i] = lo;
                    max[i] = hi;
                }
            }
            else
            {
                // Folding the level below, not rescanning the PCM - this is what keeps the
                // whole pyramid to roughly a single pass.
                int step = bucket / prevBucket;
                for (int i = 0; i < count; i++)
                {
                    int s = i * step, e = Math.Min(s + step, prevMin.Length);
                    short lo = short.MaxValue, hi = short.MinValue;
                    for (int j = s; j < e; j++)
                    {
                        if (prevMin[j] < lo) lo = prevMin[j];
                        if (prevMax[j] > hi) hi = prevMax[j];
                    }
                    min[i] = lo;
                    max[i] = hi;
                }
            }

            _levels.Add(new Level(min, max, bucket));
            prevMin = min;
            prevMax = max;
            prevBucket = bucket;
        }
    }

    // The coarsest level whose buckets still fit within one pixel column, so a column never
    // has to combine more than LevelFactor of them. Null means "zoomed in past the finest
    // summary" - at that point a column spans under 256 frames and reading the raw samples
    // is already trivial.
    internal Level? Pick(double samplesPerColumn)
    {
        Level? best = null;
        foreach (var level in _levels)
        {
            if (level.Bucket > samplesPerColumn) break;
            best = level;
        }
        return best;
    }
}
