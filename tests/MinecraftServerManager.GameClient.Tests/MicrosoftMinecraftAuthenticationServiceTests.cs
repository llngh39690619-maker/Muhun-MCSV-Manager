using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using MinecraftServerManager.GameClient.Contracts;
using System.Net;
using System.Text;
using System.Text.Json;
using XboxAuthNet.OAuth;
using XboxAuthNet.XboxLive;
using XboxAuthNet.Game.Accounts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MicrosoftMinecraftAuthenticationServiceTests
{
    [Fact]
    public void DeferredManager_SuppressesPipelineSaveUntilExplicitCommit()
    {
        var inner = new TrackingAccountManager();
        var manager = new DeferredSaveXboxGameAccountManager(inner);

        manager.SaveAccounts();

        Assert.Equal(0, inner.SaveCount);

        manager.CommitAccounts();

        Assert.Equal(1, inner.SaveCount);
    }

    [Fact]
    public void AuthenticationSource_ValidatesOwnedProfileBeforeCommittingAndRollsBackFailures()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));

        const string validation =
            "var authenticated = ConvertSession(RequireAccountIdentifier(account), session);";
        var validationIndex = source.IndexOf(validation, StringComparison.Ordinal);
        Assert.True(validationIndex >= 0);
        var commitIndex = source.IndexOf(
            "_accountManager.CommitAccounts();",
            validationIndex,
            StringComparison.Ordinal);
        Assert.True(commitIndex > validationIndex);
        Assert.DoesNotContain("_accountManager.SaveAccounts();", source, StringComparison.Ordinal);
        Assert.Contains("ReloadPersistedAuthenticationState();", source, StringComparison.Ordinal);
        Assert.Contains("IsAuthoritativeAccountFailure(error)", source, StringComparison.Ordinal);
        Assert.Contains("await RemoveFailedAccountAsync(account)", source, StringComparison.Ordinal);
        Assert.Contains("await _loginHandler.Signout(account, CancellationToken.None)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureClassification_RetainsAccountForTransientAndAmbiguousFailures()
    {
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new OperationCanceledException()));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new TimeoutException()));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new HttpRequestException("temporary DNS or transport failure")));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new JEAuthException("server error", "upstream", "temporary", 503)));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new JEAuthException("The user doesn't own the game.")));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new MicrosoftOAuthException("temporarily_unavailable", "retry", [], 503)));
    }

    [Fact]
    public void FailureClassification_RemovesOnlyAuthoritativeCredentialOrProfileFailures()
    {
        Assert.True(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new MicrosoftOAuthException("invalid_grant", "refresh token revoked", [], 400)));
        Assert.True(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new JEAuthException("Unauthorized", "invalid_token", "expired", 401)));
        Assert.True(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new XboxAuthException("Unauthorized", 401)));
        Assert.True(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(
            new MinecraftAccountValidationException("owned Java profile is invalid")));
    }

    [Fact]
    public void FailureClassification_TransientInnerFailureWinsOverOuterAuthorizationError()
    {
        var failure = new JEAuthException(
            "Authentication failed",
            new HttpRequestException("connection reset"));

        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(failure));
    }

    [Fact]
    public void FailureClassification_DoesNotDeleteAccountForGenericMsalInteractionRequirement()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));
        var classifier = SliceMethod(
            source,
            "internal static bool IsAuthoritativeAccountFailure",
            "private static bool IsTransientAuthenticationFailure");

        Assert.DoesNotContain("MsalUiRequiredException => true", classifier, StringComparison.Ordinal);
        Assert.Contains("invalid_grant", classifier, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedSession_DoesNotExposeAccessTokenThroughPublicSurface()
    {
        var publicProperties = typeof(AuthenticatedMinecraftSession)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();
        var session = new AuthenticatedMinecraftSession(
            "account",
            "Player",
            "1234567890abcdef1234567890abcdef",
            "secret-access-token-value");

        Assert.DoesNotContain("AccessToken", publicProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("secret-access-token-value", session.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileMutation_RefetchesAndVerifiesOfficialProfileBeforeCommit()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));
        var skinMethod = SliceMethod(source, "public async Task<MinecraftClientAccountInfo> UpdateSkinAsync", "public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync");
        var capeMethod = SliceMethod(source, "public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync", "private async Task<AuthenticatedMinecraftSession> AuthenticateAccountCoreAsync");

        Assert.Contains("FetchVerifiedProfileAfterAcceptedMutationAsync(", skinMethod, StringComparison.Ordinal);
        Assert.Contains(
            "var expectedState = (JEProfile candidate) => HasExpectedActiveSkinState(",
            skinMethod,
            StringComparison.Ordinal);
        Assert.Contains("uploadedBytesDiffer", skinMethod, StringComparison.Ordinal);
        Assert.Contains("_pendingProfileExpectations[accountId] = expectedState;", skinMethod, StringComparison.Ordinal);
        Assert.Contains("StoreVerifiedProfile(account, profile);", skinMethod, StringComparison.Ordinal);
        var skinStore = skinMethod.IndexOf("StoreVerifiedProfile(account, profile);", StringComparison.Ordinal);
        Assert.True(
            skinMethod.IndexOf("_accountManager.CommitAccounts();", skinStore, StringComparison.Ordinal) >
            skinStore);
        Assert.Contains("FetchVerifiedProfileAfterAcceptedMutationAsync(", capeMethod, StringComparison.Ordinal);
        Assert.Contains(
            "var expectedState = (JEProfile candidate) => HasExpectedActiveCape(candidate, capeId)",
            capeMethod,
            StringComparison.Ordinal);
        Assert.Contains("_pendingProfileExpectations[accountId] = expectedState;", capeMethod, StringComparison.Ordinal);
        Assert.Contains("!currentProfile.Capes.Any", capeMethod, StringComparison.Ordinal);
        Assert.Contains("StoreVerifiedProfile(account, profile);", capeMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void VariantOnlyMutation_FetchesLatestOfficialProfileBeforeDownloadingSkin()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));
        var skinMethod = SliceMethod(
            source,
            "public async Task<MinecraftClientAccountInfo> UpdateSkinAsync",
            "public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync");

        var fetch = skinMethod.IndexOf("var currentProfile = await FetchProfileAsync(", StringComparison.Ordinal);
        var activeSkin = skinMethod.IndexOf(
            "var currentActiveSkin = GetSingleActiveSkin(currentProfile)",
            StringComparison.Ordinal);
        var download = skinMethod.IndexOf("var currentImage = await DownloadCurrentSkinAsync(", StringComparison.Ordinal);
        var comparison = skinMethod.IndexOf("SequenceEqual(currentImage)", StringComparison.Ordinal);
        var mutation = skinMethod.IndexOf(
            "new HttpRequestMessage(HttpMethod.Post, MinecraftSkinsUri)",
            StringComparison.Ordinal);

        Assert.True(fetch >= 0);
        Assert.True(activeSkin > fetch);
        Assert.True(download > activeSkin);
        Assert.True(comparison > download);
        Assert.True(mutation > comparison);
        Assert.DoesNotContain(
            "DownloadCurrentSkinAsync(client, account",
            skinMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedProfileState_RequiresExactActiveSkinVariantAndCape()
    {
        var classicWithCape = ParseProfile("CLASSIC", activeCapeId: "cape-a");
        var slimWithoutCape = ParseProfile("SLIM", activeCapeId: null);

        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinVariant(
            classicWithCape,
            MinecraftClientSkinVariant.Classic));
        Assert.False(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinVariant(
            classicWithCape,
            MinecraftClientSkinVariant.Slim));
        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveCape(
            classicWithCape,
            "cape-a"));
        Assert.False(MicrosoftMinecraftAuthenticationService.HasExpectedActiveCape(
            classicWithCape,
            "cape-b"));
        Assert.False(MicrosoftMinecraftAuthenticationService.HasExpectedActiveCape(
            classicWithCape,
            capeId: null));

        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinVariant(
            slimWithoutCape,
            MinecraftClientSkinVariant.Slim));
        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveCape(
            slimWithoutCape,
            capeId: null));
    }

    [Fact]
    public void UploadedSkinExpectation_RequiresIdentityChangeOnlyWhenBytesDiffer()
    {
        var previousUri = new Uri(
            "https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef");
        var unchanged = ParseProfile(
            "SLIM",
            activeCapeId: null,
            skinId: "skin-id",
            textureHash: "0123456789abcdef0123456789abcdef");
        var changedId = ParseProfile(
            "SLIM",
            activeCapeId: null,
            skinId: "new-skin-id",
            textureHash: "0123456789abcdef0123456789abcdef");
        var changedUrl = ParseProfile(
            "SLIM",
            activeCapeId: null,
            skinId: "skin-id",
            textureHash: "abcdefabcdefabcdefabcdefabcdefab");

        Assert.False(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinState(
            unchanged,
            MinecraftClientSkinVariant.Slim,
            "skin-id",
            previousUri,
            requireIdentityChange: true));
        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinState(
            changedId,
            MinecraftClientSkinVariant.Slim,
            "skin-id",
            previousUri,
            requireIdentityChange: true));
        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinState(
            changedUrl,
            MinecraftClientSkinVariant.Slim,
            "skin-id",
            previousUri,
            requireIdentityChange: true));
        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinState(
            unchanged,
            MinecraftClientSkinVariant.Slim,
            "skin-id",
            previousUri,
            requireIdentityChange: false));
        Assert.False(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinState(
            changedUrl,
            MinecraftClientSkinVariant.Classic,
            "skin-id",
            previousUri,
            requireIdentityChange: true));
    }

    [Fact]
    public async Task AcceptedMutation_ContinuesGetRetryWhenValidProfileStillHasOldState()
    {
        var handler = new SequencedProfileHandler(
            ProfileJson("CLASSIC", activeCapeId: null),
            ProfileJson("SLIM", activeCapeId: null));
        using var client = new HttpClient(handler);

        var profile = await MicrosoftMinecraftAuthenticationService.FetchProfileUntilExpectedStateAsync(
            client,
            "minecraft-access-token",
            candidate => MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinVariant(
                candidate,
                MinecraftClientSkinVariant.Slim),
            CancellationToken.None);

        Assert.True(MicrosoftMinecraftAuthenticationService.HasExpectedActiveSkinVariant(
            profile,
            MinecraftClientSkinVariant.Slim));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://api.minecraftservices.com/minecraft/profile",
                request.Uri.AbsoluteUri);
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal("minecraft-access-token", request.AuthorizationParameter);
        });
    }

    [Fact]
    public void AcceptedProfileMutation_UsesBoundedVerifiedReadRetryWithoutResendingMutation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));
        var retry = SliceMethod(
            source,
            "private static async Task<JEProfile> FetchVerifiedProfileAfterAcceptedMutationAsync",
            "private static bool IsProfileSynchronizationRetryable");

        Assert.Contains("ProfileSynchronizationRetryDelays", retry, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(", retry, StringComparison.Ordinal);
        Assert.Contains("FetchProfileAsync(client, accessToken", retry, StringComparison.Ordinal);
        Assert.Contains("EnsureProfileMatchesAccount(account, profile);", retry, StringComparison.Ordinal);
        Assert.Contains("hasExpectedState(profile)", retry, StringComparison.Ordinal);
        Assert.Contains("MinecraftProfileSynchronizationPendingException", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Post", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Put", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Delete", retry, StringComparison.Ordinal);

        var pending = new MinecraftProfileSynchronizationPendingException(
            "accepted",
            new MinecraftAccountValidationException("profile has not propagated"));
        Assert.False(MicrosoftMinecraftAuthenticationService.IsAuthoritativeAccountFailure(pending));
    }

    [Fact]
    public void DeviceCodeFlow_OnlyPublishesSafePromptAndUsesOfficialHttpsPage()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));

        Assert.Contains("new MinecraftDeviceCodePrompt(verificationUri, userCode, expiresAtUtc)", source, StringComparison.Ordinal);
        Assert.Contains("verificationUri.Scheme != Uri.UriSchemeHttps", source, StringComparison.Ordinal);
        Assert.Contains("\"microsoft.com\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", typeof(MinecraftDeviceCodePrompt).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void BrowserLoginHint_IsNormalizedAndValidatedWithoutAcceptingPasswordLikeWhitespace()
    {
        Assert.Equal(
            "player@example.com",
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint(
                "  player@example.com  "));
        Assert.Equal(
            "+886912345678",
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint(
                "+886912345678"));

        Assert.Throws<ArgumentException>(() =>
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint(null));
        Assert.Throws<ArgumentException>(() =>
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint(""));
        Assert.Throws<ArgumentException>(() =>
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint("player account"));
        Assert.Throws<ArgumentException>(() =>
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint("player@example.com\r\nsecret"));
        Assert.Throws<ArgumentException>(() =>
            MicrosoftMinecraftAuthenticationService.NormalizeMicrosoftLoginHint(
                new string('a', MicrosoftMinecraftAuthenticationService.MaximumMicrosoftLoginHintLength + 1)));
    }

    [Fact]
    public void BrowserLoginHint_UsesOfficialSystemBrowserWhileDeviceCodeRemainsSeparate()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));

        Assert.Contains(".WithUseEmbeddedWebView(false)", source, StringComparison.Ordinal);
        Assert.Contains(".WithLoginHint(loginHint)", source, StringComparison.Ordinal);
        Assert.Contains("builder.DeviceCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordBox", source, StringComparison.Ordinal);

        var overloads = typeof(IMinecraftAccountAuthenticationService)
            .GetMethods()
            .Where(method => method.Name == nameof(
                IMinecraftAccountAuthenticationService.AddAccountInteractivelyAsync))
            .ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.Contains(overloads, method => method.GetParameters() is [{ ParameterType: var type }]
            && type == typeof(CancellationToken));
        Assert.Contains(overloads, method => method.GetParameters() is
            [{ ParameterType: var hintType }, { ParameterType: var tokenType }]
            && hintType == typeof(string)
            && tokenType == typeof(CancellationToken));
    }

    [Fact]
    public void OfficialTextureValidation_UpgradesLegacyMinecraftHttpUrlsToHttps()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MinecraftServerManager.GameClient",
            "MicrosoftMinecraftAuthenticationService.cs"));

        Assert.Contains("candidate.Scheme != Uri.UriSchemeHttp", source, StringComparison.Ordinal);
        Assert.Contains("Scheme = Uri.UriSchemeHttps", source, StringComparison.Ordinal);
        Assert.Contains("\"textures.minecraft.net\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true, "https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef", true, "https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef")]
    [InlineData("https://textures.minecraft.net:8443/texture/0123456789abcdef0123456789abcdef", false, null)]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef?token=no", false, null)]
    [InlineData("https://player@textures.minecraft.net/texture/0123456789abcdef0123456789abcdef", false, null)]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef/extra", false, null)]
    [InlineData("https://textures.minecraft.net/texture/0123456789abcdef0123456789abcdeg", false, null)]
    public void OfficialTextureValidation_RejectsUnexpectedAuthorityAndPath(
        string input,
        bool expected,
        string? expectedUri)
    {
        var accepted = MicrosoftMinecraftAuthenticationService.TryGetOfficialTextureUri(
            input,
            out var textureUri);

        Assert.Equal(expected, accepted);
        Assert.Equal(expectedUri, textureUri?.AbsoluteUri);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static JEProfile ParseProfile(
        string variant,
        string? activeCapeId,
        string skinId = "skin-id",
        string textureHash = "0123456789abcdef0123456789abcdef")
    {
        using var document = JsonDocument.Parse(ProfileJson(
            variant,
            activeCapeId,
            skinId,
            textureHash));
        return JEProfile.ParseFromJson(document.RootElement);
    }

    private static string ProfileJson(
        string variant,
        string? activeCapeId,
        string skinId = "skin-id",
        string textureHash = "0123456789abcdef0123456789abcdef")
    {
        var cape = activeCapeId is null
            ? "[]"
            : $$"""
                [{"id":"{{activeCapeId}}","state":"ACTIVE","url":"https://textures.minecraft.net/texture/abcdefabcdefabcdefabcdefabcdefab","alias":"Test cape"}]
                """;
        return $$"""
            {
              "id": "0123456789abcdef0123456789abcdef",
              "name": "Player",
              "skins": [
                {
                  "id": "{{skinId}}",
                  "state": "ACTIVE",
                  "url": "https://textures.minecraft.net/texture/{{textureHash}}",
                  "textureKey": "texture-key",
                  "variant": "{{variant}}"
                }
              ],
              "capes": {{cape}}
            }
            """;
    }

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

    private sealed class TrackingAccountManager : IXboxGameAccountManager
    {
        public int SaveCount { get; private set; }

        public XboxGameAccountCollection GetAccounts() =>
            XboxGameAccountCollection.FromAccounts([]);

        public IXboxGameAccount GetDefaultAccount() =>
            throw new InvalidOperationException("No test account is available.");

        public IXboxGameAccount NewAccount() =>
            throw new InvalidOperationException("No test account is available.");

        public void ClearAccounts()
        {
        }

        public void SaveAccounts() => SaveCount++;
    }

    private sealed class SequencedProfileHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Missing request URI."),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted profile response remains.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
