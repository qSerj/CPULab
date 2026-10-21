using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CpuLab.Core;

public sealed class RunStore : IDisposable
{
    private readonly FileStream sessionLock;
    private readonly StreamWriter events;
    private readonly StreamWriter raw;
    private readonly StreamWriter samples;
    private readonly Dictionary<string, StreamWriter> attemptSamples = new();
    private readonly Dictionary<string, SensorDescriptor> sensors;
    public string DirectoryPath { get; }
    public const string CsvHeader = "sequence,phase,attempt_id,sensor_id,metric,unit,provider,provider_version,scope,core_type,core_id,interval_start_utc,interval_end_utc,monotonic_end_ticks,value,validity,reason,raw_reference";

    private RunStore(string directory, SensorDescriptor[] catalogue)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        sessionLock = AcquireLock(DirectoryPath);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "raw"));
        sensors = catalogue.ToDictionary(s => s.Id);
        events = Writer("events.jsonl");
        raw = Writer("raw/telemetry.jsonl");
        samples = Writer("samples.csv");
        samples.WriteLine(CsvHeader);
    }

    public static RunStore Create(string root, RunMetadata metadata, Scenario? scenario, SensorDescriptor[] sensors,
        LegacyDocument? legacy = null)
    {
        if (legacy is not null) legacy.Validate();
        // Do not use user-provided names as file paths.
        string directory = Path.Combine(root, $"{metadata.CreatedUtc:yyyy-MM-dd_HH-mm-ss}_{metadata.Mode.Replace('/', '-')}_{metadata.SessionId}");
        if (Directory.Exists(directory)) throw new IOException("Session already exists; refusing to overwrite.");
        var store = new RunStore(directory, sensors);
        try
        {
            store.SaveMetadata(metadata);
            if (scenario is not null) store.WriteJson("scenario.json", scenario);
            store.WriteJson("sensors.json", sensors);
            store.SaveAttempts([]);
            if (legacy is not null) store.WriteJson("legacy.json", legacy);
            return store;
        }
        catch { store.Dispose(); throw; }
    }

    internal static FileStream AcquireLock(string directory) => new(Path.Combine(directory, ".session.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private StreamWriter Writer(string relativePath)
    {
        string path = Path.Combine(DirectoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void SaveMetadata(RunMetadata metadata) => WriteJson("metadata.json", metadata);
    public void SaveAttempts(IReadOnlyList<Attempt> attempts) => WriteJson("benchmarks.json", attempts);
    public void Event(DateTimeOffset experimentUtc, string state, string? attemptId = null, string? reason = null) =>
        events.WriteLine(JsonSerializer.Serialize(new RunEvent(DateTimeOffset.UtcNow, experimentUtc, state, attemptId, reason), DataJson.Compact));

    public void BeginAttempt(Attempt attempt, string initialOutput = "MOCK: no process or workload was launched.\n")
    {
        var stream = Writer($"attempts/{attempt.Id}/samples.csv");
        stream.WriteLine(CsvHeader);
        attemptSamples.Add(attempt.Id, stream);
        AppendOutput(attempt, initialOutput, false);
        AppendOutput(attempt, "", true);
    }

    public void AppendOutput(Attempt attempt, string output, bool error) => File.AppendAllText(
        Path.Combine(DirectoryPath, "attempts", attempt.Id, error ? "stderr.txt" : "stdout.txt"), output);

    public void Append(TelemetryFrame frame)
    {
        string rawReference = $"raw/telemetry.jsonl#sequence={frame.Sequence}";
        raw.WriteLine(JsonSerializer.Serialize(frame, DataJson.Compact));
        foreach (var sample in frame.Samples)
        {
            var s = sensors[sample.SensorId];
            string line = string.Join(',', new object?[] { frame.Sequence, frame.Phase, frame.AttemptId,
                s.Id, s.Metric, s.Unit, s.Provider, s.ProviderVersion, s.Scope, s.CoreType, s.CoreId,
                sample.IntervalStartUtc.ToString("O"), sample.IntervalEndUtc.ToString("O"), sample.MonotonicEndTicks,
                sample.Value?.ToString("R", CultureInfo.InvariantCulture), sample.Validity, sample.Reason, rawReference }.Select(Csv));
            samples.WriteLine(line);
            if (frame.AttemptId is { } id) attemptSamples[id].WriteLine(line);
        }
    }

    // All model snapshots are replaced atomically; append-only raw/events are never rewritten.
    public void WriteJson<T>(string filename, T value) => AtomicJson(Path.Combine(DirectoryPath, filename), value);
    internal static void AtomicJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, DataJson.Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string Csv(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : '"' + text.Replace("\"", "\"\"") + '"';
    }

    public void Dispose()
    {
        foreach (var writer in attemptSamples.Values) writer.Dispose();
        samples.Dispose();
        raw.Dispose();
        events.Dispose();
        sessionLock.Dispose();
    }

    public static RunData Read(string directory)
    {
        var metadata = DataJson.Read<RunMetadata>(Path.Combine(directory, "metadata.json"));
        if (metadata.SchemaVersion != DataJson.SchemaVersion) throw new InvalidDataException("Unsupported schema version");
        var catalogue = DataJson.Read<SensorDescriptor[]>(Path.Combine(directory, "sensors.json"));
        var attempts = DataJson.Read<List<Attempt>>(Path.Combine(directory, "benchmarks.json"));
        var frames = new List<TelemetryFrame>();
        string rawPath = Path.Combine(directory, "raw", "telemetry.jsonl");
        string[] lines = File.ReadAllLines(rawPath);
        for (int i = 0; i < lines.Length; i++)
        {
            try
            {
                frames.Add(JsonSerializer.Deserialize<TelemetryFrame>(lines[i], DataJson.Options)
                    ?? throw new JsonException("Null frame"));
            }
            catch (JsonException) when (i == lines.Length - 1 && metadata.Status is not ("completed" or "imported"))
            {
                // A crash may leave an incomplete last write. Earlier corruption is an error.
                metadata = metadata with { Warnings = [.. metadata.Warnings, "Incomplete final raw frame ignored during recovery."] };
            }
        }
        string scenarioPath = Path.Combine(directory, "scenario.json");
        string legacyPath = Path.Combine(directory, "legacy.json");
        var legacy = File.Exists(legacyPath) ? DataJson.Read<LegacyDocument>(legacyPath) : null;
        legacy?.Validate();
        return new RunData(metadata, File.Exists(scenarioPath) ? DataJson.Read<Scenario>(scenarioPath) : null,
            catalogue, attempts, frames, legacy);
    }

    public static RunData Recover(string directory)
    {
        using var guard = AcquireLock(directory);
        var data = Read(directory);
        if (data.Metadata.Status is "completed" or "cancelled" or "failed" or "timeout" or "interrupted" or "imported") return data;
        data.Metadata.Status = "interrupted";
        data.Metadata.Reason = "Previous session did not finalize; recovered without restarting any workload.";
        data.Metadata.FinalizedUtc = DateTimeOffset.UtcNow;
        foreach (var attempt in data.Attempts.Where(a => a.Status == "running"))
        {
            attempt.Status = "interrupted";
            attempt.Reason = "Actual end time and score unknown after interruption.";
            attempt.Score = null;
            attempt.LastObservedUtc = data.Frames.Where(f => f.AttemptId == attempt.Id)
                .Select(f => (DateTimeOffset?)f.ReceivedUtc).LastOrDefault() ?? attempt.LastObservedUtc;
        }
        AtomicJson(Path.Combine(directory, "benchmarks.json"), data.Attempts);
        AtomicJson(Path.Combine(directory, "metadata.json"), data.Metadata);
        var evt = new RunEvent(DateTimeOffset.UtcNow, data.Frames.LastOrDefault()?.ReceivedUtc ?? data.Metadata.CreatedUtc,
            "interrupted", null, data.Metadata.Reason);
        File.AppendAllText(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(evt, DataJson.Compact) + "\n");
        return data;
    }
}
