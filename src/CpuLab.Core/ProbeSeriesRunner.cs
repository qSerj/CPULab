using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CpuLab.Core;

public sealed record ProbeSeriesScenario
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "process-probe";
    public int Repeat { get; init; } = 3;
    public int PauseMs { get; init; } = 100;
    public int AttemptTimeoutMs { get; init; } = 10000;
    public int TotalTimeoutMs { get; init; } = 60000;
    public int FailAttempt { get; init; }
    public int WaitAttempt { get; init; }
    public int MissingScoreAttempt { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || Mode != "process-probe" || Repeat is < 1 or > 20
            || PauseMs is < 0 or > 5000 || AttemptTimeoutMs is < 50 or > 60000
            || TotalTimeoutMs is < 50 or > 300000
            || new[] { FailAttempt, WaitAttempt, MissingScoreAttempt }.Any(i => i < 0 || i > Repeat))
            throw new InvalidDataException("Invalid bounded process-probe scenario.");
        if (new[] { FailAttempt, WaitAttempt, MissingScoreAttempt }.Where(i => i > 0).GroupBy(i => i).Any(g => g.Count() > 1))
            throw new InvalidDataException("Only one fault is allowed per probe attempt.");
    }
}

public sealed record ProbeScore(int SchemaVersion, string Kind, int Ordinal, double Score, string Unit);
public sealed record ProbeOutcome(ProcessRunResult Process, double? Score, string? Error);

// This adapter only knows the bundled idle fixture. It cannot select arbitrary benchmarks.
public sealed class ProbeBenchmarkAdapter(string hostAssembly, string probeAssembly)
{
    public void Preflight()
    {
        if (!Path.IsPathFullyQualified(hostAssembly) || !File.Exists(hostAssembly)
            || !Path.IsPathFullyQualified(probeAssembly) || !File.Exists(probeAssembly))
            throw new FileNotFoundException("Bundled process host/probe is missing. Build CPU Lab first.");
        _ = ProcessProtocol.DotNetHost();
    }

    public object Provenance()
    {
        var files = new[] { hostAssembly, probeAssembly, ProcessProtocol.DotNetHost() }
            .Concat(Directory.GetFiles(Path.GetDirectoryName(hostAssembly)!, "*.dll"))
            .Concat(Directory.GetFiles(Path.GetDirectoryName(probeAssembly)!, "*.dll"))
            .Distinct().Select(path => new
            {
                Path = path,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            }).ToArray();
        return new { SchemaVersion = 1, Kind = "bundled-idle-fixture", Files = files };
    }

    public async Task<ProbeOutcome> RunAsync(int ordinal, ProbeSeriesScenario scenario, string root, CancellationToken token)
    {
        string[] arguments = ordinal == scenario.FailAttempt ? ["fail"]
            : ordinal == scenario.WaitAttempt ? ["wait"]
            : ordinal == scenario.MissingScoreAttempt ? ["echo", "no fixture score"]
            : ["score", ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var command = new ProcessCommand(ProcessProtocol.DotNetHost(), [probeAssembly, .. arguments], Path.GetDirectoryName(probeAssembly)!);
        var result = await new ProcessSupervisor(hostAssembly).RunAsync(command, root,
            TimeSpan.FromMilliseconds(scenario.AttemptTimeoutMs), token);
        if (result.Status != "completed") return new(result, null, result.Error ?? $"Process status: {result.Status}");
        try
        {
            string text = File.ReadAllText(Path.Combine(result.Directory, "stdout.bin"), new UTF8Encoding(false, true));
            var score = JsonSerializer.Deserialize<ProbeScore>(text, DataJson.Options);
            if (score is null || score.SchemaVersion != 1 || score.Kind != "synthetic-probe-score"
                || score.Ordinal != ordinal || score.Unit != "fixture_points" || !double.IsFinite(score.Score) || score.Score <= 0)
                throw new InvalidDataException("Missing or invalid fixture score.");
            return new(result, score.Score, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException)
        {
            return new(result, null, $"Probe score rejected: {ex.Message}");
        }
    }
}

public sealed class ProbeSeriesRunner(ProbeBenchmarkAdapter adapter)
{
    public async Task<RunResult> RunAsync(ProbeSeriesScenario scenario, string outputRoot,
        CancellationToken cancellationToken = default)
    {
        scenario.Validate();
        adapter.Preflight();
        var metadata = new RunMetadata
        {
            SessionId = Guid.NewGuid().ToString("N"), Mode = "process-probe", CreatedUtc = DateTimeOffset.UtcNow,
            TimeMode = "wall-clock-with-monotonic-process-timer", MonotonicFrequency = Stopwatch.Frequency,
            Inventory = new("unavailable", new()
            {
                ["hardware"] = new(null, null, "unknown", "process-probe", "unavailable", "No hardware inventory or telemetry collected")
            }),
            Profile = new("process-probe", "Bundled idle process fixture; neither stand A nor stand B", new()),
            Warnings = [
                "Real lightweight processes; scores are fixture constants, not CPU performance measurements.",
                "No sensor telemetry. Process lifecycle includes startup/cleanup and is not a confirmed benchmark window.",
                "Windows Job execution still requires Windows qualification; see process-supervisor.md."
            ]
        };
        using var store = RunStore.Create(outputRoot, metadata, null, []);
        store.WriteJson("probe-scenario.json", scenario);
        store.WriteJson("probe-provenance.json", adapter.Provenance());
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(scenario.TotalTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var attempts = new List<Attempt>();
        Attempt? current = null;

        void State(string state, string? reason = null)
        {
            metadata.Status = state;
            metadata.Reason = reason;
            store.Event(DateTimeOffset.UtcNow, state, current?.Id, reason);
            if (state is "preflight" or "running" or "pause") store.SaveMetadata(metadata);
        }
        try
        {
            State("preflight");
            for (int ordinal = 1; ordinal <= scenario.Repeat; ordinal++)
            {
                linked.Token.ThrowIfCancellationRequested();
                current = new Attempt
                {
                    Id = $"attempt-{ordinal:D3}", Ordinal = ordinal, StartUtc = DateTimeOffset.UtcNow,
                    StartTicks = clock.ElapsedTicks, Benchmark = "cpulab-process-probe/1",
                    ScoreUnit = "fixture_points", WindowOrigin = "observed-process-lifecycle"
                };
                attempts.Add(current);
                store.BeginAttempt(current, "PROCESS PROBE: raw bytes are in the process subdirectory; no sensor measurements.\n");
                store.SaveAttempts(attempts);
                State("running");
                var outcome = await adapter.RunAsync(ordinal, scenario,
                    Path.Combine(store.DirectoryPath, "attempts", current.Id, "processes"), linked.Token);
                current.ProcessResultDirectory = Path.GetRelativePath(store.DirectoryPath, outcome.Process.Directory).Replace('\\', '/');
                current.EndUtc = DateTimeOffset.UtcNow;
                current.EndTicks = clock.ElapsedTicks;
                current.LastObservedUtc = current.EndUtc;
                if (cancellationToken.IsCancellationRequested) current.Status = "cancelled";
                else if (deadline.IsCancellationRequested || outcome.Process.Status == "timeout") current.Status = "timeout";
                else current.Status = outcome.Process.Status == "completed" && outcome.Score.HasValue ? "completed" : "failed";
                current.Score = current.Status == "completed" ? outcome.Score : null;
                current.Reason = current.Status == "completed" ? null
                    : cancellationToken.IsCancellationRequested ? "Cancelled by operator; see process cleanup status."
                    : deadline.IsCancellationRequested ? "Total series deadline reached."
                    : outcome.Process.Status == "timeout" ? "Attempt deadline reached."
                    : outcome.Error ?? $"Series stopped: {current.Status}";
                // Cleanup failure takes precedence over a requested stop.
                if (outcome.Process.Status == "cleanup-failed")
                {
                    current.Status = "failed";
                    current.Score = null;
                    current.Reason = outcome.Error ?? "Process cleanup failed.";
                }
                store.AppendOutput(current, $"Process journal: {current.ProcessResultDirectory}\n", false);
                store.SaveAttempts(attempts);
                store.Event(DateTimeOffset.UtcNow, "attempt_finalized", current.Id, current.Status);
                if (current.Status != "completed")
                {
                    State(current.Status, current.Reason);
                    current = null;
                    break;
                }
                current = null;
                if (ordinal < scenario.Repeat)
                {
                    State("pause");
                    await Task.Delay(scenario.PauseMs, linked.Token);
                }
            }
            if (attempts.Count == scenario.Repeat && attempts.All(a => a.Status == "completed")) State("completed");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            State(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", "Series interrupted; completed attempts retained.");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            State("failed", ex.Message);
        }
        finally
        {
            if (current is not null && current.Status == "running")
            {
                current.Status = metadata.Status;
                current.Reason = metadata.Reason;
                current.Score = null;
                // Without a returned supervisor result, actual process end remains unknown.
            }
            store.SaveAttempts(attempts);
            metadata.FinalizedUtc = DateTimeOffset.UtcNow;
            store.SaveMetadata(metadata);
        }
        return new(store.DirectoryPath, metadata.Status, metadata.Reason);
    }
}
