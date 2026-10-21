using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CpuLab.Core;

public sealed record ProcessCommand(string Executable, string[] Arguments, string WorkingDirectory)
{
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable)
            || !Path.IsPathFullyQualified(WorkingDirectory) || !Directory.Exists(WorkingDirectory)
            || Arguments is null || Arguments.Length > 256 || Arguments.Any(a => a is null || a.Contains('\0') || a.Length > 32768))
            throw new ArgumentException("Process command requires an existing absolute executable/work directory and bounded arguments.");
    }
}

public sealed record HostCompletion(int? ProcessId, int? ExitCode, string? Error, DateTimeOffset ObservedUtc);
public sealed record ProcessRunResult(string Directory, string Status, int? ExitCode, int? ProcessId,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, double ElapsedMs, string Cleanup, string? Error,
    long StdoutBytes, long StderrBytes, bool OutputTruncated);

public static class ProcessProtocol
{
    public static void Write<T>(string directory, string name, T value)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, DataJson.Options));
        File.Move(path + ".tmp", path);
    }

    public static string DotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null && File.Exists(configured)) return Path.GetFullPath(configured);
        // Runtime layout: <dotnet>/shared/Microsoft.NETCore.App/<version>/.
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string candidate = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(candidate)) throw new FileNotFoundException("Could not locate dotnet host. Set DOTNET_HOST_PATH.");
        return candidate;
    }
}

public sealed class ProcessSupervisor(string hostAssembly)
{
    public async Task<ProcessRunResult> RunAsync(ProcessCommand command, string outputRoot,
        TimeSpan timeout, CancellationToken cancellationToken = default, long outputLimitBytes = 8 * 1024 * 1024)
    {
        command.Validate();
        if (!Path.IsPathFullyQualified(hostAssembly) || !File.Exists(hostAssembly))
            throw new ArgumentException("An existing absolute CpuLab.ProcessHost assembly path is required.");
        if (timeout < TimeSpan.FromMilliseconds(50) || timeout > TimeSpan.FromHours(2)
            || outputLimitBytes is < 1024 or > 128 * 1024 * 1024)
            throw new ArgumentException("Timeout/output limits exceed supported bounds.");
        string directory = Path.GetFullPath(Path.Combine(outputRoot, $"process_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        ProcessProtocol.Write(directory, "command.json", command);
        ProcessProtocol.Write(directory, "supervision.json", new
        {
            SchemaVersion = 1, HostAssembly = hostAssembly, TimeoutMs = timeout.TotalMilliseconds,
            OutputLimitBytesPerStream = outputLimitBytes,
            Containment = OperatingSystem.IsWindows() ? "windows-job" : "linux-process-group",
            WindowOrigin = "process-lifecycle-not-benchmark-window"
        });
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(timeout);
        using var outputLimit = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, outputLimit.Token);
        using var drainCancel = new CancellationTokenSource();
        using var containment = new ProcessContainment();
        using var host = new Process();
        host.StartInfo = new ProcessStartInfo(ProcessProtocol.DotNetHost())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory
        };
        host.StartInfo.ArgumentList.Add(hostAssembly);
        host.StartInfo.ArgumentList.Add(directory);
        bool launched = false, contained = false;
        Task[] drains = [];
        string status = "failed", cleanup = "not-started";
        string? error = null;
        HostCompletion? completion = null;
        await using var stdout = new FileStream(Path.Combine(directory, "stdout.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var stderr = new FileStream(Path.Combine(directory, "stderr.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!host.Start()) throw new InvalidOperationException("Process host did not start.");
            launched = true;
            drains = [
                DrainAsync(host.StandardOutput.BaseStream, stdout, outputLimitBytes, outputLimit, drainCancel.Token),
                DrainAsync(host.StandardError.BaseStream, stderr, outputLimitBytes, outputLimit, drainCancel.Token)
            ];
            await WaitForControlAsync("ready.json");
            if (DataJson.Read<int>(Path.Combine(directory, "ready.json")) != host.Id)
                throw new InvalidDataException("Unexpected process host identity.");
            containment.AttachReadyHost(host);
            contained = true;
            linked.Token.ThrowIfCancellationRequested();
            // Trusted host cannot start the target before this gate.
            await host.StandardInput.WriteLineAsync("START".AsMemory(), linked.Token);
            await host.StandardInput.FlushAsync(linked.Token);
            await WaitForControlAsync("completed.json");
            completion = DataJson.Read<HostCompletion>(Path.Combine(directory, "completed.json"));
            status = completion.Error is not null ? "start-failed" : completion.ExitCode == 0 ? "completed" : "failed";
            error = completion.Error;
        }
        catch (OperationCanceledException)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : outputLimit.IsCancellationRequested ? "output-limit" : "timeout";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or InvalidOperationException or JsonException)
        {
            error = ex.Message;
        }
        finally
        {
            if (launched)
            {
                try
                {
                    cleanup = contained ? await containment.StopAsync() : "host-only-before-start";
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
                {
                    cleanup = "failed";
                    error = Append(error, ex.Message);
                }
                // Fallback only targets our own live host. No process-name matching.
                try
                {
                    try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (host.HasExited) { }
                    catch (Win32Exception) when (host.HasExited) { }
                    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
                {
                    cleanup = "failed";
                    error = Append(error, ex.Message);
                }
                host.StandardInput.Close();
            }
            // Draining after exit is bounded even if a process outside the group inherited a pipe.
            drainCancel.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(drains); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                error = Append(error, $"Output capture incomplete: {ex.Message}");
                if (status == "completed") status = "capture-failed";
            }
            await stdout.FlushAsync();
            await stderr.FlushAsync();
        }
        if (outputLimit.IsCancellationRequested && status == "completed") status = "output-limit";
        if (cleanup == "failed") status = "cleanup-failed";
        int? pid = completion?.ProcessId;
        string pidPath = Path.Combine(directory, "target.json");
        if (pid is null && File.Exists(pidPath)) pid = DataJson.Read<int>(pidPath);
        var result = new ProcessRunResult(directory, status, completion?.ExitCode, pid,
            started, DateTimeOffset.UtcNow, elapsed.Elapsed.TotalMilliseconds, cleanup, error,
            stdout.Length, stderr.Length, outputLimit.IsCancellationRequested);
        ProcessProtocol.Write(directory, "process-result.json", result);
        return result;

        async Task WaitForControlAsync(string name)
        {
            string path = Path.Combine(directory, name);
            while (!File.Exists(path))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (drains.Any(t => t.IsFaulted)) await Task.WhenAll(drains.Where(t => t.IsFaulted));
                if (host.HasExited)
                    throw new InvalidOperationException($"Process host exited before {name} (exit {host.ExitCode}).");
                await Task.Delay(20, linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
        }
    }

    private static string Append(string? prior, string next) => prior is null ? next : $"{prior}; {next}";

    private static async Task DrainAsync(Stream input, Stream output, long limit,
        CancellationTokenSource reached, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        long written = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            int keep = (int)Math.Min(count, limit - written);
            if (keep > 0) { await output.WriteAsync(buffer.AsMemory(0, keep), token); written += keep; }
            if (keep < count) reached.Cancel();
        }
    }
}
