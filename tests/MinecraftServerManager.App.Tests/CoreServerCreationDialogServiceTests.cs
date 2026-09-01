using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class CoreServerCreationDialogServiceTests
{
    [Fact]
    public void ServiceContract_AcceptsOwnerAndReturnsOptionalServerInstance()
    {
        var method = Assert.Single(typeof(ICoreServerCreationDialogService).GetMethods());

        Assert.Equal("ShowCreateDialog", method.Name);
        Assert.Equal(typeof(ServerInstance), Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType);
        var owner = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(Window), owner.ParameterType);
        Assert.Throws<ArgumentNullException>(() => new CoreServerCreationDialogService(null!));
    }

    [Fact]
    public void ShowCreateDialog_WithOwner_ReturnsNullWhenUserClosesDialog()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = CreateHiddenOwner();
            owner.Show();
            try
            {
                var service = new CoreServerCreationDialogService(new FakeWorkflow());
                var inspectedOwner = false;
                ScheduleDialogAction(dialog =>
                {
                    inspectedOwner = ReferenceEquals(owner, dialog.Owner);
                    dialog.Close();
                });

                var result = service.ShowCreateDialog(owner);

                Assert.True(inspectedOwner);
                Assert.Null(result);
                Assert.True(owner.IsEnabled);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public void ShowCreateDialog_ReturnsServerProducedByWorkflow()
    {
        WpfStaTestHost.Run(() =>
        {
            var core = new CoreServerProduct(
                CoreServerSoftware.CatServer,
                "catserver",
                "CatServer",
                "Hybrid Server");
            var version = new CoreServerVersion(
                core.CoreId,
                "1.12.2-latest",
                "1.12.2 Latest",
                "1.12.2",
                "latest");
            var expected = new ServerInstance
            {
                Name = "CatServer",
                DirectoryPath = "C:\\servers\\catserver",
                ServerJarPath = "server.jar"
            };
            var service = new CoreServerCreationDialogService(new FakeWorkflow
            {
                Cores = [core],
                Versions = [version],
                CreatedServer = expected
            });
            ScheduleDialogAction(dialog =>
            {
                var coreList = Assert.IsType<ListBox>(dialog.FindName("CoreList"));
                coreList.SelectedItem = Assert.Single(coreList.Items.Cast<CoreServerProduct>());
                var viewModel = Assert.IsType<CoreServerCreationViewModel>(dialog.DataContext);
                Assert.True(viewModel.RequiresMinecraftEula);
                viewModel.MinecraftEulaAccepted = true;
                var createButton = Assert.IsType<Button>(dialog.FindName("CreateServerButton"));
                Assert.True(createButton.IsEnabled);
                createButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            var result = service.ShowCreateDialog(null);

            Assert.Same(expected, result);
        });
    }

    private static Window CreateHiddenOwner()
        => new()
        {
            Width = 320,
            Height = 220,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000
        };

    private static void ScheduleDialogAction(Action<CoreServerCreationDialog> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(
            () =>
            {
                var dialog = Application.Current.Windows
                    .OfType<CoreServerCreationDialog>()
                    .Single(window => window.IsVisible);
                try
                {
                    action(dialog);
                }
                catch
                {
                    // An assertion in the scheduled modal action must not leave a visible dialog
                    // behind and poison every later WPF test in this shared process.
                    dialog.Close();
                    throw;
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    private sealed class FakeWorkflow : ICoreServerCreationWorkflow
    {
        public IReadOnlyList<CoreServerProduct> Cores { get; init; } = [];
        public IReadOnlyList<CoreServerVersion> Versions { get; init; } = [];
        public ServerInstance CreatedServer { get; init; } = new();

        public Task<IReadOnlyList<CoreServerProduct>> GetAvailableCoresAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(Cores);

        public Task<IReadOnlyList<CoreServerVersion>> GetVersionsAsync(
            CoreServerProduct core,
            CancellationToken cancellationToken)
            => Task.FromResult(Versions);

        public Task<ServerInstance> CreateAsync(
            CoreServerCreationRequest request,
            IProgress<CoreServerCreationProgress> progress,
            CancellationToken cancellationToken)
            => Task.FromResult(CreatedServer);
    }
}
