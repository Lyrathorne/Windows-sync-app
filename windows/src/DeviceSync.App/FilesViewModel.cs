using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DeviceSync.Application;
using DeviceSync.Protocol;

namespace DeviceSync.App;

public sealed class FilesViewModel : ObservableObject, IDisposable
{
    private const int MaximumVisibleItems = 1_000;
    private readonly MediaCatalogManager _catalog;
    private readonly IncomingFileTransferManager _incoming;
    private readonly SemaphoreSlim _thumbnailSlots = new(4, 4);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<IncomingFileTransfer>> _downloadWaiters = new(StringComparer.Ordinal);
    private CancellationTokenSource _viewLifetime = new();
    private string _searchText = "";
    private int _categoryIndex;
    private int _sortIndex;
    private bool _isGridMode = true;
    private bool _isLoading;
    private bool _hasMore;
    private string _status = "Connect a phone to browse its files.";
    private string? _error;
    private string _downloadStatus = "";
    private double _downloadProgress;
    private string? _activeTransferId;

    public FilesViewModel(MediaCatalogClientFactory catalogFactory, IncomingFileTransferManager incoming)
    {
        _catalog = catalogFactory.Create();
        _incoming = incoming;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => !IsLoading && HasMore);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync,
            () => !IsLoading && Items.Any(item => item.IsSelected) && _catalog.IsAvailable);
        CancelDownloadCommand = new AsyncRelayCommand(CancelDownloadAsync, () => _activeTransferId is not null);
        OpenDownloadedCommand = new AsyncRelayCommand(OpenDownloadedAsync,
            () => Items.Count(item => item.IsSelected && item.IsDownloaded) == 1);
        ToggleViewCommand = new AsyncRelayCommand(() => { IsGridMode = !IsGridMode; return Task.CompletedTask; });
        _catalog.ConnectionChanged += OnConnectionChanged;
        _catalog.CatalogChanged += OnCatalogChanged;
        _catalog.PermissionChanged += OnPermissionChanged;
        _catalog.Error += OnCatalogError;
        _catalog.DownloadFailed += OnDownloadFailed;
        _incoming.TransferChanged += OnTransferChanged;
        _incoming.ProgressChanged += OnProgressChanged;
    }

    public ObservableCollection<MediaCatalogItemViewModel> Items { get; } = [];
    public IReadOnlyList<string> Categories { get; } = ["Photos", "Videos", "Documents", "Recent"];
    public IReadOnlyList<string> SortOptions { get; } = ["Newest first", "Oldest first", "Name", "Largest"];
    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenDownloadedCommand { get; }
    public ICommand ToggleViewCommand { get; }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public int CategoryIndex { get => _categoryIndex; set { if (SetProperty(ref _categoryIndex, value)) _ = RefreshAsync(); } }
    public int SortIndex { get => _sortIndex; set { if (SetProperty(ref _sortIndex, value)) _ = RefreshAsync(); } }
    public bool IsGridMode { get => _isGridMode; set { if (SetProperty(ref _isGridMode, value)) OnPropertyChanged(nameof(ViewModeLabel)); } }
    public string ViewModeLabel => IsGridMode ? "List view" : "Grid view";
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) RefreshCommands(); } }
    public bool HasMore { get => _hasMore; private set { if (SetProperty(ref _hasMore, value)) RefreshCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public string DownloadStatus { get => _downloadStatus; private set => SetProperty(ref _downloadStatus, value); }
    public double DownloadProgress { get => _downloadProgress; private set => SetProperty(ref _downloadProgress, value); }
    public bool IsOffline => !_catalog.IsAvailable;

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            if (!_catalog.IsAvailable)
            {
                Items.Clear();
                HasMore = false;
                Status = _catalog.IsAvailable ? "Loading…" : "The connected phone does not provide file browsing.";
                OnPropertyChanged(nameof(IsOffline));
                return;
            }
            var page = await _catalog.StartQueryAsync(BuildQuery(), _viewLifetime.Token);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Items.Clear();
                AddPage(page);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = "Could not load phone files.";
        }
        finally { IsLoading = false; }
    }

    public async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMore) return;
        IsLoading = true;
        try
        {
            var page = await _catalog.LoadNextPageAsync(_viewLifetime.Token);
            if (page is not null) await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => AddPage(page));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error = exception.Message; }
        finally { IsLoading = false; }
    }

    public async Task EnsureThumbnailAsync(MediaCatalogItemViewModel item)
    {
        if (item.Thumbnail is not null || item.IsThumbnailLoading || !item.Item.ThumbnailAvailable) return;
        item.IsThumbnailLoading = true;
        await _thumbnailSlots.WaitAsync(_viewLifetime.Token);
        try
        {
            var response = await _catalog.GetThumbnailAsync(item.Item, IsGridMode ? 320 : 96, IsGridMode ? 240 : 96,
                _viewLifetime.Token);
            if (response is null) return;
            var data = Convert.FromBase64String(response.Data);
            if (data.LongLength != response.SizeBytes || data.Length > 256 * 1024) return;
            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => item.Thumbnail = image);
        }
        catch (OperationCanceledException) { }
        catch { item.ThumbnailFailed = true; }
        finally
        {
            item.IsThumbnailLoading = false;
            _thumbnailSlots.Release();
        }
    }

    private async Task DownloadSelectedAsync()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where to save verified files from the phone",
            UseDescriptionForTitle = true,
            SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeviceSync"),
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Directory.CreateDirectory(dialog.SelectedPath);
        var selected = Items.Where(item => item.IsSelected).ToArray();
        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                var item = selected[index];
                DownloadStatus = $"Requesting {index + 1}/{selected.Length}: {item.Item.DisplayName}";
                DownloadProgress = 0;
                item.LocalPath = await DownloadItemAsync(item.Item, dialog.SelectedPath, _viewLifetime.Token);
                item.IsSelected = false;
            }
            DownloadProgress = 100;
            DownloadStatus = $"{selected.Length} verified file(s) downloaded.";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled.";
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            DownloadStatus = "Download failed.";
        }
    }

    public async Task<string> DownloadItemAsync(
        CatalogItemPayload item,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        await _downloadGate.WaitAsync(cancellationToken);
        var transferId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<IncomingFileTransfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_downloadWaiters) _downloadWaiters[transferId] = completion;
        _activeTransferId = transferId;
        RefreshCommands();
        try
        {
            DownloadStatus = $"Downloading {item.DisplayName}…";
            DownloadProgress = 0;
            await _catalog.RequestDownloadAsync(item, destinationDirectory, transferId, cancellationToken);
            var transfer = await completion.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
            if (transfer.State != IncomingFileTransferState.Completed || string.IsNullOrWhiteSpace(transfer.DestinationPath))
                throw new IOException($"Download failed: {transfer.Error ?? transfer.State.ToString()}");
            DownloadProgress = 100;
            DownloadStatus = $"Downloaded {item.DisplayName}.";
            return transfer.DestinationPath;
        }
        finally
        {
            lock (_downloadWaiters) _downloadWaiters.Remove(transferId);
            _catalog.CompleteDownload(transferId);
            _activeTransferId = null;
            RefreshCommands();
            _downloadGate.Release();
        }
    }

    private async Task CancelDownloadAsync()
    {
        var transferId = _activeTransferId;
        if (transferId is null) return;
        await _catalog.CancelDownloadAsync(transferId);
        if (_incoming.ActiveTransfer?.TransferId == transferId) await _incoming.CancelByReceiverAsync("user_cancelled");
        DownloadStatus = "Download cancelled.";
    }

    private Task OpenDownloadedAsync()
    {
        var item = Items.SingleOrDefault(value => value.IsSelected && value.IsDownloaded);
        if (item?.LocalPath is { } path && File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private MediaCatalogQuery BuildQuery()
    {
        IReadOnlyList<string> categories = CategoryIndex switch
        {
            0 => ["image"],
            1 => ["video"],
            2 => ["document", "other"],
            _ => [],
        };
        var (sortBy, direction) = SortIndex switch
        {
            1 => ("modifiedAtUtc", "asc"),
            2 => ("displayName", "asc"),
            3 => ("sizeBytes", "desc"),
            _ => ("modifiedAtUtc", "desc"),
        };
        return new(categories, SearchText, sortBy, direction, PageSize: 100);
    }

    private void AddPage(MediaCatalogPage page)
    {
        foreach (var payload in page.Items)
        {
            if (Items.Any(item => item.Item.ItemId == payload.ItemId)) continue;
            var item = new MediaCatalogItemViewModel(payload);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MediaCatalogItemViewModel.IsSelected)) RefreshCommands();
            };
            Items.Add(item);
            while (Items.Count > MaximumVisibleItems) Items.RemoveAt(0);
        }
        HasMore = page.HasMore;
        Status = Items.Count == 0
            ? "No matching files."
            : Items.Count == MaximumVisibleItems && HasMore
                ? $"Showing the latest {MaximumVisibleItems:N0} loaded items. Continue paging without growing memory."
                : $"{Items.Count:N0} item(s) loaded.";
    }

    private void OnConnectionChanged(object? sender, EventArgs args) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            OnPropertyChanged(nameof(IsOffline));
            if (_catalog.IsAvailable) await RefreshAsync();
            else
            {
                Items.Clear();
                HasMore = false;
                Status = "Phone files are offline.";
            }
        });

    private void OnCatalogChanged(object? sender, MediaCatalogChangedEventArgs args)
    {
        if (!args.RequiresRefresh) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Status = "Phone library changed. Refreshing…";
            return RefreshAsync();
        });
    }

    private void OnPermissionChanged(MediaCatalogPermission permission) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (permission.State is "denied" or "revoked")
            {
                Items.Clear();
                Status = "Allow file access in DeviceSync on the phone.";
            }
        });

    private void OnCatalogError(string message) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Error = message);

    private void OnDownloadFailed(string transferId, MediaCatalogException error)
    {
        TaskCompletionSource<IncomingFileTransfer>? waiter;
        lock (_downloadWaiters) _downloadWaiters.TryGetValue(transferId, out waiter);
        waiter?.TrySetException(error);
    }

    private void OnTransferChanged(object? sender, IncomingFileTransferChangedEventArgs args)
    {
        TaskCompletionSource<IncomingFileTransfer>? waiter;
        lock (_downloadWaiters) _downloadWaiters.TryGetValue(args.Transfer.TransferId, out waiter);
        if (waiter is null) return;
        if (args.Transfer.State is IncomingFileTransferState.Completed or IncomingFileTransferState.Failed
            or IncomingFileTransferState.Cancelled or IncomingFileTransferState.Rejected)
            waiter.TrySetResult(args.Transfer);
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            DownloadStatus = $"{args.Transfer.SafeFileName}: {args.Transfer.State}");
    }

    private void OnProgressChanged(object? sender, IncomingFileTransferProgressEventArgs args)
    {
        if (args.Transfer.TransferId != _activeTransferId) return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            DownloadProgress = args.Transfer.SizeBytes == 0 ? 100 :
                args.Transfer.ReceivedBytes * 100d / args.Transfer.SizeBytes);
    }

    private void RefreshCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelDownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenDownloadedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task CloseAsync()
    {
        _viewLifetime.Cancel();
        await _catalog.CancelActiveQueryAsync();
        _viewLifetime.Dispose();
        _viewLifetime = new();
    }

    public void Dispose()
    {
        _catalog.ConnectionChanged -= OnConnectionChanged;
        _catalog.CatalogChanged -= OnCatalogChanged;
        _catalog.PermissionChanged -= OnPermissionChanged;
        _catalog.Error -= OnCatalogError;
        _catalog.DownloadFailed -= OnDownloadFailed;
        _incoming.TransferChanged -= OnTransferChanged;
        _incoming.ProgressChanged -= OnProgressChanged;
        _viewLifetime.Cancel();
        _viewLifetime.Dispose();
        _downloadGate.Dispose();
        _catalog.Dispose();
    }
}

public sealed class MediaCatalogItemViewModel(CatalogItemPayload item) : ObservableObject
{
    private bool _isSelected;
    private bool _isThumbnailLoading;
    private bool _thumbnailFailed;
    private BitmapSource? _thumbnail;
    private string? _localPath;

    public CatalogItemPayload Item { get; } = item;
    public string DisplayName => Item.DisplayName;
    public string Detail => $"{FormatSize(Item.SizeBytes)} • {Item.ModifiedAtUtc}";
    public string Category => Item.Category;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsThumbnailLoading { get => _isThumbnailLoading; set => SetProperty(ref _isThumbnailLoading, value); }
    public bool ThumbnailFailed { get => _thumbnailFailed; set => SetProperty(ref _thumbnailFailed, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
    public string? LocalPath { get => _localPath; set { if (SetProperty(ref _localPath, value)) OnPropertyChanged(nameof(IsDownloaded)); } }
    public bool IsDownloaded => LocalPath is not null && File.Exists(LocalPath);

    private static string FormatSize(long? bytes)
    {
        if (bytes is null) return "Unknown size";
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
