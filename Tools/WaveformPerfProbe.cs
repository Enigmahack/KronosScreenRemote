using System.Diagnostics;
using System.IO;
using System.Text;

namespace KronosScreenRemote;

// Temporary measurement scaffolding for the Sample Editor's zoom/drag cost, NOT a shipped
// feature - delete it once the bottleneck is identified and fixed.
//
// It exists because that cost scales with WINDOW SIZE (a 4K fullscreen pane is slow, a
// small window is fine), which makes it unreproducible in the dev/test environment here:
// several rounds of plausible-looking fixes to the waveform drawing changed nothing at
// all, so the remaining question is not "which drawing optimisation" but "which phase is
// actually taking the time." Timings are buffered in memory (writing per sample would
// itself distort what is being measured) and flushed to a file on window close.
static class WaveformPerfProbe
{
    public static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), "kronos_waveform_perf.txt");

    const int MaxSamples = 20000;
    static readonly List<(string Label, double Ms)> Samples = [];

    public static bool Enabled { get; set; } = true;

    // The window's own OnClosed is the normal flush point, but the app can also go away
    // without it (the visual-check harness exits directly) - without this the whole
    // session's measurements would be lost exactly when they matter.
    static WaveformPerfProbe() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush("process exit");

    // Usage: `using (WaveformPerfProbe.Time("label")) { ... }`
    public static Scope Time(string label) => new(label);

    // Time from an action until WPF next begins composing a frame - i.e. until the change
    // can actually reach the glass. Every other measurement here stops when OnRender
    // returns, which is BEFORE compositing, so a delay living in presentation rather than
    // in drawing would be invisible to all of them.
    public static void MeasureToNextPresent(string label)
    {
        if (!Enabled) return;
        long start = Stopwatch.GetTimestamp();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            Record(label, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
    }

    public static void Record(string label, double ms)
    {
        if (!Enabled) return;
        lock (Samples)
        {
            if (Samples.Count < MaxSamples) Samples.Add((label, ms));
        }
    }

    public readonly struct Scope(string label) : IDisposable
    {
        readonly long _start = Enabled ? Stopwatch.GetTimestamp() : 0;

        public void Dispose()
        {
            if (!Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            lock (Samples)
            {
                if (Samples.Count < MaxSamples) Samples.Add((label, ms));
            }
        }
    }

    // One line per label: how many times it ran, the total, the mean, and the worst single
    // occurrence. The worst case is the interesting column - a stutter is a long tail, not
    // a raised average.
    public static void Flush(string context)
    {
        lock (Samples)
        {
            if (Samples.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"=== {context} @ {DateTime.Now:HH:mm:ss} - {Samples.Count} samples ===");

            // Tier 2 = full hardware acceleration; Tier 1 = partial; Tier 0 = EVERYTHING
            // rasterised on the CPU. Tier 0 would explain a cost that scales with window
            // size, is fine in a small window, and is untouched by any drawing change -
            // which is exactly the profile being chased here.
            try
            {
                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                sb.AppendLine($"render tier: {tier} ({(tier switch
                {
                    >= 2 => "full hardware acceleration",
                    1 => "PARTIAL hardware acceleration",
                    _ => "SOFTWARE RENDERING - all rasterisation on the CPU",
                })})");
            }
            catch (Exception ex) { sb.AppendLine($"render tier: unavailable ({ex.Message})"); }

            // Percentiles, not just the mean: an idle pause between spins is indistinguishable
            // from a stall in an average, and that ambiguity has already sent this
            // investigation down one wrong path. p50 is what the interaction actually feels
            // like; a p50 far below the mean means the mean is just measuring pauses.
            sb.AppendLine($"{"label",-40} {"count",7} {"mean",9} {"p50",9} {"p90",9} {"p99",9} {"max",9}");
            foreach (var group in Samples.GroupBy(s => s.Label).OrderByDescending(g => g.Sum(s => s.Ms)))
            {
                var sorted = group.Select(s => s.Ms).OrderBy(m => m).ToArray();
                double Pct(double p) => sorted[Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1)];
                sb.AppendLine($"{group.Key,-40} {sorted.Length,7} {sorted.Average(),9:F2} " +
                              $"{Pct(0.50),9:F2} {Pct(0.90),9:F2} {Pct(0.99),9:F2} {sorted[^1],9:F2}");
            }
            sb.AppendLine();

            try { File.AppendAllText(OutputPath, sb.ToString()); }
            catch (Exception ex) { AppLog.Debug($"[perf] cannot write {OutputPath}: {ex.Message}"); }
            Samples.Clear();
        }
    }
}
