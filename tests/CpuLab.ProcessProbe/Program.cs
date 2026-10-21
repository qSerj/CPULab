using System.Diagnostics;
using System.Text.Json;
using CpuLab.Core;

// Bounded, idle fixture. No hardware APIs and no stress workload.
if (args.Length == 0) return 2;
switch (args[0])
{
    case "score":
        int ordinal = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine(JsonSerializer.Serialize(
            new ProbeScore(1, "synthetic-probe-score", ordinal, 1000 + (ordinal - 2) * 5, "fixture_points"), DataJson.Compact));
        return 0;
    case "supervise":
        // Test fixture for abrupt owner death; the nested target is also this idle probe.
        var command = new ProcessCommand(Environment.ProcessPath!,
            [typeof(Program).Assembly.Location, "tree", args[3]], Environment.CurrentDirectory);
        await new ProcessSupervisor(args[1]).RunAsync(command, args[2], TimeSpan.FromSeconds(20));
        return 0;
    case "echo":
        Console.WriteLine(JsonSerializer.Serialize(args.Skip(1)));
        Console.Error.WriteLine("probe-stderr");
        return 0;
    case "fail":
        Console.WriteLine("before-failure");
        return 7;
    case "wait":
        Console.WriteLine($"probe-pid:{Environment.ProcessId}");
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "flood":
        byte[] block = Enumerable.Repeat((byte)'x', 4096).ToArray();
        await Task.WhenAll(
            WriteBlocks(Console.OpenStandardOutput(), block),
            WriteBlocks(Console.OpenStandardError(), block));
        return 0;
    case "tree":
    case "orphan":
        string dotnet = Environment.ProcessPath!;
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("wait");
        using (var child = Process.Start(start)!)
        {
            ProcessProtocol.Write(Path.GetDirectoryName(args[1])!, Path.GetFileName(args[1]), child.Id);
            Console.WriteLine($"child-pid:{child.Id}");
            if (args[0] == "tree") await child.WaitForExitAsync();
        }
        return 0;
    default: return 2;
}

static async Task WriteBlocks(Stream stream, byte[] block)
{
    for (int i = 0; i < 128; i++) await stream.WriteAsync(block);
    await stream.FlushAsync();
}
