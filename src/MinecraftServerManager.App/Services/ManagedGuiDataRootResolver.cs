using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.App.Services;

internal sealed record ManagedGuiDataRootBinding(
    string InstallRoot,
    string UserDataRoot,
    string ProductExchangeRoot,
    string Channel,
    string CurrentUserSid);

/// <summary>
/// Binds mutable GUI data to the installation selected by the elevated installer. The binding is
/// accepted only when the running executable is the active GUI inside a marker-owned A/B slot.
/// This prevents a loose release, copied EXE, working directory or environment variable from
/// silently recreating product data elsewhere.
/// </summary>
internal static partial class ManagedGuiDataRootResolver
{
    private const string GuiFileName = "Muhun MCSV Manager.exe";
    private const string GuiPayloadDirectoryName = "gui-win-x64";
    private const string VersionsDirectoryName = "versions";
    private const string InstallMarkerFileName = ".muhun-mcsv-install-root";
    private const string ExpectedInstallMarker = "muhun.mcsv.manager:1";
    private const string ActiveVersionFileName = "active-version.v1";
    private const string InstalledVersionFileName = "installed-version.v1.json";
    private const string ProductId = "muhun.mcsv.manager";
    private const string ExpectedEntryPoint = "gui-win-x64/Muhun MCSV Manager.exe";
    private const int MaximumSmallMetadataBytes = 8 * 1024;

    public static ManagedGuiDataRootBinding Resolve(
        string? guiExecutablePath,
        string? currentUserSid = null)
    {
        var guiPath = RequireLocalRegularFile(guiExecutablePath, GuiFileName);
        var guiDirectory = Directory.GetParent(guiPath)?.FullName
            ?? throw InvalidLayout("GUI payload directory is unavailable.");
        if (!string.Equals(
                Path.GetFileName(guiDirectory),
                GuiPayloadDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidLayout("GUI is not inside a managed gui-win-x64 payload.");
        }

        var versionRoot = Directory.GetParent(guiDirectory)?.FullName
            ?? throw InvalidLayout("Managed version directory is unavailable.");
        var versionsRoot = Directory.GetParent(versionRoot)?.FullName
            ?? throw InvalidLayout("Managed versions directory is unavailable.");
        if (!string.Equals(
                Path.GetFileName(versionsRoot),
                VersionsDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidLayout("GUI is not inside the managed versions directory.");
        }

        var installRoot = Directory.GetParent(versionsRoot)?.FullName
            ?? throw InvalidLayout("Managed installation root is unavailable.");
        installRoot = NormalizeSafeLocalDirectory(installRoot, "installation root");
        RejectExistingReparsePoints(installRoot);
        RequireExactTextFile(
            Path.Combine(installRoot, InstallMarkerFileName),
            ExpectedInstallMarker,
            "installation marker");

        var version = Path.GetFileName(versionRoot);
        ValidateSemanticVersion(version);
        RequireExactTextFile(
            Path.Combine(installRoot, ActiveVersionFileName),
            version,
            "active version pointer");
        ValidateInstalledVersionMetadata(versionRoot, version);

        var sid = ResolveCanonicalSid(currentUserSid);
        var channel = version.Contains('-', StringComparison.Ordinal) ? "beta" : "stable";
        var userDataRoot = Path.Combine(installRoot, "users", sid, channel);
        var exchangeRoot = Path.Combine(installRoot, "exchange", channel);
        EnsureLexicallyUnderRoot(installRoot, userDataRoot);
        EnsureLexicallyUnderRoot(installRoot, exchangeRoot);
        return new ManagedGuiDataRootBinding(
            installRoot,
            userDataRoot,
            exchangeRoot,
            channel,
            sid);
    }

    public static void EnsureDirectoryUnderInstallRoot(string installRoot, string directoryPath)
    {
        var root = NormalizeSafeLocalDirectory(installRoot, "installation root");
        var target = Path.GetFullPath(directoryPath);
        EnsureLexicallyUnderRoot(root, target);
        RejectExistingReparsePoints(root);
        RequireExactTextFile(
            Path.Combine(root, InstallMarkerFileName),
            ExpectedInstallMarker,
            "installation marker");

        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw new IOException($"Managed data directory conflicts with a file: {current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"Managed data directories cannot traverse a reparse point: {current}");
            }
        }
    }

    private static void ValidateInstalledVersionMetadata(string versionRoot, string version)
    {
        var path = RequireLocalRegularFile(
            Path.Combine(versionRoot, InstalledVersionFileName),
            InstalledVersionFileName);
        var bytes = ReadBoundedFile(path, "installed version metadata");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            var allowed = new HashSet<string>(
                ["schemaVersion", "productId", "version", "entryPoint"],
                StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => !allowed.Remove(property.Name)) ||
                allowed.Count != 0 ||
                root.GetProperty("schemaVersion").GetInt32() != 1 ||
                !string.Equals(root.GetProperty("productId").GetString(), ProductId, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("version").GetString(), version, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("entryPoint").GetString(), ExpectedEntryPoint, StringComparison.Ordinal))
            {
                throw InvalidLayout("Installed version metadata does not match the active GUI.");
            }
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw InvalidLayout("Installed version metadata is invalid.", exception);
        }
    }

    private static string ResolveCanonicalSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Managed GUI data roots require a Windows user SID.");
            }

            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            value = identity.User?.Value;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidLayout("The current Windows user SID is unavailable.");
        }

        try
        {
            return new SecurityIdentifier(value).Value;
        }
        catch (ArgumentException exception)
        {
            throw InvalidLayout("The current Windows user SID is invalid.", exception);
        }
    }

    private static string RequireLocalRegularFile(string? path, string requiredFileName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw InvalidLayout("A managed executable or metadata path is unavailable.");
        }

        var fullPath = Path.GetFullPath(path);
        _ = NormalizeSafeLocalDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw InvalidLayout("A managed file has no parent directory."),
            "managed file parent");
        RejectExistingReparsePoints(fullPath);
        if (!string.Equals(Path.GetFileName(fullPath), requiredFileName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw InvalidLayout($"Required managed file is missing or unsafe: {requiredFileName}");
        }

        return fullPath;
    }

    private static byte[] ReadBoundedFile(string path, string label)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4_096,
            FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumSmallMetadataBytes)
        {
            throw InvalidLayout($"The {label} has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireExactTextFile(string path, string expected, string label)
    {
        path = RequireLocalRegularFile(path, Path.GetFileName(path));
        string actual;
        try
        {
            actual = new UTF8Encoding(false, true).GetString(ReadBoundedFile(path, label)).Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidLayout($"The {label} is not valid UTF-8.", exception);
        }

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw InvalidLayout($"The {label} does not match this managed installation.");
        }
    }

    private static string NormalizeSafeLocalDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw InvalidLayout($"The {label} must be an absolute local path.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(volumeRoot) ||
            string.Equals(fullPath, volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidLayout($"The {label} cannot be a UNC path or volume root.");
        }

        return fullPath;
    }

    private static void EnsureLexicallyUnderRoot(string root, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
                     Path.DirectorySeparatorChar;
        var normalized = Path.GetFullPath(candidate);
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidLayout("Managed data path escaped the selected installation root.");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw InvalidLayout("Managed installation paths cannot traverse a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }

    private static void ValidateSemanticVersion(string value)
    {
        if (!SemanticVersionPattern().IsMatch(value))
        {
            throw InvalidLayout("The managed version directory name is invalid.");
        }
    }

    private static InvalidDataException InvalidLayout(string message, Exception? inner = null)
        => new(
            message + " Reinstall or repair X MCSV instead of creating data in another location.",
            inner);

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-(?:[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}
