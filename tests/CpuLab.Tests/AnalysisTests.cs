using CpuLab.Core;
using Xunit;

namespace CpuLab.Tests;

public sealed class AnalysisTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SensorDescriptor Power = new("power", "package_power", "W", "package");
    private static Attempt Attempt => new() { Id = "a", Ordinal = 1, StartUtc = Start, EndUtc = Start.AddSeconds(4), StartTicks = 0, Status = "completed" };
    private static TelemetryFrame Frame(int id, double from, double to, double? value, string validity = "valid") =>
        new(id, "running", "a", Start.AddSeconds(to), [new("power", Start.AddSeconds(from), Start.AddSeconds(to), (long)(to * TimeSpan.TicksPerSecond), value, validity)]);

    [Fact]
    public void IrregularIntervalsUseTimeWeightsAndConserveEnergy()
    {
        var result = Analyzer.Summarize(Power, Attempt, [Frame(1, 0, 1, 100), Frame(2, 1, 4, 200)]);
        Assert.Equal(175, result.Mean);
        Assert.Equal(700, result.EnergyJ);
        Assert.Equal(200, result.Median);
        Assert.Equal(1, result.Coverage);
    }

    [Fact]
    public void NoInterpolationAcrossMissingOrStaleIntervals()
    {
        var result = Analyzer.Summarize(Power, Attempt,
            [Frame(1, 0, 1, 100), Frame(2, 1, 3, 900, "stale"), Frame(3, 3, 4, null, "unavailable")]);
        Assert.Equal(100, result.Mean);
        Assert.Equal(100, result.EnergyJ);
        Assert.Equal(0.25, result.Coverage);
        Assert.Equal(1, result.ValidSamples);
    }

    [Fact]
    public void BoundaryAndDuplicateIntervalsAreExcluded()
    {
        var result = Analyzer.Summarize(Power, Attempt,
            [Frame(1, -1, 1, 999), Frame(2, 0, 1, 100), Frame(3, 0, 1, 888), Frame(4, 3, 5, 777)]);
        Assert.Equal(100, result.Mean);
        Assert.Equal(0.25, result.Coverage);
        Assert.Equal(1, result.ValidSamples);
    }

    [Fact]
    public void UnavailableIsNullAndNeverZero()
    {
        var result = Analyzer.Summarize(Power, Attempt, []);
        Assert.Null(result.Mean);
        Assert.Null(result.Median);
        Assert.Null(result.Max);
        Assert.Null(result.EnergyJ);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("CB 6258.00 (0.00)", 0, true, 6258)]
    [InlineData("CB 6258.00 (0.00)\nCB 6351.49 (0.00)", 0, false, 0)]
    [InlineData("CB 6258.00 (0.00)", 1, false, 0)]
    [InlineData("CB 6258,00 (0,00)", 0, false, 0)]
    [InlineData("Ranking: 6258.00 pts", 0, false, 0)]
    [InlineData("CB 6258.00 (0.00)\nError: render failed", 0, false, 0)]
    public void CinebenchParserRejectsAmbiguityAndUnknownFormats(string text, int exit, bool valid, double score)
    {
        var result = BenchmarkParsers.CinebenchR23(text, exit, true);
        Assert.Equal(valid, result.Valid);
        if (valid) Assert.Equal(score, result.Score); else Assert.Null(result.Score);
        Assert.Contains("provisional", result.Qualification);
    }

    [Theory]
    [InlineData("Tot: 1900 4500 85500", 0, true)]
    [InlineData("Tot: 1900 4500 85500\nTot: 1900 4500 85500", 0, false)]
    [InlineData("Avr: 1900 4500 85500", 0, false)]
    [InlineData("Tot: 1900 4500 85500", 2, false)]
    public void SevenZipParserRequiresOneFinalTotalAndSuccessfulExit(string text, int exit, bool valid)
    {
        var result = BenchmarkParsers.SevenZip(text, exit);
        Assert.Equal(valid, result.Valid);
        if (valid) Assert.Equal(85500, result.Score); else Assert.Null(result.Score);
    }
}
