using System.Windows;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Dialogs;

public partial class ExistingServerImportChoiceDialog : Window
{
    public ExistingServerImportChoiceDialog()
    {
        InitializeComponent();
    }

    internal ExistingServerImportKind? SelectedImportKind { get; private set; }

    private void OnImportFolderClick(object sender, RoutedEventArgs e)
        => Complete(ExistingServerImportKind.ServerFolder);

    private void OnImportJarClick(object sender, RoutedEventArgs e)
        => Complete(ExistingServerImportKind.ServerJar);

    private void Complete(ExistingServerImportKind importKind)
    {
        SelectedImportKind = importKind;
        DialogResult = true;
    }
}
