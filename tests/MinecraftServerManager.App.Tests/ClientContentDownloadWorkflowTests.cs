using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientContentDownloadWorkflowTests
{
    [Fact]
    public void ResolveFallbackUri_PrefersDirectDownloadAndSupportsExplicitProjectFallback()
    {
        var projectPage = new Uri("https://modrinth.com/mod/example");
        var versionPage = new Uri("https://modrinth.com/mod/example/version/1");
        var directDownload = new Uri("https://cdn.modrinth.com/data/example/file.jar");
        var fallback = new ModrinthClientContentFallback(
            "example",
            "1",
            "Example",
            ModrinthClientContentFallbackReason.MissingVerifiedFile,
            "Manual download required.",
            versionPage,
            directDownload);

        Assert.Equal(
            directDownload,
            ClientWorkspaceViewModel.ResolveContentDownloadFallbackUri(
                [fallback],
                projectPage,
                useProjectPageWhenEmpty: false));
        Assert.Null(ClientWorkspaceViewModel.ResolveContentDownloadFallbackUri(
            [],
            projectPage,
            useProjectPageWhenEmpty: false));
        Assert.Equal(
            projectPage,
            ClientWorkspaceViewModel.ResolveContentDownloadFallbackUri(
                [],
                projectPage,
                useProjectPageWhenEmpty: true));
    }

    [Fact]
    public void InstallJob_CancelAndTerminalTransitionsKeepCommandStateConsistent()
    {
        using var applicationCancellation = new CancellationTokenSource();
        using var job = CreateJob(applicationCancellation.Token);

        Assert.True(job.IsRunning);
        Assert.True(job.CancelCommand.CanExecute(null));

        job.CancelCommand.Execute(null);

        Assert.True(job.CancellationToken.IsCancellationRequested);
        job.MarkCanceled("Canceled");
        Assert.True(job.IsTerminal);
        Assert.False(job.IsRunning);
        Assert.False(job.CancelCommand.CanExecute(null));

        job.Report("download", "Must not replace terminal state", 0.9d);
        Assert.Equal("Canceled", job.StatusText);
    }

    [Fact]
    public void InstallJob_LinkedApplicationCancellationCancelsBackgroundWork()
    {
        using var applicationCancellation = new CancellationTokenSource();
        using var job = CreateJob(applicationCancellation.Token);

        applicationCancellation.Cancel();

        Assert.True(job.CancellationToken.IsCancellationRequested);
    }

    private static ClientContentInstallJobViewModel CreateJob(CancellationToken cancellationToken) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Target",
            "example",
            "Example",
            "version-1",
            "Version 1",
            "Queued",
            cancellationToken);
}
