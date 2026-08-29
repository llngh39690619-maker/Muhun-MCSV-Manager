using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Microsoft.Identity.Client;
using MinecraftServerManager.GameClient.Contracts;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth;
using XboxAuthNet.XboxLive;
using XboxAuthNet.Game.Accounts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Performs Microsoft OAuth, Xbox authentication, Minecraft ownership and profile validation.
/// Passwords are never requested; refresh-token state is persisted only through CurrentUser DPAPI.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrosoftMinecraftAuthenticationService : IMinecraftAccountAuthenticationService
{
    // Public-client registration used by the upstream XboxAuthNet.Game.Msal reference flow.
    // A public client id is an identifier, not a secret; no client secret is shipped or needed.
    // Formal distribution must replace this with an X MCSV-owned Entra public-client
    // registration once its redirect URI and publisher verification have been provisioned.
    private const string MsalPublicClientId = "499c8d36-be2a-4231-9ebd-ef291b7bb64c";
    internal const int MaximumMicrosoftLoginHintLength = 254;
    private const int MaximumSkinBytes = 1024 * 1024;
    private static readonly TimeSpan[] ProfileSynchronizationRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
    ];
    private static readonly Uri MinecraftProfileUri =
        new("https://api.minecraftservices.com/minecraft/profile");
    private static readonly Uri MinecraftSkinsUri =
        new("https://api.minecraftservices.com/minecraft/profile/skins");
    private static readonly Uri MinecraftActiveCapeUri =
        new("https://api.minecraftservices.com/minecraft/profile/capes/active");
    private readonly DpapiXboxAccountJsonStorage _storage;
    private readonly HttpClient? _httpClient;
    private readonly Lazy<Task<IPublicClientApplication>> _msalApplication;
    private readonly Dictionary<string, Func<JEProfile, bool>> _pendingProfileExpectations =
        new(StringComparer.Ordinal);
    private DeferredSaveXboxGameAccountManager _accountManager = null!;
    private JELoginHandler _loginHandler = null!;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);

    public MicrosoftMinecraftAuthenticationService(
        string protectedAccountVaultPath,
        Guid installationId,
        HttpClient? httpClient = null)
    {
        var normalizedVaultPath = Path.GetFullPath(protectedAccountVaultPath);
        _storage = new DpapiXboxAccountJsonStorage(normalizedVaultPath, installationId);
        _httpClient = httpClient;
        var cacheDirectory = Path.GetDirectoryName(normalizedVaultPath)
            ?? throw new ArgumentException("Account vault path has no parent directory.", nameof(protectedAccountVaultPath));
        _msalApplication = new Lazy<Task<IPublicClientApplication>>(
            () => MsalClientHelper.BuildApplicationWithCache(
                MsalPublicClientId,
                new MsalCacheSettings
                {
                    CacheDir = cacheDirectory,
                    CacheFileName = "microsoft-msal-cache.v1.bin",
                    KeyChainServiceName = $"x-mcsv-{installationId:N}",
                    KeyChainAccountName = "minecraft-account-cache",
                }),
            LazyThreadSafetyMode.ExecutionAndPublication);
        ReloadPersistedAuthenticationState();
    }

    private void ReloadPersistedAuthenticationState()
    {
        _accountManager = new DeferredSaveXboxGameAccountManager(
            new JsonXboxGameAccountManager(
                _storage,
                JEGameAccount.FromSessionStorage,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var builder = new JELoginHandlerBuilder
        {
            AccountManager = _accountManager,
        };
        if (_httpClient is not null)
        {
            builder.HttpClient = _httpClient;
        }

        _loginHandler = builder.Build();
    }

    public IReadOnlyList<MinecraftClientAccountInfo> GetAccounts() =>
        _accountManager.GetAccounts()
            .OfType<JEGameAccount>()
            .Where(account => !string.IsNullOrWhiteSpace(account.Identifier) &&
                              account.Profile is not null &&
                              !string.IsNullOrWhiteSpace(account.Profile.UUID) &&
                              !string.IsNullOrWhiteSpace(account.Profile.Username))
            .Select(account =>
            {
                var profile = account.Profile!;
                return ToAccountInfo(account);
            })
            .OrderByDescending(account => account.LastAuthenticatedAtUtc)
            .ToArray();

    public async Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        CancellationToken cancellationToken = default)
        => await AddMsalAccountAsync(
            loginHint: null,
            deviceCodeCallback: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        string loginHint,
        CancellationToken cancellationToken = default)
        => await AddMsalAccountAsync(
            NormalizeMicrosoftLoginHint(loginHint),
            deviceCodeCallback: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<AuthenticatedMinecraftSession> AddAccountWithDeviceCodeAsync(
        Func<MinecraftDeviceCodePrompt, Task> promptCallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promptCallback);
        return await AddMsalAccountAsync(
                loginHint: null,
                promptCallback,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AuthenticatedMinecraftSession> AddMsalAccountAsync(
        string? loginHint,
        Func<MinecraftDeviceCodePrompt, Task>? deviceCodeCallback,
        CancellationToken cancellationToken)
    {
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = _accountManager.NewAccount();
            try
            {
                var msal = await _msalApplication.Value.ConfigureAwait(false);
                var authenticator = _loginHandler.CreateAuthenticator(account, cancellationToken);
                if (deviceCodeCallback is null)
                {
                    // MSAL's system-browser mode keeps password entry exclusively on Microsoft's
                    // page. The in-app account text is only a non-secret login hint; X MCSV never
                    // hosts a password box or receives the password.
                    authenticator.AddMsalOAuth(msal, builder => loginHint is null
                        ? builder.SystemBrowser()
                        : builder.Interactive(interactive => interactive
                            .WithUseEmbeddedWebView(false)
                            .WithLoginHint(loginHint)));
                }
                else
                {
                    authenticator.AddMsalOAuth(msal, builder => builder.DeviceCode(async result =>
                    {
                        var prompt = CreateDeviceCodePrompt(result);
                        await deviceCodeCallback(prompt).ConfigureAwait(false);
                    }));
                }

                authenticator.AddForceXboxAuthForJE(builder => builder.Basic());
                authenticator.AddForceJEAuthenticator(builder =>
                    builder.WithGameOwnershipChecker().Build());
                var session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);
                var authenticated = ConvertSession(RequireAccountIdentifier(account), session);
                _accountManager.CommitAccounts();
                return authenticated;
            }
            catch
            {
                // The upstream authentication pipeline attempts an automatic SaveAccounts at
                // the end of its chain. DeferredSaveXboxGameAccountManager suppresses that
                // write, so reloading here drops every partially authenticated new account.
                ReloadPersistedAuthenticationState();
                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    internal static string NormalizeMicrosoftLoginHint(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumMicrosoftLoginHintLength ||
            normalized.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "Enter a valid Microsoft account identifier without whitespace or control characters.",
                nameof(value));
        }

        return normalized;
    }

    public async Task<AuthenticatedMinecraftSession> AuthenticateAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = FindAccount(accountId);
            try
            {
                return await AuthenticateAccountCoreAsync(
                    account,
                    forceRefresh: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (IsAuthoritativeAccountFailure(error))
                {
                    await RemoveFailedAccountAsync(account).ConfigureAwait(false);
                }
                else
                {
                    // Authentication pipelines can update an in-memory refresh/session value
                    // before a later network request fails. Restore the last committed DPAPI
                    // state, but never sign out a usable account because of cancellation,
                    // timeout, DNS, transport or ambiguous upstream failures.
                    ReloadPersistedAuthenticationState();
                }

                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task<bool> RefreshIfExpiringAsync(
        string accountId,
        TimeSpan renewalWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (renewalWindow < TimeSpan.Zero || renewalWindow > TimeSpan.FromHours(12))
        {
            throw new ArgumentOutOfRangeException(nameof(renewalWindow));
        }

        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = FindAccount(accountId);
            var expiresAtUtc = GetAuthenticationExpiry(account);
            if (expiresAtUtc is not null &&
                expiresAtUtc > DateTimeOffset.UtcNow.Add(renewalWindow))
            {
                return false;
            }

            try
            {
                await AuthenticateAccountCoreAsync(
                    account,
                    forceRefresh: true,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception error)
            {
                if (IsAuthoritativeAccountFailure(error))
                {
                    await RemoveFailedAccountAsync(account).ConfigureAwait(false);
                }
                else
                {
                    ReloadPersistedAuthenticationState();
                }

                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task<MinecraftClientAccountInfo> RefreshProfileAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = FindAccount(accountId);
            AuthenticatedMinecraftSession session;
            try
            {
                session = await AuthenticateAccountCoreAsync(
                    account,
                    forceRefresh: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (IsAuthoritativeAccountFailure(error))
                {
                    await RemoveFailedAccountAsync(account).ConfigureAwait(false);
                }
                else
                {
                    ReloadPersistedAuthenticationState();
                }

                throw;
            }

            try
            {
                var profile = await FetchProfileAsync(
                        RequireApiHttpClient(),
                        session.AccessToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureProfileMatchesAccount(account, profile);
                if (_pendingProfileExpectations.TryGetValue(accountId, out var expectedState) &&
                    !expectedState(profile))
                {
                    throw new MinecraftProfileSynchronizationPendingException(
                        "Minecraft Services still reports the previous player-profile state.");
                }

                StoreVerifiedProfile(account, profile);
                _accountManager.CommitAccounts();
                _pendingProfileExpectations.Remove(accountId);
                return ToAccountInfo((JEGameAccount)account);
            }
            catch
            {
                // A read failure after an accepted mutation is not proof of revocation. Keep the
                // last verified profile and let the bounded UI resynchronization schedule retry.
                ReloadPersistedAuthenticationState();
                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task SignOutAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var account = FindAccount(accountId);
                await RemoveMsalCacheAccountAsync(account).ConfigureAwait(false);
                await _loginHandler.Signout(account, cancellationToken).ConfigureAwait(false);
                _accountManager.CommitAccounts();
                _pendingProfileExpectations.Remove(accountId);
            }
            catch
            {
                ReloadPersistedAuthenticationState();
                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task SignOutAllAsync(CancellationToken cancellationToken = default)
    {
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (_msalApplication.IsValueCreated)
                {
                    var msal = await _msalApplication.Value.ConfigureAwait(false);
                    foreach (var cachedAccount in (await msal.GetAccountsAsync().ConfigureAwait(false)).ToArray())
                    {
                        await msal.RemoveAsync(cachedAccount).ConfigureAwait(false);
                    }
                }

                foreach (var account in _accountManager.GetAccounts().ToArray())
                {
                    await _loginHandler.Signout(account, cancellationToken).ConfigureAwait(false);
                }

                _accountManager.ClearAccounts();
                _accountManager.CommitAccounts();
                _pendingProfileExpectations.Clear();
            }
            catch
            {
                ReloadPersistedAuthenticationState();
                throw;
            }
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task<MinecraftClientAccountInfo> UpdateSkinAsync(
        string accountId,
        MinecraftClientSkinVariant variant,
        string? pngFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (!Enum.IsDefined(variant))
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }

        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = RequireApiHttpClient();
            var account = FindAccount(accountId);
            var session = await AuthenticateAccountCoreAsync(
                account,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
            // Always snapshot the latest official profile and texture before changing anything.
            // This prevents a variant-only save from restoring an older locally cached skin and
            // gives an uploaded PNG a concrete pre-mutation identity to verify against.
            var currentProfile = await FetchProfileAsync(
                    client,
                    session.AccessToken,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureProfileMatchesAccount(account, currentProfile);
            var currentActiveSkin = GetSingleActiveSkin(currentProfile)
                ?? throw new InvalidOperationException("The current official skin is unavailable.");
            var currentSkinTextureUri = GetOfficialSkinTextureUri(currentActiveSkin);
            var currentImage = await DownloadCurrentSkinAsync(
                    client,
                    currentActiveSkin,
                    currentSkinTextureUri,
                    cancellationToken)
                .ConfigureAwait(false);
            var image = pngFilePath is null
                ? currentImage
                : await ReadSkinFileAsync(pngFilePath, cancellationToken).ConfigureAwait(false);
            var uploadedBytesDiffer = pngFilePath is not null && !image.AsSpan().SequenceEqual(currentImage);
            var expectedState = (JEProfile candidate) => HasExpectedActiveSkinState(
                candidate,
                variant,
                currentActiveSkin.Id,
                currentSkinTextureUri,
                uploadedBytesDiffer);

            if (!uploadedBytesDiffer && HasExpectedActiveSkinVariant(currentProfile, variant))
            {
                // The official profile already has the requested pixels and arm model.
                StoreVerifiedProfile(account, currentProfile);
                _accountManager.CommitAccounts();
                _pendingProfileExpectations.Remove(accountId);
                return ToAccountInfo((JEGameAccount)account);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, MinecraftSkinsUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var form = new MultipartFormDataContent();
            form.Add(
                new StringContent(
                    variant == MinecraftClientSkinVariant.Slim ? "slim" : "classic",
                    Encoding.UTF8),
                "variant");
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "skin.png");
            request.Content = form;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MinecraftAccountValidationException(
                    $"Minecraft Services rejected the skin update ({(int)response.StatusCode}).");
            }

            _pendingProfileExpectations[accountId] = expectedState;
            // The skin endpoint has returned both an empty success response and a profile-shaped
            // response across service revisions. Never trust either shape as the source of truth:
            // fetch the authenticated profile again and verify both its UUID and requested skin
            // state before committing it.
            var profile = await FetchVerifiedProfileAfterAcceptedMutationAsync(
                    client,
                    session.AccessToken,
                    account,
                    expectedState,
                    cancellationToken)
                .ConfigureAwait(false);
            StoreVerifiedProfile(account, profile);
            _accountManager.CommitAccounts();
            _pendingProfileExpectations.Remove(accountId);
            return ToAccountInfo((JEGameAccount)account);
        }
        catch
        {
            // Never replace the stored profile when the official mutation is rejected or the
            // follow-up profile cannot be validated.
            ReloadPersistedAuthenticationState();
            throw;
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public async Task<MinecraftClientAccountInfo> SetActiveCapeAsync(
        string accountId,
        string? capeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = RequireApiHttpClient();
            var account = FindAccount(accountId);
            var session = await AuthenticateAccountCoreAsync(
                account,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
            var currentProfile = await FetchProfileAsync(client, session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            StoreVerifiedProfile(account, currentProfile);
            if (capeId is not null && !currentProfile.Capes.Any(cape =>
                    !string.IsNullOrWhiteSpace(cape.Id) &&
                    string.Equals(cape.Id, capeId, StringComparison.Ordinal)))
            {
                throw new UnauthorizedAccessException("The selected cape is not owned by this Minecraft account.");
            }

            using var request = new HttpRequestMessage(
                capeId is null ? HttpMethod.Delete : HttpMethod.Put,
                MinecraftActiveCapeUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (capeId is not null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { capeId }),
                    Encoding.UTF8,
                    "application/json");
            }

            using (var response = await client.SendAsync(
                       request,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new MinecraftAccountValidationException(
                        $"Minecraft Services rejected the cape update ({(int)response.StatusCode}).");
                }
            }

            var expectedState = (JEProfile candidate) => HasExpectedActiveCape(candidate, capeId);
            _pendingProfileExpectations[accountId] = expectedState;
            var profile = await FetchVerifiedProfileAfterAcceptedMutationAsync(
                    client,
                    session.AccessToken,
                    account,
                    expectedState,
                    cancellationToken)
                .ConfigureAwait(false);
            StoreVerifiedProfile(account, profile);
            _accountManager.CommitAccounts();
            _pendingProfileExpectations.Remove(accountId);
            return ToAccountInfo((JEGameAccount)account);
        }
        catch
        {
            ReloadPersistedAuthenticationState();
            throw;
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    private async Task<AuthenticatedMinecraftSession> AuthenticateAccountCoreAsync(
        IXboxGameAccount account,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        MSession session;
        if (HasStoredCodeFlowRefreshToken(account))
        {
            if (!forceRefresh)
            {
                session = await _loginHandler.Authenticate(account, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var authenticator = _loginHandler.CreateAuthenticator(account, cancellationToken);
                authenticator.AddForceMicrosoftOAuthForJE(builder => builder.Silent());
                authenticator.AddForceXboxAuthForJE(builder => builder.Basic());
                authenticator.AddForceJEAuthenticator(builder =>
                    builder.WithGameOwnershipChecker().Build());
                session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);
            }
        }
        else
        {
            var msal = await _msalApplication.Value.ConfigureAwait(false);
            var authenticator = _loginHandler.CreateAuthenticator(account, cancellationToken);
            authenticator.AddMsalOAuth(msal, builder => builder.Silent());
            if (forceRefresh)
            {
                authenticator.AddForceXboxAuthForJE(builder => builder.Basic());
                authenticator.AddForceJEAuthenticator(builder =>
                    builder.WithGameOwnershipChecker().Build());
            }
            else
            {
                authenticator.AddXboxAuthForJE(builder => builder.Basic());
                authenticator.AddJEAuthenticator();
            }

            session = await authenticator.ExecuteForLauncherAsync().ConfigureAwait(false);
        }

        var authenticated = ConvertSession(RequireAccountIdentifier(account), session);
        _accountManager.CommitAccounts();
        return authenticated;
    }

    private static bool HasStoredCodeFlowRefreshToken(IXboxGameAccount account) =>
        !string.IsNullOrWhiteSpace(
            MicrosoftOAuthSessionSource.Default.Get(account.SessionStorage)?.RefreshToken);

    private async Task RemoveMsalCacheAccountAsync(IXboxGameAccount account)
    {
        if (!_msalApplication.IsValueCreated || HasStoredCodeFlowRefreshToken(account))
        {
            return;
        }

        var loginHint = MicrosoftOAuthLoginHintSource.Default.Get(account.SessionStorage);
        if (string.IsNullOrWhiteSpace(loginHint))
        {
            return;
        }

        var msal = await _msalApplication.Value.ConfigureAwait(false);
        var cached = (await msal.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Username, loginHint, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            await msal.RemoveAsync(cached).ConfigureAwait(false);
        }
    }

    private static MinecraftDeviceCodePrompt CreateDeviceCodePrompt(DeviceCodeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Uri.TryCreate(result.VerificationUrl, UriKind.Absolute, out var verificationUri) ||
            verificationUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(verificationUri.Host, "microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(verificationUri.AbsolutePath.TrimEnd('/'), "/devicelogin", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(verificationUri.UserInfo) ||
            !string.IsNullOrEmpty(verificationUri.Query) ||
            !string.IsNullOrEmpty(verificationUri.Fragment))
        {
            throw new MinecraftAccountValidationException(
                "Microsoft returned an unexpected device-login address.");
        }

        var userCode = result.UserCode?.Trim();
        if (string.IsNullOrWhiteSpace(userCode) || userCode.Length > 32 ||
            userCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new MinecraftAccountValidationException("Microsoft returned an invalid device code.");
        }

        var expiresAtUtc = result.ExpiresOn.ToUniversalTime();
        if (expiresAtUtc <= DateTimeOffset.UtcNow ||
            expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(30))
        {
            throw new MinecraftAccountValidationException(
                "Microsoft returned an invalid device-code expiration time.");
        }

        return new MinecraftDeviceCodePrompt(verificationUri, userCode, expiresAtUtc);
    }

    private static MinecraftClientAccountInfo ToAccountInfo(JEGameAccount account)
    {
        var profile = account.Profile
            ?? throw new MinecraftAccountValidationException("The Minecraft profile is unavailable.");
        var activeSkin = profile.Skins
            .Where(static skin => string.Equals(skin.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .Select(ToSkinInfo)
            .FirstOrDefault(static skin => skin is not null);
        var capes = profile.Capes
            .Select(ToCapeInfo)
            .Where(static cape => cape is not null)
            .Cast<MinecraftClientCapeInfo>()
            .ToArray();
        return new MinecraftClientAccountInfo(
            account.Identifier!,
            profile.Username!,
            NormalizeUuid(profile.UUID!),
            new DateTimeOffset(DateTime.SpecifyKind(account.LastAccess, DateTimeKind.Utc)),
            GetAuthenticationExpiry(account),
            activeSkin,
            capes);
    }

    private static MinecraftClientSkinInfo? ToSkinInfo(JEProfileSkin skin)
    {
        if (string.IsNullOrWhiteSpace(skin.Id) || !TryGetOfficialTextureUri(skin.Url, out var textureUri))
        {
            return null;
        }

        return new MinecraftClientSkinInfo(
            skin.Id,
            textureUri!,
            string.Equals(skin.Variant, "SLIM", StringComparison.OrdinalIgnoreCase)
                ? MinecraftClientSkinVariant.Slim
                : MinecraftClientSkinVariant.Classic,
            string.Equals(skin.State, "ACTIVE", StringComparison.OrdinalIgnoreCase));
    }

    private static MinecraftClientCapeInfo? ToCapeInfo(JEProfileCape cape)
    {
        if (string.IsNullOrWhiteSpace(cape.Id))
        {
            return null;
        }

        TryGetOfficialTextureUri(cape.Url, out var textureUri);
        return new MinecraftClientCapeInfo(
            cape.Id,
            string.IsNullOrWhiteSpace(cape.Alias) ? cape.Id : cape.Alias,
            textureUri,
            string.Equals(cape.State, "ACTIVE", StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset? GetAuthenticationExpiry(IXboxGameAccount account)
    {
        if (account is not JEGameAccount { Token: { } token } || token.ExpiresOn == default)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresOn, DateTimeKind.Utc));
    }

    internal static bool TryGetOfficialTextureUri(string? value, out Uri? textureUri)
    {
        textureUri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp) ||
            !string.Equals(candidate.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        const string texturePrefix = "/texture/";
        if (!candidate.AbsolutePath.StartsWith(texturePrefix, StringComparison.Ordinal) ||
            candidate.AbsolutePath.Length > texturePrefix.Length + 128)
        {
            return false;
        }

        var textureHash = candidate.AbsolutePath.AsSpan(texturePrefix.Length);
        if (textureHash.Length is < 32 or > 128 ||
            textureHash.Contains('/') ||
            !textureHash.ToString().All(Uri.IsHexDigit))
        {
            return false;
        }

        textureUri = candidate.Scheme == Uri.UriSchemeHttps
            ? candidate
            : new UriBuilder(candidate)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1,
            }.Uri;
        return true;
    }

    private HttpClient RequireApiHttpClient() => _httpClient
        ?? throw new InvalidOperationException("Minecraft profile management requires the product HTTP client.");

    private static async Task<byte[]> ReadSkinFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Select a regular local PNG skin file.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 24 or > MaximumSkinBytes)
        {
            throw new InvalidDataException("Minecraft skin PNG size is outside the safe limit.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        ValidateSkinPng(bytes);
        return bytes;
    }

    private static async Task<byte[]> DownloadCurrentSkinAsync(
        HttpClient client,
        JEProfileSkin activeSkin,
        Uri textureUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSkin);
        ArgumentNullException.ThrowIfNull(textureUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, textureUri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength is > MaximumSkinBytes)
        {
            throw new InvalidDataException("The current official skin could not be downloaded safely.");
        }

        var bytes = await ReadBoundedAsync(response.Content, MaximumSkinBytes, cancellationToken)
            .ConfigureAwait(false);
        ValidateSkinPng(bytes);
        return bytes;
    }

    private static void ValidateSkinPng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("The selected skin is not a valid PNG file.");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (width != 64 || height is not (32 or 64))
        {
            throw new InvalidDataException("Minecraft skins must be 64x64 or legacy 64x32 PNG images.");
        }
    }

    private static async Task<JEProfile> FetchProfileAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        return await ReadProfileResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JEProfile> FetchVerifiedProfileAfterAcceptedMutationAsync(
        HttpClient client,
        string accessToken,
        IXboxGameAccount account,
        Func<JEProfile, bool> hasExpectedState,
        CancellationToken cancellationToken)
        => await FetchProfileUntilExpectedStateAsync(
                client,
                accessToken,
                profile =>
                {
                    EnsureProfileMatchesAccount(account, profile);
                    return hasExpectedState(profile);
                },
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<JEProfile> FetchProfileUntilExpectedStateAsync(
        HttpClient client,
        string accessToken,
        Func<JEProfile, bool> hasExpectedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(hasExpectedState);
        Exception? lastFailure = null;

        // The mutation has already been accepted. Read immediately, then retry only the
        // authoritative GET; never resend the POST/PUT/DELETE and risk applying it twice.
        for (var attempt = 0; attempt <= ProfileSynchronizationRetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                await Task.Delay(
                        ProfileSynchronizationRetryDelays[attempt - 1],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                var profile = await FetchProfileAsync(client, accessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (hasExpectedState(profile))
                {
                    return profile;
                }

                lastFailure = new MinecraftAccountValidationException(
                    "Minecraft Services still reports the previous player-profile state.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsProfileSynchronizationRetryable(error))
            {
                lastFailure = error;
            }
        }

        throw new MinecraftProfileSynchronizationPendingException(
            "Minecraft Services accepted the change, but the verified player profile is still synchronizing.",
            lastFailure);
    }

    internal static bool HasExpectedActiveSkinVariant(
        JEProfile profile,
        MinecraftClientSkinVariant variant)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(variant))
        {
            return false;
        }

        var activeSkins = profile.Skins
            .Where(skin => string.Equals(skin.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedVariant = variant == MinecraftClientSkinVariant.Slim ? "SLIM" : "CLASSIC";
        return activeSkins.Length == 1 &&
               string.Equals(activeSkins[0].Variant, expectedVariant, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasExpectedActiveSkinState(
        JEProfile profile,
        MinecraftClientSkinVariant variant,
        string? previousSkinId,
        Uri previousTextureUri,
        bool requireIdentityChange)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(previousTextureUri);
        if (!HasExpectedActiveSkinVariant(profile, variant))
        {
            return false;
        }

        if (!requireIdentityChange)
        {
            return true;
        }

        var activeSkin = GetSingleActiveSkin(profile);
        if (activeSkin is null || !TryGetOfficialTextureUri(activeSkin.Url, out var activeTextureUri))
        {
            return false;
        }

        return !string.Equals(activeSkin.Id, previousSkinId, StringComparison.Ordinal) ||
               !string.Equals(
                   activeTextureUri!.AbsoluteUri,
                   previousTextureUri.AbsoluteUri,
                   StringComparison.Ordinal);
    }

    internal static bool HasExpectedActiveCape(JEProfile profile, string? capeId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var activeCapes = profile.Capes
            .Where(cape => string.Equals(cape.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return capeId is null
            ? activeCapes.Length == 0
            : activeCapes.Length == 1 &&
              string.Equals(activeCapes[0].Id, capeId, StringComparison.Ordinal);
    }

    private static JEProfileSkin? GetSingleActiveSkin(JEProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var activeSkins = profile.Skins
            .Where(skin => string.Equals(skin.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return activeSkins.Length == 1 ? activeSkins[0] : null;
    }

    private static Uri GetOfficialSkinTextureUri(JEProfileSkin activeSkin)
    {
        if (!TryGetOfficialTextureUri(activeSkin.Url, out var textureUri) || textureUri is null)
        {
            throw new InvalidOperationException("The current official skin texture is unavailable.");
        }

        return textureUri;
    }

    private static bool IsProfileSynchronizationRetryable(Exception error) => error is
        HttpRequestException or
        IOException or
        TimeoutException or
        JsonException or
        InvalidDataException or
        MinecraftAccountValidationException;

    private static async Task<JEProfile> ReadProfileResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1024 * 1024)
        {
            throw new MinecraftAccountValidationException(
                $"Minecraft Services rejected the profile update ({(int)response.StatusCode}).");
        }

        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        return JEProfile.ParseFromJson(document.RootElement);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Minecraft Services returned an oversized response.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void StoreVerifiedProfile(IXboxGameAccount account, JEProfile profile)
    {
        EnsureProfileMatchesAccount(account, profile);
        JEProfileSource.Default.Set(account.SessionStorage, profile);
    }

    private static void EnsureProfileMatchesAccount(IXboxGameAccount account, JEProfile profile)
    {
        if (account is not JEGameAccount javaAccount ||
            string.IsNullOrWhiteSpace(profile.UUID) ||
            string.IsNullOrWhiteSpace(profile.Username) ||
            !string.Equals(
                NormalizeUuid(profile.UUID),
                NormalizeUuid(javaAccount.Profile?.UUID ?? string.Empty),
                StringComparison.Ordinal))
        {
            throw new MinecraftAccountValidationException(
                "Minecraft Services returned a profile that does not match the selected account.");
        }
    }

    private IXboxGameAccount FindAccount(string accountId)
    {
        if (!_accountManager.GetAccounts().TryGetAccount(accountId, out var account) || account is null)
        {
            throw new KeyNotFoundException("The selected Microsoft account is not registered on this computer.");
        }

        return account;
    }

    private async Task RemoveFailedAccountAsync(IXboxGameAccount account)
    {
        try
        {
            // Refresh/profile/ownership failure means the cached account is no longer eligible
            // to launch Java Edition. Signout clears its OAuth/Xbox/JE session material; the
            // deferred manager then commits only that cleared state.
            await _loginHandler.Signout(account, CancellationToken.None).ConfigureAwait(false);
            _accountManager.CommitAccounts();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never persist a partially refreshed account. If local signout unexpectedly fails,
            // restore the last committed vault state and preserve the original authentication
            // exception for the caller.
            ReloadPersistedAuthenticationState();
        }
    }

    internal static bool IsAuthoritativeAccountFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = EnumerateExceptionChain(exception).ToArray();
        if (chain.Any(static error => error is MinecraftProfileSynchronizationPendingException) ||
            chain.Any(IsTransientAuthenticationFailure))
        {
            return false;
        }

        return chain.Any(static error => error switch
        {
            MinecraftAccountValidationException => true,
            MicrosoftOAuthException oauth =>
                oauth.Error?.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase) == true,
            MsalServiceException msal =>
                msal.ErrorCode?.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase) == true,
            JEAuthException javaEdition => javaEdition.StatusCode is 401 or 403 or 404,
            XboxAuthException xbox => xbox.StatusCode is 401 or 403 or 404,
            _ => false,
        });
    }

    private static bool IsTransientAuthenticationFailure(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        TimeoutException => true,
        HttpRequestException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        MicrosoftOAuthException oauth when IsTransientStatusCode(oauth.StatusCode) => true,
        MsalServiceException msal when IsTransientStatusCode(msal.StatusCode) => true,
        JEAuthException javaEdition when IsTransientStatusCode(javaEdition.StatusCode) => true,
        XboxAuthException xbox when IsTransientStatusCode(xbox.StatusCode) => true,
        _ => false,
    };

    private static bool IsTransientStatusCode(int statusCode) =>
        statusCode is 408 or 425 or 429 || statusCode is >= 500 and <= 599;

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static AuthenticatedMinecraftSession ConvertSession(string accountId, MSession session)
    {
        if (!session.CheckIsValid() || string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(session.Username) || string.IsNullOrWhiteSpace(session.UUID))
        {
            throw new MinecraftAccountValidationException(
                "Microsoft authentication did not return a valid owned Minecraft Java profile.");
        }

        return new AuthenticatedMinecraftSession(
            accountId,
            session.Username!,
            session.UUID!,
            session.AccessToken!,
            session.Xuid);
    }

    private static string RequireAccountIdentifier(IXboxGameAccount account) =>
        string.IsNullOrWhiteSpace(account.Identifier)
            ? throw new MinecraftAccountValidationException(
                "Microsoft account storage returned an invalid account identifier.")
            : account.Identifier;

    private static string NormalizeUuid(string uuid) =>
        uuid.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}

internal sealed class MinecraftAccountValidationException(string message)
    : UnauthorizedAccessException(message);

/// <summary>
/// Prevents XboxAuthNet's AccountSaver stage from persisting partially completed authentication.
/// The owner must call <see cref="CommitAccounts"/> only after product-level profile validation.
/// </summary>
internal sealed class DeferredSaveXboxGameAccountManager(IXboxGameAccountManager inner)
    : IXboxGameAccountManager
{
    public XboxGameAccountCollection GetAccounts() => inner.GetAccounts();

    public IXboxGameAccount GetDefaultAccount() => inner.GetDefaultAccount();

    public IXboxGameAccount NewAccount() => inner.NewAccount();

    public void ClearAccounts() => inner.ClearAccounts();

    public void SaveAccounts()
    {
        // Deliberately ignored. XboxAuthNet inserts AccountSaver into authentication pipelines
        // before the application can enforce owned Java profile invariants.
    }

    public void CommitAccounts() => inner.SaveAccounts();
}
