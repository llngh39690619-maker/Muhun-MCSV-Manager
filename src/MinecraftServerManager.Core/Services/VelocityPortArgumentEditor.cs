using System.Globalization;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Reads and normalizes Velocity's official <c>-p</c>/<c>--port</c> command-line override. This
/// lets the manager allocate proxy ports without creating an unrelated server.properties file.
/// </summary>
public static class VelocityPortArgumentEditor
{
    public static bool TryReadPort(IReadOnlyList<string>? arguments, out int port)
    {
        port = 0;
        if (arguments is null)
        {
            return false;
        }

        var found = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--port", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-p", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count
                    && TryParsePort(arguments[index + 1], out var parsed))
                {
                    port = parsed;
                    found = true;
                    index++;
                }

                continue;
            }

            if (TryReadEqualsForm(argument, "--port=", out var longPort)
                || TryReadEqualsForm(argument, "-p=", out longPort))
            {
                port = longPort;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Removes every existing port override, preserving unrelated arguments, then appends one
    /// canonical <c>--port &lt;value&gt;</c> pair.
    /// </summary>
    public static void SetPort(List<string> arguments, int port)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);

        var retained = new List<string>(arguments.Count + 2);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--port", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-p", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count
                    && ShouldConsumeSeparatedValue(arguments[index + 1]))
                {
                    index++;
                }

                continue;
            }

            if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-p=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            retained.Add(argument);
        }

        retained.Add("--port");
        retained.Add(port.ToString(CultureInfo.InvariantCulture));
        arguments.Clear();
        arguments.AddRange(retained);
    }

    private static bool TryReadEqualsForm(string? argument, string prefix, out int port)
    {
        port = 0;
        return argument is not null
            && argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && TryParsePort(argument[prefix.Length..], out port);
    }

    private static bool TryParsePort(string? value, out int port)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
           && port is >= 1 and <= 65_535;

    private static bool ShouldConsumeSeparatedValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '-')
        {
            return true;
        }

        // Preserve a following unrelated option (for example --help), but consume malformed
        // numeric values such as -1 so SetPort never leaves an orphaned port token behind.
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
