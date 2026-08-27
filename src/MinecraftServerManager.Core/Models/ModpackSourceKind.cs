namespace MinecraftServerManager.Core.Models;

/// <summary>Verified catalog provenance for a manager-installed modpack.</summary>
public enum ModpackSourceKind
{
    None = 0,
    Ftb,
    Modrinth,
    CurseForge,
}
