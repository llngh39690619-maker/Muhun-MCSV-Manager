using System.IO;
using System.Text.Json;
using MinecraftServerManager.App.ViewModels;

namespace MinecraftServerManager.App.Services;

internal sealed record PlayerRegistryReadWarning(string FileName, string Message);

internal sealed record PlayerRegistryReadResult(
    IReadOnlyList<PlayerStatusRecord> Players,
    IReadOnlyList<PlayerRegistryReadWarning> Warnings);

/// <summary>
/// Reads the disk-backed player registries without doing JSON parsing or aggregation on WPF's
/// dispatcher thread. These files are written by the running server, so every read also allows
/// concurrent writes and is cancellation-aware.
/// </summary>
internal static class PlayerRegistryReader
{
    private const long MaximumRegistryFileBytes = 16L * 1024 * 1024;
    internal const int MaximumPlayerRecords = 4_096;

    public static Task<PlayerRegistryReadResult> ReadAsync(
        string serverDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectory);

        // Task.Run is intentional. JsonDocument can complete synchronously for cached/small files;
        // ConfigureAwait(false) alone would then still enumerate and merge the document on the UI
        // thread that initiated the request.
        return Task.Run(
            () => ReadCoreAsync(serverDirectory, cancellationToken),
            cancellationToken);
    }

    private static async Task<PlayerRegistryReadResult> ReadCoreAsync(
        string serverDirectory,
        CancellationToken cancellationToken)
    {
        var players = new Dictionary<string, PlayerAccumulator>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<PlayerRegistryReadWarning>();
        var wasTruncated = false;
        var registries = new (string Path, PlayerRegistryKind Kind)[]
        {
            (Path.Combine(serverDirectory, "usercache.json"), PlayerRegistryKind.Known),
            (Path.Combine(serverDirectory, "ops.json"), PlayerRegistryKind.Operator),
            (Path.Combine(serverDirectory, "whitelist.json"), PlayerRegistryKind.Whitelisted),
            (Path.Combine(serverDirectory, "banned-players.json"), PlayerRegistryKind.Banned)
        };

        foreach (var registry in registries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                wasTruncated |= await MergeRegistryAsync(
                        registry.Path,
                        players,
                        registry.Kind,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or InvalidDataException)
            {
                warnings.Add(new PlayerRegistryReadWarning(
                    Path.GetFileName(registry.Path),
                    exception.Message));
            }
        }

        if (wasTruncated)
        {
            warnings.Add(new PlayerRegistryReadWarning(
                "玩家登錄檔",
                $"不同玩家超過 {MaximumPlayerRecords:N0} 筆；為避免介面卡頓，只顯示前 {MaximumPlayerRecords:N0} 筆。"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var records = players.Values
            .Select(player => new PlayerStatusRecord(
                player.Name,
                player.Uuid,
                IsOnline: false,
                player.IsOperator,
                player.IsWhitelisted,
                player.IsBanned))
            .ToArray();
        return new PlayerRegistryReadResult(records, warnings);
    }

    private static async Task<bool> MergeRegistryAsync(
        string path,
        IDictionary<string, PlayerAccumulator> players,
        PlayerRegistryKind kind,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var file = new FileInfo(path);
        if (file.Length > MaximumRegistryFileBytes)
        {
            throw new InvalidDataException($"玩家資料檔案過大，已拒絕讀取：{file.Name}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

        var inspected = 0;
        var wasTruncated = false;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if ((inspected++ & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var nameProperty)
                || nameProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameProperty.GetString()))
            {
                continue;
            }

            var name = nameProperty.GetString()!.Trim();
            if (!IsValidMinecraftPlayerName(name)) continue;

            if (!players.TryGetValue(name, out var player))
            {
                if (players.Count >= MaximumPlayerRecords)
                {
                    wasTruncated = true;
                    continue;
                }

                player = new PlayerAccumulator(name);
                players.Add(name, player);
            }

            if (item.TryGetProperty("uuid", out var uuidProperty)
                && uuidProperty.ValueKind == JsonValueKind.String)
            {
                player.Uuid = uuidProperty.GetString();
            }

            switch (kind)
            {
                case PlayerRegistryKind.Operator:
                    player.IsOperator = true;
                    break;
                case PlayerRegistryKind.Whitelisted:
                    player.IsWhitelisted = true;
                    break;
                case PlayerRegistryKind.Banned:
                    player.IsBanned = true;
                    break;
            }
        }

        return wasTruncated;
    }

    private static bool IsValidMinecraftPlayerName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 16
           && name.All(character => character is >= 'a' and <= 'z'
               or >= 'A' and <= 'Z'
               or >= '0' and <= '9'
               or '_');

    private enum PlayerRegistryKind
    {
        Known,
        Operator,
        Whitelisted,
        Banned
    }

    private sealed class PlayerAccumulator(string name)
    {
        public string Name { get; } = name;
        public string? Uuid { get; set; }
        public bool IsOperator { get; set; }
        public bool IsWhitelisted { get; set; }
        public bool IsBanned { get; set; }
    }
}
