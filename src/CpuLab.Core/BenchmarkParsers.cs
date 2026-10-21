using System.Globalization;
using System.Text.RegularExpressions;

namespace CpuLab.Core;

public sealed record ParsedBenchmark(bool Valid, double? Score, string Unit, string? Reason,
    string Qualification = "provisional: fixture-tested, not qualified on a Windows binary");

// These conservative parsers are preparation for adapters, not evidence that a
// particular installed executable supports stdout redirection or these formats.
public static partial class BenchmarkParsers
{
    [GeneratedRegex(@"^CB\s+([0-9]+(?:\.[0-9]+)?)\s+\([0-9]+(?:\.[0-9]+)?\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CinebenchLine();
    [GeneratedRegex(@"^Tot:\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipLine();
    [GeneratedRegex(@"\b(error|exception|aborted|failed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    public static ParsedBenchmark CinebenchR23(string stdout, int exitCode, bool multiCoreOnly)
    {
        if (!multiCoreOnly) return new(false, null, "R23_points", "Only a single MC invocation is supported; mixed scores are ambiguous.");
        return Parse(stdout, exitCode, CinebenchLine(), 1, "R23_points");
    }

    public static ParsedBenchmark SevenZip(string stdout, int exitCode) => Parse(stdout, exitCode, SevenZipLine(), 3, "MIPS");

    private static ParsedBenchmark Parse(string stdout, int exitCode, Regex expression, int scoreGroup, string unit)
    {
        if (exitCode != 0) return new(false, null, unit, $"Nonzero exit code {exitCode}; output is not a successful score.");
        string[] lines = stdout.Replace("\r\n", "\n").Split('\n');
        if (lines.Any(line => ErrorLine().IsMatch(line))) return new(false, null, unit, "Error marker in output.");
        var matches = lines.Select(line => expression.Match(line)).Where(m => m.Success).ToArray();
        if (matches.Length != 1) return new(false, null, unit, $"Expected exactly one result, found {matches.Length}; format may be unsupported.");
        if (!double.TryParse(matches[0].Groups[scoreGroup].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double score) || !double.IsFinite(score) || score <= 0)
            return new(false, null, unit, "Score is not positive and finite.");
        return new(true, score, unit, null);
    }
}
