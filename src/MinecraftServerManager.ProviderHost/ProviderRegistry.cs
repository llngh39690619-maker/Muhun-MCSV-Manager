using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.ProviderHost;

/// <summary>Atomic, bounded registry for installed out-of-process providers.</summary>
public sealed class ProviderRegistry(ProviderHostLayout layout, TimeProvider? timeProvider = null)
{
    public const int MaximumProviders = 128;
    public const long MaximumRegistryBytes = 2L * 1024 * 1024;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ProviderRegistration> _providers = new(StringComparer.Ordinal);
    private bool _loaded;

    public string FilePath => layout.RegistryFile;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            layout.EnsureCreated();
            var loadedProviders = new Dictionary<string, ProviderRegistration>(StringComparer.Ordinal);
            if (File.Exists(FilePath))
            {
                ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Root, FilePath);
                await using var stream = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is < 2 or > MaximumRegistryBytes)
                {
                    throw new InvalidDataException("Provider registry size is outside its allowed range.");
                }

                using var json = await JsonDocument.ParseAsync(
                        stream,
                        new JsonDocumentOptions { MaxDepth = 24 },
                        cancellationToken)
                    .ConfigureAwait(false);
                RejectDuplicateProperties(json.RootElement);
                var document = json.RootElement.Deserialize<RegistryDocument>(JsonOptions)
                               ?? throw new InvalidDataException("Provider registry is empty.");
                if (document.SchemaVersion != 1 || document.Providers.Count > MaximumProviders)
                {
                    throw new InvalidDataException("Provider registry schema or entry count is unsupported.");
                }

                foreach (var registration in document.Providers)
                {
                    ProviderRegistrationValidator.ValidateAndThrow(registration, layout);
                    if (!loadedProviders.TryAdd(
                            registration.Manifest.Id,
                            ProviderRegistrationValidator.Clone(registration)))
                    {
                        throw new InvalidDataException("Provider registry contains duplicate provider ids.");
                    }
                }
            }

            lock (_providers)
            {
                _providers.Clear();
                foreach (var pair in loadedProviders)
                {
                    _providers.Add(pair.Key, pair.Value);
                }
            }

            _loaded = true;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Provider registry JSON is invalid.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ProviderRegistration> GetAll()
    {
        EnsureLoaded();
        lock (_providers)
        {
            return _providers.Values
                .OrderBy(provider => provider.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.Manifest.Id, StringComparer.Ordinal)
                .Select(ProviderRegistrationValidator.Clone)
                .ToArray();
        }
    }

    public bool TryGet(string providerId, out ProviderRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        EnsureLoaded();
        lock (_providers)
        {
            if (_providers.TryGetValue(providerId, out var stored))
            {
                registration = ProviderRegistrationValidator.Clone(stored);
                return true;
            }
        }

        registration = null!;
        return false;
    }

    public Task UpsertAsync(
        ProviderRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ProviderRegistrationValidator.ValidateAndThrow(registration, layout);
        return MutateAsync(
            providers =>
            {
                if (!providers.ContainsKey(registration.Manifest.Id) &&
                    providers.Count >= MaximumProviders)
                {
                    throw new InvalidOperationException("Provider registry has reached its provider limit.");
                }

                providers[registration.Manifest.Id] = ProviderRegistrationValidator.Clone(registration);
                return true;
            },
            cancellationToken);
    }

    public Task SetEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return MutateAsync(
            providers =>
            {
                if (!providers.TryGetValue(providerId, out var current))
                {
                    throw new KeyNotFoundException("Provider is not registered.");
                }

                if (current.IsEnabled == enabled)
                {
                    return false;
                }

                providers[providerId] = current with
                {
                    IsEnabled = enabled,
                    Health = enabled ? ProviderHealthStatus.Stopped : ProviderHealthStatus.Disabled,
                    LastHealthTransitionUtc = _timeProvider.GetUtcNow(),
                    ConsecutiveFailures = enabled ? current.ConsecutiveFailures : 0,
                    LastError = null,
                };
                return true;
            },
            cancellationToken);
    }

    public Task ReportHealthAsync(
        string providerId,
        ProviderHealthStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!Enum.IsDefined(status) || status == ProviderHealthStatus.Disabled)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var safeError = SanitizeError(error);
        return MutateAsync(
            providers =>
            {
                if (!providers.TryGetValue(providerId, out var current))
                {
                    throw new KeyNotFoundException("Provider is not registered.");
                }

                if (!current.IsEnabled)
                {
                    throw new InvalidOperationException("A disabled provider cannot report runtime health.");
                }

                var failures = status switch
                {
                    ProviderHealthStatus.Failed or ProviderHealthStatus.Degraded =>
                        Math.Min(1_000_000, current.ConsecutiveFailures + 1),
                    ProviderHealthStatus.Starting => current.ConsecutiveFailures,
                    _ => 0,
                };
                providers[providerId] = current with
                {
                    Health = status,
                    LastHealthTransitionUtc = _timeProvider.GetUtcNow(),
                    ConsecutiveFailures = failures,
                    LastError = status switch
                    {
                        ProviderHealthStatus.Failed or ProviderHealthStatus.Degraded => safeError,
                        ProviderHealthStatus.Starting => current.LastError,
                        _ => null,
                    },
                };
                return true;
            },
            cancellationToken);
    }

    public Task<bool> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return MutateWithResultAsync(
            providers => providers.Remove(providerId),
            cancellationToken);
    }

    private async Task MutateAsync(
        Func<Dictionary<string, ProviderRegistration>, bool> mutation,
        CancellationToken cancellationToken)
    {
        _ = await MutateWithResultAsync(mutation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MutateWithResultAsync(
        Func<Dictionary<string, ProviderRegistration>, bool> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            Dictionary<string, ProviderRegistration> snapshot;
            lock (_providers)
            {
                snapshot = _providers.ToDictionary(
                    pair => pair.Key,
                    pair => ProviderRegistrationValidator.Clone(pair.Value),
                    StringComparer.Ordinal);
            }

            if (!mutation(snapshot))
            {
                return false;
            }

            foreach (var registration in snapshot.Values)
            {
                ProviderRegistrationValidator.ValidateAndThrow(registration, layout);
            }

            await SaveAsync(snapshot.Values, cancellationToken).ConfigureAwait(false);
            lock (_providers)
            {
                _providers.Clear();
                foreach (var pair in snapshot)
                {
                    _providers.Add(pair.Key, pair.Value);
                }
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(
        IEnumerable<ProviderRegistration> registrations,
        CancellationToken cancellationToken)
    {
        layout.EnsureCreated();
        var document = new RegistryDocument(
            1,
            registrations
                .OrderBy(provider => provider.Manifest.Id, StringComparer.Ordinal)
                .Select(ProviderRegistrationValidator.Clone)
                .ToArray());
        var temporaryPath = Path.Combine(
            layout.State,
            $".provider-registry.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumRegistryBytes)
            {
                throw new InvalidDataException("Provider registry exceeds its file size limit.");
            }

            ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Root, temporaryPath);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A private stale temporary file can never become registry state.
            }
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Provider registry has not been loaded.");
        }
    }

    private static string? SanitizeError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value
            .Where(character => character is not ('\0' or '\r' or '\n') && !char.IsControl(character))
            .Take(1024)
            .ToArray());
        return sanitized.Length == 0 ? null : sanitized;
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
                    throw new InvalidDataException("Provider registry contains duplicate JSON properties.");
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
        WriteIndented = true,
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record RegistryDocument(
        int SchemaVersion,
        IReadOnlyList<ProviderRegistration> Providers);
}
