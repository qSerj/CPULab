using System.Diagnostics;
using System.Text.Json;
using CpuLab.Core;
using Xunit;

namespace CpuLab.Tests;

public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpulab-process-tests-" + Guid.NewGuid().ToString("N"));
    private static string Host => Path.Combine(AppContext.BaseDirectory, "process-host", "CpuLab.ProcessHost.dll");
    private static string Probe => Path.Combine(AppContext.BaseDirectory, "process-probe", "CpuLab.ProcessProbe.dll");
    private ProcessCommand Command(params string[] arguments) =>
        new(ProcessProtocol.DotNetHost(), [Probe, .. arguments], AppContext.BaseDirectory);
    private Task<ProcessRunResult> Run(string[] arguments, CancellationToken token = default, long limit = 8 * 1024 * 1024) =>
        new ProcessSupervisor(Host).RunAsync(Command(arguments), root, TimeSpan.FromSeconds(10), token, limit);

    [Fact]
    public async Task HostNeverStartsTargetWithoutExplicitStartGate()
    {
        Directory.CreateDirectory(root);
        ProcessProtocol.Write(root, "command.json", Command("echo", "must-not-run"));
        var start = new ProcessStartInfo(ProcessProtocol.DotNetHost()) { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add(Host);
        start.ArgumentList.Add(root);
        using var host = Process.Start(start)!;
        try
        {
            await WaitUntil(() => File.Exists(Path.Combine(root, "ready.json")));
            host.StandardInput.Close();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(Path.Combine(root, "target.json")));
            Assert.False(File.Exists(Path.Combine(root, "completed.json")));
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CapturesBothStreamsAndPassesArgumentsWithoutShellParsing()
    {
        string[] arguments = ["spaces in argument", "\"quoted\"", "Привет", "$HOME; & echo injected", ""];
        var result = await Run(["echo", .. arguments]);
        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(result.Directory, "stdout.bin"))));
        Assert.Equal("probe-stderr", File.ReadAllText(Path.Combine(result.Directory, "stderr.bin")).Trim());
        Assert.Equal(OperatingSystem.IsWindows() ? "windows-job-empty" : "linux-group-kill-requested", result.Cleanup);
        Assert.True(File.Exists(Path.Combine(result.Directory, "process-result.json")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "supervision.json")));
        Assert.False(result.OutputTruncated);
        Assert.NotNull(result.ProcessId);
    }

    [Fact]
    public async Task NonzeroExitRetainsOutputAndExactExitCode()
    {
        var result = await Run(["fail"]);
        Assert.Equal("failed", result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("before-failure", File.ReadAllText(Path.Combine(result.Directory, "stdout.bin")));
    }

    [Fact]
    public async Task BothFullPipesDrainWithoutDeadlock()
    {
        var result = await Run(["flood"]);
        Assert.Equal("completed", result.Status);
        Assert.Equal(512 * 1024, new FileInfo(Path.Combine(result.Directory, "stdout.bin")).Length);
        Assert.Equal(512 * 1024, new FileInfo(Path.Combine(result.Directory, "stderr.bin")).Length);
    }

    [Fact]
    public async Task OutputLimitStopsAndBoundsFiles()
    {
        var result = await Run(["flood"], limit: 4096);
        Assert.Equal("output-limit", result.Status);
        Assert.True(result.OutputTruncated);
        Assert.InRange(result.StdoutBytes, 0, 4096);
        Assert.InRange(result.StderrBytes, 0, 4096);
        Assert.InRange(new FileInfo(Path.Combine(result.Directory, "stdout.bin")).Length, 0, 4096);
        Assert.InRange(new FileInfo(Path.Combine(result.Directory, "stderr.bin")).Length, 0, 4096);
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotLaunchHost()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await Run(["wait"], cancel.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.Null(result.ExitCode);
        Assert.Null(result.ProcessId);
        Assert.False(File.Exists(Path.Combine(result.Directory, "ready.json")));
    }

    [Fact]
    public async Task TimeoutTerminatesRootAndDescendant()
    {
        Directory.CreateDirectory(root);
        string pidFile = Path.Combine(root, "child.pid");
        var result = await new ProcessSupervisor(Host).RunAsync(Command("tree", pidFile), root, TimeSpan.FromSeconds(5));
        Assert.Equal("timeout", result.Status);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.ProcessId);
        await AssertStopped(result.ProcessId.Value);
        await AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [Fact]
    public async Task CancellationAfterChildStartsTerminatesEntireOwnedTree()
    {
        Directory.CreateDirectory(root);
        string pidFile = Path.Combine(root, "child.pid");
        using var cancel = new CancellationTokenSource();
        Task<ProcessRunResult> running = Run(["tree", pidFile], cancel.Token);
        try { await WaitUntil(() => File.Exists(pidFile)); }
        finally { cancel.Cancel(); }
        var result = await running;
        Assert.Equal("cancelled", result.Status);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.ProcessId);
        await AssertStopped(result.ProcessId.Value);
        await AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [Fact]
    public async Task NormalRootExitStillCleansUpLeftoverDescendant()
    {
        Directory.CreateDirectory(root);
        string pidFile = Path.Combine(root, "child.pid");
        var result = await Run(["orphan", pidFile]);
        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.ExitCode);
        await AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [Fact]
    public async Task AbruptOwnerDeathDoesNotLeaveFixtureTreeRunning()
    {
        Directory.CreateDirectory(root);
        string pidFile = Path.Combine(root, "child.pid");
        var start = new ProcessStartInfo(ProcessProtocol.DotNetHost()) { UseShellExecute = false };
        foreach (string argument in new[] { Probe, "supervise", Host, root, pidFile }) start.ArgumentList.Add(argument);
        using var owner = Process.Start(start)!;
        try
        {
            await WaitUntil(() => File.Exists(pidFile));
            string session = Directory.GetDirectories(root).Single();
            int hostPid = DataJson.Read<int>(Path.Combine(session, "ready.json"));
            int targetPid = DataJson.Read<int>(Path.Combine(session, "target.json"));
            int descendantPid = int.Parse(File.ReadAllText(pidFile));
            owner.Kill(); // Deliberately not entireProcessTree: exercise job/pipe lifetime.
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await AssertStopped(hostPid);
            await AssertStopped(targetPid);
            await AssertStopped(descendantPid);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MissingExecutableIsRejectedBeforeCreatingOutput()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessSupervisor(Host).RunAsync(
            new ProcessCommand(Path.Combine(root, "missing.exe"), [], AppContext.BaseDirectory),
            root, TimeSpan.FromSeconds(1)));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task InvalidBinaryReportsStartFailureWithoutInventedExitCode()
    {
        Directory.CreateDirectory(root);
        string fake = Path.Combine(root, "invalid.exe");
        File.WriteAllText(fake, "This is not an executable.");
        var result = await new ProcessSupervisor(Host).RunAsync(
            new ProcessCommand(fake, [], root), root, TimeSpan.FromSeconds(5));
        Assert.Equal("start-failed", result.Status);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.Error);
    }

    private static async Task AssertStopped(int pid) => await WaitUntil(() => !IsRunning(pid));

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (OperatingSystem.IsLinux())
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                // An adopted zombie has stopped executing, even if container init has not reaped it.
                return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Process fixture condition was not reached.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
