namespace MinecraftServerManager.Core.Models;

/// <summary>Top-level persisted settings owned by the manager.</summary>
public sealed class ManagerSettings
{
    public const int CurrentSchemaVersion = 12;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ApplicationAppearanceSettings Appearance { get; set; } = new();

    public RemoteControlSettings RemoteControl { get; set; } = new();

    public ManagerUiSettings UserInterface { get; set; } = new();

    public NewServerDefaultsSettings NewServerDefaults { get; set; } = new();

    /// <summary>
    /// GUI-owned appearance metadata for Service-managed servers. Service launch paths and server
    /// files are deliberately absent; every referenced image must remain below the GUI themes root.
    /// </summary>
    public Dictionary<Guid, ServerAppearancePreference> ServiceServerAppearances { get; set; } = [];

    public List<ServerInstance> Instances { get; set; } = [];
}
