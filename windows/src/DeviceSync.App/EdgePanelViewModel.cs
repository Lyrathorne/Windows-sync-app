using DeviceSync.Application;

namespace DeviceSync.App;

public sealed class EdgePanelViewModel : ObservableObject
{
    private bool _enabled = true;
    private EdgePanelState _state = EdgePanelState.Hidden;
    private int _selectedSection;
    private int _hoverDelayMilliseconds = 350;
    private EdgePanelSide _side = EdgePanelSide.Right;
    private string? _monitorDeviceName;
    private bool _hideRecentThumbnails = true;

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public EdgePanelState State => _state;
    public bool IsHidden => _state == EdgePanelState.Hidden;
    public bool IsExpanded => _state == EdgePanelState.Expanded;
    public int SelectedSection { get => _selectedSection; set => SetProperty(ref _selectedSection, Math.Clamp(value, 0, 4)); }
    public int HoverDelayMilliseconds { get => _hoverDelayMilliseconds; set => SetProperty(ref _hoverDelayMilliseconds, Math.Clamp(value, 0, 2000)); }
    public EdgePanelSide Side { get => _side; set => SetProperty(ref _side, value); }
    public string? MonitorDeviceName { get => _monitorDeviceName; set => SetProperty(ref _monitorDeviceName, value); }
    public bool HideRecentThumbnails { get => _hideRecentThumbnails; set => SetProperty(ref _hideRecentThumbnails, value); }

    public void Apply(EdgePanelSettings settings)
    {
        Enabled = settings.Enabled;
        SetState(EdgePanelState.Hidden);
        SelectedSection = settings.SelectedSection;
        HoverDelayMilliseconds = settings.HoverDelayMilliseconds;
        Side = settings.Side;
        MonitorDeviceName = settings.MonitorDeviceName;
        // Previous versions persisted thumbnail hiding as an unreachable default. Recent
        // phone photos are now visible unless the privacy monitor detects a sensitive session.
        HideRecentThumbnails = false;
    }

    public EdgePanelSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Expanded = false,
        SelectedSection = SelectedSection,
        HoverDelayMilliseconds = HoverDelayMilliseconds,
        Side = Side,
        MonitorDeviceName = MonitorDeviceName,
        HideRecentThumbnails = HideRecentThumbnails,
    };

    public void SetState(EdgePanelState state)
    {
        if (_state == state) return;
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsExpanded));
    }
}
