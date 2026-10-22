using System.Text.Json;
using CpuLab.Core;
using Xunit;

namespace CpuLab.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpulab-tests-" + Guid.NewGuid().ToString("N"));

    private async Task<(RunResult Result, RunData Data)> Run(Scenario? scenario = null,
        CancellationToken token = default, Action<TelemetryFrame>? onFrame = null)
    {
        scenario ??= new Scenario();
        var runner = new ScenarioRunner(new MockInventory(), new MockTelemetryProvider(scenario.Faults), new MockBenchmarkAdapter(scenario));
        var result = await runner.RunAsync(scenario, root, cancellationToken: token, onFrame: onFrame);
        return (result, RunStore.Read(result.Directory));
    }

    [Fact]
    public async Task ThreeAttemptsHaveContinuousRawAndIndividualSlices()
    {
        var (result, data) = await Run();
        Assert.Equal("completed", result.Status);
        Assert.Equal(3, data.Attempts.Count);
        Assert.Equal(37, data.Frames.Count); // 3 baseline + 3*10 running + 2*2 pauses
        Assert.Equal(Enumerable.Range(1, 37), data.Frames.Select(f => f.Sequence));
        Assert.Equal(1000, Analyzer.Analyze(data).Series.Mean);
        foreach (var attempt in data.Attempts)
        {
            Assert.Equal("completed", attempt.Status);
            Assert.NotNull(attempt.EndUtc);
            Assert.Equal(61, File.ReadLines(Path.Combine(result.Directory, attempt.TelemetryFile)).Count());
            Assert.True(File.Exists(Path.Combine(result.Directory, "attempts", attempt.Id, "stdout.txt")));
            Assert.Equal(1, Analyzer.AnalyzeAttempt(data, attempt).Metrics[0].Coverage);
        }
        Assert.Equal(223, File.ReadLines(Path.Combine(result.Directory, "samples.csv")).Count());
    }

    [Fact]
    public async Task MockIsDeterministicButSessionIdsAreUnique()
    {
        var first = await Run();
        var second = await Run();
        Assert.NotEqual(first.Data.Metadata.SessionId, second.Data.Metadata.SessionId);
        Assert.Equal(JsonSerializer.Serialize(first.Data.Frames), JsonSerializer.Serialize(second.Data.Frames));
        Assert.True(Analyzer.Compare(first.Data, second.Data).Compatible);
        Assert.Equal(0, Analyzer.Compare(first.Data, second.Data).ScoreChangePercent);
    }

    [Fact]
    public async Task FailedSecondAttemptKeepsFirstAndDoesNotStartThird()
    {
        var (result, data) = await Run(new Scenario { Faults = new() { FailAttempt = 2 } });
        Assert.Equal("failed", result.Status);
        Assert.Equal(2, data.Attempts.Count);
        Assert.Equal("completed", data.Attempts[0].Status);
        Assert.Equal("failed", data.Attempts[1].Status);
        Assert.Null(data.Attempts[1].Score);
        Assert.Equal(2, data.Frames.Count(f => f.AttemptId == "attempt-002"));
        Assert.Equal(1, Analyzer.Analyze(data).Series.ValidCount);
        Assert.False(Analyzer.Compare(data, data).Compatible);
    }

    [Fact]
    public async Task HangingMockTimesOutWithoutFabricatingSuccess()
    {
        var (result, data) = await Run(new Scenario { AttemptTimeoutMs = 2000, Faults = new() { HangAttempt = 1 } });
        Assert.Equal("timeout", result.Status);
        Assert.Single(data.Attempts);
        Assert.Equal(2, data.Frames.Count(f => f.AttemptId != null));
        Assert.Null(data.Attempts[0].Score);
    }

    [Fact]
    public async Task TotalTimeoutAppliesDuringBaseline()
    {
        var (result, data) = await Run(new Scenario { TotalTimeoutMs = 1000 });
        Assert.Equal("timeout", result.Status);
        Assert.Empty(data.Attempts);
        Assert.Single(data.Frames);
    }

    [Fact]
    public async Task CancellationPreservesPartialAttemptAndReportCanBeGenerated()
    {
        using var cancel = new CancellationTokenSource();
        var (result, data) = await Run(token: cancel.Token, onFrame: f => { if (f.Sequence == 5) cancel.Cancel(); });
        Assert.Equal("cancelled", result.Status);
        Assert.Single(data.Attempts);
        Assert.Equal("cancelled", data.Attempts[0].Status);
        Assert.Equal(5, data.Frames.Count);
        ReportGenerator.Write(result.Directory, data);
        Assert.Contains("cancelled", File.ReadAllText(Path.Combine(result.Directory, "report.html")));
    }

    [Fact]
    public async Task CancellationBeforeStartCreatesAnHonestEmptyJournal()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var (result, data) = await Run(token: cancel.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.Empty(data.Attempts);
        Assert.Empty(data.Frames);
    }

    [Theory]
    [InlineData("hot")]
    [InlineData("stale")]
    [InlineData("gap")]
    public async Task UntrustedTemperatureOrThresholdStopsAfterSavingEvidence(string fault)
    {
        var faults = fault switch
        {
            "hot" => new MockFaults { HotSequence = 5 },
            "stale" => new MockFaults { StaleFromSequence = 5 },
            _ => new MockFaults { GapSequence = 5 }
        };
        var (result, data) = await Run(new Scenario { Faults = faults });
        Assert.Equal("failed", result.Status);
        Assert.Equal(5, data.Frames.Count);
        Assert.Null(data.Attempts.Single().Score);
    }

    [Fact]
    public async Task MissingOptionalEffectiveClocksAreUnknownNotZero()
    {
        var (result, data) = await Run(new Scenario { Faults = new() { MissingEffectiveClocks = true } });
        Assert.Equal("completed", result.Status);
        var metrics = Analyzer.Analyze(data).Attempts[0].Metrics.Where(m => m.Metric == "effective_clock");
        Assert.All(metrics, m => { Assert.Null(m.Mean); Assert.Equal(0, m.Coverage); Assert.Contains("missing", m.Reason); });
    }

    [Fact]
    public async Task RecoverRunningManifestKeepsRawAndDoesNotInventEndTime()
    {
        var (result, data) = await Run();
        var rawPath = Path.Combine(result.Directory, "raw", "telemetry.jsonl");
        byte[] original = File.ReadAllBytes(rawPath);
        data.Metadata.Status = "running";
        data.Metadata.FinalizedUtc = null;
        var a = data.Attempts[^1];
        a.Status = "running";
        a.EndUtc = null;
        a.EndTicks = null;
        File.WriteAllText(Path.Combine(result.Directory, "metadata.json"), JsonSerializer.Serialize(data.Metadata, DataJson.Options));
        File.WriteAllText(Path.Combine(result.Directory, "benchmarks.json"), JsonSerializer.Serialize(data.Attempts, DataJson.Options));
        File.AppendAllText(rawPath, "{\"sequence\":");
        var recovered = RunStore.Recover(result.Directory);
        Assert.Equal("interrupted", recovered.Metadata.Status);
        Assert.Equal(37, recovered.Frames.Count);
        Assert.Null(recovered.Attempts[^1].Score);
        Assert.Null(recovered.Attempts[^1].EndUtc);
        Assert.NotNull(recovered.Attempts[^1].LastObservedUtc);
        Assert.True(File.ReadAllBytes(rawPath).AsSpan().StartsWith(original));
        Assert.Equal("interrupted", RunStore.Recover(result.Directory).Metadata.Status);
    }

    [Fact]
    public async Task RecoveryDoesNotTreatMiddleCorruptionAsACrashTail()
    {
        var (result, data) = await Run();
        data.Metadata.Status = "running";
        File.WriteAllText(Path.Combine(result.Directory, "metadata.json"), JsonSerializer.Serialize(data.Metadata, DataJson.Options));
        var path = Path.Combine(result.Directory, "raw", "telemetry.jsonl");
        var lines = File.ReadAllLines(path);
        lines[1] = "broken";
        File.WriteAllLines(path, lines);
        Assert.Throws<JsonException>(() => RunStore.Recover(result.Directory));
    }

    [Fact]
    public void RecoveryRefusesAnActiveWriter()
    {
        var m = Metadata();
        using var store = RunStore.Create(root, m, new Scenario(), []);
        Assert.Throws<IOException>(() => RunStore.Recover(store.DirectoryPath));
    }

    [Fact]
    public async Task ReportEscapesNotesIsOfflineAndKeepsManualResultsSeparate()
    {
        var (result, data) = await Run(new Scenario { Note = "<script>alert('test')</script> & private" });
        var legacy = DataJson.Read<LegacyDocument>(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", "manual-results.json"));
        legacy.Validate();
        data = data with { Legacy = legacy };
        string html = ReportGenerator.Render(data, Analyzer.Analyze(data));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src", html);
        Assert.Contains("22877", html);
        Assert.Contains("21703", html);
        Assert.Contains("synthetic_points", html);
        Assert.Contains("legacy/manual", html);
        ReportGenerator.Write(result.Directory, data);
        Assert.True(File.Exists(Path.Combine(result.Directory, "analysis.json")));
    }

    [Fact]
    public async Task ReplayReadsDataWithoutChangingRawFiles()
    {
        var (result, data) = await Run();
        string path = Path.Combine(result.Directory, "raw", "telemetry.jsonl");
        string original = File.ReadAllText(path);
        ReportGenerator.Write(result.Directory, RunStore.Read(result.Directory));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(3, Analyzer.Analyze(data).Series.ValidCount);
    }

    [Fact]
    public async Task ComparisonRejectsDifferentWorkload()
    {
        var a = await Run();
        var b = await Run(new Scenario { SamplesPerAttempt = 9 });
        Assert.False(Analyzer.Compare(a.Data, b.Data).Compatible);
        Assert.Null(Analyzer.Compare(a.Data, b.Data).ScoreChangePercent);
    }

    [Fact]
    public void LegacyImportHasSixManualRowsAndNoReconstructedTelemetry()
    {
        var legacy = DataJson.Read<LegacyDocument>(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", "manual-results.json"));
        legacy.Validate();
        Assert.Equal(6, legacy.Entries.Length);
        Assert.All(legacy.Entries, e => Assert.False(e.RawAvailable));
        string directory;
        using (var store = RunStore.Create(root, Metadata() with { Mode = "legacy/manual", Status = "imported" }, null, [], legacy))
            directory = store.DirectoryPath;
        var data = RunStore.Read(directory);
        Assert.Empty(data.Frames);
        Assert.Empty(data.Attempts);
        Assert.Null(Analyzer.Analyze(data).Series.Mean);
    }

    [Theory]
    [InlineData("{\"mode\":\"real\"}")]
    [InlineData("{\"repeat\":0}")]
    [InlineData("{\"sampleIntervalMs\":0}")]
    [InlineData("{\"faults\":{\"failAttempt\":9}}")]
    [InlineData("{\"faults\":null}")]
    [InlineData("{\"note\":null}")]
    public void InvalidScenariosNeverStart(string json)
    {
        var s = JsonSerializer.Deserialize<Scenario>(json, DataJson.Options)!;
        Assert.Throws<InvalidDataException>(s.Validate);
    }

    private static RunMetadata Metadata() => new()
    {
        SessionId = Guid.NewGuid().ToString("N"), Mode = "mock", CreatedUtc = DateTimeOffset.UtcNow,
        TimeMode = "synthetic-fast", Inventory = new MockInventory().Capture(), Profile = MockInventory.Profile
    };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
