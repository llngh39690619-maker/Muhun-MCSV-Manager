using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.Service;

/// <summary>
/// Durable, public-key-only trust store for signed provider packages. The file is held under the
/// Service-owned provider root and all changes are serialized and atomically replaced.
/// </summary>
public sealed partial class ProductProviderPublisherTrustStore : IProviderPackageTrustVerifier
{
    public const int MaximumPublishers = 64;
    public const long MaximumStoreBytes = 512 * 1024;
    public const string FileName = "trusted-publishers.v1.json";

    private readonly ProviderHostLayout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TrustedPublisher> _publishers = new(StringComparer.Ordinal);
    private bool _loaded;

    public ProductProviderPublisherTrustStore(
        ProviderHostLayout layout,
        TimeProvider? timeProvider = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string StorePath => Path.Combine(_layout.State, FileName);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            _layout.EnsureCreated();
            var loaded = new Dictionary<string, TrustedPublisher>(StringComparer.Ordinal);
            if (File.Exists(StorePath))
            {
                RejectReparsePoint(StorePath);
                await using var stream = new FileStream(
                    StorePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is < 2 or > MaximumStoreBytes)
                {
                    throw new InvalidDataException("Provider publisher trust store size is invalid.");
                }

                using var document = await JsonDocument.ParseAsync(
                        stream,
                        new JsonDocumentOptions { MaxDepth = 12 },
                        cancellationToken)
                    .ConfigureAwait(false);
                RejectDuplicateProperties(document.RootElement);
                var persisted = document.RootElement.Deserialize<TrustStoreDocument>(JsonOptions)
                                ?? throw new InvalidDataException("Provider publisher trust store is empty.");
                if (persisted.SchemaVersion != 1 || persisted.Publishers.Count > MaximumPublishers)
                {
                    throw new InvalidDataException("Provider publisher trust store schema is unsupported.");
                }

                foreach (var publisher in persisted.Publishers)
                {
                    var normalized = NormalizePublisher(
                        publisher.PublisherId,
                        publisher.PublicKeyPem,
                        publisher.PinnedAtUtc);
                    if (!loaded.TryAdd(normalized.PublisherId, normalized))
                    {
                        throw new InvalidDataException("Provider publisher trust store contains duplicates.");
                    }
                }
            }

            lock (_publishers)
            {
                _publishers.Clear();
                foreach (var item in loaded)
                {
                    _publishers.Add(item.Key, item.Value);
                }
            }

            _loaded = true;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Provider publisher trust store JSON is invalid.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ProductTrustedProviderPublisherSummary> List()
    {
        EnsureLoaded();
        lock (_publishers)
        {
            return _publishers.Values
                .OrderBy(value => value.PublisherId, StringComparer.Ordinal)
                .Select(value => new ProductTrustedProviderPublisherSummary(
                    value.PublisherId,
                    value.PublicKeySha256,
                    value.PinnedAtUtc))
                .ToArray();
        }
    }

    public async Task<ProductTrustedProviderPublisherSummary> PinAsync(
        string publisherId,
        string publicKeyPem,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePublisher(
            publisherId,
            publicKeyPem,
            _timeProvider.GetUtcNow().ToUniversalTime());
        var effective = normalized;
        await MutateAsync(
            publishers =>
            {
                if (publishers.TryGetValue(normalized.PublisherId, out var existing))
                {
                    if (!string.Equals(
                            existing.PublicKeySha256,
                            normalized.PublicKeySha256,
                            StringComparison.Ordinal))
                    {
                        throw new CryptographicException(
                            "An existing provider publisher identity cannot be replaced by a different key.");
                    }

                    effective = existing;
                    return false;
                }

                if (!publishers.ContainsKey(normalized.PublisherId) &&
                    publishers.Count >= MaximumPublishers)
                {
                    throw new InvalidOperationException("Provider publisher trust store is full.");
                }

                publishers[normalized.PublisherId] = normalized;
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return new ProductTrustedProviderPublisherSummary(
            effective.PublisherId,
            effective.PublicKeySha256,
            effective.PinnedAtUtc);
    }

    public Task<bool> RemoveAsync(
        string publisherId,
        CancellationToken cancellationToken = default)
    {
        ValidatePublisherId(publisherId);
        return MutateAsync(
            publishers => publishers.Remove(publisherId),
            cancellationToken);
    }

    public ValueTask<ProviderPackageTrustDecision> VerifyAsync(
        ProviderPackageTrustContext context,
        ProviderPackageSignature signature,
        CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        IReadOnlyDictionary<string, string> publicKeys;
        lock (_publishers)
        {
            publicKeys = _publishers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.PublicKeyPem,
                StringComparer.Ordinal);
        }

        return new EcdsaProviderPackageTrustVerifier(publicKeys)
            .VerifyAsync(context, signature, cancellationToken);
    }

    private async Task<bool> MutateAsync(
        Func<Dictionary<string, TrustedPublisher>, bool> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            Dictionary<string, TrustedPublisher> snapshot;
            lock (_publishers)
            {
                snapshot = _publishers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
            }

            if (!mutation(snapshot))
            {
                return false;
            }

            await SaveAsync(snapshot.Values, cancellationToken).ConfigureAwait(false);
            lock (_publishers)
            {
                _publishers.Clear();
                foreach (var item in snapshot)
                {
                    _publishers.Add(item.Key, item.Value);
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
        IEnumerable<TrustedPublisher> publishers,
        CancellationToken cancellationToken)
    {
        _layout.EnsureCreated();
        var temporaryPath = Path.Combine(
            _layout.State,
            $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
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
                var document = new TrustStoreDocument(
                    1,
                    publishers
                        .OrderBy(value => value.PublisherId, StringComparer.Ordinal)
                        .ToArray());
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumStoreBytes)
            {
                throw new InvalidDataException("Provider publisher trust store exceeds its size limit.");
            }

            RejectReparsePoint(temporaryPath);
            File.Move(temporaryPath, StorePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static TrustedPublisher NormalizePublisher(
        string publisherId,
        string publicKeyPem,
        DateTimeOffset pinnedAtUtc)
    {
        ValidatePublisherId(publisherId);
        if (pinnedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Provider publisher pin timestamp must use UTC.");
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem) || publicKeyPem.Length > 16 * 1024 ||
            publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Provider publisher public key is missing, too large, or contains private key material.");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (key.KeySize != 256 ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Provider publisher key must use ECDSA P-256.");
            }

            var publicKey = key.ExportSubjectPublicKeyInfo();
            try
            {
                return new TrustedPublisher(
                    publisherId,
                    key.ExportSubjectPublicKeyInfoPem(),
                    Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant(),
                    pinnedAtUtc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Provider publisher public key is invalid.", error);
        }
    }

    private static void ValidatePublisherId(string publisherId)
    {
        if (publisherId is null || publisherId.Length is < 1 or > 128 ||
            !PublisherIdPattern().IsMatch(publisherId))
        {
            throw new InvalidDataException("Provider publisher id is invalid.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Provider trust store paths cannot be reparse points.");
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
                    throw new InvalidDataException("Provider trust store contains duplicate JSON properties.");
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

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Provider publisher trust store has not been loaded.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublisherIdPattern();

    private sealed record TrustedPublisher(
        string PublisherId,
        string PublicKeyPem,
        string PublicKeySha256,
        DateTimeOffset PinnedAtUtc);

    private sealed record TrustStoreDocument(
        int SchemaVersion,
        IReadOnlyList<TrustedPublisher> Publishers);
}
