namespace MinecraftServerManager.GameClient.Contracts;

/// <summary>How one client instance obtains its effective Java heap range.</summary>
public enum MinecraftClientMemoryMode
{
    UseGlobalDefault = 0,
    Automatic = 1,
    Manual = 2,
}
