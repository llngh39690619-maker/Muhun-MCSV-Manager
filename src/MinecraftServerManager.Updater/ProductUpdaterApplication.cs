using System.Security.Cryptography;

namespace MinecraftServerManager.Updater;

public enum ProductUpdaterApplicationCheckpoint
{
    ProvisionedBeforeConsumption,
    ConsumedBeforeActivationJournal,
}

public static class ProductUpdaterApplication
{
    private const int MaximumSignatureBytes = 1024;
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(
        string[] args,
        Func<int, string, IProductUpdateHealthController>? healthControllerFactory = null,
        Func<string>? installRootResolver = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default,
        Action<Exception>? failureObserver = null,
        Action<ProductUpdaterApplicationCheckpoint>? checkpointObserver = null,
        Action<ProductUpdateActivationCheckpoint>? activationCheckpointObserver = null)
    {
        if (!TryParseArguments(args, out var requestPath))
        {
            Console.Error.WriteLine("Muhun MCSV Updater requires one authenticated activation request.");
            return 2;
        }

        var clock = timeProvider ?? TimeProvider.System;
        VerifiedProductUpdateActivationRequest? verifiedRequest = null;
        ProductUpdateActivator? activator = null;
        IDisposable? updaterLease = null;
        var ownsUpdaterLease = false;
        try
        {
            verifiedRequest = ProductUpdateActivationRequestProtocol.VerifyForActivation(requestPath, clock);
            var dataRoot = Path.GetDirectoryName(verifiedRequest.UpdatesRoot)
                ?? throw new InvalidDataException("Product data root is invalid.");
            var healthController = (healthControllerFactory ??
                ((port, root) => new ProductWindowsActivationHealthController(port, root)))(
                verifiedRequest.Request.ServicePort,
                dataRoot);
            using var healthLifetime = healthController as IDisposable;
            var installRoot = (installRootResolver ?? ResolveOwnInstallRoot)();
            ValidateManagedInstallRoot(installRoot);
            activator = new ProductUpdateActivator(
                installRoot,
                healthController,
                clock,
                activationCheckpointObserver);
            updaterLease = activator.AcquireUpdaterLease();
            ownsUpdaterLease = true;

            var existingReceipt = ProductUpdateActivationReceiptProtocol.Read(
                verifiedRequest.UpdatesRoot,
                verifiedRequest.Request.OperationId);
            if (existingReceipt?.Outcome == ProductUpdateActivationReceiptOutcome.Rejected)
            {
                throw new InvalidDataException(
                    "Activation operation was durably rejected and must be rescheduled.");
            }

            // Recovery always precedes a new switch. A crash cannot strand the active pointer on
            // an unconfirmed version and then begin a second, unrelated activation.
            var journal = activator.ReadActivationJournal();
            if (journal is not null && !IsTerminal(journal.State))
            {
                _ = await activator.RecoverInterruptedActivationAsync(HealthTimeout, cancellationToken)
                    .ConfigureAwait(false);
                journal = activator.ReadActivationJournal()
                    ?? throw new InvalidDataException(
                        "Interrupted activation recovery did not persist a terminal journal.");
                if (journal.OperationId == verifiedRequest.Request.OperationId)
                {
                    ProductUpdateActivationReceiptProtocol.WriteTerminal(
                        verifiedRequest,
                        journal,
                        clock);
                    return ExitCodeFor(journal);
                }
            }

            journal = activator.ReadActivationJournal();
            if (journal?.OperationId == verifiedRequest.Request.OperationId)
            {
                if (!string.Equals(
                        journal.TargetVersion,
                        verifiedRequest.Request.TargetVersion,
                        StringComparison.Ordinal) ||
                    !IsTerminal(journal.State))
                {
                    throw new InvalidDataException(
                        "Activation journal does not match the authenticated operation.");
                }

                ProductUpdateActivationReceiptProtocol.WriteTerminal(
                    verifiedRequest,
                    journal,
                    clock);
                return ExitCodeFor(journal);
            }

            var manifest = LoadAndVerifyManifest(verifiedRequest, clock);
            if (!string.Equals(manifest.Version, verifiedRequest.Request.TargetVersion, StringComparison.Ordinal) ||
                !string.Equals(manifest.Channel, verifiedRequest.Request.Channel, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Package.Sha256,
                    verifiedRequest.Request.PackageSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.KeyId, verifiedRequest.Request.SigningKeyId, StringComparison.Ordinal))
            {
                throw new CryptographicException("Activation request does not match the signed update manifest.");
            }

            ProductFormalUpdateManifestValidator.Validate(manifest);

            if (!ProductUpdateActivationRequestProtocol.IsConsumed(verifiedRequest))
            {
                await ProvisionTargetVersionAsync(
                        verifiedRequest.UpdatesRoot,
                        installRoot,
                        manifest,
                        cancellationToken)
                    .ConfigureAwait(false);
                checkpointObserver?.Invoke(ProductUpdaterApplicationCheckpoint.ProvisionedBeforeConsumption);
                ProductUpdateActivationRequestProtocol.MarkConsumed(verifiedRequest);
                checkpointObserver?.Invoke(ProductUpdaterApplicationCheckpoint.ConsumedBeforeActivationJournal);
            }

            var result = await activator.ActivateAsync(
                    manifest,
                    HealthTimeout,
                    cancellationToken,
                    verifiedRequest.Request.OperationId)
                .ConfigureAwait(false);
            journal = activator.ReadActivationJournal()
                ?? throw new InvalidDataException(
                    "Activation completed without a durable terminal journal.");
            ProductUpdateActivationReceiptProtocol.WriteTerminal(verifiedRequest, journal, clock);
            return result.RolledBack ? 10 : 0;
        }
        catch (ProductUpdateInterruptionException exception)
        {
            failureObserver?.Invoke(exception);
            return 13;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 11;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (ownsUpdaterLease && verifiedRequest is not null && activator is not null)
            {
                TryPersistFailureState(verifiedRequest, activator, clock);
            }

            failureObserver?.Invoke(exception);
            // Do not echo local paths, feed locations or cryptographic metadata into a shell or
            // service log. Detailed state remains in the ACL-protected activation journal.
            Console.Error.WriteLine("Muhun MCSV update activation was rejected or rolled back.");
            return 12;
        }
        finally
        {
            updaterLease?.Dispose();
        }
    }

    private static void TryPersistFailureState(
        VerifiedProductUpdateActivationRequest verified,
        ProductUpdateActivator activator,
        TimeProvider timeProvider)
    {
        try
        {
            var journal = activator.ReadActivationJournal();
            if (journal?.OperationId == verified.Request.OperationId)
            {
                if (IsTerminal(journal.State))
                {
                    ProductUpdateActivationReceiptProtocol.WriteTerminal(
                        verified,
                        journal,
                        timeProvider);
                }

                // A non-terminal journal is intentionally left without a terminal receipt. The
                // Service will relaunch this same authenticated operation and recovery is idempotent.
                return;
            }

            if (journal is not null && !IsTerminal(journal.State))
            {
                return;
            }

            ProductUpdateActivationReceiptProtocol.WriteRejected(
                verified,
                "activation.preflight_rejected",
                timeProvider);
        }
        catch (Exception stateFailure) when (stateFailure is not OutOfMemoryException and not StackOverflowException)
        {
            // Never mask the activation failure. Missing/malformed receipt leaves pending intact,
            // which is the fail-closed and diagnosable state.
        }
    }

    private static bool IsTerminal(ProductUpdateActivationState state)
        => state is ProductUpdateActivationState.Committed or ProductUpdateActivationState.RolledBack;

    private static int ExitCodeFor(ProductUpdateActivationJournal journal)
        => journal.State switch
        {
            ProductUpdateActivationState.Committed => 0,
            ProductUpdateActivationState.RolledBack => 10,
            _ => throw new InvalidDataException("Activation journal is not terminal."),
        };

    private static async Task ProvisionTargetVersionAsync(
        string updatesRoot,
        string installRoot,
        ProductUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var versionsRoot = Path.Combine(installRoot, "versions");
        Directory.CreateDirectory(versionsRoot);
        RejectExistingReparsePoints(versionsRoot);
        var targetRoot = Path.Combine(versionsRoot, manifest.Version);
        if (Directory.Exists(targetRoot))
        {
            await ProductInstalledVersionVerifier.VerifyAsync(targetRoot, manifest, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var packagePath = Path.Combine(updatesRoot, "packages", manifest.Version + ".zip");
        if (!File.Exists(packagePath) || (File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("Verified update package was not found.", packagePath);
        }

        var stagingRoot = Path.Combine(versionsRoot, $".{manifest.Version}.{Guid.NewGuid():N}.staging");
        try
        {
            await using var package = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await new SafeProductPackageExtractor()
                .ExtractAndVerifyAsync(package, stagingRoot, manifest, cancellationToken)
                .ConfigureAwait(false);
            ProductInstalledVersionMetadataStore.Write(
                stagingRoot,
                new ProductInstalledVersionMetadata(
                    1,
                    "muhun.mcsv.manager",
                    manifest.Version,
                    manifest.EntryPoint));
            await ProductInstalledVersionVerifier.VerifyAsync(
                    stagingRoot,
                    manifest,
                    cancellationToken,
                    requireVersionDirectoryName: false)
                .ConfigureAwait(false);
            Directory.Move(stagingRoot, targetRoot);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    internal static string ResolveOwnInstallRoot()
        => ResolveInstallRootFromUpdaterBase(AppContext.BaseDirectory);

    internal static string ResolveInstallRootFromUpdaterBase(string updaterBaseDirectory)
    {
        if (!Path.IsPathFullyQualified(updaterBaseDirectory))
        {
            throw new InvalidDataException("Updater base directory must be absolute.");
        }

        var updaterRoot = new DirectoryInfo(Path.GetFullPath(updaterBaseDirectory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (!string.Equals(updaterRoot.Name, "updater-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater is not running from the formal updater-win-x64 directory.");
        }

        var versionRoot = updaterRoot.Parent
            ?? throw new InvalidDataException("Updater version directory is missing.");
        ProductUpdateManifestParser.ValidateVersion(versionRoot.Name);
        var versionsRoot = versionRoot.Parent
            ?? throw new InvalidDataException("Updater version directory has no parent.");
        if (!string.Equals(versionsRoot.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater is not running from a managed version directory.");
        }

        return versionsRoot.Parent?.FullName
            ?? throw new InvalidDataException("Updater install root is invalid.");
    }

    private static void ValidateManagedInstallRoot(string installRoot)
    {
        if (!Path.IsPathFullyQualified(installRoot))
        {
            throw new InvalidDataException("Product install root must be absolute.");
        }

        var root = Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        RejectExistingReparsePoints(root);
        var marker = Path.Combine(root, ".muhun-mcsv-install-root");
        if (!File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker).Trim(), "muhun.mcsv.manager:1", StringComparison.Ordinal) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Product install root marker is missing or invalid.");
        }
    }

    private static ProductUpdateManifest LoadAndVerifyManifest(
        VerifiedProductUpdateActivationRequest verified,
        TimeProvider timeProvider)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var publicKeyBytes = ReadBounded(
            verified.PublicKeyDocumentPath,
            ProductUpdatePublicKeyLoader.MaximumDocumentBytes);
        using var publicKey = ProductUpdatePublicKeyLoader.Load(publicKeyBytes, out var keyDocument);
        if (!string.Equals(keyDocument.KeyId, verified.Request.SigningKeyId, StringComparison.Ordinal) ||
            !string.Equals(
                keyDocument.SubjectPublicKeyInfoSha256,
                verified.Request.SigningPublicKeySha256,
                StringComparison.OrdinalIgnoreCase) ||
            nowUtc < keyDocument.NotBeforeUtc || nowUtc > keyDocument.NotAfterUtc)
        {
            throw new CryptographicException("Update signing key is outside the authenticated trust binding.");
        }

        var manifestBytes = ReadBounded(
            verified.ManifestPath,
            ProductUpdateManifestParser.MaximumManifestBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(manifestBytes),
                Convert.FromHexString(verified.Request.ManifestSha256)))
        {
            throw new CryptographicException("Signed update manifest does not match the authenticated handoff.");
        }

        var signature = ReadBounded(verified.ManifestSignaturePath, MaximumSignatureBytes);
        var verifier = new SignedProductUpdateManifestVerifier(
            new Dictionary<string, RSA>(StringComparer.Ordinal)
            {
                [keyDocument.KeyId] = publicKey,
            },
            new HashSet<string>(verified.Request.AllowedPackageHosts, StringComparer.OrdinalIgnoreCase),
            timeProvider);
        return verifier.Verify(manifestBytes, signature);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Updater input files cannot be reparse points.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Updater input file has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Managed update paths cannot traverse a reparse point.");
            }
        }
    }

    private static bool TryParseArguments(string[]? args, out string requestPath)
    {
        requestPath = string.Empty;
        if (args is not { Length: 2 } ||
            !string.Equals(args[0], "--activation-request", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[1]) ||
            !Path.IsPathFullyQualified(args[1]))
        {
            return false;
        }

        requestPath = args[1];
        return true;
    }
}
