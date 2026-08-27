using System.Windows;
using MinecraftServerManager.App.Services;
using System.Windows.Input;

namespace MinecraftServerManager.App.Dialogs;

public partial class PaperVersionDialog : Window
{
    private readonly IReadOnlyList<string> _versions;

    public PaperVersionDialog(IReadOnlyList<string> versions)
    {
        InitializeComponent();
        _versions = versions;
        ApplyFilter(string.Empty);
        VersionList.SelectedIndex = VersionList.Items.Count > 0 ? 0 : -1;
    }

    public string? SelectedVersion => VersionList.SelectedItem as string;

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string filter)
    {
        var matches = string.IsNullOrWhiteSpace(filter)
            ? _versions
            : _versions.Where(version => version.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        VersionList.ItemsSource = matches;
        VersionList.SelectedIndex = VersionList.Items.Count > 0 ? 0 : -1;
    }

    private void OnVersionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedVersion is not null)
        {
            DialogResult = true;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null)
        {
            DarkMessageBox.Show(
                this,
                LocalizationService.Current.Get("paper.validation.selectVersion"),
                LocalizationService.Current.Get("common.notSelected"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
