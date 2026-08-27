using System.Text.RegularExpressions;

namespace MinecraftServerManager.Data;

public sealed partial class ProductSequenceStore
{
    private readonly ProductDatabase _database;

    public ProductSequenceStore(ProductDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<long> NextAsync(
        string sequenceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
        if (!SequenceNamePattern().IsMatch(sequenceName))
        {
            throw new ArgumentException("Sequence name is invalid.", nameof(sequenceName));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO product_sequences(sequence_name, next_value)
            VALUES ($sequence_name, 2)
            ON CONFLICT(sequence_name) DO UPDATE SET
                next_value = product_sequences.next_value + 1
            RETURNING next_value - 1;
            """;
        command.Parameters.AddWithValue("$sequence_name", sequenceName);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex SequenceNamePattern();
}
