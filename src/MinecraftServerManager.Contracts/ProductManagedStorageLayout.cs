namespace MinecraftServerManager.Contracts;

/// <summary>
/// Canonical machine-wide storage layout selected by the installed product. Executable,
/// Service-owned and desktop/Service exchange trees share one user-selected ancestor, but are
/// deliberately separate ownership and ACL boundaries.
/// </summary>
public static class ProductManagedStorageLayout
{
    public const string BetaChannel = "beta";

    public static string ResolveServiceDataRoot(
        string installRoot,
        string channel = BetaChannel)
        => CombineManagedRoot(installRoot, "service", channel);

    public static string ResolveExchangeRoot(
        string installRoot,
        string channel = BetaChannel)
        => CombineManagedRoot(installRoot, "exchange", channel);

    public static string ResolveExchangeRootFromServiceDataRoot(string dataRoot)
    {
        var normalizedDataRoot = NormalizeAbsoluteRoot(dataRoot, nameof(dataRoot));
        var channelDirectory = new DirectoryInfo(normalizedDataRoot);
        var serviceDirectory = channelDirectory.Parent;
        var installDirectory = serviceDirectory?.Parent;
        if (serviceDirectory is null || installDirectory is null ||
            !serviceDirectory.Name.Equals("service", StringComparison.OrdinalIgnoreCase) ||
            (!channelDirectory.Name.Equals("stable", StringComparison.OrdinalIgnoreCase) &&
             !channelDirectory.Name.Equals("beta", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Service data root must use <install-root>\\service\\<channel>.");
        }

        return ResolveExchangeRoot(installDirectory.FullName, channelDirectory.Name);
    }

    public static void ValidateSeparatedRoots(string dataRoot, string exchangeRoot)
    {
        var normalizedData = NormalizeAbsoluteRoot(dataRoot, nameof(dataRoot));
        var normalizedExchange = NormalizeAbsoluteRoot(exchangeRoot, nameof(exchangeRoot));
        if (IsWithinOrEqual(normalizedData, normalizedExchange) ||
            IsWithinOrEqual(normalizedExchange, normalizedData))
        {
            throw new InvalidDataException(
                "Service data and exchange roots must be separate, non-overlapping directories.");
        }

    }

    public static void ValidateCanonicalSiblingRoots(string dataRoot, string exchangeRoot)
    {
        ValidateSeparatedRoots(dataRoot, exchangeRoot);
        var normalizedData = NormalizeAbsoluteRoot(dataRoot, nameof(dataRoot));
        var normalizedExchange = NormalizeAbsoluteRoot(exchangeRoot, nameof(exchangeRoot));
        var expectedExchange = ResolveExchangeRootFromServiceDataRoot(normalizedData);
        if (!expectedExchange.Equals(
                normalizedExchange,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Service data and exchange roots must use sibling service/<channel> and " +
                "exchange/<channel> trees below the same install root.");
        }
    }

    private static string CombineManagedRoot(string installRoot, string kind, string channel)
    {
        var normalizedInstall = NormalizeAbsoluteRoot(installRoot, nameof(installRoot));
        if (string.IsNullOrWhiteSpace(channel) ||
            channel is "." or ".." ||
            channel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Product channel must be one safe path segment.", nameof(channel));
        }

        return Path.Combine(normalizedInstall, kind, channel);
    }

    private static string NormalizeAbsoluteRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Managed product root must be absolute.", parameterName);
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized) ?? string.Empty);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(volumeRoot) ||
            normalized.Equals(
                volumeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Managed product root must be a non-root local directory.",
                parameterName);
        }

        return normalized;
    }

    private static bool IsWithinOrEqual(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (candidate.Equals(root, comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }
}
