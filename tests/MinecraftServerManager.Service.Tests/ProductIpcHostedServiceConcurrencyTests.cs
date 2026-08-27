using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductIpcHostedServiceConcurrencyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };

    [Fact]
    public async Task SimulatedLongBackup_DoesNotBlockStatusAndDoesNotUseShortDeadline()
    {
        var backupEntered = NewSignal();
        var releaseBackup = NewSignal();
        var serverId = Guid.NewGuid();
        await using var harness = await Harness.CreateAsync(
            async (request, cancellationToken) =>
            {
                if (request?.Method == ProductIpcProtocol.ServerBackupCreateMethod)
                {
                    backupEntered.TrySetResult();
                    await releaseBackup.Task.WaitAsync(cancellationToken);
                }

                return Success(request?.RequestId ?? Guid.Empty);
            },
            TestOptions() with
            {
                ReadOnlyOperationTimeout = TimeSpan.FromMilliseconds(120),
                LongMutationOperationTimeout = TimeSpan.FromSeconds(3),
            });

        var backup = harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        });
        await backupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Keep the backup alive beyond the ordinary read deadline. A former single 10-second
        // request deadline (scaled down by the injected test options) would cancel it here.
        await Task.Delay(250);
        var stopwatch = Stopwatch.StartNew();
        var statuses = Enumerable.Range(0, 16)
            .Select(_ => harness.SendAsync(Request(ProductIpcProtocol.ServerStatusMethod) with
            {
                ServerId = serverId,
            }))
            .ToArray();
        var responses = await Task.WhenAll(statuses).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(backup.IsCompleted);

        releaseBackup.TrySetResult();
        Assert.True((await backup.WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Fact]
    public async Task LongMutationLimit_FailsFastWithoutConsumingReadCapacity()
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var serverId = Guid.NewGuid();
        var invocations = 0;
        await using var harness = await Harness.CreateAsync(
            async (request, cancellationToken) =>
            {
                if (request?.Method == ProductIpcProtocol.ServerBackupCreateMethod)
                {
                    Interlocked.Increment(ref invocations);
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return Success(request?.RequestId ?? Guid.Empty);
            },
            TestOptions() with { MaximumConcurrentLongMutations = 1 });

        var first = harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopwatch = Stopwatch.StartNew();
        var rejected = await harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        }).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.False(rejected.Success);
        Assert.Equal("service.busy", rejected.Error?.Code);
        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));

        var status = await harness.SendAsync(Request(ProductIpcProtocol.ServerStatusMethod) with
        {
            ServerId = serverId,
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(status.Success);

        releaseFirst.TrySetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Fact]
    public async Task LongMutationDeadline_ReturnsBoundedTimeoutResponse()
    {
        var entered = NewSignal();
        var serverId = Guid.NewGuid();
        await using var harness = await Harness.CreateAsync(
            async (request, cancellationToken) =>
            {
                if (request?.Method == ProductIpcProtocol.ServerBackupCreateMethod)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return Success(request?.RequestId ?? Guid.Empty);
            },
            TestOptions() with { LongMutationOperationTimeout = TimeSpan.FromMilliseconds(300) });

        var responseTask = harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(response.Success);
        Assert.Equal("service.operation_timeout", response.Error?.Code);
    }

    [Fact]
    public async Task Shutdown_CancelsAndDrainsActiveLongMutation()
    {
        var entered = NewSignal();
        var cancellationObserved = NewSignal();
        var serverId = Guid.NewGuid();
        await using var harness = await Harness.CreateAsync(
            async (request, cancellationToken) =>
            {
                if (request?.Method == ProductIpcProtocol.ServerBackupCreateMethod)
                {
                    entered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return Success(request?.RequestId ?? Guid.Empty);
            },
            TestOptions());

        var requestTask = harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await harness.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await requestTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CallerDisconnect_CancelsLongMutationAndReleasesItsSlot()
    {
        var firstEntered = NewSignal();
        var firstCancelled = NewSignal();
        var invocation = 0;
        var serverId = Guid.NewGuid();
        await using var harness = await Harness.CreateAsync(
            async (request, cancellationToken) =>
            {
                if (request?.Method == ProductIpcProtocol.ServerBackupCreateMethod &&
                    Interlocked.Increment(ref invocation) == 1)
                {
                    firstEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancelled.TrySetResult();
                        throw;
                    }
                }

                return Success(request?.RequestId ?? Guid.Empty);
            },
            TestOptions() with { MaximumConcurrentLongMutations = 1 });

        var abandonedClient = await harness.ConnectAndWriteAsync(
            Request(ProductIpcProtocol.ServerBackupCreateMethod) with { ServerId = serverId });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await abandonedClient.DisposeAsync();
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retry = await harness.SendAsync(Request(ProductIpcProtocol.ServerBackupCreateMethod) with
        {
            ServerId = serverId,
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(retry.Success);
        Assert.Equal(2, Volatile.Read(ref invocation));
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerStopMethod)]
    [InlineData(ProductIpcProtocol.ServerRestartMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupCreateMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupRestoreMethod)]
    [InlineData(ProductIpcProtocol.ServerImportCommitMethod)]
    [InlineData(ProductIpcProtocol.ServerModpackUpdateCommitMethod)]
    [InlineData(ProductIpcProtocol.UpdateDownloadMethod)]
    [InlineData(ProductIpcProtocol.ProviderInstallMethod)]
    public void SlowFilesystemAndProcessMethods_UseLongMutationPolicy(string method)
        => Assert.Equal(
            ProductIpcExecutionClass.LongMutation,
            ProductIpcExecutionPolicy.Classify(method));

    private static ProductIpcHostOptions TestOptions() => new()
    {
        MaximumConcurrentClients = 8,
        MaximumConcurrentMutations = 3,
        MaximumConcurrentLongMutations = 2,
        FrameReadTimeout = TimeSpan.FromSeconds(1),
        FrameWriteTimeout = TimeSpan.FromSeconds(1),
        ReadOnlyOperationTimeout = TimeSpan.FromSeconds(1),
        MutationOperationTimeout = TimeSpan.FromSeconds(2),
        LongMutationOperationTimeout = TimeSpan.FromSeconds(4),
        ShutdownDrainTimeout = TimeSpan.FromSeconds(1),
    };

    private static ProductIpcRequest Request(string method) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        Guid.NewGuid(),
        method,
        ProductApiProtocol.MinimumSupportedVersion,
        ProductApiProtocol.CurrentVersion);

    private static ProductIpcResponse Success(Guid requestId) => new(
        ProductIpcProtocol.CurrentSchemaVersion,
        requestId,
        true,
        null,
        null);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _pipeName;
        private readonly ProductIpcHostedService _service;
        private int _stopped;

        private Harness(string root, string pipeName, ProductIpcHostedService service)
        {
            _root = root;
            _pipeName = pipeName;
            _service = service;
        }

        public static async Task<Harness> CreateAsync(
            Func<ProductIpcRequest?, CancellationToken, Task<ProductIpcResponse>> process,
            ProductIpcHostOptions options)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "muhun-mcsv-ipc-concurrency-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var database = new ProductDatabase(Path.Combine(root, "product.v1.db"));
            await database.InitializeAsync();
            var audit = new ProductLocalIpcAuditPolicy(
                new ProductSecurityAuditStore(database),
                TimeProvider.System);
            var pipeName = $"muhun-mcsv-ipc-test-{Guid.NewGuid():N}";
            var service = new ProductIpcHostedService(
                process,
                audit,
                NullLogger<ProductIpcHostedService>.Instance,
                _ => new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    options.MaximumConcurrentClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    8 * 1024,
                    8 * 1024),
                options);
            var harness = new Harness(root, pipeName, service);
            await service.StartAsync(CancellationToken.None);
            return harness;
        }

        public async Task<ProductIpcResponse> SendAsync(ProductIpcRequest request)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync(3_000, deadline.Token);
            await WriteRequestAsync(pipe, request, deadline.Token);
            return await ReadResponseAsync(pipe, deadline.Token);
        }

        public async Task<NamedPipeClientStream> ConnectAndWriteAsync(ProductIpcRequest request)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                await pipe.ConnectAsync(3_000, deadline.Token);
                await WriteRequestAsync(pipe, request, deadline.Token);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _service.StopAsync(deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _service.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task WriteRequestAsync(
        Stream output,
        ProductIpcRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        Assert.InRange(payload.Length, 2, ProductIpcProtocol.MaximumFrameBytes);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<ProductIpcResponse> ReadResponseAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var length = new byte[sizeof(int)];
        await input.ReadExactlyAsync(length, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        Assert.InRange(payloadLength, 2, ProductIpcProtocol.MaximumFrameBytes);
        var payload = new byte[payloadLength];
        await input.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<ProductIpcResponse>(payload, JsonOptions)
               ?? throw new InvalidDataException("IPC response was null.");
    }
}
