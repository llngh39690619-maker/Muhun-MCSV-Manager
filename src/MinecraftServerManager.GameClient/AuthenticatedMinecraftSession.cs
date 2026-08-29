using CmlLib.Core.Auth;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Short-lived, in-memory proof of a successful Microsoft/Xbox/Minecraft ownership flow.
/// It is deliberately not serializable and never exposes the access token through ToString().
/// </summary>
public sealed class AuthenticatedMinecraftSession
{
    private readonly string _accessToken;

    public AuthenticatedMinecraftSession(
        string accountId,
        string username,
        string minecraftUuid,
        string accessToken,
        string? xuid = null)
    {
        AccountId = Require(accountId, nameof(accountId), 256);
        Username = Require(username, nameof(username), 64);
        MinecraftUuid = RequireUuid(minecraftUuid);
        _accessToken = Require(accessToken, nameof(accessToken), 16_384);
        Xuid = string.IsNullOrWhiteSpace(xuid) ? null : Require(xuid, nameof(xuid), 64);
    }

    public string AccountId { get; }

    public string Username { get; }

    public string MinecraftUuid { get; }

    public string? Xuid { get; }

    internal string AccessToken => _accessToken;

    internal MSession ToCmlSession() => new(Username, _accessToken, MinecraftUuid)
    {
        Xuid = Xuid ?? string.Empty,
        UserType = "msa",
    };

    public override string ToString() => $"{Username} ({MinecraftUuid})";

    private static string Require(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static string RequireUuid(string value)
    {
        var normalized = Require(value, nameof(value), 36).Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 32 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Minecraft UUID must contain 32 hexadecimal characters.", nameof(value));
        }

        return normalized.ToLowerInvariant();
    }
}
