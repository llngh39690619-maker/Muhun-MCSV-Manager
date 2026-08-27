using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public sealed record ProviderPackageInstallRequest(
    string PackagePath,
    string ExpectedSha256,
    string ExpectedProviderId,
    string ExpectedVersion,
    string ExpectedPublisherId,
    ProviderPackageSignature Signature,
    bool AllowDowngrade = false);

public sealed record ProviderPackageInstallResult(
    ProviderRegistration Registration,
    string InstalledDirectory);

public sealed class ProviderPackageInstaller(
    ProviderHostLayout layout,
    ProviderRegistry registry,
    IProviderPackageTrustVerifier trustVerifier,
    TimeProvider? timeProvider = null)
{
    public const string ManifestFileName = "provider.manifest.json";
    public const long MaximumPackageBytes = 256L * 1024 * 1024;
    public const long MaximumUncompressedBytes = 512L * 1024 * 1024;
    public const long MaximumEntryBytes = 128L * 1024 * 1024;
    public const long MaximumManifestBytes = 128L * 1024;
    public const int MaximumEntries = 4096;
    public const double MaximumCompressionRatio = 200d;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public async Task<ProviderPackageInstallResult> InstallAsync(
        ProviderPackageInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInstallRequest(request);
        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            layout.EnsureCreated();
            var packagePath = Path.GetFullPath(request.PackagePath);
            var packageInfo = new FileInfo(packagePath);
            if (!packageInfo.Exists || packageInfo.Length is < 1 or > MaximumPackageBytes ||
                packageInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Provider package file is unavailable or outside its size limit.");
            }

            await using var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
            var expectedDigest = Convert.FromHexString(request.ExpectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(digest, expectedDigest))
            {
                throw new CryptographicException("Provider package SHA-256 does not match the expected digest.");
            }

            var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
            var trust = await trustVerifier.VerifyAsync(
                    new ProviderPackageTrustContext(packageInfo.Name, packageInfo.Length, digestHex),
                    request.Signature,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!trust.IsTrusted)
            {
                throw new CryptographicException("Provider publisher/signature verification failed.");
            }

            packageStream.Position = 0;
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            var plan = await InspectAsync(archive, request, cancellationToken).ConfigureAwait(false);
            if (registry.TryGet(plan.Manifest.Id, out var current))
            {
                if (!string.Equals(
                        current.PublisherId,
                        request.ExpectedPublisherId,
                        StringComparison.Ordinal))
                {
                    throw new CryptographicException(
                        "A different publisher cannot replace an installed provider identity.");
                }

                if (!request.AllowDowngrade &&
                    ProviderSemanticVersionComparer.Compare(
                        plan.Manifest.Version,
                        current.Manifest.Version) < 0)
                {
                    throw new InvalidOperationException(
                        "Provider version downgrade requires an explicit rollback authorization.");
                }
            }

            var staging = Path.Combine(layout.Packages, $".install-{Guid.NewGuid():N}");
            var destination = Path.Combine(
                layout.Packages,
                plan.Manifest.Id,
                plan.Manifest.Version);
            var promoted = false;
            try
            {
                Directory.CreateDirectory(staging);
                ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Root, staging);
                await ExtractAsync(archive, plan, staging, cancellationToken).ConfigureAwait(false);
                ProviderPathSafety.EnsureTreeHasNoReparsePoints(staging, MaximumEntries + 1);

                var entryPoint = ProviderPathSafety.ResolveOwnedRelativePath(staging, plan.Manifest.EntryPoint);
                var entryPointInfo = new FileInfo(entryPoint);
                if (!entryPointInfo.Exists || entryPointInfo.Length < 1 ||
                    entryPointInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Provider entry point is missing or unsafe.");
                }

                ProviderPathSafety.CreateSafeParentDirectories(layout.Packages, destination);
                ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Root, Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new IOException("This provider package version is already installed.");
                }

                Directory.Move(staging, destination);
                promoted = true;
                ProviderPathSafety.EnsureTreeHasNoReparsePoints(destination, MaximumEntries + 1);
                var now = _timeProvider.GetUtcNow();
                var relative = Path.GetRelativePath(layout.Root, destination).Replace('\\', '/');
                var registration = new ProviderRegistration(
                    plan.Manifest,
                    request.ExpectedPublisherId,
                    digestHex,
                    relative,
                    false,
                    ProviderHealthStatus.Disabled,
                    now,
                    now,
                    0,
                    null);
                await registry.UpsertAsync(registration, cancellationToken).ConfigureAwait(false);
                return new ProviderPackageInstallResult(
                    ProviderRegistrationValidator.Clone(registration),
                    destination);
            }
            catch
            {
                if (promoted)
                {
                    ProviderPathSafety.DeleteOwnedTree(destination);
                }

                throw;
            }
            finally
            {
                ProviderPathSafety.DeleteOwnedTree(staging);
            }
        }
        finally
        {
            _installGate.Release();
        }
    }

    private static async Task<PackagePlan> InspectAsync(
        ZipArchive archive,
        ProviderPackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count is < 2 or > MaximumEntries)
        {
            throw new InvalidDataException("Provider package entry count is outside its allowed range.");
        }

        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PlannedEntry>(archive.Entries.Count);
        ZipArchiveEntry? manifestEntry = null;
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderPathSafety.RejectArchiveLink(entry);
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var rawName = isDirectory ? entry.FullName[..^1] : entry.FullName;
            var relativePath = ProviderPathSafety.NormalizeRelativePath(rawName);
            RegisterArchivePath(relativePath, isDirectory, filePaths, directoryPaths, declaredPaths);

            if (isDirectory && entry.Length != 0)
            {
                throw new InvalidDataException("Provider package directory has file content.");
            }

            if (entry.Length < 0 || entry.Length > MaximumEntryBytes ||
                entry.Length > MaximumUncompressedBytes - totalLength)
            {
                throw new InvalidDataException("Provider package exceeds its uncompressed size limit.");
            }

            totalLength += entry.Length;

            if (!isDirectory && entry.Length > 0 &&
                (entry.CompressedLength <= 0 ||
                 (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new InvalidDataException("Provider package compression ratio is unsafe.");
            }

            if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory || !entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal) ||
                    manifestEntry is not null)
                {
                    throw new InvalidDataException("Provider manifest must be one exact root-level file.");
                }

                manifestEntry = entry;
            }

            entries.Add(new PlannedEntry(entry, relativePath, isDirectory, entry.Length, null));
        }

        if (manifestEntry is null || manifestEntry.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Provider manifest is missing or outside its size limit.");
        }

        ProductProviderManifest manifest;
        try
        {
            await using var manifestStream = manifestEntry.Open();
            using var document = await JsonDocument.ParseAsync(
                    manifestStream,
                    new JsonDocumentOptions { MaxDepth = 24 },
                    cancellationToken)
                .ConfigureAwait(false);
            RejectDuplicateProperties(document.RootElement);
            manifest = document.RootElement.Deserialize<ProductProviderManifest>(JsonOptions)
                       ?? throw new InvalidDataException("Provider manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Provider manifest JSON is invalid.", error);
        }

        var validation = ProductProviderManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Provider manifest validation failed: " + string.Join(" ", validation.Errors));
        }

        if (!string.Equals(manifest.Id, request.ExpectedProviderId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, request.ExpectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider manifest identity/version does not match the install request.");
        }

        if (!string.Equals(request.Signature.PublisherId, request.ExpectedPublisherId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider signature publisher does not match the expected publisher.");
        }

        var payloadFiles = entries
            .Where(entry => !entry.IsDirectory &&
                            !entry.RelativePath.Equals(ManifestFileName, StringComparison.Ordinal))
            .ToArray();
        if (payloadFiles.Length != manifest.FileSha256.Count)
        {
            throw new InvalidDataException("Provider payload files do not match the signed digest table.");
        }

        var verifiedEntries = new List<PlannedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || entry.RelativePath.Equals(ManifestFileName, StringComparison.Ordinal))
            {
                verifiedEntries.Add(entry);
                continue;
            }

            var declared = manifest.FileSha256
                .Where(pair => pair.Key.Equals(entry.RelativePath, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .SingleOrDefault();
            if (declared is null)
            {
                throw new InvalidDataException("Provider payload files do not match the signed digest table.");
            }

            verifiedEntries.Add(entry with { ExpectedSha256 = declared });
        }

        return new PackagePlan(manifest, verifiedEntries);
    }

    private static void RegisterArchivePath(
        string path,
        bool isDirectory,
        ISet<string> files,
        ISet<string> directories,
        ISet<string> declaredPaths)
    {
        var declarationKey = isDirectory ? path + "/" : path;
        if (!declaredPaths.Add(declarationKey))
        {
            throw new InvalidDataException("Provider package contains duplicate normalized paths.");
        }

        if (isDirectory)
        {
            if (files.Contains(path))
            {
                throw new InvalidDataException("Provider package contains a file/directory path conflict.");
            }

            directories.Add(path);
        }
        else
        {
            if (files.Contains(path) || directories.Contains(path))
            {
                throw new InvalidDataException("Provider package contains a file/directory path conflict.");
            }

            files.Add(path);
        }

        var separator = path.IndexOf('/');
        while (separator >= 0)
        {
            var parent = path[..separator];
            if (files.Contains(parent))
            {
                throw new InvalidDataException("Provider package contains a file/directory path conflict.");
            }

            directories.Add(parent);
            separator = path.IndexOf('/', separator + 1);
        }
    }

    private static async Task ExtractAsync(
        ZipArchive archive,
        PackagePlan plan,
        string staging,
        CancellationToken cancellationToken)
    {
        foreach (var planned in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderPathSafety.RejectArchiveLink(planned.Entry);
            if (planned.Entry.Length != planned.ExpectedLength)
            {
                throw new InvalidDataException("Provider package changed after validation.");
            }

            var destination = ProviderPathSafety.ResolveOwnedRelativePath(staging, planned.RelativePath);
            if (planned.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(staging, destination);
                continue;
            }

            ProviderPathSafety.CreateSafeParentDirectories(staging, destination);
            await using var input = planned.Entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var contentHash = planned.ExpectedSha256 is null
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > planned.ExpectedLength)
                {
                    throw new InvalidDataException("Provider package entry exceeded its declared length.");
                }

                contentHash?.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (written != planned.ExpectedLength)
            {
                throw new InvalidDataException("Provider package entry length does not match its declaration.");
            }

            if (contentHash is not null)
            {
                var actual = contentHash.GetHashAndReset();
                var expected = Convert.FromHexString(planned.ExpectedSha256!);
                var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
                CryptographicOperations.ZeroMemory(actual);
                CryptographicOperations.ZeroMemory(expected);
                if (!matches)
                {
                    throw new CryptographicException("Provider payload file does not match its signed digest.");
                }
            }
        }
    }

    private static void ValidateInstallRequest(ProviderPackageInstallRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedPublisherId);
        ArgumentNullException.ThrowIfNull(request.Signature);
        if (!string.Equals(
                request.Signature.PublisherId,
                request.ExpectedPublisherId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Expected provider publisher does not match the detached signature publisher.",
                nameof(request));
        }

        if (request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected provider package SHA-256 is invalid.", nameof(request));
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Provider manifest contains duplicate JSON properties.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record PlannedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory,
        long ExpectedLength,
        string? ExpectedSha256);

    private sealed record PackagePlan(
        ProductProviderManifest Manifest,
        IReadOnlyList<PlannedEntry> Entries);
}

public static class ProviderSemanticVersionComparer
{
    public static int Compare(string left, string right)
    {
        var leftParts = Parse(left);
        var rightParts = Parse(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = CompareNumeric(leftParts.Core[index], rightParts.Core[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (leftParts.Prerelease.Length == 0 || rightParts.Prerelease.Length == 0)
        {
            return leftParts.Prerelease.Length == rightParts.Prerelease.Length
                ? 0
                : leftParts.Prerelease.Length == 0 ? 1 : -1;
        }

        var count = Math.Min(leftParts.Prerelease.Length, rightParts.Prerelease.Length);
        for (var index = 0; index < count; index++)
        {
            var leftIdentifier = leftParts.Prerelease[index];
            var rightIdentifier = rightParts.Prerelease[index];
            var leftNumeric = leftIdentifier.All(char.IsAsciiDigit);
            var rightNumeric = rightIdentifier.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric
                ? CompareNumeric(leftIdentifier, rightIdentifier)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.Compare(leftIdentifier, rightIdentifier, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Prerelease.Length.CompareTo(rightParts.Prerelease.Length);
    }

    private static (string[] Core, string[] Prerelease) Parse(string value)
    {
        var split = value.Split('-', 2);
        return (
            split[0].Split('.'),
            split.Length == 1 ? [] : split[1].Split('.'));
    }

    private static int CompareNumeric(string left, string right)
    {
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : string.Compare(left, right, StringComparison.Ordinal);
    }
}
