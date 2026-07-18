using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeviceSync.Application;
using DrawingPoint = System.Drawing.Point;
using FormsScreen = System.Windows.Forms.Screen;

namespace DeviceSync.App;

public interface IEdgePanelPlacementService
{
    Task PlaceAsync(Window window, EdgePanelSettings settings, EdgePanelState state, bool animate, bool activate);
    void CancelAnimation();
    bool IsCursorInActivationZone(EdgePanelSettings settings, int widthPixels = 3);
    bool IsForegroundApplicationFullScreen(Window edgePanel);
    string GetCurrentMonitorDeviceName(Window window);
}

public sealed class EdgePanelPlacementService : IEdgePanelPlacementService
{
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpShowWindow = 0x0040;
    private const int AnimationMilliseconds = 310;
    private CancellationTokenSource? _animationCancellation;

    public async Task PlaceAsync(Window window, EdgePanelSettings settings, EdgePanelState state, bool animate, bool activate)
    {
        CancelAnimation();
        var cancellation = new CancellationTokenSource();
        _animationCancellation = cancellation;
        var screen = ResolveScreen(settings.MonitorDeviceName, window);
        var dpiScale = GetMonitorDpiScale(screen);
        var target = EdgePanelGeometry.Calculate(
            new ScreenWorkAreaPixels(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                dpiScale),
            settings.Side,
            state);

        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!animate || !SystemParameters.ClientAreaAnimation || !GetWindowRect(handle, out var current))
        {
            Apply(handle, target, activate, raiseWithoutActivation: state != EdgePanelState.Hidden);
        }
        else
        {
            await AnimateAsync(
                handle,
                current,
                target,
                state,
                activate,
                raiseWithoutActivation: state != EdgePanelState.Hidden,
                cancellation.Token).ConfigureAwait(true);
        }

        if (activate)
        {
            window.Activate();
        }
    }

    public void CancelAnimation()
    {
        _animationCancellation?.Cancel();
        _animationCancellation?.Dispose();
        _animationCancellation = null;
    }

    public bool IsCursorInActivationZone(EdgePanelSettings settings, int widthPixels = 3)
    {
        var screen = ResolveScreen(settings.MonitorDeviceName, window: null);
        var cursor = System.Windows.Forms.Cursor.Position;
        var workArea = screen.WorkingArea;
        return EdgePanelGeometry.IsInActivationZone(
            new ScreenWorkAreaPixels(workArea.Left, workArea.Top, workArea.Width, workArea.Height, 1),
            settings.Side,
            cursor.X,
            cursor.Y,
            widthPixels);
    }

    public bool IsForegroundApplicationFullScreen(Window edgePanel)
    {
        var foreground = GetForegroundWindow();
        var ownHandle = new WindowInteropHelper(edgePanel).Handle;
        if (foreground == IntPtr.Zero || foreground == ownHandle || !GetWindowRect(foreground, out var rect)) return false;

        var screen = FormsScreen.FromHandle(foreground);
        var bounds = screen.Bounds;
        const int tolerance = 2;
        return Math.Abs(rect.Left - bounds.Left) <= tolerance &&
               Math.Abs(rect.Top - bounds.Top) <= tolerance &&
               Math.Abs(rect.Right - bounds.Right) <= tolerance &&
               Math.Abs(rect.Bottom - bounds.Bottom) <= tolerance;
    }

    public string GetCurrentMonitorDeviceName(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero
            ? FormsScreen.PrimaryScreen?.DeviceName ?? string.Empty
            : FormsScreen.FromHandle(handle).DeviceName;
    }

    private static FormsScreen ResolveScreen(string? deviceName, Window? window)
    {
        var saved = FormsScreen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (saved is not null) return saved;

        var handle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) return FormsScreen.FromHandle(handle);
        return FormsScreen.FromPoint(new DrawingPoint(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y));
    }

    private static double GetMonitorDpiScale(FormsScreen screen)
    {
        var point = new NativePoint { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 };
        var monitor = MonitorFromPoint(point, 2);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96d;
        }
        return 1d;
    }

    private static Task AnimateAsync(
        IntPtr handle,
        NativeRect from,
        EdgePanelBoundsPixels target,
        EdgePanelState targetState,
        bool activate,
        bool raiseWithoutActivation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        EventHandler? render = null;
        render = (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CompositionTarget.Rendering -= render;
                completion.TrySetResult();
                return;
            }
            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / AnimationMilliseconds, 0, 1);
            var eased = CubicBezierEase(progress);
            var bounds = new EdgePanelBoundsPixels(
                Lerp(from.Left, target.Left, eased),
                Lerp(from.Top, target.Top, eased),
                Lerp(from.Right - from.Left, target.Width, eased),
                Lerp(from.Bottom - from.Top, target.Height, eased));
            Apply(handle, bounds, activate: false, raiseWithoutActivation);
            if (progress < 1) return;
            CompositionTarget.Rendering -= render;
            Apply(handle, target, activate, raiseWithoutActivation);
            completion.SetResult();
        };
        CompositionTarget.Rendering += render;
        return completion.Task;
    }

    private static int Lerp(int start, int end, double progress) =>
        (int)Math.Round(start + ((end - start) * progress), MidpointRounding.AwayFromZero);

    // CSS cubic-bezier(0.22, 1, 0.36, 1), solved for x so the native-window
    // transition matches the concept instead of ending with a visible snap.
    internal static double CubicBezierEase(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        const double x1 = 0.22;
        const double y1 = 1.0;
        const double x2 = 0.36;
        const double y2 = 1.0;
        var t = progress;
        for (var i = 0; i < 7; i++)
        {
            var x = Cubic(t, x1, x2) - progress;
            var slope = CubicDerivative(t, x1, x2);
            if (Math.Abs(slope) < 0.000001) break;
            t = Math.Clamp(t - (x / slope), 0, 1);
        }
        return Cubic(t, y1, y2);
    }

    private static double Cubic(double t, double p1, double p2)
    {
        var inverse = 1 - t;
        return (3 * inverse * inverse * t * p1) + (3 * inverse * t * t * p2) + (t * t * t);
    }

    private static double CubicDerivative(double t, double p1, double p2)
    {
        var inverse = 1 - t;
        return (3 * inverse * inverse * p1) + (6 * inverse * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }

    private static void Apply(IntPtr handle, EdgePanelBoundsPixels bounds, bool activate, bool raiseWithoutActivation)
    {
        var flags = SwpShowWindow;
        if (!raiseWithoutActivation && !activate) flags |= SwpNoZOrder;
        if (!activate) flags |= SwpNoActivate;
        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
