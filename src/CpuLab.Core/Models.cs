using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuLab.Core;

public static class DataJson
{
    public const int SchemaVersion = 1;
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON: {path}");
}

public sealed record Evidence(string? Value, string? Unit, string Origin, string Source,
    string Status = "valid", string? Reason = null);
public sealed record HardwareProfile(string Id, string Description, Dictionary<string, Evidence> Fields);
public sealed record InventorySnapshot(string Mode, Dictionary<string, Evidence> Fields);

public sealed record Scenario
{
    public int SchemaVersion { get; init; } = DataJson.SchemaVersion;
    public string Mode { get; init; } = "mock";
    public string Name { get; init; } = "mock-demo";
    public int Repeat { get; init; } = 3;
    public int BaselineSamples { get; init; } = 3;
    public int CooldownSamples { get; init; } = 2;
    public int SamplesPerAttempt { get; init; } = 10;
    public int SampleIntervalMs { get; init; } = 1000;
    public int AttemptTimeoutMs { get; init; } = 15000;
    public int TotalTimeoutMs { get; init; } = 120000;
    public double TemperatureLimitC { get; init; } = 95;
    public DateTimeOffset SyntheticStartUtc { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public string Note { get; init; } = "Synthetic demonstration, not a CPU measurement.";
    public MockFaults Faults { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != DataJson.SchemaVersion || Mode != "mock")
            throw new InvalidDataException("Only schema 1 and mode=mock are supported. No real loads are implemented.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Note is null || Note.Length > 4000 || Faults is null)
            throw new InvalidDataException("Name must have 1–80 characters; note at most 4000.");
        if (Repeat is < 1 or > 20 || BaselineSamples is < 1 or > 60 || CooldownSamples is < 0 or > 60
            || SamplesPerAttempt is < 2 or > 600 || SampleIntervalMs is < 10 or > 10000
            || AttemptTimeoutMs is < 10 or > 3600000 || TotalTimeoutMs is < 10 or > 7200000
            || !double.IsFinite(TemperatureLimitC) || TemperatureLimitC is < 1 or > 120)
            throw new InvalidDataException("Scenario values exceed bounded mock limits.");
        if (Faults.FailAttempt < 0 || Faults.FailAttempt > Repeat || Faults.HangAttempt < 0 || Faults.HangAttempt > Repeat
            || Faults.MissingScoreAttempt < 0 || Faults.MissingScoreAttempt > Repeat || Faults.StaleFromSequence < 0
            || Faults.GapSequence < 0 || Faults.HotSequence < 0)
            throw new InvalidDataException("Invalid mock fault index (0 means disabled).");
        if (SyntheticStartUtc.Offset != TimeSpan.Zero || SyntheticStartUtc.Year is < 2000 or > 2099)
            throw new InvalidDataException("Synthetic start must be UTC in years 2000–2099.");
    }
}

public sealed record MockFaults
{
    public int FailAttempt { get; init; }
    public int HangAttempt { get; init; }
    public int MissingScoreAttempt { get; init; }
    public int StaleFromSequence { get; init; }
    public int GapSequence { get; init; }
    public int HotSequence { get; init; }
    public bool MissingEffectiveClocks { get; init; }
}

public sealed record SensorDescriptor(string Id, string Metric, string Unit, string Scope,
    string? CoreType = null, string? CoreId = null, string Provider = "mock", string ProviderVersion = "1");
public sealed record Sample(string SensorId, DateTimeOffset IntervalStartUtc, DateTimeOffset IntervalEndUtc,
    long MonotonicEndTicks, double? Value, string Validity = "valid", string? Reason = null);
public sealed record TelemetryFrame(int Sequence, string Phase, string? AttemptId,
    DateTimeOffset ReceivedUtc, IReadOnlyList<Sample> Samples);
public sealed record SampleRequest(int Sequence, string Phase, string? AttemptId, int AttemptOrdinal,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, long MonotonicEndTicks);

public sealed record Attempt
{
    public required string Id { get; init; }
    public required int Ordinal { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; set; }
    public DateTimeOffset? LastObservedUtc { get; set; }
    public required long StartTicks { get; init; }
    public long? EndTicks { get; set; }
    public string Status { get; set; } = "running";
    public string? Reason { get; set; }
    public double? Score { get; set; }
    public string ScoreUnit { get; init; } = "synthetic_points";
    public string Benchmark { get; init; } = "mock-benchmark/1";
    public string WindowOrigin { get; init; } = "synthetic";
    public string? ProcessResultDirectory { get; set; }
    public string TelemetryFile => $"attempts/{Id}/samples.csv";
}

public sealed record RunMetadata
{
    public int SchemaVersion { get; init; } = DataJson.SchemaVersion;
    public required string SessionId { get; init; }
    public required string Mode { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? FinalizedUtc { get; set; }
    public string Status { get; set; } = "created";
    public string? Reason { get; set; }
    public required string TimeMode { get; init; }
    public long MonotonicFrequency { get; init; } = TimeSpan.TicksPerSecond;
    public required InventorySnapshot Inventory { get; init; }
    public required HardwareProfile Profile { get; init; }
    public string[] Warnings { get; init; } = ["SIMULATED DATA: no hardware was measured and no load was started."];
}

public sealed record RunEvent(DateTimeOffset RecordedUtc, DateTimeOffset ExperimentUtc, string State,
    string? AttemptId, string? Reason);
public sealed record LegacyEntry(string Id, double AcMohm, double DcMohm, double Score,
    double PeakPackagePowerW, double MaxTemperatureC, string Origin = "user",
    string Source = "Codex_CPU_Lab_TZ.md", bool RawAvailable = false);
public sealed record LegacyDocument(int SchemaVersion, string Mode, string Benchmark, LegacyEntry[] Entries)
{
    public void Validate()
    {
        if (SchemaVersion != DataJson.SchemaVersion || Mode != "legacy/manual" || Benchmark != "Cinebench R23 MC"
            || Entries is null || Entries.Length == 0 || Entries.Any(e => e is null)
            || Entries.Select(e => e.Id).Distinct().Count() != Entries.Length)
            throw new InvalidDataException("Expected schema 1, legacy/manual Cinebench R23 MC entries with unique IDs.");
        foreach (var e in Entries)
            if (string.IsNullOrWhiteSpace(e.Id) || e.RawAvailable || e.Origin != "user"
                || new[] { e.AcMohm, e.DcMohm, e.Score, e.PeakPackagePowerW, e.MaxTemperatureC }
                    .Any(v => !double.IsFinite(v) || v <= 0))
                throw new InvalidDataException("Legacy entries require positive finite manual values and no raw telemetry.");
    }
}

public sealed record RunData(RunMetadata Metadata, Scenario? Scenario, SensorDescriptor[] Sensors,
    List<Attempt> Attempts, List<TelemetryFrame> Frames, LegacyDocument? Legacy);
