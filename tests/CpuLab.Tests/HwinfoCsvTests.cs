using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CpuLab.Core;
using Xunit;

namespace CpuLab.Tests;

public sealed class HwinfoCsvTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cpulab-csv-tests-" + Guid.NewGuid().ToString("N"));
    private static HwinfoCsvProfile Profile => new()
    {
        EvidenceKind = "synthetic-fixture", Encoding = "utf-8", Delimiter = ";",
        DecimalSeparator = ",", DateFormat = "dd.MM.yyyy", TimeFormat = "HH:mm:ss.fff",
        UtcOffsetMinutes = 180, DateHeader = "Date", TimeHeader = "Time"
    };
    private const string Header = "Date;Time;Power [W]";
    private const string Row = "20.10.2026;12:00:00.000;100,5";
    private static CsvImportData Parse(string text, HwinfoCsvProfile? profile = null) =>
        HwinfoCsvImporter.Parse(Encoding.UTF8.GetBytes(text), profile ?? Profile);

    [Fact]
    public void ExamplePreservesUnknownsDuplicateNamesAndUnequalTimeSteps()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "hwinfo");
        var profile = DataJson.Read<HwinfoCsvProfile>(Path.Combine(directory, "profile.json"));
        var data = HwinfoCsvImporter.Parse(File.ReadAllBytes(Path.Combine(directory, "synthetic.csv")), profile);
        Assert.Equal(3, data.Observations.Count);
        Assert.Equal(6, data.Channels.Length);
        Assert.Equal("W", data.Channels[0].Unit);
        Assert.Equal(data.Channels[3].Header, data.Channels[4].Header);
        Assert.NotEqual(data.Channels[3].Id, data.Channels[4].Id);
        Assert.All(data.Channels, c => Assert.Equal("unknown", c.Metric));
        Assert.Equal(100.5, data.Observations[0].Values[0].Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), data.Observations[0].TimestampUtc);
        Assert.Equal(TimeSpan.FromSeconds(2), data.Observations[2].TimestampUtc - data.Observations[1].TimestampUtc);
        Assert.Null(data.Observations[1].Values[4].Value);
        Assert.Equal("unavailable", data.Observations[1].Values[4].Status);
        Assert.Null(data.Observations[2].Values[2].Value);
        Assert.Equal("Yes", data.Observations[2].Values[5].RawText);
        Assert.Equal("unparsed", data.Observations[2].Values[5].Status);
    }

    [Fact]
    public void QuotedDelimitersEscapedQuotesBomAndCrLfAreParsed()
    {
        var data = Parse("\uFEFFDate;Time;\"CPU; \"\"Package\"\" [W]\"\r\n20.10.2026;12:00:00.000;\"100,5\"\r\n");
        Assert.Equal("CPU; \"Package\" [W]", data.Channels[0].Header);
        Assert.Equal(100.5, data.Observations[0].Values[0].Value);
        Assert.Equal("100,5", data.Observations[0].Values[0].RawText);
    }

    [Theory]
    [InlineData("windows-1251", "Мощность [Вт]")]
    [InlineData("windows-1252", "Température [°C]")]
    public void ExplicitAnsiEncodingsPreserveSensorNames(string encodingName, string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(encodingName).GetBytes($"Date;Time;{name}\n{Row}\n");
        var data = HwinfoCsvImporter.Parse(bytes, Profile with { Encoding = encodingName });
        Assert.Equal(name, data.Channels[0].Header);
    }

    [Fact]
    public void InvalidUtf8IsRejectedInsteadOfReplacingBytes()
    {
        Assert.Throws<InvalidDataException>(() => HwinfoCsvImporter.Parse([0xFF, 0xFE, 0xAA], Profile));
    }

    [Fact]
    public void DotDecimalsAndCommaDelimiterAreIndependent()
    {
        var data = Parse("Date,Time,Power [W]\n2026-10-20,12:00:00,100.5",
            Profile with { DateFormat = "yyyy-MM-dd", TimeFormat = "HH:mm:ss", DecimalSeparator = ".", Delimiter = "," });
        Assert.Equal(100.5, data.Observations[0].Values[0].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("1.234,5")]
    [InlineData("100.5")]
    [InlineData("1 234,5")]
    public void InvalidAndMissingNumericCellsStayNullWithRawText(string value)
    {
        var data = Parse($"{Header}\n20.10.2026;12:00:00.000;{value}\n");
        var cell = data.Observations[0].Values[0];
        Assert.Null(cell.Value);
        Assert.Equal(value, cell.RawText);
        Assert.NotEqual("numeric", cell.Status);
    }

    [Theory]
    [InlineData("20.10.2026;12:00:01.000;\"100")]
    [InlineData("20.10.2026;12:00:01.000")]
    public void IncompleteFinalRecordRequiresExplicitOptIn(string tail)
    {
        string text = $"{Header}\n{Row}\n{tail}";
        Assert.ThrowsAny<Exception>(() => Parse(text));
        var data = Parse(text, Profile with { AllowIncompleteFinalRecord = true });
        Assert.Single(data.Observations);
        Assert.Contains(data.Warnings, w => w.Contains("Dropped incomplete final record at line 3"));
        Assert.ThrowsAny<Exception>(() => Parse(text + "\n", Profile with { AllowIncompleteFinalRecord = true }));
    }

    [Theory]
    [InlineData("20.10.2026;12:00:01.000;\"100\"oops")]
    [InlineData("20.10.2026;12:00:01.000;100;extra")]
    [InlineData("not-a-date;12:00:01.000;100")]
    [InlineData("Date;Time;Other Power [W]")]
    [InlineData("20.10.2026;12:00:00.000;100")]
    [InlineData("20.10.2026;11:59:59.000;100")]
    public void AmbiguousOrCorruptRowsAreNotHiddenByTailRecovery(string tail)
    {
        Assert.Throws<InvalidDataException>(() => Parse($"{Header}\n{Row}\n{tail}",
            Profile with { AllowIncompleteFinalRecord = true }));
    }

    [Fact]
    public void MiddleDamageCannotBeDropped()
    {
        Assert.Throws<InvalidDataException>(() => Parse($"{Header}\n{Row}\nshort\n20.10.2026;12:00:01.000;10",
            Profile with { AllowIncompleteFinalRecord = true }));
    }

    [Fact]
    public void IdenticalFooterHeaderIsRecordedWithoutCreatingObservations()
    {
        var data = Parse($"{Header}\n{Row}\n{Header}\n");
        Assert.Single(data.Observations);
        Assert.Contains(data.Warnings, w => w.Contains("Repeated identical header"));
    }

    [Fact]
    public void ProfileRequiresExplicitTimezoneAndRejectsUnsupportedDialects()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HwinfoCsvProfile>("{}", DataJson.Options));
        Assert.Throws<InvalidDataException>(() => Parse($"{Header}\n{Row}", Profile with { UtcOffsetMinutes = 841 }));
        Assert.Throws<InvalidDataException>(() => Parse($"{Header}\n{Row}", Profile with { DateFormat = "g" }));
        Assert.Throws<InvalidDataException>(() => Parse($"{Header}\n{Row}", Profile with { EvidenceKind = "measured" }));
    }

    [Fact]
    public void ImportArchivesExactBytesHashAndSeparateArtifactWithoutBenchmarkClaims()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "input.csv");
        byte[] bytes = Encoding.UTF8.GetBytes($"\uFEFF{Header}\r\n{Row}\r\n");
        File.WriteAllBytes(source, bytes);
        var result = HwinfoCsvImporter.Import(source, Profile, Path.Combine(root, "output"));
        Assert.Equal("imported", result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(result.Directory, "source.csv")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "import.json")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), manifest.RootElement.GetProperty("sourceSha256").GetString());
        Assert.Equal("unknown", manifest.RootElement.GetProperty("measurementInterval").GetString());
        Assert.Equal("unknown", manifest.RootElement.GetProperty("benchmarkWindows").GetString());
        Assert.False(File.Exists(Path.Combine(result.Directory, "attempts.json")));
        Assert.Single(File.ReadAllLines(Path.Combine(result.Directory, "observations.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "report.html")));
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(root, "output")), d => d.EndsWith(".tmp"));
    }

    [Fact]
    public void FailedValidationCreatesNoOutput()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "bad.csv");
        File.WriteAllText(source, "bad");
        string output = Path.Combine(root, "output");
        Assert.Throws<InvalidDataException>(() => HwinfoCsvImporter.Import(source, Profile, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ReportEscapesHeadersAndClearlyMarksSyntheticData()
    {
        var data = Parse($"Date;Time;<script>alert(1)</script> [W]\n{Row}");
        string html = HwinfoCsvReport.Render(data);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("СИНТЕТИЧЕСКИЙ ПРИМЕР", html);
        Assert.DoesNotContain("src=\"http", html);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
