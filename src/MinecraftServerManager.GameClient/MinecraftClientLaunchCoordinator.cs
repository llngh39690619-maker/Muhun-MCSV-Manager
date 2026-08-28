using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

public sealed class MinecraftClientLaunchCoordinator
{
    private static readonly TimeSpan FailedLaunchTerminationTimeout = TimeSpan.FromSeconds(5);
    private readonly MinecraftClientMemoryRecommendationService _memoryRecommendationService;
    private readonly IMinecraftClientProcessBuilder _processBuilder;
    private readonly MinecraftClientProcessRecoveryService _processRecoveryService;
    private readonly IMinecraftClientJavaExecutableProbe _javaExecutableProbe;

    public MinecraftClientLaunchCoordinator(
        MinecraftClientMemoryRecommendationService memoryRecommendationService,
        IMinecraftClientProcessBuilder processBuilder)
        : this(memoryRecommendationService, processBuilder, new MinecraftClientProcessRecoveryService())
    {
    }

    public MinecraftClientLaunchCoordinator(
        MinecraftClientMemoryRecommendationService memoryRecommendationService,
        IMinecraftClientProcessBuilder processBuilder,
        MinecraftClientProcessRecoveryService processRecoveryService)
        : this(
            memoryRecommendationService,
            processBuilder,
            processRecoveryService,
            new MinecraftClientJavaExecutableProbe())
    {
    }

    internal MinecraftClientLaunchCoordinator(
        MinecraftClientMemoryRecommendationService memoryRecommendationService,
        IMinecraftClientProcessBuilder processBuilder,
        MinecraftClientProcessRecoveryService processRecoveryService,
        IMinecraftClientJavaExecutableProbe javaExecutableProbe)
    {
        _memoryRecommendationService = memoryRecommendationService ??
            throw new ArgumentNullException(nameof(memoryRecommendationService));
        _processBuilder = processBuilder ?? throw new ArgumentNullException(nameof(processBuilder));
        _processRecoveryService = processRecoveryService ??
            throw new ArgumentNullException(nameof(processRecoveryService));
        _javaExecutableProbe = javaExecutableProbe
            ?? throw new ArgumentNullException(nameof(javaExecutableProbe));
    }

    public async Task<MinecraftClientProcessSession> LaunchAsync(
        MinecraftClientInstance instance,
        NewMinecraftClientDefaultsSettings globalDefaults,
        AuthenticatedMinecraftSession authenticatedSession,
        CancellationToken cancellationToken = default)
    {
        return await LaunchCoreAsync(
                instance,
                globalDefaults,
                authenticatedSession,
                persistProcessIdentityAsync: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts Minecraft only when its recovery identity can be durably persisted. Once the
    /// operating-system process has started, cancellation no longer skips persistence or rollback:
    /// a persistence failure terminates the new process before this method reports the failure.
    /// </summary>
    public async Task<MinecraftClientProcessSession> LaunchAsync(
        MinecraftClientInstance instance,
        NewMinecraftClientDefaultsSettings globalDefaults,
        AuthenticatedMinecraftSession authenticatedSession,
        Func<MinecraftClientProcessIdentity, CancellationToken, Task> persistProcessIdentityAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistProcessIdentityAsync);
        return await LaunchCoreAsync(
                instance,
                globalDefaults,
                authenticatedSession,
                persistProcessIdentityAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MinecraftClientProcessSession> LaunchCoreAsync(
        MinecraftClientInstance instance,
        NewMinecraftClientDefaultsSettings globalDefaults,
        AuthenticatedMinecraftSession authenticatedSession,
        Func<MinecraftClientProcessIdentity, CancellationToken, Task>? persistProcessIdentityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(globalDefaults);
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        if (!string.IsNullOrWhiteSpace(instance.AccountId) &&
            !string.Equals(instance.AccountId, authenticatedSession.AccountId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The selected Microsoft account does not match this client instance.");
        }

        if (_processRecoveryService.IsMatchingProcessActive(instance))
        {
            throw new InvalidOperationException(
                "This Minecraft client instance is already running in another manager session.");
        }

        await ValidateJavaExecutableAsync(instance, cancellationToken).ConfigureAwait(false);

        var memory = await _memoryRecommendationService.ResolveAsync(instance, globalDefaults, cancellationToken)
            .ConfigureAwait(false);
        var process = await _processBuilder.BuildAsync(instance, authenticatedSession, memory, cancellationToken)
            .ConfigureAwait(false);
        process.EnableRaisingEvents = true;
        var startedAtUtc = DateTimeOffset.UtcNow;
        MinecraftClientProcessSession? session = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Minecraft process did not start.");
            }

            var persistentIdentity = MinecraftClientProcessRecoveryService
                .CaptureStartedProcessIdentity(process);
            session = persistentIdentity is null
                ? new MinecraftClientProcessSession(process, startedAtUtc)
                : new MinecraftClientProcessSession(process, persistentIdentity);
            if (process.StartInfo.RedirectStandardOutput || process.StartInfo.RedirectStandardError)
            {
                session.BeginLogCapture();
            }

            if (persistProcessIdentityAsync is not null)
            {
                if (persistentIdentity is null)
                {
                    throw new InvalidOperationException(
                        "Minecraft started without an inspectable Java process identity. " +
                        "The process was stopped because crash recovery could not be guaranteed.");
                }

                // The process is already live. Persistence and rollback deliberately ignore a
                // caller cancellation from this point so cancellation cannot orphan Minecraft.
                await persistProcessIdentityAsync(persistentIdentity, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception launchError)
        {
            if (session is not null)
            {
                Exception? terminationError = null;
                try
                {
                    await session.TerminateImmediatelyAsync(
                            FailedLaunchTerminationTimeout,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    terminationError = error;
                }
                finally
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                if (terminationError is not null)
                {
                    throw new AggregateException(
                        "Minecraft launch failed and the newly started process could not be " +
                        "confirmed stopped within the safety timeout.",
                        launchError,
                        terminationError);
                }
            }
            else
            {
                process.Dispose();
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(launchError)
                .Throw();
            throw;
        }
    }

    private async Task ValidateJavaExecutableAsync(
        MinecraftClientInstance instance,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.JavaExecutablePath))
        {
            return;
        }

        int actualMajorVersion;
        try
        {
            actualMajorVersion = await _javaExecutableProbe
                .ProbeMajorVersionAsync(instance.JavaExecutablePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException(
                "Minecraft was not started because X MCSV could not revalidate the selected " +
                "Java executable. Choose a working java.exe/javaw.exe in client settings, or " +
                "clear the custom path to restore the managed Java runtime.",
                error);
        }

        if (instance.JavaMajorVersion is { } savedMajorVersion
            && savedMajorVersion != actualMajorVersion)
        {
            throw new InvalidDataException(
                $"Minecraft was not started because the selected Java executable changed after " +
                $"it was saved (saved Java {savedMajorVersion}, current Java " +
                $"{actualMajorVersion}). Re-select the executable in client settings, or clear " +
                "the custom path to restore the managed Java runtime.");
        }

        MinecraftClientJavaCompatibility.EnsureMatchesMinecraft(
            instance.GameVersion,
            actualMajorVersion);
    }
}
