using System.Xml.Linq;
using System.IO;
using System.Linq;
using Xunit;

namespace DeviceSync.App.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void EnglishAndRussianResourceKeysMatch()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Resources");
        var english = Keys(Path.Combine(directory, "Strings.en.xaml"));
        var russian = Keys(Path.Combine(directory, "Strings.ru.xaml"));

        Assert.NotEmpty(english);
        Assert.Equal(english, russian);
    }

    private static string[] Keys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray()!;
    }
}
