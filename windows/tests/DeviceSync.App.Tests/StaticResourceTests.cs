using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DeviceSync.App.Tests;

public sealed partial class StaticResourceTests
{
    [Fact]
    public void AppXamlStaticResourcesAreDeclared()
    {
        var appDirectory = FindAppSourceDirectory();
        var declaredKeys = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(ReadDeclaredKeys)
            .ToHashSet(StringComparer.Ordinal);

        var unresolved = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.TopDirectoryOnly)
            .SelectMany(path => ReadStaticResourceKeys(path)
                .Where(key => key.StartsWith("Ds.", StringComparison.Ordinal) ||
                              key.StartsWith("Edge.", StringComparison.Ordinal))
                .Where(key => !declaredKeys.Contains(key))
                .Select(key => $"{Path.GetFileName(path)}: {key}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
    }

    [Fact]
    public void AppXamlDynamicResourcesAreDeclared()
    {
        var appDirectory = FindAppSourceDirectory();
        var declaredKeys = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(ReadDeclaredKeys)
            .ToHashSet(StringComparer.Ordinal);

        var unresolved = Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.TopDirectoryOnly)
            .SelectMany(path => ReadDynamicResourceKeys(path)
                .Where(key => key.StartsWith("Ds.", StringComparison.Ordinal) ||
                              key.StartsWith("Loc.", StringComparison.Ordinal))
                .Where(key => !declaredKeys.Contains(key))
                .Select(key => $"{Path.GetFileName(path)}: {key}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
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

    private static IEnumerable<string> ReadDeclaredKeys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path)
            .Descendants()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!);
    }

    private static IEnumerable<string> ReadStaticResourceKeys(string path) =>
        StaticResourcePattern()
            .Matches(File.ReadAllText(path))
            .Select(match => match.Groups["key"].Value);

    private static IEnumerable<string> ReadDynamicResourceKeys(string path) =>
        DynamicResourcePattern()
            .Matches(File.ReadAllText(path))
            .Select(match => match.Groups["key"].Value);

    [GeneratedRegex(@"\{StaticResource\s+(?<key>[^},\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StaticResourcePattern();

    [GeneratedRegex(@"\{DynamicResource\s+(?<key>[^},\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicResourcePattern();
}
