using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DeviceSync.App.Tests;

public sealed class BindingModeTests
{
    [Fact]
    public void ProgressBarValueBindingsAreExplicitlyOneWay()
    {
        var appDirectory = FindAppSourceDirectory();
        var invalidBindings = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.TopDirectoryOnly)
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProgressBar")
                .Select(element => (Path: path, Value: (string?)element.Attribute("Value")))
                .Where(binding => binding.Value?.StartsWith("{Binding", StringComparison.Ordinal) == true)
                .Where(binding => !binding.Value!.Contains("Mode=OneWay", StringComparison.Ordinal))
                .Select(binding => $"{Path.GetFileName(binding.Path)}: {binding.Value}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalidBindings);
    }

    private static string FindAppSourceDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DEVICESYNC_APP_SOURCE");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DeviceSync.App");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DeviceSync.App source directory.");
    }
}
