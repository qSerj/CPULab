using System.Diagnostics;
using CpuLab.Core;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
string directory = args[0];
Process? target = null;
bool prepared = false;
try
{
    ProcessContainment.PrepareHost();
    prepared = true;
    ProcessProtocol.Write(directory, "ready.json", Environment.ProcessId);
    // No target creation before the parent's successful containment handshake.
    string? gate = await Task.Run(Console.ReadLine).WaitAsync(TimeSpan.FromSeconds(30));
    if (gate != "START") return 2;
    var command = DataJson.Read<ProcessCommand>(Path.Combine(directory, "command.json"));
    command.Validate();
    // This pipe has a single writer: the supervisor. EOF means the owner is gone.
    Task<string?> ownerGone = Task.Run(Console.ReadLine);
    var start = new ProcessStartInfo(command.Executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = command.WorkingDirectory,
        RedirectStandardInput = true
    };
    foreach (string arg in command.Arguments) start.ArgumentList.Add(arg);
    target = Process.Start(start) ?? throw new InvalidOperationException("Target did not start.");
    target.StandardInput.Close();
    ProcessProtocol.Write(directory, "target.json", target.Id);
    Task exited = target.WaitForExitAsync();
    if (await Task.WhenAny(exited, ownerGone) == ownerGone) return 3;
    await exited;
    ProcessProtocol.Write(directory, "completed.json", new HostCompletion(target.Id, target.ExitCode, null, DateTimeOffset.UtcNow));
    // Retain group identity while the parent terminates any descendants left behind.
    await ownerGone;
    return 0;
}
catch (Exception ex)
{
    ProcessProtocol.Write(directory, "completed.json", new HostCompletion(target?.Id, null, ex.Message, DateTimeOffset.UtcNow));
    return 2;
}
finally
{
    if (OperatingSystem.IsLinux() && prepared) ProcessContainment.KillLinuxGroup(Environment.ProcessId);
    if (target is not null)
    {
        if (!target.HasExited) target.Kill(entireProcessTree: true);
        target.Dispose();
    }
}
