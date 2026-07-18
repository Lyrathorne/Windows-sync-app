namespace DeviceSync.App;

public partial class MainWindow
{
    private PairingWindow? _pairingWindow;
    private IncomingFileWindow? _incomingFileWindow;
    private readonly IncomingFileViewModel _incomingFileViewModel;
    private readonly FilesViewModel _filesViewModel;
    private FilesWindow? _filesWindow;

    public MainWindow(MainViewModel viewModel, IncomingFileViewModel incomingFileViewModel, FilesViewModel filesViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _incomingFileViewModel = incomingFileViewModel;
        _filesViewModel = filesViewModel;
        _incomingFileViewModel.ShowRequested += OnIncomingFileShowRequested;
        _incomingFileViewModel.CloseRequested += OnIncomingFileCloseRequested;
    }

    private void PhoneFiles_Click(object sender, System.Windows.RoutedEventArgs e) => ShowPhoneFiles();

    public void ShowPhoneFiles()
    {
        if (_filesWindow is { IsVisible: true })
        {
            _filesWindow.Activate();
            return;
        }
        _filesWindow = new FilesWindow(_filesViewModel) { Owner = this };
        _filesWindow.Closed += (_, _) => _filesWindow = null;
        _filesWindow.Show();
    }

    private void OnIncomingFileCloseRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_incomingFileWindow is { } window)
            {
                window.Close();
            }
        });
    }

    private void OnIncomingFileShowRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_incomingFileWindow is { IsVisible: true })
            {
                _incomingFileWindow.Activate();
                return;
            }

            _incomingFileWindow = new IncomingFileWindow
            {
                Owner = this,
                DataContext = _incomingFileViewModel,
            };
            _incomingFileWindow.Closed += (_, _) => _incomingFileWindow = null;
            _incomingFileWindow.Show();
        });
    }

    private async void AddPhone_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.AddPhoneCommand.CanExecute(null))
        {
            viewModel.AddPhoneCommand.Execute(null);
        }

        if (_pairingWindow is { IsVisible: true })
        {
            _pairingWindow.Activate();
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _pairingWindow = new PairingWindow
            {
                Owner = this,
                DataContext = viewModel,
            };
            _pairingWindow.Closed += (_, _) => _pairingWindow = null;
            _pairingWindow.Show();
        });
    }
}
