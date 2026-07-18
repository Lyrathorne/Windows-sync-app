using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeviceSync.Application;

public sealed record DiagnosticRecord(
    DateTimeOffset TimestampUtc,
    string Domain,
    string Name,
    string Level = "information",
    string? CorrelationId = null,
    long? DurationMs = null,
    long? ByteCount = null,
    string? ErrorCode = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public static partial class DiagnosticRedactor
{
    private static readonly string[] ForbiddenKeys =
        ["content", "text", "clipboard", "filename", "filepath", "path", "notification", "title", "message", "secret", "token", "password", "key"];

    public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null) return new Dictionary<string, string>();
        return attributes
            .Where(item => !ForbiddenKeys.Any(value => item.Key.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .Take(24)
            .ToDictionary(
                item => SafeKey(item.Key),
                item => SanitizeValue(item.Value));
    }

    public static string SanitizeValue(string value)
    {
        var result = value.Length > 512 ? value[..512] : value;
        result = WindowsPathRegex().Replace(result, "[PATH]");
        result = Ipv4Regex().Replace(result, "[IP]");
        result = EmailRegex().Replace(result, "[EMAIL]");
        result = LongTokenRegex().Replace(result, "[TOKEN]");
        return result;
    }

    private static string SafeKey(string value)
    {
        var safe = Regex.Replace(value, "[^A-Za-z0-9_]", "");
        if (string.IsNullOrWhiteSpace(safe)) return "attribute";
        return safe[..Math.Min(40, safe.Length)];
    }

    [GeneratedRegex(@"(?i)\b[a-z]:\\[^\s]*")]
    private static partial Regex WindowsPathRegex();
    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4Regex();
    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{32,}(?![A-Za-z0-9_-])")]
    private static partial Regex LongTokenRegex();
}

public static class PrivacySafeDiagnostics
{
    private const long MaxFileBytes = 256 * 1024;
    private const int MaxFiles = 4;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string? _directory;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
    }

    public static void Record(DiagnosticRecord record)
    {
        if (!Regex.IsMatch(record.Name, "^[A-Z][A-Z0-9_]{2,63}$")) return;
        var sanitized = record with
        {
            CorrelationId = record.CorrelationId is null ? null : DiagnosticRedactor.SanitizeValue(record.CorrelationId),
            ErrorCode = record.ErrorCode is null ? null : DiagnosticRedactor.SanitizeValue(record.ErrorCode),
            Attributes = DiagnosticRedactor.Sanitize(record.Attributes),
        };
        var line = JsonSerializer.Serialize(sanitized, JsonOptions);
        lock (Gate) AppendRotating(line);
    }

    public static IReadOnlyList<string> ReadLines(int limit = 1000)
    {
        lock (Gate)
        {
            if (_directory is null) return [];
            return Enumerable.Range(0, MaxFiles)
                .Select(index => Path.Combine(_directory, $"events-{index}.jsonl"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .SelectMany(File.ReadLines)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();
        }
    }

    public static byte[] BuildSupportBundle(IReadOnlyDictionary<string, string> summary)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var summaryEntry = zip.CreateEntry("summary.txt");
            using (var writer = new StreamWriter(summaryEntry.Open()))
            {
                foreach (var item in DiagnosticRedactor.Sanitize(summary))
                    writer.WriteLine($"{item.Key}: {item.Value}");
                writer.WriteLine("Private contents, paths, secrets, notification text and clipboard data are excluded.");
            }
            var events = zip.CreateEntry("events.jsonl");
            using var eventWriter = new StreamWriter(events.Open());
            foreach (var line in ReadLines()) eventWriter.WriteLine(line);
        }
        return output.ToArray();
    }

    private static void AppendRotating(string line)
    {
        if (_directory is null) return;
        var active = Path.Combine(_directory, "events-0.jsonl");
        if (File.Exists(active) && new FileInfo(active).Length + line.Length + Environment.NewLine.Length > MaxFileBytes)
        {
            File.Delete(Path.Combine(_directory, $"events-{MaxFiles - 1}.jsonl"));
            for (var index = MaxFiles - 2; index >= 0; index--)
            {
                var source = Path.Combine(_directory, $"events-{index}.jsonl");
                if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"events-{index + 1}.jsonl"), overwrite: true);
            }
        }
        File.AppendAllText(active, line + Environment.NewLine);
    }
}
