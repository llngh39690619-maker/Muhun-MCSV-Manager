using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.App.Services;

internal sealed record VerificationCodeDispatchResult(DateTimeOffset ExpiresAtUtc);

internal sealed class RemoteEmailVerificationRequiredException(string message)
    : InvalidOperationException(message);

internal sealed class RemoteAccountProvisioningService : IDisposable
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan VerificationTicketLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedVerificationLockout = TimeSpan.FromMinutes(15);
    private const int MaximumSendsPerHour = 5;
    private const int MaximumVerificationFailures = 5;

    private readonly object _gate = new();
    private readonly IRemoteSecurityStore _store;
    private readonly IVerificationEmailSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly Queue<DateTimeOffset> _sentAtUtc = new();
    private Challenge? _challenge;
    private VerifiedTicket? _verifiedTicket;
    private DateTimeOffset? _lastSentAtUtc;
    private DateTimeOffset? _verificationLockedUntilUtc;
    private int _verificationFailures;
    private bool _disposed;

    public RemoteAccountProvisioningService(
        IRemoteSecurityStore store,
        IVerificationEmailSender? sender = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? new GmailSmtpVerificationSender();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void SaveSmtpCredential(string senderGmail, string appPassword)
        => _store.SaveSmtpCredential(senderGmail, appPassword);

    public void DeleteSmtpCredential()
    {
        _store.DeleteSmtpCredential();
        ResetTransientVerification();
    }

    public async Task<VerificationCodeDispatchResult> SendVerificationCodeAsync(
        string recipientGmail,
        CancellationToken cancellationToken = default)
    {
        recipientGmail = recipientGmail?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RemoteIdentity.IsCanonicalGmailLogin(recipientGmail))
        {
            throw new InvalidOperationException("收件帳號必須是完整且有效的 @gmail.com 帳號。");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                RemoveOldSendHistory(now);
                if (_verificationLockedUntilUtc is { } lockedUntil && lockedUntil > now)
                {
                    throw new InvalidOperationException(
                        $"驗證碼輸入錯誤次數過多，請於 {lockedUntil.ToLocalTime():HH:mm:ss} 後再試。");
                }

                if (_lastSentAtUtc is { } lastSent && now - lastSent < ResendCooldown)
                {
                    var availableAt = lastSent.Add(ResendCooldown);
                    throw new InvalidOperationException(
                        $"請於 {availableAt.ToLocalTime():HH:mm:ss} 後再重新寄送驗證碼。");
                }

                if (_sentAtUtc.Count >= MaximumSendsPerHour)
                {
                    throw new InvalidOperationException("一小時內最多寄送 5 封驗證郵件，請稍後再試。");
                }

                // Any resend invalidates the previous code before network I/O. If sending
                // fails, no old or new challenge remains usable.
                ClearChallenge();
                _verifiedTicket = null;
            }

            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
            var expiresAtUtc = now.Add(CodeLifetime);
            var nonce = RandomNumberGenerator.GetBytes(32);
            var hash = ComputeHash(recipientGmail, code, nonce);
            try
            {
                var credential = _store.GetSmtpCredential();
                await _sender.SendVerificationCodeAsync(
                        credential,
                        recipientGmail,
                        code,
                        expiresAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _challenge = new Challenge(
                        recipientGmail,
                        nonce.ToArray(),
                        hash.ToArray(),
                        expiresAtUtc);
                    _lastSentAtUtc = now;
                    _sentAtUtc.Enqueue(now);
                }

                return new VerificationCodeDispatchResult(expiresAtUtc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public DateTimeOffset VerifyCode(string recipientGmail, string code)
    {
        recipientGmail = recipientGmail?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsSixDigitCode(code))
        {
            return RegisterVerificationFailure();
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (_verificationLockedUntilUtc is { } lockedUntil && lockedUntil > now)
            {
                throw new InvalidOperationException(
                    $"驗證碼輸入錯誤次數過多，請於 {lockedUntil.ToLocalTime():HH:mm:ss} 後再試。");
            }

            if (_challenge is not { } challenge || challenge.ExpiresAtUtc <= now ||
                !string.Equals(challenge.RecipientGmail, recipientGmail, StringComparison.OrdinalIgnoreCase))
            {
                return RegisterVerificationFailureLocked(now);
            }

            var supplied = ComputeHash(recipientGmail, code, challenge.Nonce);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(challenge.Hash, supplied))
                {
                    return RegisterVerificationFailureLocked(now);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(supplied);
            }

            CryptographicOperations.ZeroMemory(challenge.Nonce);
            CryptographicOperations.ZeroMemory(challenge.Hash);
            _challenge = null;
            _verificationFailures = 0;
            _verificationLockedUntilUtc = null;
            var ticketExpiresAtUtc = now.Add(VerificationTicketLifetime);
            _verifiedTicket = new VerifiedTicket(recipientGmail, ticketExpiresAtUtc);
            return ticketExpiresAtUtc;
        }
    }

    public async Task RegisterAccountAsync(
        string verifiedGmail,
        string username,
        string pin,
        string confirmedPin,
        CancellationToken cancellationToken = default)
        => await RegisterAccountAsync(
                verifiedGmail,
                username,
                pin,
                confirmedPin,
                RemoteWebPermission.All,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task RegisterAccountAsync(
        string verifiedGmail,
        string username,
        string pin,
        string confirmedPin,
        RemoteWebPermission permissions,
        CancellationToken cancellationToken = default)
    {
        verifiedGmail = verifiedGmail?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!string.Equals(pin, confirmedPin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("兩次輸入的數字密碼不相同。");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (_verifiedTicket is not { } ticket || ticket.ExpiresAtUtc <= now ||
                !string.Equals(ticket.Gmail, verifiedGmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteEmailVerificationRequiredException(
                    "請先完成 Gmail 驗證；驗證資格可能已到期，請重新寄送驗證碼。");
            }
        }

        await Task.Run(
                () => _store.RegisterAccount(verifiedGmail, username, pin, permissions),
                cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _verifiedTicket = null;
        }
    }

    public async Task RegisterLocalAccountAsync(
        string username,
        string pin,
        string confirmedPin,
        RemoteWebPermission permissions,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(pin, confirmedPin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("兩次輸入的數字密碼不相同。");
        }

        await Task.Run(
                () => _store.RegisterAccount(null, username, pin, permissions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResetAccountPinAsync(
        string username,
        string newPin,
        string confirmedPin,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(newPin, confirmedPin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("兩次輸入的數字密碼不相同。");
        }

        await Task.Run(
                () => _store.ResetAccountPin(username, newPin),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void ResetTransientVerification()
    {
        lock (_gate)
        {
            ClearChallenge();
            _verifiedTicket = null;
        }
    }

    private DateTimeOffset RegisterVerificationFailure()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RegisterVerificationFailureLocked(_timeProvider.GetUtcNow());
        }
    }

    private DateTimeOffset RegisterVerificationFailureLocked(DateTimeOffset now)
    {
        _verificationFailures++;
        if (_verificationFailures >= MaximumVerificationFailures)
        {
            _verificationFailures = 0;
            _verificationLockedUntilUtc = now.Add(FailedVerificationLockout);
            ClearChallenge();
            throw new InvalidOperationException(
                $"驗證碼輸入錯誤次數過多，請於 {_verificationLockedUntilUtc.Value.ToLocalTime():HH:mm:ss} 後再試。");
        }

        throw new InvalidOperationException("驗證碼不正確、已到期或不是目前收件 Gmail 的驗證碼。");
    }

    private byte[] ComputeHash(string gmail, string code, byte[] nonce)
    {
        var input = Encoding.UTF8.GetBytes($"{gmail}\n{code}");
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _hmacKey);
            hmac.AppendData(nonce);
            hmac.AppendData(input);
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static bool IsSixDigitCode(string? value)
        => value is { Length: 6 } && value.All(character => character is >= '0' and <= '9');

    private void RemoveOldSendHistory(DateTimeOffset now)
    {
        while (_sentAtUtc.TryPeek(out var sentAt) && now - sentAt >= HourlyWindow)
        {
            _sentAtUtc.Dequeue();
        }
    }

    private void ClearChallenge()
    {
        if (_challenge is not { } challenge) return;
        CryptographicOperations.ZeroMemory(challenge.Nonce);
        CryptographicOperations.ZeroMemory(challenge.Hash);
        _challenge = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearChallenge();
            _verifiedTicket = null;
            CryptographicOperations.ZeroMemory(_hmacKey);
        }
        // An SMTP send may still be unwinding on another thread. Disposing the semaphore here
        // would make that in-flight operation throw from its finally block; it owns no native
        // resource and is left for GC after the service becomes unreachable.
    }

    private sealed record Challenge(
        string RecipientGmail,
        byte[] Nonce,
        byte[] Hash,
        DateTimeOffset ExpiresAtUtc);

    private sealed record VerifiedTicket(string Gmail, DateTimeOffset ExpiresAtUtc);
}
