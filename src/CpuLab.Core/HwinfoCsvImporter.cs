using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CpuLab.Core;

// An explicit dialect profile, not a claim to support every HWiNFO CSV version.
public sealed record HwinfoCsvProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string EvidenceKind { get; init; }
    public required string Encoding { get; init; }
    public required string Delimiter { get; init; }
    public required string DecimalSeparator { get; init; }
    public required string DateFormat { get; init; }
    public required string TimeFormat { get; init; }
    public required int UtcOffsetMinutes { get; init; }
    public required string DateHeader { get; init; }
    public required string TimeHeader { get; init; }
    public string SourceVersion { get; init; } = "unknown";
    public bool AllowIncompleteFinalRecord { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || EvidenceKind is not ("synthetic-fixture" or "external-log")
            || Encoding is not ("utf-8" or "windows-1251" or "windows-1252")
            || Delimiter is not ("," or ";" or "\t") || DecimalSeparator is not ("." or ",")
            || UtcOffsetMinutes is < -840 or > 840
            || string.IsNullOrWhiteSpace(DateHeader) || string.IsNullOrWhiteSpace(TimeHeader)
            || DateHeader == TimeHeader || string.IsNullOrWhiteSpace(SourceVersion))
            throw new InvalidDataException("Invalid HWiNFO CSV profile: specify dialect, evidence kind and UTC offset explicitly.");
        // Deliberately bounded formats: no culture-dependent or timezone-bearing formats.
        if (DateFormat is not ("d.M.yyyy" or "dd.MM.yyyy" or "M/d/yyyy" or "MM/dd/yyyy" or "yyyy-MM-dd")
            || TimeFormat is not ("H:mm:ss" or "HH:mm:ss" or "H:mm:ss.fff" or "HH:mm:ss.fff"))
            throw new InvalidDataException("Unsupported date/time format. See docs/hwinfo-csv.md.");
    }
}

public sealed record CsvChannel(string Id, int ColumnIndex, string Header, string? Unit,
    string Metric = "unknown", string Scope = "unknown");
public sealed record CsvValue(string ChannelId, string RawText, double? Value, string Status);
public sealed record CsvObservation(int SourceLine, string RawDate, string RawTime,
    DateTimeOffset TimestampUtc, CsvValue[] Values);
public sealed record CsvImportData(HwinfoCsvProfile Profile, CsvChannel[] Channels,
    List<CsvObservation> Observations, List<string> Warnings);
public sealed record CsvImportResult(string Directory, int Records, int Channels, string Status);

public static class HwinfoCsvImporter
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxCells = 1_000_000;

    public static CsvImportData Parse(byte[] bytes, HwinfoCsvProfile profile)
    {
        profile.Validate();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("CSV exceeds the 32 MiB prototype limit.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(profile.Encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text;
        try { text = encoding.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("CSV encoding does not match profile; original bytes were not changed.", ex); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (text.Contains('\0')) throw new InvalidDataException("NUL in CSV; check file format and encoding.");
        bool terminated = text.EndsWith('\n') || text.EndsWith('\r');
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int count = lines.Length - (terminated ? 1 : 0);
        if (count < 2) throw new InvalidDataException("CSV requires a header and at least one data row.");
        string[] header = ParseRecord(lines[0], profile.Delimiter[0]);
        if (header.Length < 3 || header.Length > 4096
            || header[0] != profile.DateHeader || header[1] != profile.TimeHeader
            || header.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Expected explicit date/time columns followed by named sensors in a single header.");
        var channels = header.Skip(2).Select((h, i) =>
        {
            int start = h.LastIndexOf('[');
            string? unit = start >= 0 && h.EndsWith(']') ? h[(start + 1)..^1] : null;
            return new CsvChannel($"csv-column-{i + 2}", i + 2, h, unit);
        }).ToArray();
        var result = new CsvImportData(profile, channels, [], [
            "Provisional single-header CSV import; not qualified against a captured Windows HWiNFO log.",
            "UTC offset is user supplied; timestamps are wall-clock observations, not monotonic time or measurement intervals.",
            "Column IDs are local to this file. Hardware sensor identity, core type, benchmark windows and sampling intervals are unknown."
        ]);
        var numberFormat = new NumberFormatInfo { NumberDecimalSeparator = profile.DecimalSeparator };
        const NumberStyles numberStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        DateTimeOffset? previous = null;
        for (int i = 1; i < count; i++)
        {
            string[] fields;
            try { fields = ParseRecord(lines[i], profile.Delimiter[0]); }
            catch (IncompleteCsvRecordException) when (i == count - 1 && !terminated && profile.AllowIncompleteFinalRecord)
            {
                result.Warnings.Add($"Dropped incomplete final record at line {i + 1}; original bytes retained.");
                break;
            }
            if (fields.SequenceEqual(header))
            {
                result.Warnings.Add($"Repeated identical header at line {i + 1} was not treated as data.");
                continue;
            }
            if (fields.Length != header.Length)
            {
                if (i == count - 1 && !terminated && fields.Length < header.Length && profile.AllowIncompleteFinalRecord)
                {
                    result.Warnings.Add($"Dropped incomplete final record at line {i + 1}; original bytes retained.");
                    break;
                }
                throw new InvalidDataException($"CSV line {i + 1}: column count changed. Dynamic headers/footer formats are unsupported.");
            }
            if (!DateOnly.TryParseExact(fields[0], profile.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !TimeOnly.TryParseExact(fields[1], profile.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new InvalidDataException($"CSV line {i + 1}: timestamp does not match profile; footer and changed headers are unsupported.");
            var timestamp = new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromMinutes(profile.UtcOffsetMinutes)).ToUniversalTime();
            if (previous.HasValue && timestamp <= previous.Value)
                throw new InvalidDataException($"CSV line {i + 1}: duplicate or backwards timestamp; no reordering performed.");
            previous = timestamp;
            if ((long)(result.Observations.Count + 1) * channels.Length > MaxCells)
                throw new InvalidDataException("CSV exceeds the 1,000,000 observation cells prototype limit.");
            var values = channels.Select(c =>
            {
                string raw = fields[c.ColumnIndex];
                string trimmed = raw.Trim();
                bool valid = double.TryParse(trimmed, numberStyles, numberFormat, out double value) && double.IsFinite(value);
                return new CsvValue(c.Id, raw, valid ? value : null,
                    valid ? "numeric" : trimmed.Length == 0 || trimmed is "N/A" or "-" ? "unavailable" : "unparsed");
            }).ToArray();
            result.Observations.Add(new CsvObservation(i + 1, fields[0], fields[1], timestamp, values));
        }
        if (result.Observations.Count == 0) throw new InvalidDataException("No complete observations in CSV.");
        if (result.Observations.Any(o => o.Values.Any(v => v.Status != "numeric")))
            result.Warnings.Add("Some cells are unavailable or unparsed; their raw text is retained and numeric values are null.");
        return result;
    }

    public static CsvImportResult Import(string sourcePath, HwinfoCsvProfile profile, string outputRoot)
    {
        // Read one immutable snapshot and use the very same bytes for parsing, hash and archive.
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaxBytes) throw new InvalidDataException("CSV exceeds the 32 MiB prototype limit.");
        using var snapshot = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            if (snapshot.Length + read > MaxBytes) throw new InvalidDataException("CSV exceeds the 32 MiB prototype limit.");
            snapshot.Write(buffer, 0, read);
        }
        byte[] bytes = snapshot.ToArray();
        var data = Parse(bytes, profile);
        string id = Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, $".csv-import-{id}.tmp");
        string final = Path.Combine(root, $"{DateTimeOffset.UtcNow:yyyy-MM-dd_HH-mm-ss}_hwinfo-csv_{id}");
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "source.csv"), bytes);
            WriteJson(staging, "profile.json", profile);
            WriteJson(staging, "channels.json", data.Channels);
            using (var writer = new StreamWriter(Path.Combine(staging, "observations.jsonl"), false, new UTF8Encoding(false)))
                foreach (var observation in data.Observations)
                    writer.WriteLine(JsonSerializer.Serialize(observation, DataJson.Compact));
            string status = data.Warnings.Any(w => w.StartsWith("Dropped", StringComparison.Ordinal)) ? "partial" : "imported";
            WriteJson(staging, "import.json", new
            {
                SchemaVersion = 1, ArtifactType = "hwinfo-csv-import", Status = status,
                EvidenceKind = profile.EvidenceKind, ImportedUtc = DateTimeOffset.UtcNow,
                SourceName = Path.GetFileName(sourcePath), SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                SourceBytes = bytes.Length, Records = data.Observations.Count, Channels = data.Channels.Length,
                TimestampOrigin = "source-wall-clock-with-user-offset", MeasurementInterval = "unknown",
                HardwareIdentity = "unknown", BenchmarkWindows = "unknown", Warnings = data.Warnings
            });
            File.WriteAllText(Path.Combine(staging, "report.html"), HwinfoCsvReport.Render(data), new UTF8Encoding(false));
            Directory.Move(staging, final);
            return new(final, data.Observations.Count, data.Channels.Length, status);
        }
        catch
        {
            // Retain unfinished output for inspection; it has no published final directory.
            throw;
        }
    }

    private static void WriteJson<T>(string directory, string name, T value) =>
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, DataJson.Options));

    private static string[] ParseRecord(string line, char delimiter)
    {
        var fields = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, closed = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"') { cell.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c == delimiter)
            {
                fields.Add(cell.ToString()); cell.Clear(); closed = false;
            }
            else if (c == '"' && cell.Length == 0 && !closed) quoted = true;
            else if (c == '"' || closed) throw new InvalidDataException("Malformed CSV quoting; multiline fields are unsupported.");
            else cell.Append(c);
        }
        if (quoted) throw new IncompleteCsvRecordException("Unclosed CSV quote; multiline fields are unsupported.");
        fields.Add(cell.ToString());
        return fields.ToArray();
    }

    private sealed class IncompleteCsvRecordException(string message) : IOException(message);
}
