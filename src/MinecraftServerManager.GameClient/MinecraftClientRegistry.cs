using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Atomic, per-user registry for client instances. Authentication tokens never enter it.</summary>
public sealed class MinecraftClientRegistry : IDisposable
{
    private readonly JsonSettingsStore<MinecraftClientRegistryDocument> _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Action? _afterDurableSaveForTesting;
    private int _disposeState;

    public MinecraftClientRegistry(string registryPath)
        : this(registryPath, afterDurableSaveForTesting: null)
    {
    }

    internal MinecraftClientRegistry(
        string registryPath,
        Action? afterDurableSaveForTesting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        _store = new JsonSettingsStore<MinecraftClientRegistryDocument>(registryPath);
        _afterDurableSaveForTesting = afterDurableSaveForTesting;
    }

    internal string RegistryPath => _store.FilePath;

    public async Task<MinecraftClientRegistryDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SaveAsync(
        MinecraftClientRegistryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Performs one in-process read/modify/validate/atomic-save transaction. The callback is
    /// synchronous so user code cannot hold the registry lock across arbitrary asynchronous work.
    /// </summary>
    public async Task<TResult> UpdateAsync<TResult>(
        Func<MinecraftClientRegistryDocument, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            var document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var result = update(document);
            cancellationToken.ThrowIfCancellationRequested();
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        // Wait for an already-started atomic save to finish before disposing its underlying
        // store. In particular, never dispose the semaphore after File.Move committed but before
        // UpdateAsync released it; that could make a durable commit appear to have failed.
        _mutationGate.Wait();
        try
        {
            _store.Dispose();
        }
        finally
        {
            // Deliberately keep the gate undisposed so operations already queued during the race
            // can wake, observe _disposeState, and fail without an ObjectDisposedException from
            // SemaphoreSlim.Release obscuring a completed registry transaction.
            _mutationGate.Release();
        }
    }

    private void ThrowIfDisposingOrDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private async Task<MinecraftClientRegistryDocument> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? new MinecraftClientRegistryDocument();
        Validate(document);
        return document;
    }

    private async Task SaveCoreAsync(
        MinecraftClientRegistryDocument document,
        CancellationToken cancellationToken)
    {
        Validate(document);
        document.SchemaVersion = MinecraftClientRegistryDocument.CurrentSchemaVersion;
        await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        // Test seam for the exact boundary where the atomic settings store has committed but the
        // caller has not yet observed success. Production callers always use the public ctor.
        _afterDurableSaveForTesting?.Invoke();
    }

    internal static void Validate(MinecraftClientRegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is < 1 or > MinecraftClientRegistryDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Minecraft client registry schema {document.SchemaVersion}.");
        }

        if (document.Instances.Count > 1_024)
        {
            throw new InvalidDataException("Minecraft client registry contains too many instances.");
        }

        var ids = new HashSet<Guid>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeProcesses = new HashSet<(int ProcessId, long StartedAtUtcTicks)>();
        foreach (var instance in document.Instances)
        {
            if (instance.Edition != MinecraftClientEdition.Java)
            {
                throw new InvalidDataException(
                    "The managed Minecraft client registry only accepts Java Edition instances.");
            }

            if (instance.Id == Guid.Empty || !ids.Add(instance.Id))
            {
                throw new InvalidDataException("Minecraft client registry contains an invalid or duplicate id.");
            }

            if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Length > 128)
            {
                throw new InvalidDataException("Minecraft client instance name is invalid.");
            }

            if (string.IsNullOrWhiteSpace(instance.DirectoryPath) ||
                !Path.IsPathFullyQualified(instance.DirectoryPath))
            {
                throw new InvalidDataException("Minecraft client instance directory is invalid.");
            }

            var fullDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(instance.DirectoryPath));
            if (!directories.Add(fullDirectory))
            {
                throw new InvalidDataException("Minecraft client registry contains duplicate directories.");
            }

            if (instance.Edition == MinecraftClientEdition.Java &&
                (string.IsNullOrWhiteSpace(instance.GameVersion) ||
                 string.IsNullOrWhiteSpace(instance.InstalledVersionId)))
            {
                throw new InvalidDataException("Java client instance is missing its release or installed profile id.");
            }

            if (instance.MinimumMemoryMb is < 512 or > 262_144 ||
                instance.MaximumMemoryMb < instance.MinimumMemoryMb ||
                instance.MaximumMemoryMb > 262_144 ||
                instance.JavaMajorVersion is < 8 or > 99)
            {
                throw new InvalidDataException("Minecraft client memory range or Java major is invalid.");
            }

            if (instance.WindowWidth is < 640 or > 16_384 ||
                instance.WindowHeight is < 360 or > 16_384)
            {
                throw new InvalidDataException("Minecraft client resolution is invalid.");
            }

            if (instance.AccountId?.Length > 256 || instance.JvmArguments.Count > 256 ||
                instance.EnvironmentVariables.Count > 128 || instance.CatalogProvider?.Length > 32 ||
                instance.CatalogProjectId?.Length > 64 || instance.CatalogVersionId?.Length > 64)
            {
                throw new InvalidDataException("Minecraft client instance settings exceed safe limits.");
            }

            if (instance.CatalogProvider?.Equals("modrinth", StringComparison.Ordinal) == true)
            {
                if (!IsCatalogIdentifier(instance.CatalogProjectId) ||
                    !IsCatalogIdentifier(instance.CatalogVersionId) ||
                    instance.CatalogIconUri is not null &&
                    !ModrinthClientModpackCatalog.IsOfficialCdnUri(instance.CatalogIconUri) ||
                    instance.CatalogPreviewUri is not null &&
                    !ModrinthClientModpackCatalog.IsOfficialCdnUri(instance.CatalogPreviewUri))
                {
                    throw new InvalidDataException("Modrinth client catalog provenance is invalid.");
                }
            }
            else if (instance.CatalogProvider?.Equals("ftb", StringComparison.Ordinal) == true)
            {
                if (!IsPositiveCatalogNumber(instance.CatalogProjectId) ||
                    !IsPositiveCatalogNumber(instance.CatalogVersionId) ||
                    instance.CatalogIconUri is not null &&
                    !FtbMinecraftClientPackInstaller.IsOfficialFtbArtworkUri(instance.CatalogIconUri) ||
                    instance.CatalogPreviewUri is not null &&
                    !FtbMinecraftClientPackInstaller.IsOfficialFtbArtworkUri(instance.CatalogPreviewUri))
                {
                    throw new InvalidDataException("FTB client catalog provenance is invalid.");
                }
            }
            else if (instance.CatalogProvider is not null)
            {
                throw new InvalidDataException("Minecraft client catalog provider is invalid.");
            }

            if (instance.CatalogProvider is null &&
                (instance.CatalogProjectId is not null || instance.CatalogVersionId is not null ||
                 instance.CatalogIconUri is not null || instance.CatalogPreviewUri is not null))
            {
                throw new InvalidDataException("Minecraft client catalog metadata has no provider.");
            }

            var activeMarkerFieldCount =
                (instance.ActiveProcessId is null ? 0 : 1) +
                (instance.ActiveProcessStartedAtUtc is null ? 0 : 1) +
                (instance.ActiveProcessExecutablePath is null ? 0 : 1);
            if (activeMarkerFieldCount != 0)
            {
                if (activeMarkerFieldCount != 3 ||
                    !MinecraftClientProcessRecoveryService.TryGetPersistedIdentity(
                        instance,
                        out var activeIdentity) ||
                    !activeProcesses.Add((activeIdentity.ProcessId, activeIdentity.StartedAtUtc.UtcTicks)))
                {
                    throw new InvalidDataException(
                        "Minecraft client active-process identity is incomplete, unsafe or duplicated.");
                }

                if (!string.IsNullOrWhiteSpace(instance.JavaExecutablePath) &&
                    !PathsEqual(instance.JavaExecutablePath, activeIdentity.ExecutablePath))
                {
                    throw new InvalidDataException(
                        "Minecraft client active-process executable does not match its selected Java runtime.");
                }
            }

            foreach (var pair in instance.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 ||
                    pair.Key.Contains('=') || pair.Value.Length > 4_096)
                {
                    throw new InvalidDataException("Minecraft client environment variable is invalid.");
                }
            }
        }

        static bool IsCatalogIdentifier(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
               value.All(char.IsAsciiLetterOrDigit);

        static bool IsPositiveCatalogNumber(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= 10 &&
               int.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsed) &&
               parsed > 0 && parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;

        static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (
                error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}
