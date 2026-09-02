using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductServiceCompatibilityRepairViewModelTests
{
    [Fact]
    public async Task Api15_ShowsRepairActionButKeepsEveryCreateAndImportEntryPointLocked()
    {
        using var temporary = new TemporaryDirectory();
        var client = new TransitioningServiceClient(new ProductApiVersion(1, 5));
        var launcher = new FakeLauncher(
            new BundledProductServiceUpdateResult(BundledProductServiceUpdateOutcome.UpdateFailed, 1));
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            new ApplicationPaths(temporary.Path),
            client,
            productServiceUpdateLauncher: launcher);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.False(viewModel.IsProductServiceConnected);
        Assert.Null(viewModel.ProductServiceNegotiatedApiVersion);
        Assert.True(viewModel.ShowProductServiceUpdateAction);
        Assert.True(viewModel.UpdateProductServiceCommand.CanExecute(null));
        AssertCreateAndImportCommands(viewModel, expected: false);
    }

    [Fact]
    public async Task SuccessfulRepair_ReprobesApi16AndRestoresCreateAndImportCommands()
    {
        using var temporary = new TemporaryDirectory();
        var client = new TransitioningServiceClient(new ProductApiVersion(1, 5));
        var launcher = new FakeLauncher(() =>
        {
            client.MaximumVersion = ProductApiProtocol.CurrentVersion;
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.Completed,
                0);
        });
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            new ApplicationPaths(temporary.Path),
            client,
            productServiceUpdateLauncher: launcher);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.UpdateProductServiceAsync();

        Assert.Equal(1, launcher.InvocationCount);
        Assert.True(viewModel.IsProductServiceConnected);
        Assert.Equal(ProductApiProtocol.CurrentVersion, viewModel.ProductServiceNegotiatedApiVersion);
        Assert.False(viewModel.ShowProductServiceUpdateAction);
        Assert.False(viewModel.UpdateProductServiceCommand.CanExecute(null));
        Assert.False(viewModel.IsProductServiceUpdateRunning);
        AssertCreateAndImportCommands(viewModel, expected: true);
    }

    [Fact]
    public async Task FailedRepair_LeavesApi15ReadOnlyAndDoesNotClaimRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var client = new TransitioningServiceClient(new ProductApiVersion(1, 5));
        var launcher = new FakeLauncher(
            new BundledProductServiceUpdateResult(BundledProductServiceUpdateOutcome.UpdateFailed, 19));
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            new ApplicationPaths(temporary.Path),
            client,
            productServiceUpdateLauncher: launcher);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        await viewModel.UpdateProductServiceAsync();

        Assert.Equal(1, launcher.InvocationCount);
        Assert.False(viewModel.IsProductServiceConnected);
        Assert.Null(viewModel.ProductServiceNegotiatedApiVersion);
        Assert.True(viewModel.ShowProductServiceUpdateAction);
        Assert.True(viewModel.UpdateProductServiceCommand.CanExecute(null));
        Assert.False(viewModel.IsProductServiceUpdateRunning);
        AssertCreateAndImportCommands(viewModel, expected: false);
    }

    [Fact]
    public async Task RolledBackRepair_ReprobesRestoredServiceBeforePublishingFailureState()
    {
        using var temporary = new TemporaryDirectory();
        var client = new TransitioningServiceClient(new ProductApiVersion(1, 5));
        var launcher = new FakeLauncher(() =>
        {
            client.MaximumVersion = ProductApiProtocol.CurrentVersion;
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.RolledBack,
                10);
        });
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(
            new ApplicationPaths(temporary.Path),
            client,
            productServiceUpdateLauncher: launcher);
        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);

        Assert.False(viewModel.IsProductServiceConnected);

        await viewModel.UpdateProductServiceAsync();

        Assert.Equal(1, launcher.InvocationCount);
        Assert.True(viewModel.IsProductServiceConnected);
        Assert.Equal(ProductApiProtocol.CurrentVersion, viewModel.ProductServiceNegotiatedApiVersion);
        Assert.False(viewModel.ShowProductServiceUpdateAction);
        Assert.False(viewModel.IsProductServiceUpdateRunning);
        AssertCreateAndImportCommands(viewModel, expected: true);
    }

    private static void AssertCreateAndImportCommands(
        MainWindowViewModel viewModel,
        bool expected)
    {
        Assert.Equal(expected, viewModel.ImportExistingServerCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.ImportServerCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.ImportServerFolderCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.CreateCoreServerCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.InstallOnlineModpackCommand.CanExecute(null));
    }

    private sealed class FakeLauncher : IBundledProductServiceUpdateLauncher
    {
        private readonly Func<BundledProductServiceUpdateResult> _result;

        public FakeLauncher(BundledProductServiceUpdateResult result)
            : this(() => result)
        {
        }

        public FakeLauncher(Func<BundledProductServiceUpdateResult> result)
        {
            _result = result;
        }

        public int InvocationCount { get; private set; }

        public Task<BundledProductServiceUpdateResult> UpdateAsync(
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(_result());
        }
    }

    private sealed class TransitioningServiceClient(ProductApiVersion maximumVersion)
        : IProductServiceClient
    {
        public ProductApiVersion MaximumVersion { get; set; } = maximumVersion;

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductLocalHandshakePayload(
                new ProductHandshakeResponse(
                    "Muhun MCSV Manager",
                    "1.0.8",
                    MaximumVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    Ready: true),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerSummary>>([]);

        public Task<IReadOnlyList<ProductServerStatus>> ListStatusesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerStatus>>([]);

        public Task<ProductServerStatus> GetStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerMutationResult> StopAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerMutationResult> RestartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid serverId,
            long afterCursor,
            int limit = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProductServerStatus> SendCommandAsync(
            Guid serverId,
            string command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mcsv-service-compatibility-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup on Windows CI.
            }
        }
    }
}
