namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>Client runtimes exposed by the Java-edition creation workflow.</summary>
public enum MinecraftClientLoader
{
    Vanilla = 0,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
    OptiFine,
    LabyMod,
}

public enum MinecraftClientLoaderInstallKind
{
    Managed = 0,
    ExternalInstallerRequired,
    Unsupported,
}
