using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace MinecraftServerManager.Remote;

public static partial class RemoteIdentity
{
    public static bool TryGetAllowedLogin(
        IHeaderDictionary headers,
        IReadOnlySet<string> allowedLogins,
        out string login)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(allowedLogins);

        login = string.Empty;
        if (!headers.TryGetValue(RemoteControlOptions.TailscaleLoginHeaderName, out var values) ||
            values.Count != 1)
        {
            return false;
        }

        var candidate = values[0];
        if (string.IsNullOrEmpty(candidate) ||
            !IsCanonicalGmailLogin(candidate) ||
            !allowedLogins.Contains(candidate))
        {
            return false;
        }

        login = candidate;
        return true;
    }

    public static bool IsCanonicalGmailLogin(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 74 || value != value.Trim())
        {
            return false;
        }

        var at = value.LastIndexOf('@');
        if (at is < 1 or > 64 ||
            !string.Equals(value[(at + 1)..], "gmail.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var local = value.AsSpan(0, at);
        if (local[0] == '.' || local[^1] == '.')
        {
            return false;
        }

        var previousWasDot = false;
        foreach (var character in local)
        {
            var isAsciiLetterOrDigit = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (!isAsciiLetterOrDigit && character != '.')
            {
                return false;
            }

            if (character == '.' && previousWasDot)
            {
                return false;
            }

            previousWasDot = character == '.';
        }

        return true;
    }
}
