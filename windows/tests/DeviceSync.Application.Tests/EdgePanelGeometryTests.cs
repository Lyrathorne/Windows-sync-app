using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class EdgePanelGeometryTests
{
    [Fact]
    public void ExpandedPanel_AnchorsToRightWorkAreaEdge()
    {
        var result = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(0, 0, 1920, 1040, 1),
            EdgePanelSide.Right,
            EdgePanelState.Expanded);

        Assert.Equal(new EdgePanelBoundsPixels(1472, 210, 448, 620), result);
    }

    [Fact]
    public void HiddenPanel_IsFullyOutsideLeftEdgeAt150PercentDpi()
    {
        var result = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(-2560, 40, 2560, 1400, 1.5),
            EdgePanelSide.Left,
            EdgePanelState.Hidden);

        Assert.Equal(new EdgePanelBoundsPixels(-3232, 275, 672, 930), result);
    }

    [Fact]
    public void ExpandedPanel_StaysOnNegativeCoordinateMonitorAt125PercentDpi()
    {
        var result = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(-2560, 0, 2560, 1400, 1.25),
            EdgePanelSide.Right,
            EdgePanelState.Expanded);

        Assert.Equal(new EdgePanelBoundsPixels(-560, 312, 560, 775), result);
    }

    [Fact]
    public void Width_IsClampedToSmallWorkArea()
    {
        var result = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(100, 200, 300, 500, 2),
            EdgePanelSide.Right,
            EdgePanelState.Expanded);

        Assert.Equal(new EdgePanelBoundsPixels(100, 224, 300, 452), result);
    }

    [Fact]
    public void ExpandedAndHiddenBounds_AreCorrectAt250PercentDpi()
    {
        var workArea = new ScreenWorkAreaPixels(0, 0, 3840, 2160, 2.5);

        var expanded = EdgePanelGeometry.Calculate(workArea, EdgePanelSide.Right, EdgePanelState.Expanded);
        var hidden = EdgePanelGeometry.Calculate(workArea, EdgePanelSide.Right, EdgePanelState.Hidden);

        Assert.Equal(new EdgePanelBoundsPixels(2720, 305, 1120, 1550), expanded);
        Assert.Equal(new EdgePanelBoundsPixels(3840, 305, 1120, 1550), hidden);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidDpi_IsRejected(double dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(0, 0, 1920, 1080, dpi),
            EdgePanelSide.Left,
            EdgePanelState.Expanded));
    }

    [Theory]
    [InlineData(EdgePanelSide.Left, -448)]
    [InlineData(EdgePanelSide.Right, 1920)]
    public void HiddenPanel_IsPlacedCompletelyOutsideTheSelectedEdge(EdgePanelSide side, int expectedLeft)
    {
        var result = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(0, 0, 1920, 1040, 1),
            side,
            EdgePanelState.Hidden);

        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(448, result.Width);
        Assert.Equal(620, result.Height);
    }

    [Theory]
    [InlineData(EdgePanelSide.Left, -1920, true)]
    [InlineData(EdgePanelSide.Left, -1917, false)]
    [InlineData(EdgePanelSide.Right, -1, true)]
    [InlineData(EdgePanelSide.Right, -4, false)]
    public void ActivationZone_UsesThreePhysicalPixelsOnEitherWorkAreaEdge(
        EdgePanelSide side,
        int cursorX,
        bool expected)
    {
        var workArea = new ScreenWorkAreaPixels(-1920, 40, 1920, 1000, 1.5);

        Assert.Equal(expected, EdgePanelGeometry.IsInActivationZone(
            workArea,
            side,
            cursorX,
            cursorY: 500));
    }

    [Fact]
    public void ActivationZone_ExcludesTaskbarAndOutsideVerticalWorkArea()
    {
        var workArea = new ScreenWorkAreaPixels(0, 0, 1920, 1040, 1);

        Assert.False(EdgePanelGeometry.IsInActivationZone(
            workArea,
            EdgePanelSide.Right,
            1919,
            cursorY: 1079));
    }
}
