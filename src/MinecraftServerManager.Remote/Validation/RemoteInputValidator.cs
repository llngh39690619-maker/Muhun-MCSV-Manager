using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote;

public static class RemoteInputValidator
{
    public static bool TryParseIdempotencyKey(string? value, out Guid key)
    {
        key = Guid.Empty;
        if (string.IsNullOrEmpty(value) || value.Length != 36 ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        key = parsed;
        return true;
    }

    public static bool TryValidateServerId(string? serverId, out string error)
    {
        if (string.IsNullOrEmpty(serverId) || serverId.Length > 128)
        {
            error = "Server identifier is invalid.";
            return false;
        }

        if (!IsAsciiLetterOrDigit(serverId[0]))
        {
            error = "Server identifier is invalid.";
            return false;
        }

        foreach (var character in serverId)
        {
            var allowed = IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
            if (!allowed)
            {
                error = "Server identifier is invalid.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateBackupId(string? backupId, out string error)
    {
        if (string.IsNullOrEmpty(backupId) || backupId.Length != 64)
        {
            error = "Backup identifier is invalid.";
            return false;
        }

        foreach (var character in backupId)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                error = "Backup identifier is invalid.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateCommand(string? command, int maximumLength, out string error)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "Command is required.";
            return false;
        }

        if (command.Length > maximumLength)
        {
            error = $"Command must not exceed {maximumLength} characters.";
            return false;
        }

        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            error = "Command must contain exactly one text line.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidatePlayerAction(RemotePlayerActionRequestDto? request, out string error)
    {
        if (request is null || !Enum.IsDefined(request.Action))
        {
            error = "Player action is invalid.";
            return false;
        }

        var actionDoesNotTargetPlayer = request.Action is
            RemotePlayerActionKind.WhitelistOn or RemotePlayerActionKind.WhitelistOff;
        if ((!actionDoesNotTargetPlayer && !IsMinecraftPlayerName(request.PlayerName)) ||
            (actionDoesNotTargetPlayer && request.PlayerName is not null))
        {
            error = actionDoesNotTargetPlayer
                ? "This whitelist action must not include a player name."
                : "Player name is invalid.";
            return false;
        }

        if (request.Reason is { Length: > 160 } ||
            request.Reason?.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            error = "Reason must be a single line of at most 160 characters.";
            return false;
        }

        if (request.Reason is not null &&
            request.Action is not RemotePlayerActionKind.Kick and not RemotePlayerActionKind.Ban)
        {
            error = "A reason is accepted only for kick or ban actions.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsMinecraftPlayerName(string? playerName)
    {
        if (string.IsNullOrEmpty(playerName) || playerName.Length > 16)
        {
            return false;
        }

        return playerName.All(character =>
            IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static bool IsAsciiLetterOrDigit(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
