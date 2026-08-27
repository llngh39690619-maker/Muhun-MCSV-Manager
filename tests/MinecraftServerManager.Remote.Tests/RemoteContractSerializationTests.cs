using System.Text.Json;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteContractSerializationTests
{
    [Fact]
    public void SecuritySensitiveEnums_SerializeAsNamesNotNumbers()
    {
        var request = new RemotePlayerActionRequestDto("Steve", RemotePlayerActionKind.Ban, "test");

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"Ban\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Action\":2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupWebContract_ContainsOnlyOpaquePathFreeMetadata()
    {
        var backup = new RemoteBackupSummaryDto(
            new string('a', 64),
            "backup-20260827.zip",
            1024,
            DateTimeOffset.Parse("2026-08-27T00:00:00Z"));

        var json = JsonSerializer.Serialize(backup);

        Assert.Contains(new string('a', 64), json, StringComparison.Ordinal);
        Assert.Contains("backup-20260827.zip", json, StringComparison.Ordinal);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("directory", json, StringComparison.OrdinalIgnoreCase);
    }
}
