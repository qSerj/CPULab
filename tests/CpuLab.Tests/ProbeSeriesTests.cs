using System.Text.Json;
using CpuLab.Core;
using Xunit;

namespace CpuLab.Tests;

public sealed class ProbeSeriesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpulab-series-tests-" + Guid.NewGuid().ToString("N"));
    private static ProbeBenchmarkAdapter Adapter => new(
        Path.Combine(AppContext.BaseDirectory, "process-host", "CpuLab.ProcessHost.dll"),
        Path.Combine(AppContext.BaseDirectory, "process-probe", "CpuLab.ProcessProbe.dll"));
    private Task<RunResult> Run(ProbeSeriesScenario? scenario = null, CancellationToken token = default) =>
        new ProbeSeriesRunner(Adapter).RunAsync(scenario ?? new(), root, token);

    [Fact]
    public async Task ThreeProcessesProduceOneJournalAndReportWithoutFakeTelemetry()
    {
        var result = await Run();
        var data = RunStore.Read(result.Directory);
        Assert.Equal("completed", result.Status);
        Assert.Equal("process-probe", data.Metadata.Mode);
        Assert.Empty(data.Frames);
        Assert.Empty(data.Sensors);
        Assert.Equal(3, data.Attempts.Count);
        Assert.Equal(new double?[] { 995, 1000, 1005 }, data.Attempts.Select(a => a.Score));
        Assert.Equal(1000, Analyzer.Analyze(data).Series.Mean);
        foreach (var attempt in data.Attempts)
        {
            Assert.Equal("observed-process-lifecycle", attempt.WindowOrigin);
            Assert.Equal("fixture_points", attempt.ScoreUnit);
            Assert.True(attempt.EndTicks > attempt.StartTicks);
            string directory = Path.Combine(result.Directory, attempt.ProcessResultDirectory!);
            Assert.Equal(0, DataJson.Read<ProcessRunResult>(Path.Combine(directory, "process-result.json")).ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, "stdout.bin")));
            Assert.DoesNotContain("MOCK:", File.ReadAllText(Path.Combine(result.Directory, "attempts", attempt.Id, "stdout.txt")));
        }
        Assert.True(data.Attempts[1].StartUtc >= data.Attempts[0].EndUtc);
        Assert.True(data.Attempts[2].StartUtc >= data.Attempts[1].EndUtc);
        Assert.False(Analyzer.Compare(data, data).Compatible);
        ReportGenerator.Write(result.Directory, data);
        string html = File.ReadAllText(Path.Combine(result.Directory, "report.html"));
        Assert.Contains("ПРОВЕРКА АВТОМАТИЗАЦИИ", html);
        Assert.Contains("Телеметрия unavailable", html);
        Assert.DoesNotContain("Только ручные итоги из ТЗ", html);
        Assert.Contains("process-result.json", html);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "probe-provenance.json")));
        Assert.All(provenance.RootElement.GetProperty("files").EnumerateArray(), f =>
            Assert.Equal(64, f.GetProperty("sha256").GetString()!.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondFailurePreservesFirstAndStopsSeries(bool missingScore)
    {
        var result = await Run(missingScore ? new() { MissingScoreAttempt = 2 } : new() { FailAttempt = 2 });
        var data = RunStore.Read(result.Directory);
        Assert.Equal("failed", result.Status);
        Assert.Equal(2, data.Attempts.Count);
        Assert.Equal("completed", data.Attempts[0].Status);
        Assert.Equal(995, data.Attempts[0].Score);
        Assert.Equal("failed", data.Attempts[1].Status);
        Assert.Null(data.Attempts[1].Score);
        Assert.NotNull(data.Attempts[1].ProcessResultDirectory);
        Assert.False(Directory.Exists(Path.Combine(result.Directory, "attempts", "attempt-003")));
    }

    [Fact]
    public async Task TimeoutPreservesProcessEvidenceAndStopsNextAttempt()
    {
        var result = await Run(new() { WaitAttempt = 1, AttemptTimeoutMs = 2000 });
        var data = RunStore.Read(result.Directory);
        Assert.Equal("timeout", result.Status);
        var attempt = Assert.Single(data.Attempts);
        Assert.Null(attempt.Score);
        Assert.NotNull(attempt.EndUtc);
        Assert.NotNull(attempt.ProcessResultDirectory);
        Assert.Equal("timeout", DataJson.Read<ProcessRunResult>(
            Path.Combine(result.Directory, attempt.ProcessResultDirectory!, "process-result.json")).Status);
    }

    [Fact]
    public async Task CancellationBeforeStartCreatesReadableEmptySession()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await Run(token: cancel.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.Empty(RunStore.Read(result.Directory).Attempts);
    }

    [Fact]
    public async Task CancellationDuringActiveProcessRetainsPartialJournal()
    {
        using var cancel = new CancellationTokenSource();
        var running = Run(new() { WaitAttempt = 1 }, cancel.Token);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!Directory.Exists(root) || !Directory.EnumerateFiles(root, "target.json", SearchOption.AllDirectories).Any())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Fixture did not start.");
                await Task.Delay(20);
            }
        }
        finally { cancel.Cancel(); }
        var result = await running;
        Assert.Equal("cancelled", result.Status);
        var attempt = Assert.Single(RunStore.Read(result.Directory).Attempts);
        Assert.Equal("cancelled", attempt.Status);
        Assert.Null(attempt.Score);
        Assert.NotNull(attempt.ProcessResultDirectory);
    }

    [Fact]
    public async Task TotalDeadlineDuringProcessIsDistinctFromOperatorCancellation()
    {
        var result = await Run(new() { WaitAttempt = 1, TotalTimeoutMs = 1500, AttemptTimeoutMs = 10000 });
        var attempt = Assert.Single(RunStore.Read(result.Directory).Attempts);
        Assert.Equal("timeout", result.Status);
        Assert.Equal("Total series deadline reached.", attempt.Reason);
        Assert.Null(attempt.Score);
    }

    [Fact]
    public async Task TotalDeadlineDuringPauseRetainsCompletedAttempt()
    {
        var result = await Run(new() { PauseMs = 5000, TotalTimeoutMs = 3000 });
        var data = RunStore.Read(result.Directory);
        Assert.Equal("timeout", result.Status);
        var attempt = Assert.Single(data.Attempts);
        Assert.Equal("completed", attempt.Status);
        Assert.Equal(995, attempt.Score);
    }

    [Fact]
    public async Task RecoveryMarksInterruptedWithoutRestartingOrInventingEnd()
    {
        var result = await Run(new() { Repeat = 1 });
        var data = RunStore.Read(result.Directory);
        data.Metadata.Status = "running";
        data.Metadata.FinalizedUtc = null;
        var attempt = data.Attempts[0];
        attempt.Status = "running";
        attempt.Score = null;
        attempt.EndUtc = null;
        attempt.EndTicks = null;
        var observed = attempt.LastObservedUtc;
        File.WriteAllText(Path.Combine(result.Directory, "metadata.json"), JsonSerializer.Serialize(data.Metadata, DataJson.Options));
        File.WriteAllText(Path.Combine(result.Directory, "benchmarks.json"), JsonSerializer.Serialize(data.Attempts, DataJson.Options));
        string outputPath = Path.Combine(result.Directory, attempt.ProcessResultDirectory!, "stdout.bin");
        byte[] output = File.ReadAllBytes(outputPath);
        var recovered = RunStore.Recover(result.Directory);
        Assert.Equal("interrupted", recovered.Metadata.Status);
        Assert.Equal("interrupted", recovered.Attempts[0].Status);
        Assert.Null(recovered.Attempts[0].Score);
        Assert.Null(recovered.Attempts[0].EndUtc);
        Assert.Equal(observed, recovered.Attempts[0].LastObservedUtc);
        Assert.Equal(output, File.ReadAllBytes(outputPath));
        Assert.Single(Directory.GetDirectories(Path.Combine(result.Directory, "attempts", attempt.Id, "processes")));
        Assert.Equal("interrupted", RunStore.Recover(result.Directory).Metadata.Status);
    }

    [Fact]
    public async Task InvalidScenarioRejectedBeforeCreatingSession()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(new() { Mode = "real" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(new() { FailAttempt = 1, WaitAttempt = 1 }));
        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
