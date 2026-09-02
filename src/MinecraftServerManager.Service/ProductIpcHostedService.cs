using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

internal enum ProductIpcExecutionClass
{
    ReadOnly,
    Mutation,
    LongMutation,
}

internal sealed record ProductIpcHostOptions
{
    public int MaximumConcurrentClients { get; init; } = ProductNamedPipeFactory.MaximumServerInstances;

    public int MaximumConcurrentMutations { get; init; } = 4;

    public int MaximumConcurrentLongMutations { get; init; } = 2;

    public TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan FrameWriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReadOnlyOperationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MutationOperationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan LongMutationOperationTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (MaximumConcurrentClients is < 2 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentClients));
        }

        if (MaximumConcurrentMutations < 1 ||
            MaximumConcurrentMutations >= MaximumConcurrentClients)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentMutations));
        }

        if (MaximumConcurrentLongMutations < 1 ||
            MaximumConcurrentLongMutations >= MaximumConcurrentClients)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentLongMutations));
        }

        ValidateTimeout(FrameReadTimeout, nameof(FrameReadTimeout), TimeSpan.FromMinutes(1));
        ValidateTimeout(FrameWriteTimeout, nameof(FrameWriteTimeout), TimeSpan.FromMinutes(1));
        ValidateTimeout(ReadOnlyOperationTimeout, nameof(ReadOnlyOperationTimeout), TimeSpan.FromMinutes(1));
        ValidateTimeout(MutationOperationTimeout, nameof(MutationOperationTimeout), TimeSpan.FromMinutes(10));
        ValidateTimeout(
            LongMutationOperationTimeout,
            nameof(LongMutationOperationTimeout),
            TimeSpan.FromHours(2));
        ValidateTimeout(ShutdownDrainTimeout, nameof(ShutdownDrainTimeout), TimeSpan.FromMinutes(1));
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal static class ProductIpcExecutionPolicy
{
    public static ProductIpcExecutionClass Classify(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method is
            ProductIpcProtocol.ServerStopMethod or
            ProductIpcProtocol.ServerRestartMethod or
            ProductIpcProtocol.ServerDeleteMethod or
            ProductIpcProtocol.ServerBackupCreateMethod or
            ProductIpcProtocol.ServerBackupRestoreMethod or
            ProductIpcProtocol.ServerImportCommitMethod or
            ProductIpcProtocol.ServerModpackUpdateCommitMethod or
            ProductIpcProtocol.UpdateDownloadMethod or
            ProductIpcProtocol.RemoteAccessStartMethod or
            ProductIpcProtocol.RemoteAccessStopMethod or
            ProductIpcProtocol.RemoteAccessReconnectMethod or
            ProductIpcProtocol.ProviderInstallMethod or
            ProductIpcProtocol.ProviderUninstallMethod)
        {
            return ProductIpcExecutionClass.LongMutation;
        }

        return method is
            ProductIpcProtocol.ServerSettingsUpdateMethod or
            ProductIpcProtocol.ServerPropertiesUpdateMethod or
            ProductIpcProtocol.ServerRegisterMethod or
            ProductIpcProtocol.ServerRemoveMethod or
            ProductIpcProtocol.ServerStartMethod or
            ProductIpcProtocol.ServerCommandMethod or
            ProductIpcProtocol.ServerImportBeginMethod or
            ProductIpcProtocol.ServerImportCancelMethod or
            ProductIpcProtocol.ServerModpackUpdateBeginMethod or
            ProductIpcProtocol.ServerModpackUpdateCancelMethod or
            ProductIpcProtocol.UpdateCheckMethod or
            ProductIpcProtocol.UpdateScheduleMethod or
            ProductIpcProtocol.RemoteAccountCreateMethod or
            ProductIpcProtocol.RemoteAccountAuthorizationUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinUpdateMethod or
            ProductIpcProtocol.RemoteAccountPinRevealMethod or
            ProductIpcProtocol.RemoteAccountDeleteMethod or
            ProductIpcProtocol.RemoteDeviceRevokeMethod or
            ProductIpcProtocol.NotificationDiscordSetMethod or
            ProductIpcProtocol.NotificationDiscordDeleteMethod or
            ProductIpcProtocol.NotificationPreferencesSetMethod or
            ProductIpcProtocol.ProviderSetEnabledMethod or
            ProductIpcProtocol.ProviderHealthMethod or
            ProductIpcProtocol.ProviderPublisherPinMethod or
            ProductIpcProtocol.ProviderPublisherRemoveMethod
            ? ProductIpcExecutionClass.Mutation
            : ProductIpcExecutionClass.ReadOnly;
    }
}

public sealed class ProductIpcHostedService : BackgroundService
{
    private readonly Func<ProductIpcRequest?, CancellationToken, Task<ProductIpcResponse>> _process;
    private readonly ProductLocalIpcAuditPolicy _auditPolicy;
    private readonly ILogger<ProductIpcHostedService> _logger;
    private readonly Func<bool, NamedPipeServerStream> _pipeFactory;
    private readonly ProductIpcHostOptions _options;
    private readonly ProductServiceState? _serviceState;
    private readonly SemaphoreSlim _clientSlots;
    private readonly SemaphoreSlim _mutationSlots;
    private readonly SemaphoreSlim _longMutationSlots;
    private readonly ConcurrentDictionary<long, Task> _activeHandlers = new();
    private readonly ConcurrentDictionary<long, NamedPipeServerStream> _activePipes = new();
    private long _nextHandlerId;

    public ProductIpcHostedService(
        ProductIpcMessageProcessor processor,
        ProductLocalIpcAuditPolicy auditPolicy,
        ILogger<ProductIpcHostedService> logger,
        ProductDataLayout layout,
        ProductServiceOptions serviceOptions,
        ProductServiceState serviceState)
        : this(
            processor.ProcessAsync,
            auditPolicy,
            logger,
            firstInstance => ProductNamedPipeFactory.CreateServer(
                layout,
                serviceOptions.IpcPipeName,
                firstInstance),
            new ProductIpcHostOptions(),
            serviceState)
    {
    }

    internal ProductIpcHostedService(
        Func<ProductIpcRequest?, CancellationToken, Task<ProductIpcResponse>> process,
        ProductLocalIpcAuditPolicy auditPolicy,
        ILogger<ProductIpcHostedService> logger,
        Func<bool, NamedPipeServerStream> pipeFactory,
        ProductIpcHostOptions options)
        : this(process, auditPolicy, logger, pipeFactory, options, serviceState: null)
    {
    }

    internal ProductIpcHostedService(
        Func<ProductIpcRequest?, CancellationToken, Task<ProductIpcResponse>> process,
        ProductLocalIpcAuditPolicy auditPolicy,
        ILogger<ProductIpcHostedService> logger,
        Func<bool, NamedPipeServerStream> pipeFactory,
        ProductIpcHostOptions options,
        ProductServiceState? serviceState)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(auditPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pipeFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _process = process;
        _auditPolicy = auditPolicy;
        _logger = logger;
        _pipeFactory = pipeFactory;
        _options = options;
        _serviceState = serviceState;
        _clientSlots = new SemaphoreSlim(options.MaximumConcurrentClients, options.MaximumConcurrentClients);
        _mutationSlots = new SemaphoreSlim(
            options.MaximumConcurrentMutations,
            options.MaximumConcurrentMutations);
        _longMutationSlots = new SemaphoreSlim(
            options.MaximumConcurrentLongMutations,
            options.MaximumConcurrentLongMutations);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var firstInstance = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _clientSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = _pipeFactory(firstInstance);
                    firstInstance = false;
                    _serviceState?.MarkIpcReady();
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    Dispatch(pipe, stoppingToken);
                    pipe = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    pipe?.Dispose();
                    _clientSlots.Release();
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or
                    UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    pipe?.Dispose();
                    _clientSlots.Release();
                    var diagnostic = _serviceState?.MarkIpcFailure(exception)
                        ?? ProductServiceState.CreateIpcFailureDiagnostic(exception);
                    _logger.LogError(
                        "The local IPC accept loop could not create or accept a protected named-pipe " +
                        "instance. Code: {FailureCode}; exception: {ExceptionType}; HRESULT: {HResult}.",
                        diagnostic.Code,
                        diagnostic.ExceptionType,
                        $"0x{unchecked((uint)diagnostic.HResult):X8}");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _serviceState?.MarkIpcNotReady();
            await DrainHandlersAsync().ConfigureAwait(false);
        }
    }

    private void Dispatch(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var handlerId = Interlocked.Increment(ref _nextHandlerId);
        _activePipes.TryAdd(handlerId, pipe);
        var task = RunClientAsync(handlerId, pipe, stoppingToken);
        _activeHandlers.TryAdd(handlerId, task);
        _ = task.ContinueWith(
            (completedTask, state) =>
            {
                _ = completedTask.Exception;
                var tuple = ((ProductIpcHostedService Service, long HandlerId))state!;
                tuple.Service._activeHandlers.TryRemove(tuple.HandlerId, out var ignored);
                _ = ignored;
            },
            (this, handlerId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunClientAsync(
        long handlerId,
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        try
        {
            await HandleClientAsync(pipe, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Closed a local IPC client after its framing deadline or disconnect.");
        }
        catch (Exception exception) when (
            exception is IOException or EndOfStreamException or InvalidDataException or JsonException)
        {
            _logger.LogWarning(
                "Rejected or closed an invalid local IPC request: {ErrorType}",
                exception.GetType().Name);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "An unexpected local IPC handler failure was isolated.");
        }
        finally
        {
            _activePipes.TryRemove(handlerId, out _);
            await pipe.DisposeAsync().ConfigureAwait(false);
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        ProductIpcRequest? request;
        ProductIpcResponse response;
        try
        {
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            readDeadline.CancelAfter(_options.FrameReadTimeout);
            request = await ProductIpcFrameCodec.ReadRequestAsync(pipe, readDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            request = null;
            response = ProductIpcMessageProcessor.Failure(
                Guid.Empty,
                new ProductIpcError("protocol.request_invalid", "IPC request is malformed."));
            await WriteResponseAsync(pipe, response, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            response = await _process(null, stoppingToken).ConfigureAwait(false);
        }
        else
        {
            using var peerDisconnected = new CancellationTokenSource();
            using var disconnectMonitorStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var disconnectMonitor = MonitorDisconnectAsync(
                pipe,
                peerDisconnected,
                disconnectMonitorStop.Token);
            try
            {
                response = await _auditPolicy.ExecuteAsync(
                        request,
                        TryGetClientIdentity(pipe),
                        (candidate, token) => ExecuteRequestAsync(
                            candidate,
                            peerDisconnected.Token,
                            token),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                disconnectMonitorStop.Cancel();
                await IgnoreExpectedDisconnectMonitorCompletionAsync(disconnectMonitor)
                    .ConfigureAwait(false);
            }
        }

        await WriteResponseAsync(pipe, response, stoppingToken).ConfigureAwait(false);
    }

    private async Task<ProductIpcResponse> ExecuteRequestAsync(
        ProductIpcRequest request,
        CancellationToken peerDisconnected,
        CancellationToken stoppingToken)
    {
        var executionClass = ProductIpcExecutionPolicy.Classify(request.Method);
        var operationSlots = executionClass switch
        {
            ProductIpcExecutionClass.LongMutation => _longMutationSlots,
            ProductIpcExecutionClass.Mutation => _mutationSlots,
            _ => null,
        };
        if (operationSlots is not null && !operationSlots.Wait(0))
        {
            return ProductIpcMessageProcessor.Failure(
                request.RequestId,
                new ProductIpcError(
                    "service.busy",
                    "Muhun MCSV Service is already processing the maximum number of local mutations."));
        }

        try
        {
            using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                peerDisconnected);
            operationDeadline.CancelAfter(GetOperationTimeout(executionClass));
            try
            {
                return await _process(request, operationDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (peerDisconnected.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (operationDeadline.IsCancellationRequested)
            {
                return ProductIpcMessageProcessor.Failure(
                    request.RequestId,
                    new ProductIpcError(
                        "service.operation_timeout",
                        "The local Service operation exceeded its bounded execution deadline."));
            }
        }
        finally
        {
            operationSlots?.Release();
        }
    }

    private TimeSpan GetOperationTimeout(ProductIpcExecutionClass executionClass)
        => executionClass switch
        {
            ProductIpcExecutionClass.LongMutation => _options.LongMutationOperationTimeout,
            ProductIpcExecutionClass.Mutation => _options.MutationOperationTimeout,
            _ => _options.ReadOnlyOperationTimeout,
        };

    private async Task WriteResponseAsync(
        NamedPipeServerStream pipe,
        ProductIpcResponse response,
        CancellationToken stoppingToken)
    {
        using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        writeDeadline.CancelAfter(_options.FrameWriteTimeout);
        await ProductIpcFrameCodec.WriteResponseAsync(pipe, response, writeDeadline.Token)
            .ConfigureAwait(false);
    }

    private static async Task MonitorDisconnectAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource peerDisconnected,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            _ = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            peerDisconnected.Cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            peerDisconnected.Cancel();
        }
    }

    private static async Task IgnoreExpectedDisconnectMonitorCompletionAsync(Task monitor)
    {
        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainHandlersAsync()
    {
        var handlers = _activeHandlers.Values.ToArray();
        if (handlers.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(handlers).WaitAsync(_options.ShutdownDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Forced {ActiveClientCount} local IPC clients closed after the shutdown drain deadline.",
                _activePipes.Count);
            foreach (var pipe in _activePipes.Values)
            {
                try
                {
                    pipe.Dispose();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "One or more local IPC clients failed while draining at shutdown.");
        }
    }

    private static string? TryGetClientIdentity(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return pipe.GetImpersonationUserName();
        }
        catch (Exception error) when (
            error is IOException or InvalidOperationException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            // The pipe ACL still provides the authorization boundary. A missing display name is
            // represented by a stable local-operator pseudonym in the audit policy.
            return null;
        }
    }
}
