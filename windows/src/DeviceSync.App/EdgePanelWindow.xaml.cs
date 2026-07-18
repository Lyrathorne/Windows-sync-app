using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeviceSync.Application;

namespace DeviceSync.App;

public partial class EdgePanelWindow : Window
{
    private const int HotKeyId = 0x4453;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VirtualKeyD = 0x44;

    private readonly IEdgePanelPlacementService _placement;
    private readonly IEdgePanelSettingsStore _settingsStore;
    private readonly DispatcherTimer _transitionTimer;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly EdgePanelTransitionScheduler _transitionScheduler = new();
    private int _scheduledTransitionVersion;
    private DateTimeOffset _scheduledTransitionDueUtc;
    private bool _transitionTickInProgress;
    private HwndSource? _windowSource;
    private bool _initialized;
    private bool _allowClose;
    private bool _childInteractionActive;
    private bool _suppressEnabledPropertyHandler;
    private bool _suppressHoverUntilPointerLeaves;

    public EdgePanelWindow(
        MainViewModel mainViewModel,
        EdgePanelViewModel shell,
        RecentFilesViewModel recentFiles,
        FilesViewModel filesViewModel,
        IEdgePanelPlacementService placement,
        IEdgePanelSettingsStore settingsStore)
    {
        Main = mainViewModel;
        Shell = shell;
        Recent = recentFiles;
        Files = filesViewModel;
        _placement = placement;
        _settingsStore = settingsStore;
        InitializeComponent();
        DataContext = this;

        _transitionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _transitionTimer.Tick += TransitionTimer_Tick;
        Shell.PropertyChanged += Shell_PropertyChanged;
        SourceInitialized += EdgePanelWindow_SourceInitialized;
        Closing += EdgePanelWindow_Closing;
        Closed += EdgePanelWindow_Closed;
    }

    public MainViewModel Main { get; }
    public EdgePanelViewModel Shell { get; }
    public RecentFilesViewModel Recent { get; }
    public FilesViewModel Files { get; }
    public event EventHandler? OpenDiagnosticsRequested;
    public event EventHandler? OpenPhoneFilesRequested;

    public async Task InitializeAsync()
    {
        Shell.Apply(await _settingsStore.LoadAsync());
        Shell.SetState(EdgePanelState.Hidden);
        Recent.SetPrivacyMode(Shell.HideRecentThumbnails);
        _initialized = true;
        await _placement.PlaceAsync(this, Shell.ToSettings(), EdgePanelState.Hidden, animate: false, activate: false);
        _transitionTimer.Start();
    }

    public async Task ShowPanelAsync(bool activate)
    {
        if (!Shell.Enabled) return;
        await SetStateAsync(EdgePanelState.Expanded, animate: false, activate);
        Shell.MonitorDeviceName = _placement.GetCurrentMonitorDeviceName(this);
    }

    public async Task OpenAsync(bool activate = true)
    {
        _suppressHoverUntilPointerLeaves = false;
        if (!Shell.Enabled)
        {
            await SetEnabledAsync(true);
        }

        await SetStateAsync(EdgePanelState.Expanded, animate: true, activate);
    }

    public async Task ToggleExpandedAsync(bool activate = true)
    {
        if (Shell.State == EdgePanelState.Expanded)
        {
            _suppressHoverUntilPointerLeaves = true;
            await SetStateAsync(EdgePanelState.Hidden, animate: true, activate: false);
        }
        else
        {
            await OpenAsync(activate);
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        CancelScheduledTransition();
        if (Shell.Enabled != enabled)
        {
            _suppressEnabledPropertyHandler = true;
            try
            {
                Shell.Enabled = enabled;
            }
            finally
            {
                _suppressEnabledPropertyHandler = false;
            }
        }

        await ApplyEnabledStateAsync(enabled);
        await SaveAsync();
    }

    public async Task SetSideAsync(EdgePanelSide side)
    {
        Shell.Side = side;
        if (Shell.Enabled)
        {
            await _placement.PlaceAsync(this, Shell.ToSettings(), Shell.State, animate: true, activate: false);
        }
        await SaveAsync();
    }

    public async Task SetMonitorAsync(string monitorDeviceName)
    {
        Shell.MonitorDeviceName = monitorDeviceName;
        if (Shell.Enabled)
        {
            await _placement.PlaceAsync(this, Shell.ToSettings(), Shell.State, animate: false, activate: false);
        }
        await SaveAsync();
    }

    public void SetChildInteractionActive(bool active)
    {
        _childInteractionActive = active;
        if (active) CancelScheduledTransition();
        else if (Shell.IsExpanded && !IsMouseOver) ScheduleState(EdgePanelState.Hidden, TimeSpan.FromMilliseconds(650));
    }

    public void AllowClose() => _allowClose = true;

    private void ScheduleState(EdgePanelState state, TimeSpan delay)
    {
        if (_transitionScheduler.PendingState == state) return;
        _scheduledTransitionVersion = _transitionScheduler.Schedule(state);
        _scheduledTransitionDueUtc = DateTimeOffset.UtcNow +
            (delay <= TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    private void CancelScheduledTransition()
    {
        _transitionScheduler.Cancel();
    }

    private async void TransitionTimer_Tick(object? sender, EventArgs e)
    {
        if (_transitionTickInProgress || !_initialized) return;
        _transitionTickInProgress = true;
        try
        {
            RefreshPointerIntent();
            if (_transitionScheduler.PendingState is null ||
                DateTimeOffset.UtcNow < _scheduledTransitionDueUtc ||
                !_transitionScheduler.TryTake(_scheduledTransitionVersion, out var state))
            {
                return;
            }

            if (state == EdgePanelState.Hidden &&
                (_childInteractionActive || IsKeyboardFocusWithin || IsCursorInsideWindow()))
            {
                return;
            }
            if (state == EdgePanelState.Expanded &&
                Shell.IsHidden &&
                !_placement.IsCursorInActivationZone(Shell.ToSettings()))
            {
                return;
            }

            await SetStateAsync(state, animate: true, activate: false);
        }
        finally
        {
            _transitionTickInProgress = false;
        }
    }

    private void RefreshPointerIntent()
    {
        if (!Shell.Enabled)
        {
            CancelScheduledTransition();
            return;
        }

        var atEdge = _placement.IsCursorInActivationZone(Shell.ToSettings());
        var inside = IsCursorInsideWindow();
        if (Shell.IsHidden)
        {
            if (_suppressHoverUntilPointerLeaves)
            {
                if (!atEdge) _suppressHoverUntilPointerLeaves = false;
                if (_transitionScheduler.PendingState == EdgePanelState.Expanded)
                    CancelScheduledTransition();
                return;
            }
            if (atEdge)
            {
                ScheduleState(
                    EdgePanelState.Expanded,
                    TimeSpan.FromMilliseconds(Shell.HoverDelayMilliseconds));
            }
            else if (_transitionScheduler.PendingState == EdgePanelState.Expanded)
            {
                CancelScheduledTransition();
            }
            return;
        }

        if (Shell.IsExpanded)
        {
            if (inside || _childInteractionActive || IsKeyboardFocusWithin)
            {
                if (_transitionScheduler.PendingState == EdgePanelState.Hidden)
                    CancelScheduledTransition();
            }
            else
            {
                ScheduleState(EdgePanelState.Hidden, TimeSpan.FromMilliseconds(650));
            }
        }
    }

    private async Task SetStateAsync(EdgePanelState state, bool animate, bool activate)
    {
        if (!_initialized) return;
        _placement.CancelAnimation();
        if (Shell.State == state &&
            ((state == EdgePanelState.Hidden && !IsVisible) ||
             (state == EdgePanelState.Expanded && IsVisible))) return;
        if (state == EdgePanelState.Expanded &&
            !activate &&
            _placement.IsForegroundApplicationFullScreen(this))
        {
            return;
        }

        await _transitionGate.WaitAsync();
        try
        {
            if (Shell.State == state &&
                ((state == EdgePanelState.Hidden && !IsVisible) ||
                 (state == EdgePanelState.Expanded && IsVisible))) return;

            if (state == EdgePanelState.Expanded && !IsVisible)
            {
                await _placement.PlaceAsync(
                    this, Shell.ToSettings(), EdgePanelState.Hidden,
                    animate: false, activate: false);
                Show();
            }

            if (Shell.State != state) Shell.SetState(state);
            await _placement.PlaceAsync(this, Shell.ToSettings(), state, animate, activate);
            if (state == EdgePanelState.Expanded && Shell.SelectedSection == 2)
                await Recent.ActivateAsync();
            else
                await Recent.DeactivateAsync();
            if (state == EdgePanelState.Hidden) Hide();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initialized) return;
        if (e.PropertyName == nameof(EdgePanelViewModel.Enabled))
        {
            if (!_suppressEnabledPropertyHandler)
            {
                await ApplyEnabledStateAsync(Shell.Enabled);
            }
        }
        else if (e.PropertyName == nameof(EdgePanelViewModel.HideRecentThumbnails))
        {
            Recent.SetPrivacyMode(Shell.HideRecentThumbnails);
        }

        if (e.PropertyName is not nameof(EdgePanelViewModel.State) and
            not nameof(EdgePanelViewModel.IsHidden) and
            not nameof(EdgePanelViewModel.IsExpanded))
        {
            await SaveAsync();
        }
    }

    private Task SaveAsync() => _initialized
        ? _settingsStore.SaveAsync(Shell.ToSettings())
        : Task.CompletedTask;

    private async Task ApplyEnabledStateAsync(bool enabled)
    {
        if (enabled)
        {
            await SetStateAsync(EdgePanelState.Hidden, animate: false, activate: false);
            return;
        }

        await Recent.DeactivateAsync();
        await SetStateAsync(EdgePanelState.Hidden, animate: true, activate: false);
        Hide();
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelScheduledTransition();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelScheduledTransition();
        if (Shell.IsExpanded && !_childInteractionActive && !IsKeyboardFocusWithin)
        {
            ScheduleState(EdgePanelState.Hidden, TimeSpan.FromMilliseconds(650));
        }
    }

    private async void Collapse_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledTransition();
        _suppressHoverUntilPointerLeaves = true;
        await SetStateAsync(EdgePanelState.Hidden, animate: true, activate: false);
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var section))
            SelectSection(section);
    }

    private async void Side_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<EdgePanelSide>(tag, out var side))
        {
            await SetSideAsync(side);
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e) =>
        OpenDiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenPhoneFiles_Click(object sender, RoutedEventArgs e) =>
        OpenPhoneFilesRequested?.Invoke(this, EventArgs.Empty);

    private async void RefreshRecentFiles_Click(object sender, RoutedEventArgs e) =>
        await Recent.RefreshAsync();

    private void RecentItemActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RecentActionsMenu_Opened(object sender, RoutedEventArgs e) => SetChildInteractionActive(true);

    private void RecentActionsMenu_Closed(object sender, RoutedEventArgs e) => SetChildInteractionActive(false);

    private async void DownloadRecentItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentMediaItemViewModel item) return;
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "DeviceSync");
            var path = await Files.DownloadItemAsync(item.Item, directory);
            Recent.ReportAction($"{Localize("Loc.Downloaded")}: {Path.GetFileName(path)}");
        }
        catch (Exception error)
        {
            Recent.ReportAction($"{Localize("Loc.ActionFailed")}: {error.Message}");
        }
    }

    private async void CopyRecentPhoto_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentMediaItemViewModel item) return;
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeviceSync",
                "QuickAccess");
            var path = await Files.DownloadItemAsync(item.Item, directory);
            var clipboard = new System.Windows.DataObject();
            clipboard.SetFileDropList(new StringCollection { path });
            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                clipboard.SetImage(image);
            }
            catch
            {
                // Keep the original file on the clipboard when WPF has no decoder for its format.
            }
            System.Windows.Clipboard.SetDataObject(clipboard, true);
            Recent.ReportAction(Localize("Loc.PhotoCopied"));
        }
        catch (Exception error)
        {
            Recent.ReportAction($"{Localize("Loc.ActionFailed")}: {error.Message}");
        }
    }

    private string Localize(string key) => TryFindResource(key) as string ?? key;

    private void QuickSend_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var acceptsFiles = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
        e.Effects = acceptsFiles
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        if (acceptsFiles && QuickSendDropZone is not null)
        {
            QuickSendDropZone.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x42, 0x63, 0x53, 0xD4));
            QuickSendDropOutline.Stroke = FindResource("Edge.Brush.Accent") as System.Windows.Media.Brush;
        }
        e.Handled = true;
    }

    private void QuickSend_DragEnter(object sender, System.Windows.DragEventArgs e) => QuickSend_DragOver(sender, e);

    private void QuickSend_DragLeave(object sender, System.Windows.DragEventArgs e) => ResetQuickSendDropZone();

    private async void QuickSend_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ResetQuickSendDropZone();
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var file = paths.FirstOrDefault(System.IO.File.Exists);
        if (file is not null) await Main.QueueFileAsync(file);
    }

    private void ResetQuickSendDropZone()
    {
        if (QuickSendDropZone is null) return;
        QuickSendDropZone.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x18, 0x2B, 0x36, 0x50));
        QuickSendDropOutline.Stroke = FindResource("Edge.Brush.Border") as System.Windows.Media.Brush;
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelScheduledTransition();
            _suppressHoverUntilPointerLeaves = true;
            await SetStateAsync(EdgePanelState.Hidden, animate: true, activate: false);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        var section = e.SystemKey switch
        {
            Key.D1 => 0,
            Key.D2 => 1,
            Key.D3 => 2,
            Key.D4 => 3,
            Key.D5 => 4,
            _ => -1,
        };
        if (section >= 0)
        {
            SelectSection(section);
            e.Handled = true;
        }
    }

    private void SelectSection(int section)
    {
        Shell.SelectedSection = section;
        if (!Shell.IsExpanded) return;
        if (section == 2) _ = Recent.ActivateAsync();
        else _ = Recent.DeactivateAsync();
    }

    private void EdgePanelWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        ApplyBackdrop(handle, _windowSource);
        _windowSource?.AddHook(WindowMessageHook);
        RegisterHotKey(handle, HotKeyId, ModControl | ModAlt, VirtualKeyD);
    }

    private static void ApplyBackdrop(IntPtr handle, HwndSource? source)
    {
        if (handle == IntPtr.Zero) return;
        if (source?.CompositionTarget is { } target)
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;

        var margins = new DwmMargins { Left = -1 };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        SetDwmAttribute(handle, 20, 1); // immersive dark mode
        SetDwmAttribute(handle, 33, 2); // rounded corners
        SetDwmAttribute(handle, 34, unchecked((int)0xFFFFFFFE)); // no opaque DWM border
        SetDwmAttribute(handle, 38, 3); // Windows 11 transient Acrylic backdrop

        var accent = new AccentPolicy
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND (Windows 10 fallback)
            AccentFlags = 2,
            GradientColor = unchecked((int)0x7018100A), // AABBGGRR: subtle translucent cold navy tint
        };
        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPointer = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = accentPointer,
                SizeOfData = accentSize,
            };
            _ = SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPointer);
        }
    }

    private static void SetDwmAttribute(IntPtr handle, int attribute, int value) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            _ = OpenAsync(activate: true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void EdgePanelWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _ = SetEnabledAsync(false);
    }

    private void EdgePanelWindow_Closed(object? sender, EventArgs e)
    {
        _transitionTimer.Stop();
        CancelScheduledTransition();
        _placement.CancelAnimation();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotKeyId);
        _ = Recent.DeactivateAsync();
    }

    private bool IsCursorInsideWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var rect) ||
            !GetCursorPos(out var cursor))
        {
            return IsMouseOver;
        }

        return cursor.X >= rect.Left && cursor.X < rect.Right &&
               cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr windowHandle, ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr windowHandle, ref WindowCompositionAttributeData data);

}
