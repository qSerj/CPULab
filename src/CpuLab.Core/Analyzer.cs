namespace CpuLab.Core;

public sealed record MetricSummary(string SensorId, string Metric, string Unit, double? Mean,
    double? Median, double? Max, double? EnergyJ, double Coverage, int ValidSamples, string? Reason);
public sealed record AttemptAnalysis(Attempt Attempt, MetricSummary[] Metrics, string[] Warnings);
public sealed record SeriesSummary(int ValidCount, int AttemptCount, double? Mean, double? Median,
    double? Min, double? Max, double? StandardDeviation, double? CoefficientOfVariation);
public sealed record RunAnalysis(AttemptAnalysis[] Attempts, SeriesSummary Series, string[] Warnings);
public sealed record SessionComparison(bool Compatible, string Reason, double? ScoreChangePercent);

public static class Analyzer
{
    public static RunAnalysis Analyze(RunData data)
    {
        var analyses = data.Attempts.Select(a => AnalyzeAttempt(data, a)).ToArray();
        double[] scores = data.Attempts.Where(a => a.Status == "completed" && a.Score is > 0)
            .Select(a => a.Score!.Value).Where(double.IsFinite).Order().ToArray();
        double? mean = scores.Length == 0 ? null : scores.Average();
        double? sd = scores.Length < 2 ? null : Math.Sqrt(scores.Sum(x => Math.Pow(x - mean!.Value, 2)) / (scores.Length - 1));
        var series = new SeriesSummary(scores.Length, data.Attempts.Count, mean, Median(scores),
            scores.Length == 0 ? null : scores[0], scores.Length == 0 ? null : scores[^1], sd,
            sd.HasValue && mean > 0 ? sd / mean : null);
        return new RunAnalysis(analyses, series, data.Metadata.Warnings);
    }

    public static AttemptAnalysis AnalyzeAttempt(RunData data, Attempt attempt)
    {
        var warnings = new List<string>();
        if (attempt.WindowOrigin != "confirmed") warnings.Add($"Window origin: {attempt.WindowOrigin}; not a confirmed hardware render window.");
        if (attempt.Status != "completed") warnings.Add($"Partial attempt: {attempt.Status}. {attempt.Reason}");
        var summaries = data.Sensors.Select(sensor => Summarize(sensor, attempt, data.Frames)).ToArray();
        if (summaries.Any(m => m.Coverage < 0.95)) warnings.Add("Coverage below 95% for one or more sensors; no interpolation performed.");
        return new AttemptAnalysis(attempt, summaries, warnings.ToArray());
    }

    public static MetricSummary Summarize(SensorDescriptor sensor, Attempt attempt, IEnumerable<TelemetryFrame> frames)
    {
        DateTimeOffset? end = attempt.EndUtc ?? attempt.LastObservedUtc;
        var input = frames.Where(f => f.AttemptId == attempt.Id).SelectMany(f => f.Samples)
            .Where(s => s.SensorId == sensor.Id).OrderBy(s => s.IntervalStartUtc).ToArray();
        var accepted = new List<Sample>();
        var reasons = new HashSet<string>();
        DateTimeOffset lastEnd = attempt.StartUtc;
        foreach (var sample in input)
        {
            if (sample.Validity != "valid" || sample.Value is null || !double.IsFinite(sample.Value.Value))
            { reasons.Add(sample.Reason ?? sample.Validity); continue; }
            if (sample.IntervalStartUtc < attempt.StartUtc || end is null || sample.IntervalEndUtc > end)
            { reasons.Add("Boundary interval excluded"); continue; }
            if (sample.IntervalEndUtc <= sample.IntervalStartUtc || sample.IntervalStartUtc < lastEnd)
            { reasons.Add("Overlapping, duplicate or non-positive interval excluded"); continue; }
            accepted.Add(sample);
            lastEnd = sample.IntervalEndUtc;
        }
        double duration = end is null ? 0 : (end.Value - attempt.StartUtc).TotalSeconds;
        double observed = accepted.Sum(s => (s.IntervalEndUtc - s.IntervalStartUtc).TotalSeconds);
        double integral = accepted.Sum(s => s.Value!.Value * (s.IntervalEndUtc - s.IntervalStartUtc).TotalSeconds);
        double[] values = accepted.Select(s => s.Value!.Value).Order().ToArray();
        if (accepted.Count == 0) reasons.Add("No valid samples in window");
        return new MetricSummary(sensor.Id, sensor.Metric, sensor.Unit, observed > 0 ? integral / observed : null,
            WeightedMedian(accepted), values.Length > 0 ? values[^1] : null,
            sensor.Metric == "package_power" && observed > 0 ? integral : null,
            duration > 0 ? Math.Clamp(observed / duration, 0, 1) : 0, accepted.Count,
            reasons.Count > 0 ? string.Join("; ", reasons.Order()) : null);
    }

    public static SessionComparison Compare(RunData current, RunData previous)
    {
        if (current.Metadata.Mode != previous.Metadata.Mode || current.Metadata.Mode != "mock"
            || current.Metadata.Profile.Id != previous.Metadata.Profile.Id
            || current.Scenario is null || previous.Scenario is null
            || current.Scenario.SamplesPerAttempt != previous.Scenario.SamplesPerAttempt
            || current.Scenario.SampleIntervalMs != previous.Scenario.SampleIntervalMs
            || !current.Sensors.SequenceEqual(previous.Sensors)
            || current.Attempts.Any(a => a.Benchmark != "mock-benchmark/1" || a.ScoreUnit != "synthetic_points")
            || previous.Attempts.Any(a => a.Benchmark != "mock-benchmark/1" || a.ScoreUnit != "synthetic_points"))
            return new(false, "Different mode, profile, benchmark, sensor catalogue or workload parameters; percentage comparison suppressed.", null);
        var c = Analyze(current).Series;
        var p = Analyze(previous).Series;
        if (current.Metadata.Status != "completed" || previous.Metadata.Status != "completed" || c.Mean is null || p.Mean is not > 0)
            return new(false, "Both sessions must be completed and have valid scores.", null);
        return new(true, "Comparable simulations only; not a measurement of either stand.", (c.Mean / p.Mean - 1) * 100);
    }

    private static double? Median(double[] sorted) => sorted.Length == 0 ? null
        : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    private static double? WeightedMedian(List<Sample> samples)
    {
        if (samples.Count == 0) return null;
        double halfway = samples.Sum(s => (s.IntervalEndUtc - s.IntervalStartUtc).TotalSeconds) / 2;
        double cumulative = 0;
        foreach (var sample in samples.OrderBy(s => s.Value))
        {
            cumulative += (sample.IntervalEndUtc - sample.IntervalStartUtc).TotalSeconds;
            if (cumulative >= halfway) return sample.Value;
        }
        return samples[^1].Value;
    }
}
