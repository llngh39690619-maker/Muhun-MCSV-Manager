using System.Runtime.ExceptionServices;

namespace MinecraftServerManager.GameClient;

internal static class ExceptionGraphSafety
{
    public static OutOfMemoryException? FindOutOfMemory(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException outOfMemory)
        {
            return outOfMemory;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (FindOutOfMemory(inner) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

        return exception.InnerException is { } innerException
            ? FindOutOfMemory(innerException)
            : null;
    }

    public static void RethrowOutOfMemory(Exception exception)
    {
        if (FindOutOfMemory(exception) is { } outOfMemory)
        {
            ExceptionDispatchInfo.Capture(outOfMemory).Throw();
        }
    }
}
