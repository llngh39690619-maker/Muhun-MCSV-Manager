using System.Windows;

namespace MinecraftServerManager.App.Dialogs;

internal enum GeneralSettingsCloseChoice
{
    ContinueEditing,
    SaveAndApply,
    Discard
}

internal partial class GeneralSettingsUnsavedChangesDialog : Window
{
    internal GeneralSettingsUnsavedChangesDialog()
    {
        InitializeComponent();
    }

    internal GeneralSettingsCloseChoice Choice { get; private set; } =
        GeneralSettingsCloseChoice.ContinueEditing;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Choice = GeneralSettingsCloseChoice.SaveAndApply;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = GeneralSettingsCloseChoice.Discard;
        DialogResult = true;
    }
}
