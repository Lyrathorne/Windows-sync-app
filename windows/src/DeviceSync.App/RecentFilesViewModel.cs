using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Windows.Media.Imaging;
using DeviceSync.Application;
using DeviceSync.Protocol;

namespace DeviceSync.App;

public sealed class RecentFilesViewModel : ObservableObject, IDisposable
{
    private const int MaximumPhotoItems = 5;
    private const int MaximumFileItems = 4;
    private readonly MediaCatalogManager _catalog;
    private readonly IPrivacyShieldMonitor _privacy;
    private CancellationTokenSource? _activation;
    private bool _isLoading;
    private bool _isActive;
    private bool _hideThumbnails = true;
    private string _status = "Open Files to see recent phone items.";
    private string _fileStatus = "";

    public RecentFilesViewModel(MediaCatalogClientFactory factory, IPrivacyShieldMonitor privacy)
    {
        _catalog = factory.Create();
        _privacy = privacy;
        _catalog.ConnectionChanged += OnConnectionChanged;
        _catalog.CatalogChanged += OnCatalogChanged;
        _catalog.PermissionChanged += OnPermissionChanged;
        _privacy.Changed += OnPrivacyChanged;
    }

    public ObservableCollection<RecentMediaItemViewModel> Photos { get; } = [];
    public ObservableCollection<RecentMediaItemViewModel> Files { get; } = [];
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsOffline => !_catalog.IsAvailable;
    public bool HideThumbnails
    {
        get => _hideThumbnails;
        private set
        {
            if (!SetProperty(ref _hideThumbnails, value)) return;
            OnPropertyChanged(nameof(IsPrivacyShielded));
            foreach (var item in Photos.Concat(Files)) item.IsThumbnailVisible = !IsPrivacyShielded;
        }
    }
    public bool IsPrivacyShielded => HideThumbnails || _privacy.IsSensitiveSession;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FileStatus { get => _fileStatus; private set => SetProperty(ref _fileStatus, value); }

    public void ReportAction(string message) => Status = message;

    public void SetPrivacyMode(bool hideThumbnails)
    {
        var wasShielded = IsPrivacyShielded;
        HideThumbnails = hideThumbnails;
        if (_isActive && wasShielded && !IsPrivacyShielded) _ = RefreshAsync();
    }

    public async Task ActivateAsync()
    {
        if (_isActive) return;
        _isActive = true;
        _activation = new CancellationTokenSource();
        await RefreshAsync();
    }

    public async Task DeactivateAsync()
    {
        if (!_isActive) return;
        _isActive = false;
        _activation?.Cancel();
        _activation?.Dispose();
        _activation = null;
        await _catalog.CancelActiveQueryAsync();
        IsLoading = false;
    }

    public async Task RefreshAsync()
    {
        if (!_isActive || IsLoading) return;
        var cancellationToken = _activation?.Token ?? CancellationToken.None;
        IsLoading = true;
        try
        {
            if (!_catalog.IsAvailable)
            {
                Photos.Clear();
                Files.Clear();
                Status = "Phone files are offline.";
                OnPropertyChanged(nameof(IsOffline));
                return;
            }
            Status = "Refreshing recent phone files…";
            FileStatus = "";
            MediaCatalogPage? photoPage = null;
            MediaCatalogPage? filePage = null;
            Exception? lastError = null;
            try
            {
                photoPage = await _catalog.StartQueryAsync(
                    new(["image"], SortBy: "modifiedAtUtc", SortDirection: "desc", PageSize: MaximumPhotoItems),
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
            }
            try
            {
                filePage = await _catalog.StartQueryAsync(
                    new(["document", "other"], SortBy: "modifiedAtUtc", SortDirection: "desc", PageSize: MaximumFileItems),
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
            }
            if (filePage is null || filePage.Items.Count == 0)
            {
                try
                {
                    var fallback = await _catalog.StartQueryAsync(
                        new([], SortBy: "modifiedAtUtc", SortDirection: "desc", PageSize: 200),
                        cancellationToken);
                    var fallbackFiles = fallback.Items
                        .Where(item => item.Category is "document" or "other")
                        .Take(MaximumFileItems)
                        .ToArray();
                    if (fallbackFiles.Length > 0)
                        filePage = new MediaCatalogPage(fallbackFiles, false, fallback.SnapshotGeneration);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    lastError ??= error;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var photoModels = photoPage?.Items.Take(MaximumPhotoItems).Select(item => new RecentMediaItemViewModel(item)
            {
                IsThumbnailVisible = !IsPrivacyShielded,
            }).ToArray() ?? [];
            var fileModels = filePage?.Items.Take(MaximumFileItems).Select(item => new RecentMediaItemViewModel(item)
            {
                IsThumbnailVisible = !IsPrivacyShielded,
            }).ToArray() ?? [];
            await InvokeOnUiAsync(() =>
            {
                Photos.Clear();
                Files.Clear();
                foreach (var model in photoModels) Photos.Add(model);
                foreach (var model in fileModels) Files.Add(model);
                FileStatus = Files.Count == 0
                    ? lastError is null
                        ? "No recent documents in the shared phone folder."
                        : "Allow access to a document folder in DeviceSync on the phone."
                    : string.Empty;
                var total = Photos.Count + Files.Count;
                Status = total == 0
                    ? lastError?.Message ?? "No recent phone files."
                    : string.Empty;
            });
            if (!IsPrivacyShielded)
            {
                await Task.WhenAll(photoModels.Select(model => LoadThumbnailAsync(model, cancellationToken)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally { IsLoading = false; }
    }

    private async Task LoadThumbnailAsync(RecentMediaItemViewModel model, CancellationToken cancellationToken)
    {
        if (!model.Item.ThumbnailAvailable || IsPrivacyShielded) return;
        try
        {
            // The edge panel can be rendered at 150–200% DPI. Request enough source pixels
            // to avoid stretching a tiny 96x72 JPEG into a visibly blurred card.
            var response = await _catalog.GetThumbnailAsync(model.Item, 256, 256, cancellationToken);
            if (response is null || IsPrivacyShielded) return;
            var bytes = Convert.FromBase64String(response.Data);
            if (bytes.LongLength != response.SizeBytes || bytes.Length > 256 * 1024) return;
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            await InvokeOnUiAsync(() =>
            {
                if (!IsPrivacyShielded) model.Thumbnail = image;
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void OnConnectionChanged(object? sender, EventArgs args)
    {
        _ = InvokeOnUiAsync(() =>
        {
            OnPropertyChanged(nameof(IsOffline));
            if (_isActive) _ = RefreshAsync();
        });
    }

    private void OnCatalogChanged(object? sender, MediaCatalogChangedEventArgs args)
    {
        if (_isActive && args.RequiresRefresh) _ = RefreshAsync();
    }

    private void OnPermissionChanged(MediaCatalogPermission permission)
    {
        if (permission.State is "denied" or "revoked")
            _ = InvokeOnUiAsync(() =>
            {
                Photos.Clear();
                Files.Clear();
                Status = "Allow file access on the phone.";
            });
    }

    private void OnPrivacyChanged(object? sender, EventArgs args)
    {
        _ = InvokeOnUiAsync(() =>
        {
            OnPropertyChanged(nameof(IsPrivacyShielded));
            foreach (var item in Photos.Concat(Files))
            {
                item.IsThumbnailVisible = !IsPrivacyShielded;
                if (IsPrivacyShielded) item.Thumbnail = null;
            }
            if (_isActive && !IsPrivacyShielded) _ = RefreshAsync();
        });
    }

    private static async Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        _catalog.ConnectionChanged -= OnConnectionChanged;
        _catalog.CatalogChanged -= OnCatalogChanged;
        _catalog.PermissionChanged -= OnPermissionChanged;
        _privacy.Changed -= OnPrivacyChanged;
        _activation?.Cancel();
        _activation?.Dispose();
        _catalog.Dispose();
    }
}

public sealed class RecentMediaItemViewModel(CatalogItemPayload item) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private bool _isThumbnailVisible;
    public CatalogItemPayload Item { get; } = item;
    public string DisplayName => Item.DisplayName;
    public string Detail => $"{Item.Category} • {FormatModified(Item.ModifiedAtUtc)} • {FormatSize(Item.SizeBytes)}";
    public string SizeDisplay => FormatSize(Item.SizeBytes);
    public string ModifiedDisplay => FormatRelative(Item.ModifiedAtUtc);
    public BitmapSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
    public bool IsThumbnailVisible { get => _isThumbnailVisible; set => SetProperty(ref _isThumbnailVisible, value); }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null) return "unknown size";
        var value = (double)bytes;
        var unit = "B";
        if (value >= 1024) { value /= 1024; unit = "KiB"; }
        if (value >= 1024) { value /= 1024; unit = "MiB"; }
        if (value >= 1024) { value /= 1024; unit = "GiB"; }
        return $"{value:0.#} {unit}";
    }

    private static string FormatModified(string value) =>
        DateTimeOffset.TryParse(value, out var modified)
            ? modified.ToLocalTime().ToString("g")
            : "unknown date";

    private static string FormatRelative(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var modified)) return "";
        var local = modified.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return local.ToString("t");
        var russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
        if (local.Date == now.Date.AddDays(-1)) return russian ? "Вчера" : "Yesterday";
        var days = (now.Date - local.Date).Days;
        if (days is > 1 and < 7)
            return russian ? $"{days} дн. назад" : $"{days} days ago";
        return local.ToString("d");
    }
}
