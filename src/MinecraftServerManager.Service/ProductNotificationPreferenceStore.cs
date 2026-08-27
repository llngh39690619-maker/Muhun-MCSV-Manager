using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

/// <summary>
/// Atomic, bounded Service-owned external-notification policy and coalescing ledger. It contains
/// no endpoints, account data, or secrets. Local-history delivery deliberately bypasses it.
/// </summary>
public sealed class ProductNotificationPreferenceStore(ProductDataLayout layout)
{
    internal const string FileName = "notification-preferences.v1.json";
    internal const int MaximumFileBytes = 128 * 1024;
    internal const int MaximumThrottleKeys = 512;
    private static readonly TimeSpan MaximumClaimAge = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(layout.Operations, FileName);
    private PreferenceDocument? _cached;

    public async Task<ProductNotificationPreferences> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProductNotificationPreferences> SetAsync(
        ProductNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ProductNotificationPreferencesValidator.ValidateAndThrow(preferences);
        if (string.IsNullOrWhiteSpace(preferences.CultureName))
        {
            preferences = preferences with
            {
                CultureName = MinecraftServerManager.Contracts.Localization
                    .ProductLocalizationCatalog.FallbackCulture,
            };
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = current with { Preferences = preferences };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _cached = updated;
            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryClaimExternalDeliveryAsync(
        string throttleKey,
        DateTimeOffset nowUtc,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ValidateThrottleKey(throttleKey);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Notification throttle timestamp must use UTC.", nameof(nowUtc));
        }

        if (interval < TimeSpan.Zero ||
            interval > TimeSpan.FromSeconds(ProductNotificationPreferences.MaximumThrottleSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (interval == TimeSpan.Zero)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var claims = new Dictionary<string, DateTimeOffset>(
                current.ExternalClaims,
                StringComparer.Ordinal);
            foreach (var stale in claims
                         .Where(pair => nowUtc - pair.Value > MaximumClaimAge || pair.Value > nowUtc.AddMinutes(5))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                claims.Remove(stale);
            }

            if (claims.TryGetValue(throttleKey, out var previous) && nowUtc - previous < interval)
            {
                return false;
            }

            claims[throttleKey] = nowUtc;
            if (claims.Count > MaximumThrottleKeys)
            {
                foreach (var oldest in claims
                             .OrderBy(pair => pair.Value)
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                             .Take(claims.Count - MaximumThrottleKeys)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    claims.Remove(oldest);
                }
            }

            var updated = current with { ExternalClaims = claims };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _cached = updated;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreferenceDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        layout.EnsureCreated();
        RejectReparse(layout.Operations);
        if (!File.Exists(_path))
        {
            _cached = new PreferenceDocument(
                1,
                ProductNotificationPreferences.Default,
                new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
            return _cached;
        }

        RejectReparse(_path);
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 2 or > MaximumFileBytes)
        {
            throw new InvalidDataException("Notification preference file has an invalid size.");
        }

        PreferenceDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync(
                    stream,
                    ProductNotificationPreferenceJsonContext.Default.PreferenceDocument,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Notification preference file is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Notification preference JSON is invalid.", error);
        }

        // Schema-v1 files written before notification culture was introduced deserialize the new
        // property as null. Repair only that absent legacy value; explicit unsupported values
        // continue to fail closed in ValidateDocument.
        if (document.Preferences is not null
            && string.IsNullOrWhiteSpace(document.Preferences.CultureName))
        {
            document = document with
            {
                Preferences = document.Preferences with
                {
                    CultureName = MinecraftServerManager.Contracts.Localization
                        .ProductLocalizationCatalog.FallbackCulture,
                },
            };
        }

        ValidateDocument(document);
        _cached = document with
        {
            ExternalClaims = new Dictionary<string, DateTimeOffset>(
                document.ExternalClaims,
                StringComparer.Ordinal),
        };
        return _cached;
    }

    private async Task SaveAsync(PreferenceDocument document, CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        Directory.CreateDirectory(layout.Operations);
        RejectReparse(layout.Operations);
        if (File.Exists(_path))
        {
            RejectReparse(_path);
        }

        var temporary = Path.Combine(
            layout.Operations,
            $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        ProductNotificationPreferenceJsonContext.Default.PreferenceDocument,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporary).Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Notification preference file exceeds its size limit.");
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateDocument(PreferenceDocument document)
    {
        if (document.SchemaVersion != 1 || document.ExternalClaims is null ||
            document.ExternalClaims.Count > MaximumThrottleKeys)
        {
            throw new InvalidDataException("Notification preference schema is unsupported or unbounded.");
        }

        ProductNotificationPreferencesValidator.ValidateAndThrow(document.Preferences);
        foreach (var pair in document.ExternalClaims)
        {
            ValidateThrottleKey(pair.Key);
            if (pair.Value.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("Notification throttle timestamps must use UTC.");
            }
        }
    }

    private static void ValidateThrottleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 180 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("Notification throttle key is invalid.", nameof(value));
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Notification preference path cannot be a reparse point.");
        }
    }

    internal sealed record PreferenceDocument(
        int SchemaVersion,
        ProductNotificationPreferences Preferences,
        IReadOnlyDictionary<string, DateTimeOffset> ExternalClaims);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(ProductNotificationPreferenceStore.PreferenceDocument))]
internal sealed partial class ProductNotificationPreferenceJsonContext : JsonSerializerContext;
