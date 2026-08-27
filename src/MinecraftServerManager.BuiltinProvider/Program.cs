using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.BuiltinProvider;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--mcsv-provider-rpc", var apiVersion] ||
            !string.Equals(apiVersion, ProductApiProtocol.CurrentVersion.ToString(), StringComparison.Ordinal))
        {
            return 64;
        }

        try
        {
            var hostInput = Console.OpenStandardInput();
            var hostOutput = Console.OpenStandardOutput();
            var request = await ProviderRpcFrameCodec.ReadAsync<ProductProviderRpcRequest>(
                    hostInput,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            using var brokerHandler = new ProviderBrokerHttpMessageHandler(
                hostInput,
                hostOutput,
                request.RequestId);
            using var brokerClient = new HttpClient(brokerHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var response = await BuiltinProviderRequestHandler.HandleAsync(
                    request,
                    CancellationToken.None,
                    brokerClient)
                .ConfigureAwait(false);
            await ProviderRpcFrameCodec.WriteAsync(
                    hostOutput,
                    response,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is
            EndOfStreamException or
            IOException or
            InvalidDataException or
            System.Text.Json.JsonException or
            HttpRequestException)
        {
            return 65;
        }
    }
}
