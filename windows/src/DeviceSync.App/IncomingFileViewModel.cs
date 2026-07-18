using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using DeviceSync.Application;
using Microsoft.Win32;

namespace DeviceSync.App;

public sealed class IncomingFileViewModel : ObservableObject
{
    private readonly IncomingFileTransferDecisionCoordinator _decisions;
    private readonly IncomingFileTransferManager _manager;
    private readonly IIncomingFileStorage _storage;
    private readonly DeviceSessionRegistry _sessions;
    private readonly AsyncRelayCommand _acceptCommand;
    private readonly AsyncRelayCommand _rejectCommand;
    private readonly AsyncRelayCommand _chooseFolderCommand;
    private readonly AsyncRelayCommand _cancelCommand;
    private readonly AsyncRelayCommand _openFileCommand;
    private readonly AsyncRelayCommand _openFolderCommand;
    private IncomingFileTransfer? _transfer;
    private string _deviceName = "-";
    private string _fileName = "-";
    private string _fileSize = "0 B";
    private string _mimeType = "-";
    private string _saveDirectory;
    private double _progressPercent;
    private string _progressText = "0 B / 0 B";
    private string _speedText = "0 B/s";
    private string _status = "Waiting";
    private string _error = "";

    public IncomingFileViewModel(
        IncomingFileTransferDecisionCoordinator decisions,
        IncomingFileTransferManager manager,
        IIncomingFileStorage storage,
        DeviceSessionRegistry sessions)
    {
        _decisions = decisions;
        _manager = manager;
        _storage = storage;
        _sessions = sessions;
        _saveDirectory = storage.DefaultReceiveDirectory;
        _acceptCommand = new AsyncRelayCommand(AcceptAsync, () => CanDecide);
        _rejectCommand = new AsyncRelayCommand(RejectAsync, () => CanDecide);
        _chooseFolderCommand = new AsyncRelayCommand(ChooseFolderAsync, () => CanDecide);
        _cancelCommand = new AsyncRelayCommand(CancelAsync, () => CanCancel);
        _openFileCommand = new AsyncRelayCommand(OpenFileAsync, () => CanOpenFile);
        _openFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => CanOpenFolder);

        _decisions.DecisionRequested += OnDecisionRequested;
        _manager.TransferChanged += OnTransferChanged;
        _manager.ProgressChanged += OnProgressChanged;
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? CloseRequested;

    public ICommand AcceptCommand => _acceptCommand;
    public ICommand RejectCommand => _rejectCommand;
    public ICommand ChooseFolderCommand => _chooseFolderCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand OpenFileCommand => _openFileCommand;
    public ICommand OpenFolderCommand => _openFolderCommand;

    public string DeviceName { get => _deviceName; private set => SetProperty(ref _deviceName, value); }
    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string FileSize { get => _fileSize; private set => SetProperty(ref _fileSize, value); }
    public string MimeType { get => _mimeType; private set => SetProperty(ref _mimeType, value); }
    public string SaveDirectory { get => _saveDirectory; private set => SetProperty(ref _saveDirectory, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    private bool CanDecide => _transfer?.State == IncomingFileTransferState.WaitingForUser;
    private bool CanCancel => _transfer?.State is IncomingFileTransferState.Accepted
        or IncomingFileTransferState.Receiving or IncomingFileTransferState.Verifying;
    private bool CanOpenFile => _transfer?.State == IncomingFileTransferState.Completed
        && File.Exists(_transfer.DestinationPath);
    private bool CanOpenFolder => _transfer?.State == IncomingFileTransferState.Completed
        && Directory.Exists(Path.GetDirectoryName(_transfer.DestinationPath));

    private void OnDecisionRequested(object? sender, IncomingFileTransferDecisionRequestedEventArgs args)
    {
        Dispatch(() =>
        {
            _transfer = args.Transfer;
            DeviceName = _sessions.ActiveSession?.DeviceName ?? args.Transfer.SenderDeviceId;
            FileName = args.Transfer.FileName;
            FileSize = FormatBytes(args.Transfer.SizeBytes);
            MimeType = args.Transfer.MimeType;
            SaveDirectory = _storage.DefaultReceiveDirectory;
            ProgressPercent = 0;
            ProgressText = $"0 B / {FormatBytes(args.Transfer.SizeBytes)}";
            SpeedText = "0 B/s";
            Status = "Waiting for your decision";
            Error = "";
            RefreshCommands();
            ShowRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTransferChanged(object? sender, IncomingFileTransferChangedEventArgs args)
    {
        if (_transfer?.TransferId != args.Transfer.TransferId)
        {
            if (args.Transfer.State != IncomingFileTransferState.Accepted) return;
            Dispatch(() =>
            {
                _transfer = args.Transfer;
                DeviceName = _sessions.ActiveSession?.DeviceName ?? args.Transfer.SenderDeviceId;
                FileName = args.Transfer.FileName;
                FileSize = FormatBytes(args.Transfer.SizeBytes);
                MimeType = args.Transfer.MimeType;
                SaveDirectory = args.Transfer.DestinationPath is { } destination
                    ? Path.GetDirectoryName(destination) ?? _storage.DefaultReceiveDirectory
                    : _storage.DefaultReceiveDirectory;
                ProgressPercent = 0;
                ProgressText = $"0 B / {FormatBytes(args.Transfer.SizeBytes)}";
                SpeedText = "0 B/s";
                Status = StateText(args.Transfer.State);
                Error = "";
                RefreshCommands();
                ShowRequested?.Invoke(this, EventArgs.Empty);
            });
            return;
        }

        Dispatch(() =>
        {
            _transfer = args.Transfer;
            Status = StateText(args.Transfer.State);
            Error = args.Transfer.Error ?? "";
            ProgressPercent = args.Transfer.SizeBytes == 0
                ? args.Transfer.State == IncomingFileTransferState.Completed ? 100 : 0
                : 100d * args.Transfer.ReceivedBytes / args.Transfer.SizeBytes;
            ProgressText = $"{FormatBytes(args.Transfer.ReceivedBytes)} / {FormatBytes(args.Transfer.SizeBytes)}";
            if (args.Transfer.DestinationPath is { } destination)
            {
                SaveDirectory = Path.GetDirectoryName(destination) ?? SaveDirectory;
            }
            RefreshCommands();
        });
    }

    private void OnProgressChanged(object? sender, IncomingFileTransferProgressEventArgs args)
    {
        if (_transfer?.TransferId != args.Transfer.TransferId)
        {
            return;
        }

        Dispatch(() => SpeedText = $"{FormatBytes(args.BytesPerSecond)}/s");
    }

    private Task AcceptAsync()
    {
        if (_transfer is not null && _decisions.Accept(_transfer.TransferId, SaveDirectory))
        {
            Status = "Accepting...";
            RefreshCommands();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    private Task RejectAsync()
    {
        if (_transfer is not null && _decisions.Reject(_transfer.TransferId))
        {
            Status = "Rejected";
            RefreshCommands();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public void OnWindowClosing()
    {
        if (_transfer is not null && CanDecide)
        {
            _decisions.Reject(_transfer.TransferId, "window_closed");
        }
    }

    private Task ChooseFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for received files",
            InitialDirectory = SaveDirectory,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            SaveDirectory = dialog.FolderName;
        }
        return Task.CompletedTask;
    }

    private async Task CancelAsync()
    {
        await _manager.CancelByReceiverAsync();
        RefreshCommands();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private Task OpenFileAsync()
    {
        if (_transfer?.DestinationPath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    private Task OpenFolderAsync()
    {
        if (_transfer?.DestinationPath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    private void RefreshCommands()
    {
        _acceptCommand.RaiseCanExecuteChanged();
        _rejectCommand.RaiseCanExecuteChanged();
        _chooseFolderCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _openFileCommand.RaiseCanExecuteChanged();
        _openFolderCommand.RaiseCanExecuteChanged();
    }

    private static string StateText(IncomingFileTransferState state) => state switch
    {
        IncomingFileTransferState.WaitingForUser => "Waiting for your decision",
        IncomingFileTransferState.Accepted => "Accepted",
        IncomingFileTransferState.Receiving => "Receiving",
        IncomingFileTransferState.Verifying => "Verifying SHA-256",
        IncomingFileTransferState.Completed => "Completed",
        IncomingFileTransferState.Rejected => "Rejected",
        IncomingFileTransferState.Cancelled => "Cancelled",
        IncomingFileTransferState.Failed => "Failed",
        _ => state.ToString(),
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount:0.##} {units[unit]}";
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
