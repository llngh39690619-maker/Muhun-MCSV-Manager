using System.Windows;

namespace MinecraftServerManager.App.Services;

internal sealed class ExistingServerImportCoordinator
{
    private readonly IExistingServerImportChoiceService _choiceService;
    private readonly Func<Task> _importFolderAsync;
    private readonly Func<Task> _importJarAsync;

    public ExistingServerImportCoordinator(
        IExistingServerImportChoiceService choiceService,
        Func<Task> importFolderAsync,
        Func<Task> importJarAsync)
    {
        _choiceService = choiceService ?? throw new ArgumentNullException(nameof(choiceService));
        _importFolderAsync = importFolderAsync ?? throw new ArgumentNullException(nameof(importFolderAsync));
        _importJarAsync = importJarAsync ?? throw new ArgumentNullException(nameof(importJarAsync));
    }

    public async Task<ExistingServerImportKind?> ChooseAndImportAsync(Window? owner)
    {
        var choice = _choiceService.ShowChoice(owner);
        switch (choice)
        {
            case ExistingServerImportKind.ServerFolder:
                await _importFolderAsync();
                break;
            case ExistingServerImportKind.ServerJar:
                await _importJarAsync();
                break;
            case null:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unsupported existing Server import choice.");
        }

        return choice;
    }
}
