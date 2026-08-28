namespace MinecraftServerManager.GameClient.Contracts;

public enum MinecraftClientInstanceState
{
    NotInstalled = 0,
    Installing,
    Ready,
    Starting,
    Running,
    Stopping,
    Failed,
}
