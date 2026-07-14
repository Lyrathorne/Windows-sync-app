namespace DeviceSync.App;

public partial class IncomingFileWindow
{
    public IncomingFileWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is IncomingFileViewModel viewModel && viewModel.RejectCommand.CanExecute(null))
            {
                viewModel.RejectCommand.Execute(null);
            }
        };
    }
}
