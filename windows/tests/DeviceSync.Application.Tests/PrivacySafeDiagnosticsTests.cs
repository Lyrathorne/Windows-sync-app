using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class PrivacySafeDiagnosticsTests
{
    [Fact]
    public void RedactorDropsSensitiveKeysAndRedactsValues()
    {
        var token = new string('A', 48);
        var result = DiagnosticRedactor.Sanitize(new Dictionary<string, string>
        {
            ["clipboardText"] = "private",
            ["duration"] = "42",
            ["detail"] = $@"host=192.168.1.25 path=C:\Users\Gleb\secret.txt user=a@example.com token={token}",
        });

        Assert.DoesNotContain("clipboardText", result.Keys);
        Assert.Equal("42", result["duration"]);
        Assert.Contains("[IP]", result["detail"]);
        Assert.Contains("[PATH]", result["detail"]);
        Assert.Contains("[EMAIL]", result["detail"]);
        Assert.Contains("[TOKEN]", result["detail"]);
        Assert.DoesNotContain("secret.txt", result["detail"]);
    }

    [Fact]
    public void SupportBundleContainsOnlySummaryAndRedactedEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceSyncDiagnosticsTests", Guid.NewGuid().ToString("N"));
        PrivacySafeDiagnostics.Initialize(root);
        PrivacySafeDiagnostics.Record(new DiagnosticRecord(
            DateTimeOffset.UtcNow,
            "file",
            "FILE_TRANSFER_FAILED",
            ErrorCode: "IO_ERROR",
            Attributes: new Dictionary<string, string> { ["filePath"] = @"C:\private.txt", ["byteCount"] = "42" }));

        var bundle = PrivacySafeDiagnostics.BuildSupportBundle(new Dictionary<string, string>
        {
            ["version"] = "1.0",
            ["privatePath"] = @"C:\private.txt",
        });

        Assert.NotEmpty(bundle);
        Assert.DoesNotContain("private.txt", System.Text.Encoding.UTF8.GetString(bundle));
    }
}
