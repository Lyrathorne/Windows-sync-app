namespace DeviceSync.App;

public partial class MainWindow
{
    private PairingWindow? _pairingWindow;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
