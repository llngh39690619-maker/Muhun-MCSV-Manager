using System.Diagnostics;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.HostileProvider;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--child", var marker])
        {
            await File.WriteAllTextAsync(marker, "escaped").ConfigureAwait(false);
            return 0;
        }

        if (args is not ["--mcsv-provider-rpc", var version] ||
            version != ProductApiProtocol.CurrentVersion.ToString())
        {
            return 64;
        }

        var scratch = Path.GetTempPath();
        var markerPath = Path.Combine(scratch, "pre-job-child-escape.txt");
        var childStarted = TryStartChild(markerPath);
        var directNetwork = await TryDirectNetworkAsync().ConfigureAwait(false);
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var request = await ReadFrameAsync<ProductProviderRpcRequest>(input)
            .ConfigureAwait(false);
        var secretPath = request.Payload.TryGetProperty("secretPath", out var secretValue)
            ? secretValue.GetString()
            : null;
        var brokerUri = request.Payload.TryGetProperty("brokerUri", out var brokerValue)
            ? brokerValue.GetString()
            : null;
        var secretRead = TryRead(secretPath);
        var packageWrite = TryWrite(Path.Combine(AppContext.BaseDirectory, "provider-write.txt"));
        var packageDelete = TryDelete(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.json")
            .FirstOrDefault());
        var scratchWrite = TryWrite(
            Path.Combine(scratch, "scratch-write.txt"),
            out var scratchWriteError);
        if (request.Payload.TryGetProperty("crash", out var crashValue) && crashValue.GetBoolean())
        {
            return 97;
        }

        string? brokerError = null;
        string? brokerBody = null;
        if (!string.IsNullOrWhiteSpace(brokerUri))
        {
            var brokerId = Guid.NewGuid().ToString("N");
            await WriteFrameAsync(
                    output,
                    new ProductProviderBrokerHttpRequest(
                        ProductProviderRpcProtocol.CurrentVersion,
                        ProductProviderRpcProtocol.BrokerHttpRequestMessageType,
                        request.RequestId,
                        brokerId,
                        "GET",
                        brokerUri,
                        new Dictionary<string, string> { ["Accept"] = "application/json" },
                        BodyBase64: null))
                .ConfigureAwait(false);
            var brokerResponse = await ReadFrameAsync<ProductProviderBrokerHttpResponse>(input)
                .ConfigureAwait(false);
            brokerError = brokerResponse.Error?.Code;
            brokerBody = brokerResponse.BodyBase64 is null
                ? null
                : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(brokerResponse.BodyBase64));
        }

        await Task.Delay(100).ConfigureAwait(false);
        var response = new ProductProviderRpcResponse(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.ResponseMessageType,
            request.RequestId,
            ProductProviderRpcProtocol.SuccessStatus,
            JsonSerializer.SerializeToElement(new
            {
                childStarted,
                childEscaped = File.Exists(markerPath),
                directNetworkConnected = directNetwork.Connected,
                directNetworkError = directNetwork.Error,
                secretRead,
                packageWrite,
                packageDelete,
                scratchWrite,
                scratch,
                scratchWriteError,
                brokerError,
                brokerBody,
            }),
            Error: null);
        await WriteFrameAsync(output, response).ConfigureAwait(false);
        return 0;
    }

    private static bool TryStartChild(string markerPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--child", markerPath },
            });
            return process is not null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<DirectNetworkResult> TryDirectNetworkAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync("1.1.1.1", 443, timeout.Token).ConfigureAwait(false);
            return new DirectNetworkResult(client.Connected, Error: null);
        }
        catch (SocketException error)
        {
            return new DirectNetworkResult(false, error.SocketErrorCode.ToString());
        }
        catch (Exception error) when (error is
            OperationCanceledException or
            IOException or
            UnauthorizedAccessException)
        {
            return new DirectNetworkResult(false, error.GetType().Name);
        }
    }

    private readonly record struct DirectNetworkResult(bool Connected, string? Error);

    private static bool TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            _ = File.ReadAllText(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWrite(string path) => TryWrite(path, out _);

    private static bool TryDelete(string? path)
    {
        if (path is null)
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWrite(string path, out string? errorMessage)
    {
        try
        {
            File.WriteAllText(path, "write");
            errorMessage = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errorMessage = error.GetType().Name + ": " + error.Message;
            return false;
        }
    }

    private static async Task<T> ReadFrameAsync<T>(Stream input)
    {
        var prefix = new byte[4];
        await input.ReadExactlyAsync(prefix).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 or > ProductProviderRpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("Hostile fixture received an invalid frame size.");
        }

        var payload = new byte[length];
        await input.ReadExactlyAsync(payload).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
               ?? throw new InvalidDataException("Hostile fixture received JSON null.");
    }

    private static async Task WriteFrameAsync<T>(Stream output, T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is < 2 or > ProductProviderRpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("Hostile fixture produced an invalid frame size.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await output.WriteAsync(prefix).ConfigureAwait(false);
        await output.WriteAsync(payload).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
    };
}
