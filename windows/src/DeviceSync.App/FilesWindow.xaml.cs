using System.Windows;

namespace DeviceSync.App;

public partial class FilesWindow
{
    public FilesWindow(FilesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
        Closing += async (_, _) => await viewModel.CloseAsync();
    }

    private async void CatalogItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCatalogItemViewModel item } &&
            DataContext is FilesViewModel viewModel)
            await viewModel.EnsureThumbnailAsync(item);
    }
}
