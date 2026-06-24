using System.Collections.Concurrent;

namespace BotService.Services;

/// <summary>
/// T380 — Prometheus-compatible metrics registry for bot swarms.
/// Thread-safe counters, gauges, and histograms.
/// </summary>
public class BotMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _histograms = new();
    private const int MaxHistogramSamples = 1000;

    // ── Counters ──────────────────────────────────────────────────────────

    public void Increment(string name, long delta = 1) =>
        _counters.AddOrUpdate(name, delta, (_, v) => v + delta);

    public long GetCounter(string name) => _counters.GetValueOrDefault(name, 0);

    // ── Gauges ────────────────────────────────────────────────────────────

    public void SetGauge(string name, double value) => _gauges[name] = value;

    public double GetGauge(string name) => _gauges.GetValueOrDefault(name, 0);

    // ── Histograms ────────────────────────────────────────────────────────

    public void Observe(string name, double value)
    {
        var queue = _histograms.GetOrAdd(name, _ => new ConcurrentQueue<double>());
        queue.Enqueue(value);
        if (queue.Count > MaxHistogramSamples && queue.TryDequeue(out _)) { }
    }

    public (double p50, double p95, double p99, long count) GetHistogram(string name)
    {
        if (!_histograms.TryGetValue(name, out var queue))
            return (0, 0, 0, 0);

        var snapshot = queue.ToArray();
        if (snapshot.Length == 0) return (0, 0, 0, 0);

        Array.Sort(snapshot);
        return (
            Percentile(snapshot, 50),
            Percentile(snapshot, 95),
            Percentile(snapshot, 99),
            snapshot.Length
        );
    }

    private static double Percentile(double[] sorted, int p) =>
        sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(p / 100.0 * sorted.Length) - 1];

    // ── Render ────────────────────────────────────────────────────────────

    public string RenderPrometheus()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var (k, v) in _counters)
        {
            sb.AppendLine($"# HELP {k} Bot swarm counter");
            sb.AppendLine($"# TYPE {k} counter");
            sb.AppendLine($"{k} {v}");
        }

        foreach (var (k, v) in _gauges)
        {
            sb.AppendLine($"# HELP {k} Bot swarm gauge");
            sb.AppendLine($"# TYPE {k} gauge");
            sb.AppendLine($"{k} {v}");
        }

        foreach (var (k, _) in _histograms)
        {
            var (p50, p95, p99, count) = GetHistogram(k);
            sb.AppendLine($"# HELP {k} Bot swarm histogram");
            sb.AppendLine($"# TYPE {k} summary");
            sb.AppendLine($"{k}_count {count}");
            sb.AppendLine($"{k}{{quantile=\"0.5\"}} {p50:F3}");
            sb.AppendLine($"{k}{{quantile=\"0.95\"}} {p95:F3}");
            sb.AppendLine($"{k}{{quantile=\"0.99\"}} {p99:F3}");
        }

        return sb.ToString();
    }
}
