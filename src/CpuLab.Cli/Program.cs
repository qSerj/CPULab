using System.Text.Json;
using CpuLab.Core;

return await ExecuteAsync(args);

static async Task<int> ExecuteAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("""
CPU Lab 0.1 — mock-first journal. No real loads, drivers or hardware access.

  mock [--scenario path.json] [--output runs] [--legacy path.json] [--realtime] [--compare session-dir]
  report --run session-dir [--compare session-dir]
  recover --run session-dir
  import-legacy --file legacy.json [--output runs]
  import-hwinfo --file sensors.csv --profile dialect.json [--output runs]
  process-demo [--output runs]
  probe-series [--scenario probe-series.json] [--output runs]
  parse-r23 --file stdout.txt --exit-code 0
  parse-7zip --file stdout.txt --exit-code 0

Mock uses synthetic experiment time (fast by default). --realtime adds delays for Ctrl+C.
report/recover never restart a workload. Parser results are provisional, not hardware-qualified.
""");
        return 0;
    }
    using var cancel = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
    Console.CancelKeyPress += handler;
    try
    {
        var options = Options.Parse(args.Skip(1).ToArray());
        switch (args[0])
        {
            case "probe-series":
            {
                options.Allow("scenario", "output");
                var scenario = options.Get("scenario") is { } path ? DataJson.Read<ProbeSeriesScenario>(path) : new();
                var adapter = new ProbeBenchmarkAdapter(
                    Path.Combine(AppContext.BaseDirectory, "process-host", "CpuLab.ProcessHost.dll"),
                    Path.Combine(AppContext.BaseDirectory, "process-probe", "CpuLab.ProcessProbe.dll"));
                Console.WriteLine("AUTOMATION CHECK: lightweight fixture processes, constant scores, no sensor telemetry.");
                var result = await new ProbeSeriesRunner(adapter).RunAsync(scenario, options.Get("output") ?? "runs", cancel.Token);
                WriteReport(result.Directory, null);
                Console.WriteLine($"{result.Status}: {result.Reason}\nReport: {Path.Combine(result.Directory, "report.html")}");
                return result.Status switch { "completed" => 0, "cancelled" => 130, "timeout" => 124, _ => 1 };
            }
            case "process-demo":
            {
                options.Allow("output");
                string host = Path.Combine(AppContext.BaseDirectory, "process-host", "CpuLab.ProcessHost.dll");
                string probe = Path.Combine(AppContext.BaseDirectory, "process-probe", "CpuLab.ProcessProbe.dll");
                var command = new ProcessCommand(ProcessProtocol.DotNetHost(),
                    [probe, "echo", "CPU Lab process demo", "spaces and \"quotes\""], Environment.CurrentDirectory);
                var result = await new ProcessSupervisor(host).RunAsync(command, options.Get("output") ?? "runs",
                    TimeSpan.FromSeconds(10), cancel.Token);
                Console.WriteLine($"{result.Status}; exit={result.ExitCode}; cleanup={result.Cleanup}\nArtifacts: {result.Directory}");
                return result.Status == "completed" ? 0 : result.Status == "cancelled" ? 130 : 1;
            }
            case "import-hwinfo":
            {
                options.Allow("file", "profile", "output");
                var profile = DataJson.Read<HwinfoCsvProfile>(options.Require("profile"));
                var result = HwinfoCsvImporter.Import(options.Require("file"), profile, options.Get("output") ?? "runs");
                Console.WriteLine($"{result.Status}: {result.Records} observations, {result.Channels} channels.\nReport: {Path.Combine(result.Directory, "report.html")}");
                return 0;
            }
            case "mock":
            {
                options.Allow("scenario", "output", "legacy", "realtime", "compare");
                var scenario = options.Get("scenario") is { } path ? DataJson.Read<Scenario>(path) : new Scenario();
                scenario.Validate();
                var legacy = ReadLegacy(options.Get("legacy"));
                Console.WriteLine("SIMULATION ONLY. No hardware measurements, no CPU stress.");
                var runner = new ScenarioRunner(new MockInventory(), new MockTelemetryProvider(scenario.Faults), new MockBenchmarkAdapter(scenario));
                var result = await runner.RunAsync(scenario, options.Get("output") ?? "runs", options.Has("realtime"), legacy,
                    cancel.Token, onState: state => Console.WriteLine($"  {state}"));
                WriteReport(result.Directory, options.Get("compare"));
                Console.WriteLine($"{result.Status}: {result.Reason}\nReport: {Path.Combine(result.Directory, "report.html")}");
                return result.Status switch { "completed" => 0, "cancelled" => 130, "timeout" => 124, _ => 1 };
            }
            case "report":
            {
                options.Allow("run", "compare");
                string directory = Path.GetFullPath(options.Require("run"));
                using var guard = new FileStream(Path.Combine(directory, ".session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                WriteReport(directory, options.Get("compare"));
                Console.WriteLine($"Report: {Path.Combine(directory, "report.html")}");
                return 0;
            }
            case "recover":
            {
                options.Allow("run");
                string directory = Path.GetFullPath(options.Require("run"));
                var data = RunStore.Recover(directory);
                ReportGenerator.Write(directory, data);
                Console.WriteLine($"{data.Metadata.Status}: {data.Metadata.Reason}\nReport: {Path.Combine(directory, "report.html")}");
                return 0;
            }
            case "import-legacy":
            {
                options.Allow("file", "output");
                var legacy = ReadLegacy(options.Require("file"))!;
                var metadata = new RunMetadata
                {
                    SessionId = Guid.NewGuid().ToString("N"), Mode = "legacy/manual", CreatedUtc = DateTimeOffset.UtcNow,
                    FinalizedUtc = DateTimeOffset.UtcNow, Status = "imported", TimeMode = "unknown-historical-times",
                    Inventory = new("legacy/manual", new() { ["cpu"] = new("Intel Core i5-13600KF", null, "user", "Technical specification") }),
                    Profile = new("stand-a-legacy", "Stand A as reported by user; not a fresh inventory", new()),
                    Warnings = ["Legacy/manual: no raw telemetry, no exact dates, no mean power, uncertainty unknown."]
                };
                string directory;
                using (var store = RunStore.Create(options.Get("output") ?? "runs", metadata, null, [], legacy))
                {
                    directory = store.DirectoryPath;
                    store.Event(metadata.CreatedUtc, "imported", reason: "Manual aggregates; no reconstructed samples.");
                }
                WriteReport(directory, null);
                Console.WriteLine($"Imported {legacy.Entries.Length} manual results. Report: {Path.Combine(directory, "report.html")}");
                return 0;
            }
            case "parse-r23":
            case "parse-7zip":
            {
                options.Allow("file", "exit-code");
                string output = File.ReadAllText(options.Require("file"));
                if (!int.TryParse(options.Require("exit-code"), out int code)) throw new ArgumentException("--exit-code must be an integer.");
                var result = args[0] == "parse-r23" ? BenchmarkParsers.CinebenchR23(output, code, true) : BenchmarkParsers.SevenZip(output, code);
                Console.WriteLine(JsonSerializer.Serialize(result, DataJson.Options));
                return result.Valid ? 0 : 1;
            }
            default: throw new ArgumentException($"Unknown command: {args[0]}. Use --help.");
        }
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
        or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
    {
        Console.Error.WriteLine($"CPU Lab: {ex.Message}");
        return 2;
    }
    finally { Console.CancelKeyPress -= handler; }
}

static LegacyDocument? ReadLegacy(string? path)
{
    if (path is null) return null;
    var legacy = DataJson.Read<LegacyDocument>(path);
    legacy.Validate();
    return legacy;
}

static void WriteReport(string directory, string? comparison)
{
    var data = RunStore.Read(directory);
    var previous = comparison is null ? null : RunStore.Read(comparison);
    ReportGenerator.Write(directory, data, previous);
}

sealed class Options
{
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected an option, got {arg}.");
            string name = arg[2..];
            string? value = null;
            if (name != "realtime")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {arg}.");
                value = args[i];
            }
            if (!result.values.TryAdd(name, value)) throw new ArgumentException($"Duplicate option {arg}.");
        }
        return result;
    }
    public void Allow(params string[] names)
    {
        foreach (string name in values.Keys)
            if (!names.Contains(name)) throw new ArgumentException($"Unsupported option --{name}.");
    }
    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.GetValueOrDefault(name);
    public string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing required option --{name}.");
}
