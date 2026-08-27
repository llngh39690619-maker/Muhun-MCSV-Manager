using System.Windows;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Dialogs;

public partial class BackgroundJobsWindow : Window
{
    public BackgroundJobsWindow(MainWindowViewModel viewModel)
        : this((object)viewModel)
    {
    }

    internal BackgroundJobsWindow(object dataContext)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        InitializeComponent();
        DataContext = dataContext;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
