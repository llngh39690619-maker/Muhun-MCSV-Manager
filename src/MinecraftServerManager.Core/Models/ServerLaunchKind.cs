namespace MinecraftServerManager.Core.Models;

/// <summary>How Java starts a server instance.</summary>
public enum ServerLaunchKind
{
    /// <summary>Starts a conventional server with <c>java -jar server.jar</c>.</summary>
    ExecutableJar = 0,

    /// <summary>
    /// Starts an installed Forge/NeoForge layout by passing its generated Java argument files
    /// directly to Java. No batch file or shell script is executed.
    /// </summary>
    JavaArgumentFiles = 1,
}
