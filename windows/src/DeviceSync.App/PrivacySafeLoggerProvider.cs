using DeviceSync.Application;
using Microsoft.Extensions.Logging;

namespace DeviceSync.App;

public sealed class PrivacySafeLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PrivacySafeLogger(categoryName);
    public void Dispose() { }

    private sealed class PrivacySafeLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var name = eventId.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = $"{category.Split('.').LastOrDefault() ?? "APPLICATION"}_{eventId.Id}";
            name = new string(name.ToUpperInvariant().Select(value => char.IsLetterOrDigit(value) ? value : '_').ToArray());
            if (name.Length < 3 || !char.IsLetter(name[0])) name = "APPLICATION_EVENT";
            PrivacySafeDiagnostics.Record(new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                Domain(category),
                name[..Math.Min(name.Length, 64)],
                logLevel.ToString().ToLowerInvariant(),
                ErrorCode: exception?.GetType().Name,
                Attributes: new Dictionary<string, string> { ["detail"] = formatter(state, exception) }));
        }

        private static string Domain(string value) => value switch
        {
            var item when item.Contains("Sharing", StringComparison.OrdinalIgnoreCase) => "clipboard",
            var item when item.Contains("File", StringComparison.OrdinalIgnoreCase) => "file",
            var item when item.Contains("Catalog", StringComparison.OrdinalIgnoreCase) => "catalog",
            var item when item.Contains("Notification", StringComparison.OrdinalIgnoreCase) => "notification",
            var item when item.Contains("Session", StringComparison.OrdinalIgnoreCase) => "session",
            _ => "transport",
        };
    }
}
