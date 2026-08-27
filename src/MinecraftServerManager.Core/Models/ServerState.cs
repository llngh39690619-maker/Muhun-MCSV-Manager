namespace MinecraftServerManager.Core.Models;

public enum ServerState
{
    Stopped = 0,
    Starting,
    Running,
    Stopping,
    Crashed,
    Faulted
}
