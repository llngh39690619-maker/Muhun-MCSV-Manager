namespace MinecraftServerManager.Updater;

/// <summary>
/// Count bounds for signed-update artifacts. Every retained package and installed version has a
/// separate signed-format size ceiling, so these count limits also establish a storage ceiling.
/// Protected activation versions are always retained in addition to these cache allowances.
/// </summary>
public sealed record ProductUpdateRetentionPolicy(
    int MaximumUnprotectedInstalledVersions = 2,
    int MaximumUnprotectedPackages = 2,
    int MaximumUnprotectedVerifiedManifests = 2)
{
    public static ProductUpdateRetentionPolicy Default { get; } = new();
}

public sealed record ProductUpdateRetentionResult(
    bool SkippedBecauseUpdaterLeaseUnavailable,
    int InstalledVersionsRemoved,
    int PackagesRemoved,
    int VerifiedManifestCachesRemoved,
    int StagingDirectoriesRemoved,
    int VerificationDirectoriesRemoved,
    int FailedArtifacts)
{
    public int TotalRemoved =>
        InstalledVersionsRemoved +
        PackagesRemoved +
        VerifiedManifestCachesRemoved +
        StagingDirectoriesRemoved +
        VerificationDirectoriesRemoved;
}

public enum ProductUpdateRetentionCheckpoint
{
    ArtifactQuarantined,
}

internal sealed class ProductUpdateRetentionInterruptionException(string checkpoint)
    : Exception($"Simulated update-retention interruption at {checkpoint}.");

/// <summary>
/// Performs bounded, crash-safe cleanup of update-owned cache and A/B version artifacts. The
/// caller must serialize this operation with Service update downloads. This type additionally
/// takes the Updater's cross-process lease before inspecting or moving any artifact.
/// </summary>
public sealed class ProductUpdateRetentionManager
{
    private const string InstallMarkerFileName = ".muhun-mcsv-install-root";
    private const string InstallMarkerValue = "muhun.mcsv.manager:1";
    private const string VersionsDirectoryName = "versions";
    private const string PackagesDirectoryName = "packages";
    private const string VerificationDirectoryName = "verification";
    private const string UpdaterLeaseFileName = ".updater.lock";
    private readonly string _installRoot;
    private readonly string _updatesRoot;
    private readonly string _executingVersion;
    private readonly ProductUpdateRetentionPolicy _policy;
    private readonly Action<ProductUpdateRetentionCheckpoint>? _checkpointObserver;

    public ProductUpdateRetentionManager(
        string installRoot,
        string updatesRoot,
        string executingVersion,
        ProductUpdateRetentionPolicy? policy = null,
        Action<ProductUpdateRetentionCheckpoint>? checkpointObserver = null)
    {
        _installRoot = NormalizeAbsolutePath(installRoot, "Product install root");
        _updatesRoot = NormalizeAbsolutePath(updatesRoot, "Product updates root");
        _executingVersion = executingVersion;
        ProductUpdateManifestParser.ValidateVersion(_executingVersion);
        _policy = policy ?? ProductUpdateRetentionPolicy.Default;
        if (_policy.MaximumUnprotectedInstalledVersions is < 0 or > 16 ||
            _policy.MaximumUnprotectedPackages is < 0 or > 16 ||
            _policy.MaximumUnprotectedVerifiedManifests is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Update retention counts must be between zero and sixteen.");
        }

        _checkpointObserver = checkpointObserver;
    }

    public ProductUpdateRetentionResult Run(
        IEnumerable<string>? additionalProtectedVersions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoots();

        using var updaterLease = TryAcquireUpdaterLease();
        if (updaterLease is null)
        {
            return new ProductUpdateRetentionResult(true, 0, 0, 0, 0, 0, 0);
        }

        // Durable state is parsed strictly before the first mutation. A malformed pointer,
        // pending operation or journal therefore fails closed and preserves every artifact.
        var activeVersion = ProductUpdateActivator.ReadActiveVersion(_installRoot);
        var journal = ProductUpdateActivator.ReadActivationJournal(_installRoot);
        var pending = ProductUpdatePendingActivationProtocol.Read(_updatesRoot);
        var protectedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProtectVersion(protectedVersions, activeVersion);
        ProtectVersion(protectedVersions, _executingVersion);
        if (journal is not null)
        {
            // PreviousVersion is the durable rollback slot. TargetVersion remains useful for a
            // terminal operation's diagnosis/retry and is mandatory for non-terminal recovery.
            ProtectVersion(protectedVersions, journal.PreviousVersion);
            ProtectVersion(protectedVersions, journal.TargetVersion);
        }

        if (pending is not null)
        {
            ProtectVersion(protectedVersions, pending.Version);
        }

        if (additionalProtectedVersions is not null)
        {
            foreach (var version in additionalProtectedVersions)
            {
                ProtectVersion(protectedVersions, version);
            }
        }

        var versionsRoot = Path.Combine(_installRoot, VersionsDirectoryName);
        EnsureExistingRegularDirectory(versionsRoot, "Managed versions directory");
        var activeDirectory = Path.Combine(versionsRoot, activeVersion);
        var executingDirectory = Path.Combine(versionsRoot, _executingVersion);
        EnsureExistingRegularDirectory(activeDirectory, "Active version directory");
        EnsureExistingRegularDirectory(executingDirectory, "Executing version directory");

        var packagesRoot = EnsureOptionalOwnedDirectory(PackagesDirectoryName);
        var verifiedRoot = EnsureOptionalOwnedDirectory(
            ProductUpdateActivationRequestProtocol.VerifiedDirectoryName);
        var verificationRoot = EnsureOptionalOwnedDirectory(VerificationDirectoryName);

        var counts = new MutableResult();
        CleanupDirectoryTombstones(
            versionsRoot,
            ["version", "staging"],
            counts,
            cancellationToken);
        CleanupDirectoryTombstones(
            verifiedRoot,
            ["cache"],
            counts,
            cancellationToken);
        CleanupDirectoryTombstones(
            verificationRoot,
            ["verification"],
            counts,
            cancellationToken);
        CleanupFileTombstones(packagesRoot, "package", counts, cancellationToken);

        var staleVersions = SelectStaleDirectories(
            versionsRoot,
            protectedVersions,
            _policy.MaximumUnprotectedInstalledVersions,
            TryGetDirectoryVersion);
        foreach (var candidate in staleVersions)
        {
            if (TryDeleteDirectory(candidate.FullName, versionsRoot, "version", cancellationToken))
            {
                counts.InstalledVersionsRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }

        foreach (var staging in EnumerateImmediateDirectories(versionsRoot)
                     .Where(directory => TryGetStagingVersion(directory.Name, out _)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteDirectory(staging.FullName, versionsRoot, "staging", cancellationToken))
            {
                counts.StagingDirectoriesRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }

        var stalePackages = SelectStaleFiles(
            packagesRoot,
            protectedVersions,
            _policy.MaximumUnprotectedPackages);
        foreach (var package in stalePackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteFile(package.FullName, packagesRoot, "package"))
            {
                counts.PackagesRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }

        var staleVerified = SelectStaleDirectories(
            verifiedRoot,
            protectedVersions,
            _policy.MaximumUnprotectedVerifiedManifests,
            TryGetDirectoryVersion);
        foreach (var cache in staleVerified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteDirectory(cache.FullName, verifiedRoot, "cache", cancellationToken))
            {
                counts.VerifiedManifestCachesRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }

        foreach (var verification in EnumerateImmediateDirectories(verificationRoot)
                     .Where(directory => Guid.TryParseExact(directory.Name, "N", out _)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteDirectory(
                    verification.FullName,
                    verificationRoot,
                    "verification",
                    cancellationToken))
            {
                counts.VerificationDirectoriesRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }

        return counts.ToResult();
    }

    private void ValidateRoots()
    {
        if (!string.Equals(
                Path.GetFileName(_updatesRoot),
                "updates",
                StringComparison.OrdinalIgnoreCase) ||
            PathsEqual(_updatesRoot, _installRoot))
        {
            throw new InvalidDataException("Product updates root does not have the fixed updates identity.");
        }

        EnsureExistingRegularDirectory(_installRoot, "Product install root");
        var marker = Path.Combine(_installRoot, InstallMarkerFileName);
        EnsureRegularFile(marker, "Product install marker");
        var markerInfo = new FileInfo(marker);
        if (markerInfo.Length is < 1 or > 128 ||
            !string.Equals(File.ReadAllText(marker).Trim(), InstallMarkerValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Product install root marker is missing or invalid.");
        }

        EnsureDirectoryPathCanBeCreated(_updatesRoot);
        Directory.CreateDirectory(_updatesRoot);
        EnsureExistingRegularDirectory(_updatesRoot, "Product updates root");
    }

    private FileStream? TryAcquireUpdaterLease()
    {
        var activationRoot = Path.Combine(
            _installRoot,
            ProductUpdateActivator.ActivationStateDirectoryName);
        EnsureDirectoryPathCanBeCreated(activationRoot);
        Directory.CreateDirectory(activationRoot);
        EnsureExistingRegularDirectory(activationRoot, "Product activation-state directory");
        var leasePath = Path.Combine(activationRoot, UpdaterLeaseFileName);
        if (File.Exists(leasePath))
        {
            EnsureRegularFile(leasePath, "Product updater lease");
        }

        try
        {
            return new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            // The signed Updater owns this lease while it provisions, switches, health-checks,
            // rolls back or finalizes a journal. Retention simply retries on the next cycle.
            return null;
        }
    }

    private string EnsureOptionalOwnedDirectory(string leafName)
    {
        var path = Path.Combine(_updatesRoot, leafName);
        EnsureDirectoryPathCanBeCreated(path);
        Directory.CreateDirectory(path);
        EnsureExistingRegularDirectory(path, $"Update-owned {leafName} directory");
        return path;
    }

    private IReadOnlyList<DirectoryInfo> SelectStaleDirectories(
        string root,
        IReadOnlySet<string> protectedVersions,
        int retainedUnprotected,
        TryGetVersion tryGetVersion)
    {
        var candidates = new List<VersionedDirectory>();
        foreach (var directory in EnumerateImmediateDirectories(root))
        {
            if (!tryGetVersion(directory.Name, out var version) ||
                protectedVersions.Contains(version) ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            candidates.Add(new VersionedDirectory(directory, SemanticVersion.Parse(version)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.Directory.Name, StringComparer.Ordinal)
            .Skip(retainedUnprotected)
            .Select(candidate => candidate.Directory)
            .ToArray();
    }

    private IReadOnlyList<FileInfo> SelectStaleFiles(
        string root,
        IReadOnlySet<string> protectedVersions,
        int retainedUnprotected)
    {
        var candidates = new List<VersionedFile>();
        foreach (var file in EnumerateImmediateFiles(root))
        {
            if (!TryGetPackageVersion(file.Name, out var version) ||
                protectedVersions.Contains(version) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            candidates.Add(new VersionedFile(file, SemanticVersion.Parse(version)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.File.Name, StringComparer.Ordinal)
            .Skip(retainedUnprotected)
            .Select(candidate => candidate.File)
            .ToArray();
    }

    private void CleanupDirectoryTombstones(
        string root,
        IReadOnlyCollection<string> kinds,
        MutableResult counts,
        CancellationToken cancellationToken)
    {
        foreach (var directory in EnumerateImmediateDirectories(root))
        {
            var kind = kinds.FirstOrDefault(value => IsTombstone(directory.Name, value));
            if (kind is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteQuarantinedDirectory(directory.FullName, root, cancellationToken))
            {
                IncrementDirectoryCount(counts, kind);
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }
    }

    private void CleanupFileTombstones(
        string root,
        string kind,
        MutableResult counts,
        CancellationToken cancellationToken)
    {
        foreach (var file in EnumerateImmediateFiles(root).Where(file => IsTombstone(file.Name, kind)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteQuarantinedFile(file.FullName, root))
            {
                counts.PackagesRemoved++;
            }
            else
            {
                counts.FailedArtifacts++;
            }
        }
    }

    private bool TryDeleteDirectory(
        string path,
        string trustedParent,
        string kind,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureImmediateChild(trustedParent, path);
            BuildNoFollowDeletionPlan(path, cancellationToken);
            var quarantine = Path.Combine(trustedParent, TombstoneName(kind));
            Directory.Move(path, quarantine);
            _checkpointObserver?.Invoke(ProductUpdateRetentionCheckpoint.ArtifactQuarantined);
            DeleteTreeWithoutFollowingReparsePoints(quarantine, cancellationToken);
            return true;
        }
        catch (ProductUpdateRetentionInterruptionException)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactFailure(exception, cancellationToken))
        {
            return false;
        }
    }

    private static bool TryDeleteQuarantinedDirectory(
        string path,
        string trustedParent,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureImmediateChild(trustedParent, path);
            DeleteTreeWithoutFollowingReparsePoints(path, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsArtifactFailure(exception, cancellationToken))
        {
            return false;
        }
    }

    private bool TryDeleteFile(string path, string trustedParent, string kind)
    {
        try
        {
            EnsureImmediateChild(trustedParent, path);
            EnsureRegularFile(path, "Cached update package");
            var quarantine = Path.Combine(trustedParent, TombstoneName(kind));
            File.Move(path, quarantine);
            _checkpointObserver?.Invoke(ProductUpdateRetentionCheckpoint.ArtifactQuarantined);
            DeleteRegularFile(quarantine);
            return true;
        }
        catch (ProductUpdateRetentionInterruptionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryDeleteQuarantinedFile(string path, string trustedParent)
    {
        try
        {
            EnsureImmediateChild(trustedParent, path);
            DeleteRegularFile(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(
        string root,
        CancellationToken cancellationToken)
    {
        var plan = BuildNoFollowDeletionPlan(root, cancellationToken);
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteRegularFile(file);
        }

        for (var index = plan.Directories.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = plan.Directories[index];
            var attributes = File.GetAttributes(directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new InvalidDataException("Retention directory changed into a reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(directory, recursive: false);
        }
    }

    private static DeletionPlan BuildNoFollowDeletionPlan(
        string root,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var attributes = File.GetAttributes(directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new InvalidDataException("Retention never follows directory reparse points.");
            }

            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = 0,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                var entryAttributes = File.GetAttributes(entry);
                if (entryAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Retention never follows child reparse points.");
                }

                if (entryAttributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return new DeletionPlan(directories, files);
    }

    private static void DeleteRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException("Retention only deletes regular files.");
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    private static IReadOnlyList<DirectoryInfo> EnumerateImmediateDirectories(string root)
        => new DirectoryInfo(root)
            .EnumerateDirectories(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                })
            .ToArray();

    private static IReadOnlyList<FileInfo> EnumerateImmediateFiles(string root)
        => new DirectoryInfo(root)
            .EnumerateFiles(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                })
            .ToArray();

    private static bool TryGetDirectoryVersion(string name, out string version)
    {
        version = name;
        return TryValidateVersion(version);
    }

    private static bool TryGetPackageVersion(string name, out string version)
    {
        version = string.Empty;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        version = name[..^4];
        return TryValidateVersion(version);
    }

    private static bool TryGetStagingVersion(string name, out string version)
    {
        version = string.Empty;
        if (!name.StartsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var core = name[1..^8];
        var separator = core.LastIndexOf('.');
        if (separator <= 0 ||
            !Guid.TryParseExact(core[(separator + 1)..], "N", out _))
        {
            return false;
        }

        version = core[..separator];
        return TryValidateVersion(version);
    }

    private static bool TryValidateVersion(string value)
    {
        try
        {
            ProductUpdateManifestParser.ValidateVersion(value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ProtectVersion(ISet<string> protectedVersions, string version)
    {
        ProductUpdateManifestParser.ValidateVersion(version);
        protectedVersions.Add(version);
    }

    private static void EnsureDirectoryPathCanBeCreated(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Managed update paths must not traverse a reparse point.");
            }
        }
    }

    private static void EnsureExistingRegularDirectory(string path, string description)
    {
        EnsureDirectoryPathCanBeCreated(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} is missing.");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException($"{description} is not a regular directory.");
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        EnsureDirectoryPathCanBeCreated(Path.GetDirectoryName(path)
                                        ?? throw new InvalidDataException($"{description} parent is invalid."));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} is missing.", path);
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException($"{description} is not a regular file.");
        }
    }

    private static void EnsureImmediateChild(string trustedParent, string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !PathsEqual(parent, trustedParent))
        {
            throw new InvalidDataException("Retention candidate escaped its fixed owned parent.");
        }
    }

    private static string NormalizeAbsolutePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{description} must be absolute.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string TombstoneName(string kind)
        => $".retention-{kind}-{Guid.NewGuid():N}";

    private static bool IsTombstone(string name, string kind)
    {
        var prefix = $".retention-{kind}-";
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static bool IsArtifactFailure(Exception exception, CancellationToken cancellationToken)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException ||
           (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static void IncrementDirectoryCount(MutableResult counts, string kind)
    {
        switch (kind)
        {
            case "version":
                counts.InstalledVersionsRemoved++;
                break;
            case "staging":
                counts.StagingDirectoriesRemoved++;
                break;
            case "cache":
                counts.VerifiedManifestCachesRemoved++;
                break;
            case "verification":
                counts.VerificationDirectoriesRemoved++;
                break;
            default:
                throw new InvalidOperationException("Unknown retention artifact kind.");
        }
    }

    private delegate bool TryGetVersion(string name, out string version);

    private sealed record VersionedDirectory(DirectoryInfo Directory, SemanticVersion Version);

    private sealed record VersionedFile(FileInfo File, SemanticVersion Version);

    private sealed record DeletionPlan(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);

    private sealed class MutableResult
    {
        public int InstalledVersionsRemoved { get; set; }
        public int PackagesRemoved { get; set; }
        public int VerifiedManifestCachesRemoved { get; set; }
        public int StagingDirectoriesRemoved { get; set; }
        public int VerificationDirectoriesRemoved { get; set; }
        public int FailedArtifacts { get; set; }

        public ProductUpdateRetentionResult ToResult()
            => new(
                false,
                InstalledVersionsRemoved,
                PackagesRemoved,
                VerifiedManifestCachesRemoved,
                StagingDirectoriesRemoved,
                VerificationDirectoriesRemoved,
                FailedArtifacts);
    }

    private sealed record SemanticVersion(
        string Major,
        string Minor,
        string Patch,
        IReadOnlyList<string>? Prerelease) : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value)
        {
            ProductUpdateManifestParser.ValidateVersion(value);
            var components = value.Split('-', 2);
            var numbers = components[0].Split('.');
            return new SemanticVersion(
                numbers[0],
                numbers[1],
                numbers[2],
                components.Length == 2 ? components[1].Split('.') : null);
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            var comparison = CompareNumeric(Major, other.Major);
            if (comparison == 0) comparison = CompareNumeric(Minor, other.Minor);
            if (comparison == 0) comparison = CompareNumeric(Patch, other.Patch);
            if (comparison != 0) return comparison;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                comparison = ComparePrereleaseIdentifier(Prerelease[index], other.Prerelease[index]);
                if (comparison != 0) return comparison;
            }

            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            if (leftNumeric && rightNumeric) return CompareNumeric(left, right);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            return StringComparer.Ordinal.Compare(left, right);
        }

        private static int CompareNumeric(string left, string right)
        {
            left = left.TrimStart('0');
            right = right.TrimStart('0');
            if (left.Length == 0) left = "0";
            if (right.Length == 0) right = "0";
            var comparison = left.Length.CompareTo(right.Length);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
