using System.Security.Cryptography;

namespace MinecraftServerManager.Updater;

internal static class ProductLocalServiceRepairApplication
{
    internal const int SuccessExitCode = 0;
    internal const int MalformedCommandExitCode = 2;
    internal const int RolledBackExitCode = 10;
    internal const int CancelledExitCode = 11;
    internal const int ValidationRejectedExitCode = 12;
    internal const int ProvisioningFailedExitCode = 13;
    internal const int ActivationRecoveryFailedExitCode = 14;

    private const string Command = "--repair-product-service";
    private const string ReleaseRootArgument = "--release-root";
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);

    public static bool IsCommand(string[]? args)
        => args is { Length: > 0 } && string.Equals(args[0], Command, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        string[] args,
        Func<bool>? administratorProbe = null,
        Func<string, ProductManagedInstallation>? installationResolver = null,
        Func<string, IProductUpdateHealthController>? healthControllerFactory = null,
        ProductLocalRepairTrustPolicy? trustPolicy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default,
        Action<Exception>? failureObserver = null,
        Action<ProductFormalActivationLayout>? executableVersionValidator = null,
        Action<string, string>? executableSignerValidator = null,
        bool requireRunningFromReleaseUpdater = true,
        Func<string, ProductLocalRepairTrustPolicy, TimeProvider, CancellationToken,
            Task<VerifiedProductLocalRelease>>? verifiedReleaseFactory = null,
        Func<string, string, ProductRepairStagingIdentity>? stagingResolver = null,
        Action<ProductRepairStagingIdentity>? stagingContentValidator = null,
        Func<ProductRepairStagingIdentity, bool>? stagingCleanupScheduler = null,
        Action<ProductRepairStagingIdentity, VerifiedProductLocalRelease, bool>?
            stagedReleaseBindingValidator = null)
    {
        if (!TryParseArguments(args, out var releaseRoot))
        {
            Console.Error.WriteLine("Muhun MCSV Service repair requires one formal release root.");
            return MalformedCommandExitCode;
        }

        ProductRepairStagingIdentity? stagingIdentity = null;
        var exitCode = ValidationRejectedExitCode;
        var failureExitCode = ValidationRejectedExitCode;
        try
        {
            if (!(administratorProbe ?? ProductManagedInstallationResolver.IsAdministrator)())
            {
                throw new UnauthorizedAccessException("Product Service repair requires elevation.");
            }

            var trust = trustPolicy ?? ProductLocalRepairTrustPolicy.Production;
            var clock = timeProvider ?? TimeProvider.System;
            var installation = (installationResolver ?? ProductManagedInstallationResolver.Resolve)(
                trust.PublisherCertificateSha256);
            stagingIdentity = (stagingResolver ?? ProductRepairStagingPolicy.ResolveBoundary)(
                releaseRoot,
                installation.InstallRoot);
            (stagingContentValidator ??
                (identity => ProductRepairStagingPolicy.ValidateProtectedTree(identity)))(stagingIdentity);
            var verifiedRelease = verifiedReleaseFactory is null
                ? await ProductLocalFormalReleaseVerifier.VerifyAsync(
                        releaseRoot,
                        trust,
                        clock,
                        cancellationToken,
                        executableVersionValidator,
                        executableSignerValidator,
                        requireRunningFromReleaseUpdater)
                    .ConfigureAwait(false)
                : await verifiedReleaseFactory(releaseRoot, trust, clock, cancellationToken)
                    .ConfigureAwait(false);
            (stagedReleaseBindingValidator ?? ProductRepairStagingPolicy.ValidateVerifiedRelease)(
                stagingIdentity,
                verifiedRelease,
                requireRunningFromReleaseUpdater);

            var healthController = (healthControllerFactory ??
                (dataRoot => new ProductServiceRepairHealthController(
                    dataRoot,
                    verifiedRelease.UpdateManifest.Version)))(installation.DataRoot);
            using var healthLifetime = healthController as IDisposable;
            var activator = new ProductUpdateActivator(installation.InstallRoot, healthController, clock);
            using var updaterLease = activator.AcquireUpdaterLease();

            var journal = activator.ReadActivationJournal();
            if (journal is not null && !IsTerminal(journal.State))
            {
                failureExitCode = ActivationRecoveryFailedExitCode;
                _ = await activator.RecoverInterruptedActivationAsync(HealthTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            failureExitCode = ValidationRejectedExitCode;
            var revalidatedInstallation = (installationResolver ??
                ProductManagedInstallationResolver.Resolve)(trust.PublisherCertificateSha256);
            ValidateRecoveredInstallation(installation, revalidatedInstallation, activator);
            var activeVersion = activator.ReadActiveVersion();
            if (string.Equals(
                    activeVersion,
                    verifiedRelease.UpdateManifest.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The signed local repair version is already active.");
            }
            if (ProductSemanticVersion.Compare(
                    verifiedRelease.UpdateManifest.Version,
                    activeVersion) <= 0)
            {
                throw new InvalidOperationException("Local Service repair refuses a product downgrade.");
            }

            failureExitCode = ProvisioningFailedExitCode;
            await ProvisionVerifiedReleaseAsync(
                    verifiedRelease,
                    installation.InstallRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            failureExitCode = ActivationRecoveryFailedExitCode;
            var result = await activator.ActivateAsync(
                    verifiedRelease.UpdateManifest,
                    HealthTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            exitCode = result.RolledBack ? RolledBackExitCode : SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitCode = CancelledExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            failureObserver?.Invoke(exception);
            // Keep user-controlled source paths, install paths and trust metadata out of a shell
            // transcript. The GUI converts this stable exit code into a localized repair message.
            Console.Error.WriteLine("Muhun MCSV Service repair was rejected or safely rolled back.");
            exitCode = failureExitCode;
        }

        if (stagingIdentity is not null)
        {
            try
            {
                if (!(stagingCleanupScheduler ??
                      (identity => ProductRepairStagingCleanup.Schedule(identity)))(stagingIdentity))
                {
                    throw new IOException("Protected repair staging cleanup could not be scheduled.");
                }
            }
            catch (Exception exception) when (
                exception is not (OutOfMemoryException or StackOverflowException))
            {
                failureObserver?.Invoke(exception);
                System.Diagnostics.Trace.TraceWarning(
                    "Protected repair staging cleanup could not be scheduled: {0}",
                    exception.GetType().Name);
                // A committed, API-healthy Service remains a successful repair. The residual
                // staging tree is immutable, protected and fully signed; changing exit 0 here
                // would strand the GUI in read-only mode because a retry must reject the now
                // active same version.
            }
        }

        return exitCode;
    }

    private static async Task ProvisionVerifiedReleaseAsync(
        VerifiedProductLocalRelease release,
        string installRoot,
        CancellationToken cancellationToken)
    {
        var root = ProductGuiActivationBroker.ValidateInstallRoot(installRoot);
        var versionsRoot = Path.Combine(root, "versions");
        Directory.CreateDirectory(versionsRoot);
        ProductActivationPathPolicy.RejectExistingReparsePoints(versionsRoot);
        var targetRoot = ProductUpdatePath.ResolveUnderRoot(
            versionsRoot,
            release.UpdateManifest.Version + "/placeholder");
        targetRoot = Directory.GetParent(targetRoot)?.FullName
            ?? throw new InvalidDataException("The target managed version directory is invalid.");
        if (Directory.Exists(targetRoot))
        {
            await ProductInstalledVersionVerifier.VerifyAsync(
                    targetRoot,
                    release.UpdateManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (File.Exists(targetRoot))
        {
            throw new InvalidDataException("The target managed version path is not a directory.");
        }

        var stagingRoot = Path.Combine(
            versionsRoot,
            $".{release.UpdateManifest.Version}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingRoot);
        ProductActivationPathPolicy.RejectExistingReparsePoints(stagingRoot);
        try
        {
            foreach (var file in release.UpdateManifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyAndVerifyAsync(
                        release.ReleaseRoot,
                        stagingRoot,
                        file,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ProductInstalledVersionMetadataStore.Write(
                stagingRoot,
                new ProductInstalledVersionMetadata(
                    1,
                    "muhun.mcsv.manager",
                    release.UpdateManifest.Version,
                    release.UpdateManifest.EntryPoint));
            await ProductInstalledVersionVerifier.VerifyAsync(
                    stagingRoot,
                    release.UpdateManifest,
                    cancellationToken,
                    requireVersionDirectoryName: false)
                .ConfigureAwait(false);

            if (Directory.Exists(targetRoot) || File.Exists(targetRoot))
            {
                throw new IOException("The immutable target version appeared during local repair provisioning.");
            }

            Directory.Move(stagingRoot, targetRoot);
            await ProductInstalledVersionVerifier.VerifyAsync(
                    targetRoot,
                    release.UpdateManifest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteOwnedStagingDirectory(stagingRoot, versionsRoot);
        }
    }

    private static async Task CopyAndVerifyAsync(
        string sourceRoot,
        string stagingRoot,
        ProductUpdateFile expected,
        CancellationToken cancellationToken)
    {
        var source = ProductUpdatePath.ResolveUnderRoot(sourceRoot, expected.Path);
        var destination = ProductUpdatePath.ResolveUnderRoot(stagingRoot, expected.Path);
        ProductActivationPathPolicy.RejectExistingReparsePoints(source);
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("A signed local repair payload file is unavailable.", source);
        }

        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("A target payload file has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        ProductActivationPathPolicy.RejectExistingReparsePoints(destinationDirectory);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expected.SizeBytes)
        {
            throw new InvalidDataException("A signed local repair payload file size changed.");
        }

        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            copied = checked(copied + read);
            if (copied > expected.SizeBytes)
            {
                throw new InvalidDataException("A signed local repair payload file grew while copying.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (copied != expected.SizeBytes ||
            !CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(expected.Sha256)))
        {
            throw new CryptographicException("A signed local repair payload file failed copy verification.");
        }
    }

    private static void TryDeleteOwnedStagingDirectory(string stagingRoot, string versionsRoot)
    {
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        var prefix = Path.GetFullPath(versionsRoot).TrimEnd(
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(stagingRoot);
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).StartsWith(".", StringComparison.Ordinal) ||
            !Path.GetFileName(candidate).EndsWith(".staging", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!CanDeleteOwnedTree(candidate))
            {
                return;
            }

            Directory.Delete(candidate, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A verified staging directory is never activated until the final atomic move. If
            // cleanup cannot prove ownership or complete, leaving it inert is safer than broadening
            // deletion authority during a repair failure.
        }
    }

    private static bool CanDeleteOwnedTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var observed = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++observed > ProductUpdateManifestParser.MaximumFiles * 2 + 1)
                {
                    return false;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }

        return true;
    }

    private static bool IsTerminal(ProductUpdateActivationState state)
        => state is ProductUpdateActivationState.Committed or ProductUpdateActivationState.RolledBack;

    private static void ValidateRecoveredInstallation(
        ProductManagedInstallation initial,
        ProductManagedInstallation current,
        ProductUpdateActivator activator)
    {
        if (!string.Equals(initial.InstallRoot, current.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(initial.DataRoot, current.DataRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(initial.ExchangeRoot, current.ExchangeRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ActiveVersion, activator.ReadActiveVersion(), StringComparison.Ordinal) ||
            !string.Equals(current.ServiceVersion, current.ActiveVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The managed Service and active pointer did not converge after activation recovery.");
        }
    }

    private static bool TryParseArguments(string[]? args, out string releaseRoot)
    {
        releaseRoot = string.Empty;
        if (args is not { Length: 3 } ||
            !string.Equals(args[0], Command, StringComparison.Ordinal) ||
            !string.Equals(args[1], ReleaseRootArgument, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]) ||
            !Path.IsPathFullyQualified(args[2]))
        {
            return false;
        }

        releaseRoot = args[2];
        return true;
    }
}
