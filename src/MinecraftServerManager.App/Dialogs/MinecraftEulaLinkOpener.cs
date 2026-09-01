using System.Diagnostics;
using System.Windows;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Dialogs;

internal static class MinecraftEulaLinkOpener
{
    internal const string OfficialEulaUri = "https://aka.ms/MinecraftEULA";

    internal static bool TryOpen(
        Window? owner,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        Action<Window?, string, string>? failurePresenter = null)
    {
        processStarter ??= static startInfo => Process.Start(startInfo);
        try
        {
            _ = processStarter(new ProcessStartInfo(OfficialEulaUri)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception exception) when (IsRecoverableUiFailure(exception))
        {
            var message = LocalizationService.Current.Get(
                "main.vm.error.minecraftEulaLink",
                OfficialEulaUri);
            var caption = LocalizationService.Current.Get(
                "main.vm.confirm.minecraftEulaTitle");
            failurePresenter ??= static (dialogOwner, text, title) =>
            {
                _ = DarkMessageBox.Show(
                    dialogOwner,
                    text,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            };

            try
            {
                failurePresenter(owner, message, caption);
            }
            catch (Exception presentationException) when (
                IsRecoverableUiFailure(presentationException))
            {
                // Opening the optional documentation link must never terminate the active dialog.
            }

            return false;
        }
    }

    private static bool IsRecoverableUiFailure(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
}
