using System.Windows;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Services;

internal interface IBackgroundCoreServerCreationDialogService
{
    bool ShowCreateDialog(Window? owner);
}

internal sealed class BackgroundCoreServerCreationDialogService(
    ICoreServerCreationWorkflow workflow,
    BackgroundServerJobCoordinator coordinator)
    : IBackgroundCoreServerCreationDialogService
{
    private readonly ICoreServerCreationWorkflow _workflow = workflow
        ?? throw new ArgumentNullException(nameof(workflow));
    private readonly BackgroundServerJobCoordinator _coordinator = coordinator
        ?? throw new ArgumentNullException(nameof(coordinator));

    public bool ShowCreateDialog(Window? owner)
    {
        var viewModel = new CoreServerCreationViewModel(_workflow);
        var dialog = new CoreServerCreationDialog(viewModel, Submit);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private BackgroundJobSubmissionResult Submit(CoreServerCreationRequest request)
    {
        var resourceClass = request.Core.Software is CoreServerSoftware.Spigot
            or CoreServerSoftware.CraftBukkit
            ? BackgroundServerJobResourceClass.BuildTools
            : BackgroundServerJobResourceClass.General;
        var definition = new BackgroundServerJobDefinition(
            BackgroundServerJobKind.CoreServer,
            request.ServerName,
            LocalizationService.Current.Get(
                "jobs.activity.createCore",
                request.Core.DisplayName,
                request.Version.DisplayName),
            async (progress, cancellationToken) => await _workflow.CreateAsync(
                    request,
                    new ProgressAdapter<CoreServerCreationProgress>(value => progress.Report(
                        new BackgroundServerJobProgress(
                            value.Message,
                            value.Percentage,
                            value.Detail))),
                    cancellationToken)
                .ConfigureAwait(false),
            resourceClass);
        return _coordinator.TryEnqueue(definition, out _, out var error)
            ? BackgroundJobSubmissionResult.Success()
            : BackgroundJobSubmissionResult.Failure(
                error ?? LocalizationService.Current.Get("jobs.error.addCore"));
    }
}

internal interface IBackgroundOnlineModpackDialogService
{
    bool ShowInstallDialog(Window? owner);
}

internal sealed class BackgroundOnlineModpackDialogService(
    IOnlineModpackWorkflow workflow,
    BackgroundServerJobCoordinator coordinator)
    : IBackgroundOnlineModpackDialogService
{
    private readonly IOnlineModpackWorkflow _workflow = workflow
        ?? throw new ArgumentNullException(nameof(workflow));
    private readonly BackgroundServerJobCoordinator _coordinator = coordinator
        ?? throw new ArgumentNullException(nameof(coordinator));

    public bool ShowInstallDialog(Window? owner)
    {
        var viewModel = new OnlineModpackViewModel(_workflow);
        var dialog = new OnlineModpackDialog(
            viewModel,
            loadFeaturedOnOpen: true,
            backgroundSubmitter: Submit);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private BackgroundJobSubmissionResult Submit(OnlineModpackInstallRequest request)
    {
        var definition = new BackgroundServerJobDefinition(
            BackgroundServerJobKind.OnlineModpack,
            request.ServerName,
            LocalizationService.Current.Get(
                "jobs.activity.installModpack",
                request.Project.Name,
                request.Version.VersionName),
            async (progress, cancellationToken) => await _workflow.InstallAsync(
                    request,
                    transientApiKey: null,
                    new ProgressAdapter<OnlineModpackInstallProgress>(value => progress.Report(
                        new BackgroundServerJobProgress(
                            value.Message,
                            value.Percentage,
                            value.Detail))),
                    cancellationToken)
                .ConfigureAwait(false));
        return _coordinator.TryEnqueue(definition, out _, out var error)
            ? BackgroundJobSubmissionResult.Success()
            : BackgroundJobSubmissionResult.Failure(
                error ?? LocalizationService.Current.Get("jobs.error.addModpack"));
    }
}

internal sealed class ProgressAdapter<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(T value) => _report(value);
}
