using System.Text.RegularExpressions;

namespace MinecraftServerManager.Contracts.Notifications;

public enum ProductEventSeverity
{
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record ProductEventEnvelope(
    int SchemaVersion,
    Guid EventId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Type,
    ProductEventSeverity Severity,
    string SummaryKey,
    Guid? ServerId,
    Guid? CorrelationId,
    IReadOnlyDictionary<string, string> Data);

public sealed record ProductContractValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ProductContractValidationResult Success { get; } = new([]);
}

public static partial class ProductEventEnvelopeValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDataEntries = 32;
    public const int MaximumValueLength = 2048;

    public static ProductContractValidationResult Validate(ProductEventEnvelope? envelope)
    {
        var errors = new List<string>();
        if (envelope is null)
        {
            errors.Add("Event envelope is required.");
            return new ProductContractValidationResult(errors.AsReadOnly());
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add("Unsupported event schema version.");
        }

        if (envelope.EventId == Guid.Empty)
        {
            errors.Add("Event id must not be empty.");
        }

        if (envelope.Sequence < 1)
        {
            errors.Add("Event sequence must be positive.");
        }

        if (envelope.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add("Event timestamp must use UTC.");
        }

        if (!EventTypePattern().IsMatch(envelope.Type ?? string.Empty))
        {
            errors.Add("Event type must be a stable lowercase dotted identifier.");
        }
        else if (!ProductEventSchemaCatalog.TryGetAllowedDataKeys(envelope.Type, out _))
        {
            errors.Add("Event type is not registered in the product event schema catalog.");
        }

        if (!Enum.IsDefined(envelope.Severity))
        {
            errors.Add("Event severity is invalid.");
        }

        if (!ResourceKeyPattern().IsMatch(envelope.SummaryKey ?? string.Empty))
        {
            errors.Add("Summary key must be a stable localization key.");
        }

        if (envelope.ServerId == Guid.Empty || envelope.CorrelationId == Guid.Empty)
        {
            errors.Add("Optional identifiers must be non-empty when supplied.");
        }

        if (envelope.Data is null || envelope.Data.Count > MaximumDataEntries)
        {
            errors.Add("Event data is missing or exceeds its bounded entry count.");
        }
        else
        {
            ProductEventSchemaCatalog.TryGetAllowedDataKeys(envelope.Type, out var allowedDataKeys);
            foreach (var pair in envelope.Data)
            {
                if (!DataKeyPattern().IsMatch(pair.Key) || ContainsSensitiveName(pair.Key))
                {
                    errors.Add("Event data contains an invalid or sensitive key.");
                    break;
                }

                if (allowedDataKeys is null || !allowedDataKeys.Contains(pair.Key))
                {
                    errors.Add("Event data contains a field that is not allowed by its event schema.");
                    break;
                }

                if (pair.Value is null || pair.Value.Length > MaximumValueLength ||
                    pair.Value.Contains('\r') || pair.Value.Contains('\n'))
                {
                    errors.Add("Event data contains an invalid or oversized value.");
                    break;
                }
            }
        }

        return errors.Count == 0
            ? ProductContractValidationResult.Success
            : new ProductContractValidationResult(errors.AsReadOnly());
    }

    private static bool ContainsSensitiveName(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("webhook", StringComparison.OrdinalIgnoreCase)
           || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*(?:[._-][A-Za-z0-9]+){1,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex DataKeyPattern();
}

public static class ProductEventSchemaCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Schemas =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["service.started"] = Keys("product_version"),
            ["service.stopped"] = Keys("product_version"),
            ["server.started"] = Keys("server_name"),
            ["server.stopped"] = Keys("server_name", "exit_code", "reason_code"),
            ["server.crashed"] = Keys("server_name", "exit_code", "failure_code"),
            ["server.player.joined"] = Keys("server_name", "player_name"),
            ["server.player.left"] = Keys("server_name", "player_name"),
            ["backup.completed"] = Keys("server_name", "backup_id", "size_bytes", "duration_ms"),
            ["backup.failed"] = Keys("server_name", "backup_id", "failure_code"),
            ["backup.restored"] = Keys("server_name", "backup_id", "duration_ms"),
            ["update.completed"] = Keys("component", "previous_version", "target_version"),
            ["update.failed"] = Keys("component", "target_version", "failure_code"),
            ["update.rolled-back"] = Keys("component", "restored_version", "failure_code"),
            ["security.login.failed"] = Keys("account_name", "source_address", "failure_code"),
            ["notification.delivery.failed"] = Keys("provider_id", "event_type", "failure_code"),
            ["provider.disabled"] = Keys("provider_id", "reason_code"),
            ["modpack.update.completed"] = Keys(
                "server_name", "previous_version", "target_version"),
            ["modpack.update.failed"] = Keys(
                "server_name", "target_version", "failure_code"),
            ["modpack.update.rolled-back"] = Keys(
                "server_name", "restored_version", "target_version", "failure_code"),
            ["product.update.available"] = Keys(
                "channel", "previous_version", "target_version"),
            ["product.update.completed"] = Keys(
                "channel", "previous_version", "target_version"),
            ["product.update.failed"] = Keys(
                "channel", "target_version", "failure_code"),
            ["product.update.rolled-back"] = Keys(
                "channel", "restored_version", "target_version", "failure_code"),
        };

    public static bool TryGetAllowedDataKeys(string? eventType, out IReadOnlySet<string> allowedDataKeys)
    {
        if (eventType is not null && Schemas.TryGetValue(eventType, out var keys))
        {
            allowedDataKeys = keys;
            return true;
        }

        allowedDataKeys = null!;
        return false;
    }

    private static IReadOnlySet<string> Keys(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);
}
