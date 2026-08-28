using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteAccountProvisioningServiceTests
{
    [Fact]
    public async Task GmailCodeMustBeVerifiedBeforeAccountCanBeCreated()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero));
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender, time);

        await Assert.ThrowsAsync<RemoteEmailVerificationRequiredException>(() => service.RegisterAccountAsync(
            "owner@gmail.com",
            "account1",
            "12345678",
            "12345678"));

        var dispatched = await service.SendVerificationCodeAsync("owner@gmail.com");
        Assert.Equal(time.GetUtcNow().AddMinutes(10), dispatched.ExpiresAtUtc);
        Assert.Matches("^[0-9]{6}$", sender.Code);
        var ticketExpiry = service.VerifyCode("owner@gmail.com", sender.Code!);
        Assert.Equal(time.GetUtcNow().AddMinutes(10), ticketExpiry);

        await service.RegisterAccountAsync(
            "owner@gmail.com",
            "account1",
            "12345678",
            "12345678");

        Assert.Equal("account1", store.ApprovedAccount?.Username);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            store.Authenticate("owner@gmail.com", "account1", "12345678").Status);
        await Assert.ThrowsAsync<RemoteEmailVerificationRequiredException>(() => service.RegisterAccountAsync(
            "owner@gmail.com",
            "account2",
            "12345678",
            "12345678"));
    }

    [Fact]
    public async Task LocalAccountRegistration_DoesNotRequireGmailOrSmtp()
    {
        var store = new EphemeralRemoteSecurityStore();
        using var service = new RemoteAccountProvisioningService(store, new CapturingSender());
        var permissions = RemoteWebPermission.StopServer | RemoteWebPermission.ManagePlayers;

        await service.RegisterLocalAccountAsync(
            "account1",
            "12345678",
            "12345678",
            permissions);

        Assert.Null(store.ApprovedAccount?.Gmail);
        Assert.Null(store.ApprovedAccount?.EmailVerifiedAtUtc);
        Assert.Equal(permissions, store.ApprovedAccount?.Permissions);
        Assert.Equal(
            RemoteCredentialAuthenticationStatus.Success,
            store.Authenticate(
                RemoteControlOptions.QuickTunnelCredentialSubject,
                "account1",
                "12345678").Status);
    }

    [Fact]
    public async Task ResendInvalidatesOldCodeAndEnforcesCooldown()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero));
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender, time);

        await service.SendVerificationCodeAsync("owner@gmail.com");
        var oldCode = sender.Code!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendVerificationCodeAsync("owner@gmail.com"));

        time.Advance(TimeSpan.FromMinutes(1));
        await service.SendVerificationCodeAsync("owner@gmail.com");
        while (sender.Code == oldCode)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await service.SendVerificationCodeAsync("owner@gmail.com");
        }

        Assert.Throws<InvalidOperationException>(() =>
            service.VerifyCode("owner@gmail.com", oldCode));
        Assert.False(store.HasCredentialForLogin("owner@gmail.com"));
    }

    [Fact]
    public async Task FailedSendLeavesNoUsableChallenge()
    {
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender { Failure = new InvalidOperationException("smtp failed") };
        using var service = new RemoteAccountProvisioningService(store, sender);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendVerificationCodeAsync("owner@gmail.com"));
        Assert.Throws<InvalidOperationException>(() =>
            service.VerifyCode("owner@gmail.com", sender.Code ?? "000000"));
    }

    [Fact]
    public async Task VerifiedTicketExpiresAfterTenMinutes()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero));
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender, time);

        await service.SendVerificationCodeAsync("owner@gmail.com");
        service.VerifyCode("owner@gmail.com", sender.Code!);
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<RemoteEmailVerificationRequiredException>(() =>
            service.RegisterAccountAsync(
                "owner@gmail.com",
                "account1",
                "12345678",
                "12345678"));
        Assert.Null(store.ApprovedAccount);
    }

    [Fact]
    public async Task FifthWrongVerificationCodeLocksFurtherAttempts()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero));
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender, time);
        await service.SendVerificationCodeAsync("owner@gmail.com");
        var wrongCode = sender.Code == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                service.VerifyCode("owner@gmail.com", wrongCode));
            Assert.DoesNotContain("錯誤次數過多", exception.Message, StringComparison.Ordinal);
        }

        var locked = Assert.Throws<InvalidOperationException>(() =>
            service.VerifyCode("owner@gmail.com", wrongCode));
        Assert.Contains("錯誤次數過多", locked.Message, StringComparison.Ordinal);
        var stillLocked = Assert.Throws<InvalidOperationException>(() =>
            service.VerifyCode("owner@gmail.com", sender.Code!));
        Assert.Contains("錯誤次數過多", stillLocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulSendsAreLimitedToFivePerRollingHour()
    {
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero));
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender, time);

        for (var send = 0; send < 5; send++)
        {
            await service.SendVerificationCodeAsync("owner@gmail.com");
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var limited = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendVerificationCodeAsync("owner@gmail.com"));
        Assert.Contains("一小時內最多", limited.Message, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromMinutes(55));
        await service.SendVerificationCodeAsync("owner@gmail.com");
    }

    [Fact]
    public async Task DeletingSmtpCredentialInvalidatesOutstandingVerification()
    {
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new CapturingSender();
        using var service = new RemoteAccountProvisioningService(store, sender);
        await service.SendVerificationCodeAsync("owner@gmail.com");
        var code = sender.Code!;

        service.DeleteSmtpCredential();

        Assert.Null(store.SmtpSenderGmail);
        Assert.Throws<InvalidOperationException>(() =>
            service.VerifyCode("owner@gmail.com", code));
    }

    [Fact]
    public async Task DisposeDuringSmtpSend_DoesNotPublishChallengeOrDisposeInFlightGate()
    {
        var store = new EphemeralRemoteSecurityStore();
        store.SaveSmtpCredential("sender@gmail.com", "abcdefghijklmnop");
        var sender = new BlockingSender();
        var service = new RemoteAccountProvisioningService(store, sender);
        var send = service.SendVerificationCodeAsync("owner@gmail.com");
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Dispose();
        sender.Complete();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send);
        Assert.Throws<ObjectDisposedException>(() =>
            service.VerifyCode("owner@gmail.com", sender.Code!));
    }

    [Fact]
    public void GmailSenderContract_IsPinnedToStartTlsOnPort587()
    {
        Assert.Equal("smtp.gmail.com", GmailSmtpVerificationSender.Host);
        Assert.Equal(587, GmailSmtpVerificationSender.Port);
        var source = File.ReadAllText(GetAppSourcePath(
            Path.Combine("Services", "GmailSmtpVerificationSender.cs")));
        Assert.Contains("SecureSocketOptions.StartTls", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateValidationCallback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AcceptAll", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private sealed class CapturingSender : IVerificationEmailSender
    {
        public string? Code { get; private set; }
        public Exception? Failure { get; init; }

        public Task SendVerificationCodeAsync(
            GmailSmtpCredential credential,
            string recipientGmail,
            string code,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken)
        {
            Code = code;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class BlockingSender : IVerificationEmailSender
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Code { get; private set; }

        public async Task SendVerificationCodeAsync(
            GmailSmtpCredential credential,
            string recipientGmail,
            string code,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken)
        {
            Code = code;
            Entered.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
