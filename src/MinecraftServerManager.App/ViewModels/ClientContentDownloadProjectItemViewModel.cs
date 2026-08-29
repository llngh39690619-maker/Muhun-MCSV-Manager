using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class ClientContentDownloadProjectItemViewModel
{
    public ClientContentDownloadProjectItemViewModel(
        ModrinthClientContentProject project,
        string compatibilityText)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        CompatibilityText = compatibilityText ?? throw new ArgumentNullException(nameof(compatibilityText));
    }

    public ModrinthClientContentProject Project { get; }

    public string ProjectId => Project.ProjectId;

    public string Title => Project.Title;

    public string Summary => Project.Description;

    public string CompatibilityText { get; }

    public Uri ProjectPageUri => Project.ProjectPageUri;
}

public sealed record ClientContentDownloadLoaderChoice(
    MinecraftClientLoader? Loader,
    string DisplayName);
