using System.Xml.Linq;
using Microsoft.Identity.Client;

namespace MinecraftServerManager.GameClient.Tests;

/// <summary>
/// Security and UI contract tests for the Microsoft account/profile surface. These tests are
/// intentionally offline: they exercise pure validators and inspect the product wiring without
/// ever opening a browser, contacting Microsoft, or requiring a real Minecraft account.
/// </summary>
public sealed class MicrosoftAccountSecurityRegressionTests
{
    private const string ValidTextureHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void OfficialTextureUri_LegacyHttpIsCanonicalizedToHttps()
    {
        var accepted = MicrosoftMinecraftAuthenticationService.TryGetOfficialTextureUri(
            $"http://textures.minecraft.net/texture/{ValidTextureHash}",
            out var textureUri);

        Assert.True(accepted);
        Assert.Equal(
            $"https://textures.minecraft.net/texture/{ValidTextureHash}",
            textureUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://textures.minecraft.net:8443/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef?download=1")]
    [InlineData("https://player@textures.minecraft.net/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net.evil.example/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://example.com/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net/textures/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/extra")]
    public void OfficialTextureUri_RejectsNonCanonicalAuthorityOrPath(string input)
    {
        var accepted = MicrosoftMinecraftAuthenticationService.TryGetOfficialTextureUri(
            input,
            out var textureUri);

        Assert.False(accepted);
        Assert.Null(textureUri);
    }

    [Fact]
    public void MsalUiRequired_IsAmbiguousAndDoesNotAuthorizeAccountDeletion()
    {
        var error = new MsalUiRequiredException(
            "interaction_required",
            "The cached session needs user interaction.");

        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(error));
    }

    [Theory]
    [InlineData("Microsoft.Identity.Client")]
    [InlineData("Microsoft.Identity.Client.Extensions.Msal")]
    public void GameClient_DirectlyPinsMsalPackagesToAuditedVersion(string packageName)
    {
        var project = XDocument.Load(RepositoryPath(
            "src",
            "MinecraftServerManager.GameClient",
            "MinecraftServerManager.GameClient.csproj"));
        var reference = project
            .Descendants("PackageReference")
            .Single(element => string.Equals(
                (string?)element.Attribute("Include"),
                packageName,
                StringComparison.Ordinal));

        Assert.Equal("4.88.0", (string?)reference.Attribute("Version"));
    }

    [Fact]
    public void SkinAndCapeMutation_IgnoreEmptySuccessBodiesAndRefetchProfileWithGet()
    {
        var source = File.ReadAllText(AuthenticationServicePath());
        var skin = Slice(
            source,
            "public async Task<MinecraftClientAccountInfo> UpdateSkinAsync",
            "public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync");
        var cape = Slice(
            source,
            "public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync",
            "private async Task<AuthenticatedMinecraftSession> AuthenticateAccountCoreAsync");
        var fetchProfile = Slice(
            source,
            "private static async Task<JEProfile> FetchProfileAsync",
            "private static async Task<JEProfile> ReadProfileResponseAsync");

        AssertMutationResponseIsNotParsed(skin, "using var response = await client.SendAsync");
        AssertMutationResponseIsNotParsed(cape, "using (var response = await client.SendAsync");

        Assert.Contains(
            "new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUri)",
            fetchProfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "return await ReadProfileResponseAsync(response, cancellationToken)",
            fetchProfile,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(source, "ReadProfileResponseAsync(response, cancellationToken)"));
    }

    [Fact]
    public void AccountXaml_UsesExplicitLoginChoicesAndNeverCollectsMicrosoftPassword()
    {
        var document = XDocument.Load(ClientWorkspaceXamlPath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(presentation + "Button").ToArray();

        Assert.Single(buttons, button => HasCommand(button, "AddAccountCommand"));
        Assert.Single(buttons, button => HasCommand(button, "StartBrowserAccountLoginCommand"));
        Assert.Single(buttons, button => HasCommand(button, "StartDeviceCodeAccountLoginCommand"));
        Assert.Empty(document.Descendants(presentation + "PasswordBox"));

        var viewModelSource = File.ReadAllText(ClientWorkspaceViewModelPath());
        var addCommandWiring = Slice(
            viewModelSource,
            "AddAccountCommand = new AsyncRelayCommand",
            "ToggleAccountPanelCommand = new RelayCommand");
        var choiceMethod = Slice(
            viewModelSource,
            "private Task OpenAccountLoginChoiceAsync()",
            "private async Task AddAccountInBrowserAsync()");

        Assert.Contains("OpenAccountLoginChoiceAsync", addCommandWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAccountInteractivelyAsync", addCommandWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", addCommandWiring, StringComparison.Ordinal);
        Assert.Contains("IsAccountLoginChoiceOpen = true;", choiceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAccountInteractivelyAsync", choiceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", choiceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountXaml_BindsClassicSlimSkinSaveCapesAndLive3dPreview()
    {
        var document = XDocument.Load(ClientWorkspaceXamlPath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace controls = "clr-namespace:MinecraftServerManager.App.Controls";
        var buttons = document.Descendants(presentation + "Button").ToArray();
        var viewer = Assert.Single(document.Descendants(controls + "MinecraftSkin3DView"));

        Assert.Equal("{Binding SelectedSkinFilePath}", (string?)viewer.Attribute("SkinPath"));
        Assert.Equal("{Binding SkinPreviewTextureSource}", (string?)viewer.Attribute("TextureSource"));
        Assert.Equal("{Binding IsSlimSkinPreview}", (string?)viewer.Attribute("IsSlim"));
        Assert.Single(buttons, button => HasCommand(button, "SelectClassicSkinCommand"));
        Assert.Single(buttons, button => HasCommand(button, "SelectSlimSkinCommand"));
        Assert.Single(buttons, button => HasCommand(button, "ChooseSkinFileCommand"));
        Assert.Single(buttons, button => HasCommand(button, "SaveSkinCommand"));
        Assert.Single(buttons, button => HasCommand(button, "ApplySelectedCapeCommand"));
        Assert.Single(buttons, button => HasCommand(button, "DisableCapeCommand"));

        var source = File.ReadAllText(ClientWorkspaceViewModelPath());
        var commandWiring = Slice(
            source,
            "SelectClassicSkinCommand = new RelayCommand",
            "RemoveSelectedAccountCommand = new AsyncRelayCommand");
        var saveSkin = Slice(
            source,
            "private async Task SaveSkinAsync()",
            "private async Task ApplySelectedCapeAsync()");
        var applyCape = Slice(
            source,
            "private async Task ApplySelectedCapeAsync()",
            "private async Task DisableCapeAsync()");
        var disableCape = Slice(
            source,
            "private async Task DisableCapeAsync()",
            "private void NotifyAccountCosmeticStateChanged()");

        Assert.Contains(
            "SkinPreviewVariant = MinecraftClientSkinVariant.Classic",
            commandWiring,
            StringComparison.Ordinal);
        Assert.Contains(
            "SkinPreviewVariant = MinecraftClientSkinVariant.Slim",
            commandWiring,
            StringComparison.Ordinal);
        Assert.Contains("_authenticationService.UpdateSkinAsync(", saveSkin, StringComparison.Ordinal);
        Assert.Contains("_authenticationService.SetActiveCapeAsync(", applyCape, StringComparison.Ordinal);
        Assert.Contains("cape.Id", applyCape, StringComparison.Ordinal);
        Assert.Contains("_authenticationService.SetActiveCapeAsync(", disableCape, StringComparison.Ordinal);
        Assert.Contains("capeId: null", disableCape, StringComparison.Ordinal);
    }

    private static bool HasCommand(XElement element, string commandName) =>
        string.Equals(
            (string?)element.Attribute("Command"),
            $"{{Binding {commandName}}}",
            StringComparison.Ordinal);

    private static void AssertMutationResponseIsNotParsed(string method, string sendMarker)
    {
        var send = method.IndexOf(sendMarker, StringComparison.Ordinal);
        var refetch = method.IndexOf("var profile = await FetchVerifiedProfileAfterAcceptedMutationAsync", send, StringComparison.Ordinal);
        Assert.True(send >= 0, $"Missing mutation send marker: {sendMarker}");
        Assert.True(refetch > send, "The mutation must be followed by an authoritative profile refetch.");

        var mutationResponseHandling = method[send..refetch];
        Assert.Contains("response.IsSuccessStatusCode", mutationResponseHandling, StringComparison.Ordinal);
        Assert.DoesNotContain("response.Content", mutationResponseHandling, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadProfileResponseAsync", mutationResponseHandling, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonDocument", mutationResponseHandling, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string AuthenticationServicePath() => RepositoryPath(
        "src",
        "MinecraftServerManager.GameClient",
        "MicrosoftMinecraftAuthenticationService.cs");

    private static string ClientWorkspaceXamlPath() => RepositoryPath(
        "src",
        "MinecraftServerManager.App",
        "Views",
        "ClientWorkspaceView.xaml");

    private static string ClientWorkspaceViewModelPath() => RepositoryPath(
        "src",
        "MinecraftServerManager.App",
        "ViewModels",
        "ClientWorkspaceViewModel.cs");

    private static string RepositoryPath(params string[] segments) =>
        Path.Combine([FindRepositoryRoot(), .. segments]);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MinecraftServerManager.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
