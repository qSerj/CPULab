namespace CpuLab.Core;

public interface IInventory
{
    InventorySnapshot Capture();
}

public interface ITelemetryProvider
{
    IReadOnlyList<SensorDescriptor> Sensors { get; }
    ValueTask<TelemetryFrame> ReadAsync(SampleRequest request, CancellationToken cancellationToken);
}

public sealed record BenchmarkObservation(bool Finished, bool Failed = false, double? Score = null,
    string? Reason = null, string? Output = null);

public interface IBenchmarkAdapter
{
    bool IsSimulated { get; }
    ValueTask StartAsync(Attempt attempt, CancellationToken cancellationToken);
    ValueTask<BenchmarkObservation> ObserveAsync(Attempt attempt, int samplesCollected,
        CancellationToken cancellationToken);
    ValueTask StopAsync(Attempt attempt);
}

// A deterministic experiment clock. The optional delay only makes Ctrl+C convenient;
// these timestamps must never be presented as measurements of the host machine.
public sealed class SimulationClock(DateTimeOffset start, bool realtime = false)
{
    public DateTimeOffset UtcNow { get; private set; } = start;
    public long Ticks { get; private set; }
    public string Mode => realtime ? "synthetic-paced" : "synthetic-fast";

    public async ValueTask AdvanceAsync(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (realtime) await Task.Delay(milliseconds, cancellationToken);
        Ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
        UtcNow = UtcNow.AddMilliseconds(milliseconds);
    }
}

public sealed class MockInventory : IInventory
{
    public InventorySnapshot Capture() => new("mock", new()
    {
        ["cpu"] = new("Synthetic P/E CPU (illustrative 1P + 1E)", null, "inferred", "mock fixture"),
        ["motherboard"] = new(null, null, "unknown", "mock", "unavailable", "No hardware inventory performed"),
        ["bios"] = new(null, null, "unknown", "mock", "unavailable", "No BIOS read"),
        ["microcode"] = new(null, null, "unknown", "mock", "unavailable", "No MSR access"),
        ["application"] = new("0.1.0", null, "inferred", "CPU Lab mock implementation")
    });

    public static HardwareProfile Profile => new("mock-pe", "Synthetic topology; neither stand A nor stand B", new()
    {
        ["iaAcLoadline"] = new(null, "mOhm", "unknown", "mock", "unavailable", "No BIOS input or readback"),
        ["iaDcLoadline"] = new(null, "mOhm", "unknown", "mock", "unavailable", "No BIOS input or readback"),
        ["cep"] = new(null, null, "unknown", "mock", "unavailable", "No CEP diagnosis in simulation")
    });
}

public sealed class MockTelemetryProvider(MockFaults faults) : ITelemetryProvider
{
    public IReadOnlyList<SensorDescriptor> Sensors { get; } =
    [
        new("mock/package/power", "package_power", "W", "package"),
        new("mock/package/temperature", "temperature", "degC", "package"),
        new("mock/P0/clock", "core_clock", "MHz", "core", "P", "P0"),
        new("mock/P0/effective", "effective_clock", "MHz", "core", "P", "P0"),
        new("mock/E0/clock", "core_clock", "MHz", "core", "E", "E0"),
        new("mock/E0/effective", "effective_clock", "MHz", "core", "E", "E0")
    ];

    public ValueTask<TelemetryFrame> ReadAsync(SampleRequest r, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool busy = r.Phase == "running";
        double wave = Math.Sin(r.Sequence * 0.7);
        var samples = Sensors.Select(s =>
        {
            double value = s.Metric switch
            {
                "package_power" => busy ? 140 + 3 * wave + r.AttemptOrdinal : 12 + wave,
                "temperature" => faults.HotSequence == r.Sequence ? 110 : busy ? 72 + 2 * wave : 34 + wave,
                "core_clock" => s.CoreType == "P" ? 5100 : 3900,
                "effective_clock" => busy ? (s.CoreType == "P" ? 4950 : 3800) + wave * 10 : 120,
                _ => throw new InvalidOperationException("Unknown mock metric")
            };
            if (faults.MissingEffectiveClocks && s.Metric == "effective_clock")
                return new Sample(s.Id, r.StartUtc, r.EndUtc, r.MonotonicEndTicks, null,
                    "unavailable", "Configured mock missing sensor");
            if (faults.StaleFromSequence > 0 && r.Sequence >= faults.StaleFromSequence)
                return new Sample(s.Id, r.StartUtc, r.EndUtc, r.MonotonicEndTicks, value,
                    "stale", "Configured mock frozen provider");
            if (faults.GapSequence == r.Sequence)
                return new Sample(s.Id, r.StartUtc, r.EndUtc, r.MonotonicEndTicks, null,
                    "unavailable", "Configured mock gap");
            return new Sample(s.Id, r.StartUtc, r.EndUtc, r.MonotonicEndTicks, value);
        }).ToArray();
        return ValueTask.FromResult(new TelemetryFrame(r.Sequence, r.Phase, r.AttemptId, r.EndUtc, samples));
    }
}

public sealed class MockBenchmarkAdapter(Scenario scenario) : IBenchmarkAdapter
{
    public bool IsSimulated => true;
    public ValueTask StartAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(Attempt attempt) => ValueTask.CompletedTask;
    public ValueTask<BenchmarkObservation> ObserveAsync(Attempt attempt, int samplesCollected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt.Ordinal == scenario.Faults.FailAttempt && samplesCollected >= 2)
            return ValueTask.FromResult(new BenchmarkObservation(true, true, Reason: "Injected mock adapter failure",
                Output: "MOCK ERROR: injected adapter failure\n"));
        if (attempt.Ordinal == scenario.Faults.HangAttempt || samplesCollected < scenario.SamplesPerAttempt)
            return ValueTask.FromResult(new BenchmarkObservation(false));
        double? score = attempt.Ordinal == scenario.Faults.MissingScoreAttempt ? null : 1000 + (attempt.Ordinal - 2) * 5;
        return ValueTask.FromResult(new BenchmarkObservation(true, Score: score,
            Output: score is null ? "MOCK finished without score\n" : $"MOCK synthetic_points={score}\n"));
    }
}
