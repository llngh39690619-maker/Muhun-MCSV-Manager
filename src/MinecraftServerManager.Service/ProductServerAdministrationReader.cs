using System.Text.RegularExpressions;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>
/// Captures a bounded, path-free add-on and Java snapshot from the Service-owned data tree.
/// It never accepts a caller path, never recurses, and never follows a reparse point.
/// </summary>
public sealed partial class ProductServerAdministrationReader(
    ProductDataLayout layout,
    ProductServerRegistry registry,
    TimeProvider timeProvider)
{
    private static readonly EnumerationOptions AddonEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public ProductServerAdministrationSnapshot? Capture(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (serverId == Guid.Empty || !registry.TryGet(serverId, out var registration))
        {
            return null;
        }

        var java = CaptureJava(registration, cancellationToken);
        var addons = new List<ProductServerAddonSummary>(
            ProductServerAdministrationContract.MaximumListedAddons);
        var addonsAvailable = false;
        var truncated = false;

        try
        {
            var serverRoot = ResolveExistingOwnedPath(
                layout.Servers,
                registration.ServerDirectory,
                directory: true);
            if (serverRoot is not null)
            {
                addonsAvailable = true;
                CaptureAddons(
                    serverRoot,
                    registration.CoreType,
                    addons,
                    ref truncated,
                    cancellationToken);
            }
        }
        catch (Exception error) when (IsExpectedReadFailure(error, cancellationToken))
        {
            // Fail closed. A missing, inaccessible, or redirecting managed tree is represented as
            // unavailable and is never replaced with a caller-controlled filesystem fallback.
            addonsAvailable = false;
            addons.Clear();
            truncated = false;
        }

        addons.Sort(static (left, right) =>
        {
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
        });

        return new ProductServerAdministrationSnapshot(
            registration.Id,
            timeProvider.GetUtcNow(),
            addonsAvailable,
            addons.ToArray(),
            truncated,
            java);
    }

    /// <summary>
    /// Captures only Java release metadata for the lightweight server-detail poll. This avoids
    /// enumerating a large modpack unless the user explicitly opens the environment tab.
    /// </summary>
    public ProductServerJavaRuntimeSummary? CaptureJava(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return serverId != Guid.Empty && registry.TryGet(serverId, out var registration)
            ? CaptureJava(registration, cancellationToken)
            : null;
    }

    private void CaptureAddons(
        string serverRoot,
        string coreTypeName,
        List<ProductServerAddonSummary> destination,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        var scanned = 0;
        foreach (var target in ResolveAddonTargets(coreTypeName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(serverRoot, target.DirectoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            directory = SafePath.EnsureNoReparsePointsUnderRoot(layout.Servers, directory);
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", AddonEnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    if (scanned > ProductServerAdministrationContract.MaximumScannedEntries)
                    {
                        truncated = true;
                        return;
                    }

                    if (!string.Equals(file.Extension, ".jar", StringComparison.OrdinalIgnoreCase) ||
                        file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        !TryCreateSafeFileName(file.Name, out var fileName))
                    {
                        continue;
                    }

                    if (destination.Count >= ProductServerAdministrationContract.MaximumListedAddons)
                    {
                        truncated = true;
                        return;
                    }

                    long sizeBytes;
                    try
                    {
                        sizeBytes = Math.Max(0, file.Length);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    destination.Add(new ProductServerAddonSummary(target.Kind, fileName, sizeBytes));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A concurrently changing directory cannot widen the boundary. Continue with any
                // other fixed add-on directory and return only entries captured safely.
            }
        }
    }

    private ProductServerJavaRuntimeSummary CaptureJava(
        ProductServerRegistration registration,
        CancellationToken cancellationToken)
    {
        var configured = !string.IsNullOrWhiteSpace(registration.JavaRuntimePath);
        var available = false;
        string? version = null;
        int? majorVersion = null;
        var vendor = "Managed Java";
        var architecture = "Unknown";
        var runtimeKind = "Unknown";

        try
        {
            var javaPath = ResolveExistingOwnedPath(
                layout.Runtimes,
                registration.JavaRuntimePath,
                directory: false);
            if (javaPath is not null)
            {
                available = true;
                var binDirectory = Path.GetDirectoryName(javaPath);
                var runtimeHome = binDirectory is null ? null : Directory.GetParent(binDirectory)?.FullName;
                if (runtimeHome is not null && Directory.Exists(runtimeHome))
                {
                    runtimeHome = SafePath.EnsureNoReparsePointsUnderRoot(layout.Runtimes, runtimeHome);
                    ReadJavaReleaseMetadata(
                        runtimeHome,
                        cancellationToken,
                        ref version,
                        ref vendor,
                        ref architecture);
                    runtimeKind = ResolveRuntimeKind(runtimeHome, registration.JavaRuntimePath);
                }
            }
        }
        catch (Exception error) when (IsExpectedReadFailure(error, cancellationToken))
        {
            available = false;
        }

        majorVersion = TryParseJavaMajor(version) ?? TryParseJavaMajorFromManagedPath(registration.JavaRuntimePath);
        if (string.Equals(vendor, "Managed Java", StringComparison.Ordinal) &&
            registration.JavaRuntimePath.Contains("temurin-", StringComparison.OrdinalIgnoreCase))
        {
            vendor = "Eclipse Temurin";
        }

        return new ProductServerJavaRuntimeSummary(
            configured,
            available,
            majorVersion,
            BoundMetadata(version),
            BoundMetadata(runtimeKind) ?? "Unknown",
            BoundMetadata(vendor) ?? "Managed Java",
            BoundMetadata(architecture) ?? "Unknown");
    }

    private void ReadJavaReleaseMetadata(
        string runtimeHome,
        CancellationToken cancellationToken,
        ref string? version,
        ref string vendor,
        ref string architecture)
    {
        var releasePath = Path.Combine(runtimeHome, "release");
        if (!File.Exists(releasePath))
        {
            return;
        }

        releasePath = SafePath.EnsureNoReparsePointsUnderRoot(layout.Runtimes, releasePath);
        var info = new FileInfo(releasePath);
        if (info.Length is <= 0 or > ProductServerAdministrationContract.MaximumJavaReleaseFileBytes)
        {
            return;
        }

        using var stream = new FileStream(
            releasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        for (var lineNumber = 0; lineNumber < 128 && !reader.EndOfStream; lineNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            if (line is null || line.Length > 512)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim().Trim('"');
            var bounded = BoundMetadata(value);
            if (bounded is null)
            {
                continue;
            }

            switch (key)
            {
                case "JAVA_VERSION":
                    version = NormalizeJavaVersion(bounded);
                    break;
                case "IMPLEMENTOR":
                    vendor = NormalizeVendor(bounded);
                    break;
                case "OS_ARCH":
                    architecture = NormalizeArchitecture(bounded);
                    break;
            }
        }
    }

    private string ResolveRuntimeKind(string runtimeHome, string relativeJavaPath)
    {
        if (relativeJavaPath.Contains("temurin-jdk-", StringComparison.OrdinalIgnoreCase))
        {
            return "JDK";
        }
        if (relativeJavaPath.Contains("temurin-jre-", StringComparison.OrdinalIgnoreCase))
        {
            return "JRE";
        }

        var javac = Path.Combine(runtimeHome, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        if (File.Exists(javac))
        {
            SafePath.EnsureNoReparsePointsUnderRoot(layout.Runtimes, javac);
            return "JDK";
        }

        return "JRE";
    }

    private static string? ResolveExistingOwnedPath(
        string ownedRoot,
        string relativePath,
        bool directory)
    {
        var candidate = SafePath.EnsureWithinRoot(ownedRoot, relativePath, allowRoot: false);
        var exists = directory ? Directory.Exists(candidate) : File.Exists(candidate);
        return exists ? SafePath.EnsureNoReparsePointsUnderRoot(ownedRoot, candidate) : null;
    }

    private static IReadOnlyList<AddonTarget> ResolveAddonTargets(string coreTypeName)
    {
        _ = Enum.TryParse<CoreType>(coreTypeName, ignoreCase: true, out var coreType);
        return coreType switch
        {
            CoreType.Vanilla => [],
            CoreType.Fabric or CoreType.Forge or CoreType.NeoForge =>
                [new("mods", ProductServerAddonKind.Mod)],
            CoreType.Mohist or CoreType.Arclight or CoreType.CatServer =>
                [new("mods", ProductServerAddonKind.Mod), new("plugins", ProductServerAddonKind.Plugin)],
            CoreType.Unknown or CoreType.CustomJar =>
                [new("mods", ProductServerAddonKind.Mod), new("plugins", ProductServerAddonKind.Plugin)],
            _ => [new("plugins", ProductServerAddonKind.Plugin)],
        };
    }

    private static bool TryCreateSafeFileName(string value, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character) || character is '/' or '\\' or ':'))
        {
            return false;
        }

        fileName = value.Length <= ProductServerAdministrationContract.MaximumAddonFileNameCharacters
            ? value
            : value[..ProductServerAdministrationContract.MaximumAddonFileNameCharacters];
        return fileName.Length > 0;
    }

    private static string? BoundMetadata(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Any(character => char.IsControl(character) || character is '/' or '\\' or ':'))
        {
            return null;
        }

        return candidate.Length <= ProductServerAdministrationContract.MaximumJavaMetadataCharacters
            ? candidate
            : candidate[..ProductServerAdministrationContract.MaximumJavaMetadataCharacters];
    }

    private static int? TryParseJavaMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = JavaVersionPattern().Match(version);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var first))
        {
            return null;
        }

        if (first == 1 && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var legacy))
        {
            return legacy is >= 1 and <= 99 ? legacy : null;
        }

        return first is >= 1 and <= 99 ? first : null;
    }

    private static int? TryParseJavaMajorFromManagedPath(string relativePath)
    {
        var match = ManagedRuntimePattern().Match(relativePath.Replace('\\', '/'));
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) && major is >= 1 and <= 99
            ? major
            : null;
    }

    private static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" => "x64",
        "aarch64" or "arm64" => "arm64",
        "x86" or "i386" or "i686" => "x86",
        _ => "Unknown",
    };

    private static string? NormalizeJavaVersion(string value)
        => value.Length <= ProductServerAdministrationContract.MaximumJavaMetadataCharacters &&
           char.IsAsciiDigit(value[0]) &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '_')
            ? value
            : null;

    private static string NormalizeVendor(string value)
    {
        var candidate = value.ToLowerInvariant();
        if (candidate.Contains("adoptium", StringComparison.Ordinal) ||
            candidate.Contains("temurin", StringComparison.Ordinal)) return "Eclipse Adoptium";
        if (candidate.Contains("microsoft", StringComparison.Ordinal)) return "Microsoft";
        if (candidate.Contains("oracle", StringComparison.Ordinal)) return "Oracle";
        if (candidate.Contains("amazon", StringComparison.Ordinal) ||
            candidate.Contains("corretto", StringComparison.Ordinal)) return "Amazon Corretto";
        if (candidate.Contains("azul", StringComparison.Ordinal) ||
            candidate.Contains("zulu", StringComparison.Ordinal)) return "Azul";
        if (candidate.Contains("bellsoft", StringComparison.Ordinal) ||
            candidate.Contains("liberica", StringComparison.Ordinal)) return "BellSoft";
        if (candidate.Contains("red hat", StringComparison.Ordinal)) return "Red Hat";
        if (candidate.Contains("sap", StringComparison.Ordinal)) return "SAP";
        if (candidate.Contains("ibm", StringComparison.Ordinal) ||
            candidate.Contains("semeru", StringComparison.Ordinal)) return "IBM Semeru";
        return "Managed Java";
    }

    private static bool IsExpectedReadFailure(Exception error, CancellationToken cancellationToken)
        => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException ||
           error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    [GeneratedRegex("^(?:1\\.)?([0-9]{1,2})(?:\\.([0-9]{1,2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex JavaVersionPattern();

    [GeneratedRegex("(?:^|/)temurin-(?:jre|jdk)-([0-9]{1,2})(?:-|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManagedRuntimePattern();

    private sealed record AddonTarget(string DirectoryName, ProductServerAddonKind Kind);
}
