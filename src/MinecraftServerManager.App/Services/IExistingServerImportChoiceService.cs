using System.Windows;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Services;

internal enum ExistingServerImportKind
{
    ServerFolder,
    ServerJar
}

internal interface IExistingServerImportChoiceService
{
    ExistingServerImportKind? ShowChoice(Window? owner);
}

internal sealed class ExistingServerImportChoiceService : IExistingServerImportChoiceService
{
    public ExistingServerImportKind? ShowChoice(Window? owner)
    {
        var dialog = new ExistingServerImportChoiceDialog();
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.SelectedImportKind : null;
    }
}
