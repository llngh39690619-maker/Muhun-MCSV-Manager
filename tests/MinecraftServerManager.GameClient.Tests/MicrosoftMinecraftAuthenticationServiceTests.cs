using CmlLib.Core.Auth.Microsoft;
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
}
