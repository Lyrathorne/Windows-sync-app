using DeviceSync.App;
using DeviceSync.Application;
using System.IO;
using Xunit;

namespace DeviceSync.App.Tests;

public sealed class EdgePanelStateTests
{
    [Fact]
    public void Apply_RestoresSettingsButStartsDisabledPanelHidden()
    {
        var viewModel = new EdgePanelViewModel();

        viewModel.Apply(new EdgePanelSettings
        {
            Enabled = false,
            Expanded = true,
            SelectedSection = 3,
            HoverDelayMilliseconds = 725,
            Side = EdgePanelSide.Left,
            MonitorDeviceName = "DISPLAY-2",
            HideRecentThumbnails = false,
        });

        Assert.False(viewModel.Enabled);
        Assert.Equal(EdgePanelState.Hidden, viewModel.State);
        Assert.True(viewModel.IsHidden);
        Assert.Equal(3, viewModel.SelectedSection);
        Assert.Equal(725, viewModel.HoverDelayMilliseconds);
        Assert.Equal(EdgePanelSide.Left, viewModel.Side);
        Assert.Equal("DISPLAY-2", viewModel.MonitorDeviceName);
        Assert.False(viewModel.HideRecentThumbnails);
        Assert.Equal(viewModel.ToSettings(), new EdgePanelSettings
        {
            Enabled = false,
            Expanded = false,
            SelectedSection = 3,
            HoverDelayMilliseconds = 725,
            Side = EdgePanelSide.Left,
            MonitorDeviceName = "DISPLAY-2",
            HideRecentThumbnails = false,
        });
    }

    [Fact]
    public void Apply_StartsEnabledPanelHiddenEvenWhenLegacySettingsWereExpanded()
    {
        var viewModel = new EdgePanelViewModel();

        viewModel.Apply(new EdgePanelSettings { Enabled = true, Expanded = true });

        Assert.Equal(EdgePanelState.Hidden, viewModel.State);
        Assert.True(viewModel.IsHidden);
        Assert.False(viewModel.IsExpanded);
        Assert.False(viewModel.ToSettings().Expanded);
    }

    [Fact]
    public void Values_AreClampedToSupportedUiRanges()
    {
        var viewModel = new EdgePanelViewModel
        {
            SelectedSection = 99,
            HoverDelayMilliseconds = -50,
        };

        Assert.Equal(4, viewModel.SelectedSection);
        Assert.Equal(0, viewModel.HoverDelayMilliseconds);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsAndRecoversFromCorruptJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"devicesync-edge-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "edge-panel.json");
        try
        {
            var store = new JsonEdgePanelSettingsStore(path);
            var expected = new EdgePanelSettings
            {
                Side = EdgePanelSide.Left,
                Expanded = true,
                SelectedSection = 4,
                HoverDelayMilliseconds = 450,
                MonitorDeviceName = "DISPLAY-2",
                HideRecentThumbnails = false,
            };
            await store.SaveAsync(expected);
            Assert.Equal(expected, await store.LoadAsync());

            await File.WriteAllTextAsync(path, "{corrupt");
            Assert.Equal(new EdgePanelSettings(), await store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
