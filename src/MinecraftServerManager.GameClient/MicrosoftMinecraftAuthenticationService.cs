using System.Runtime.Versioning;
using System.Text.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using MinecraftServerManager.GameClient.Contracts;
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
    private readonly DpapiXboxAccountJsonStorage _storage;
    private readonly HttpClient? _httpClient;
    private DeferredSaveXboxGameAccountManager _accountManager = null!;
    private JELoginHandler _loginHandler = null!;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);

    public MicrosoftMinecraftAuthenticationService(
        string protectedAccountVaultPath,
        Guid installationId,
        HttpClient? httpClient = null)
    {
        _storage = new DpapiXboxAccountJsonStorage(protectedAccountVaultPath, installationId);
        _httpClient = httpClient;
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
                return new MinecraftClientAccountInfo(
                    account.Identifier!,
                    profile.Username!,
                    NormalizeUuid(profile.UUID!),
                    new DateTimeOffset(DateTime.SpecifyKind(account.LastAccess, DateTimeKind.Utc)));
            })
            .OrderByDescending(account => account.LastAuthenticatedAtUtc)
            .ToArray();

    public async Task<AuthenticatedMinecraftSession> AddAccountInteractivelyAsync(
        CancellationToken cancellationToken = default)
    {
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = _accountManager.NewAccount();
            try
            {
                var session = await _loginHandler.AuthenticateInteractively(account, cancellationToken)
                    .ConfigureAwait(false);
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
                var session = await _loginHandler.Authenticate(account, cancellationToken).ConfigureAwait(false);
                var authenticated = ConvertSession(RequireAccountIdentifier(account), session);
                _accountManager.CommitAccounts();
                return authenticated;
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
                await _loginHandler.Signout(FindAccount(accountId), cancellationToken).ConfigureAwait(false);
                _accountManager.CommitAccounts();
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
                foreach (var account in _accountManager.GetAccounts().ToArray())
                {
                    await _loginHandler.Signout(account, cancellationToken).ConfigureAwait(false);
                }

                _accountManager.ClearAccounts();
                _accountManager.CommitAccounts();
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
        if (chain.Any(IsTransientAuthenticationFailure))
        {
            return false;
        }

        return chain.Any(static error => error switch
        {
            MinecraftAccountValidationException => true,
            MicrosoftOAuthException oauth =>
                oauth.Error?.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase) == true,
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
