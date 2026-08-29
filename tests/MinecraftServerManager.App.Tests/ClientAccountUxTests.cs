using System.IO;
using System.Net.Http;
using System.Xml.Linq;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientAccountUxTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Controls =
        "clr-namespace:MinecraftServerManager.App.Controls";

    [Fact]
    public async Task HeaderAccountSelection_UpdatesVisiblePlayerNameAndAccessibleName()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());
        var account = new MinecraftClientAccountInfo(
            "account-id",
            "PixelPlayer",
            "00000000000000000000000000000000",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            ActiveSkin: null,
            Capes: []);

        viewModel.Accounts.Add(account);
        viewModel.SelectedAccount = account;

        Assert.Equal("PixelPlayer", viewModel.SelectedPlayerName);
        Assert.Contains("PixelPlayer", viewModel.AccountButtonAccessibleName, StringComparison.Ordinal);
        Assert.Same(account, viewModel.SelectedAccount);
    }

    [Fact]
    public async Task AddAccount_OpensStyledChoiceWithoutStartingAuthentication()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());

        Assert.False(viewModel.IsAccountPanelOpen);
        Assert.False(viewModel.IsAccountLoginChoiceOpen);

        viewModel.AddAccountCommand.Execute(null);

        Assert.True(viewModel.IsAccountPanelOpen);
        Assert.True(viewModel.IsAccountLoginChoiceOpen);
        Assert.False(viewModel.IsDeviceCodePromptVisible);
        Assert.Empty(viewModel.Accounts);
        Assert.False(viewModel.StartBrowserAccountLoginCommand.CanExecute(null));

        viewModel.MicrosoftAccountLoginHint = "player@example.com";

        Assert.True(viewModel.HasValidMicrosoftAccountLoginHint);
        Assert.True(viewModel.StartBrowserAccountLoginCommand.CanExecute(null));

        viewModel.MicrosoftAccountLoginHint = "invalid account";

        Assert.False(viewModel.HasValidMicrosoftAccountLoginHint);
        Assert.False(viewModel.StartBrowserAccountLoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task LaunchWithoutAccount_OpensChoiceAndDoesNotLaunchOrOpenBrowser()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        await using var viewModel = new ClientWorkspaceViewModel(
            new ApplicationPaths(directory.Path),
            static () => new NewMinecraftClientDefaultsSettings());
        var item = new ClientInstanceItemViewModel(new MinecraftClientInstance
        {
            Name = "Needs account",
            GameVersion = "1.21.1",
            InstalledVersionId = "1.21.1",
            DirectoryPath = directory.Path,
        });
        viewModel.SelectedInstance = item;

        viewModel.LaunchCommand.Execute(null);

        Assert.True(viewModel.IsAccountPanelOpen);
        Assert.True(viewModel.IsAccountLoginChoiceOpen);
        Assert.False(item.IsRunning);
        Assert.Equal(MinecraftClientInstanceState.Ready, item.State);
    }

    [Fact]
    public void AccountSurface_UsesExplicitSafeLoginActionsAndHasNoPasswordOrFriendsUi()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var markup = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("StartBrowserAccountLoginCommand", markup, StringComparison.Ordinal);
        Assert.Contains("StartDeviceCodeAccountLoginCommand", markup, StringComparison.Ordinal);
        Assert.Contains("CopyDeviceCodeCommand", markup, StringComparison.Ordinal);
        Assert.Contains("OpenDeviceLoginPageCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordBox", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Friend", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("好友", markup, StringComparison.Ordinal);
        var loginHint = Assert.Single(
            document.Descendants(Presentation + "TextBox"),
            element => (string?)element.Attribute("Text") ==
                       "{Binding MicrosoftAccountLoginHint, UpdateSourceTrigger=PropertyChanged}");
        Assert.Equal("254", (string?)loginHint.Attribute("MaxLength"));
        Assert.Contains("L10n.client.account.login.hint.label", markup, StringComparison.Ordinal);
        var skinViewer = Assert.Single(document.Descendants(Controls + "MinecraftSkin3DView"));
        Assert.Null(skinViewer.Attribute("Cursor"));
        Assert.Contains("SkinPreviewTextureSource", markup, StringComparison.Ordinal);
        Assert.Contains("SelectClassicSkinCommand", markup, StringComparison.Ordinal);
        Assert.Contains("SelectSlimSkinCommand", markup, StringComparison.Ordinal);
        Assert.Contains("IsSlimSkinPreview", markup, StringComparison.Ordinal);
        Assert.Contains("SaveSkinCommand", markup, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedCapeCommand", markup, StringComparison.Ordinal);
        Assert.Contains("DisableCapeCommand", markup, StringComparison.Ordinal);
        Assert.Contains("SkinEditingControls", markup, StringComparison.Ordinal);
        Assert.Contains("IsMouseOver, ElementName=SkinCard", markup, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardFocusWithin, ElementName=SkinCard", markup, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.IsTabStop=\"True\"", markup, StringComparison.Ordinal);

        var variantControls = Assert.Single(
            document.Descendants(Presentation + "StackPanel"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "SkinVariantControls"));
        var editingControls = Assert.Single(
            document.Descendants(Presentation + "Grid"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "SkinEditingControls"));
        var classicButton = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding SelectClassicSkinCommand}");
        var slimButton = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding SelectSlimSkinCommand}");
        Assert.Contains(classicButton, variantControls.Descendants());
        Assert.Contains(slimButton, variantControls.Descendants());
        Assert.DoesNotContain(classicButton, editingControls.Descendants());
        Assert.DoesNotContain(slimButton, editingControls.Descendants());
    }

    [Fact]
    public void AccountSurface_ShowsPlayerAndExpandableExpiryBeforeAccountActions()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            "Views",
            "ClientWorkspaceView.xaml"));
        var markup = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("SelectedPlayerName", markup, StringComparison.Ordinal);
        Assert.Contains("SelectedPlayerUuid", markup, StringComparison.Ordinal);
        Assert.Contains("SelectedAccountExpirySummary", markup, StringComparison.Ordinal);
        Assert.Contains("ToggleAccountExpiryCommand", markup, StringComparison.Ordinal);
        Assert.Contains("SelectedAccountCapes", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountLifecycle_RefreshesInBackgroundAndAwaitsCancellationOnDispose()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "ViewModels",
            "ClientWorkspaceViewModel.cs"));

        Assert.Contains("RefreshIfExpiringAsync", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(20)", source, StringComparison.Ordinal);
        Assert.Contains("_accountLoginCancellation?.Cancel();", source, StringComparison.Ordinal);
        Assert.Contains(".Append(_accountRefreshTask)", source, StringComparison.Ordinal);
        Assert.Contains(".Append(_accountLoginTask)", source, StringComparison.Ordinal);
        Assert.Contains("_authenticationService.RefreshProfileAsync(", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(30)", source, StringComparison.Ordinal);
        Assert.Contains("Func<MinecraftClientAccountInfo, bool> hasExpectedState", source, StringComparison.Ordinal);
        Assert.Contains("if (!hasExpectedState(refreshed))", source, StringComparison.Ordinal);
        Assert.Contains("var previousSkinId = account.ActiveSkin?.Id;", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(activeSkin.Id, previousSkinId", source, StringComparison.Ordinal);
        Assert.Contains("observerTasks.Concat(profileSynchronizationTasks)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountRefresh_TransientFailureDoesNotBlockRemainingAccounts()
    {
        var called = new List<string>();

        var changed = await ClientWorkspaceViewModel.RefreshAccountSetIndependentlyAsync(
            ["A", "B", "C"],
            (accountId, _) =>
            {
                called.Add(accountId);
                return accountId switch
                {
                    "A" => Task.FromException<bool>(new HttpRequestException("temporary")),
                    "B" => Task.FromResult(true),
                    _ => Task.FromResult(false),
                };
            },
            CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(["A", "B", "C"], called);
    }

    [Fact]
    public async Task AccountRefresh_CancellationStopsBeforeAnotherAccount()
    {
        using var cancellation = new CancellationTokenSource();
        var called = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClientWorkspaceViewModel.RefreshAccountSetIndependentlyAsync(
                ["A", "B"],
                (accountId, _) =>
                {
                    called.Add(accountId);
                    cancellation.Cancel();
                    return Task.FromCanceled<bool>(cancellation.Token);
                },
                cancellation.Token));

        Assert.Equal(["A"], called);
    }
}
