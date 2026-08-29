namespace MinecraftServerManager.GameClient;

/// <summary>
/// Indicates that Minecraft Services accepted a profile mutation, but a bounded follow-up
/// read could not yet obtain and verify the resulting profile. The mutation must not be retried
/// automatically because the remote service may already have applied it.
/// </summary>
public sealed class MinecraftProfileSynchronizationPendingException : Exception
{
    public MinecraftProfileSynchronizationPendingException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
