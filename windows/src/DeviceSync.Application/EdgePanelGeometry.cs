namespace DeviceSync.Application;

public enum EdgePanelSide
{
    Left,
    Right,
}

public enum EdgePanelState
{
    Hidden,
    Expanded,
}

public sealed record ScreenWorkAreaPixels(
    int Left,
    int Top,
    int Width,
    int Height,
    double DpiScale);

public sealed record EdgePanelBoundsPixels(int Left, int Top, int Width, int Height);

public static class EdgePanelGeometry
{
    public const double DefaultExpandedWidthDip = 448;
    public const double DefaultExpandedHeightDip = 620;
    public const double DefaultVerticalMarginDip = 12;

    public static EdgePanelBoundsPixels Calculate(
        ScreenWorkAreaPixels workArea,
        EdgePanelSide side,
        EdgePanelState state,
        double expandedWidthDip = DefaultExpandedWidthDip,
        double expandedHeightDip = DefaultExpandedHeightDip,
        double verticalMarginDip = DefaultVerticalMarginDip)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || workArea.DpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        if (expandedWidthDip <= 0 || expandedHeightDip <= 0 || verticalMarginDip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expandedWidthDip));
        }

        var verticalMargin = Math.Max(
            0,
            (int)Math.Round(verticalMarginDip * workArea.DpiScale, MidpointRounding.AwayFromZero));
        var width = Math.Clamp(
            (int)Math.Round(expandedWidthDip * workArea.DpiScale, MidpointRounding.AwayFromZero),
            1,
            workArea.Width);
        var availableHeight = Math.Max(1, workArea.Height - Math.Min(workArea.Height - 1, verticalMargin * 2));
        var height = Math.Clamp(
            (int)Math.Round(expandedHeightDip * workArea.DpiScale, MidpointRounding.AwayFromZero),
            1,
            availableHeight);
        var top = checked(workArea.Top + ((workArea.Height - height) / 2));
        var left = state == EdgePanelState.Hidden
            ? side == EdgePanelSide.Left
                ? checked(workArea.Left - width)
                : checked(workArea.Left + workArea.Width)
            : side == EdgePanelSide.Left
                ? workArea.Left
                : checked(workArea.Left + workArea.Width - width);
        return new EdgePanelBoundsPixels(left, top, width, height);
    }

    public static bool IsInActivationZone(
        ScreenWorkAreaPixels workArea,
        EdgePanelSide side,
        int cursorX,
        int cursorY,
        int widthPixels = 3)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));

        var width = Math.Clamp(widthPixels, 2, 4);
        if (cursorY < workArea.Top || cursorY >= workArea.Top + workArea.Height) return false;
        return side == EdgePanelSide.Left
            ? cursorX >= workArea.Left && cursorX < workArea.Left + width
            : cursorX < workArea.Left + workArea.Width &&
              cursorX >= workArea.Left + workArea.Width - width;
    }

    public static EdgePanelBoundsPixels Calculate(
        ScreenWorkAreaPixels workArea,
        EdgePanelSide side,
        bool expanded) =>
        Calculate(workArea, side, expanded ? EdgePanelState.Expanded : EdgePanelState.Hidden);
}
