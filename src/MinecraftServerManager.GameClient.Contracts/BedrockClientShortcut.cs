namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>
/// The only two Microsoft Store products that a Bedrock shortcut may select.
/// This is deliberately not a version string: Store licensing and update selection remain
/// entirely under Microsoft's control.
/// </summary>
public enum MinecraftBedrockChannel
{
    Stable,
    Preview,
}

/// <summary>
/// User-owned display metadata for an official Minecraft for Windows handoff.
/// It contains no install path, download URI, credential, or caller-selected version.
/// </summary>
public sealed class BedrockClientShortcut
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DisplayName { get; set; } = "Minecraft for Windows";

    public MinecraftBedrockChannel Channel { get; set; } = MinecraftBedrockChannel.Stable;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Versioned, non-secret registry document for Bedrock display shortcuts.</summary>
public sealed class BedrockClientShortcutRegistryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<BedrockClientShortcut> Shortcuts { get; set; } = [];
}
