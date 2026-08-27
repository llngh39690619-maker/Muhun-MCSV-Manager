using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Service;

internal interface IProductRemoteWebHost : IAsyncDisposable
{
    void RevokeAllSessions();
    void EnterFailClosedMode();
}

internal interface IProductRemoteWebHostFactory
{
    Task<IProductRemoteWebHost> StartAsync(
        Uri publicOrigin,
        int localPort,
        CancellationToken applicationStopping,
        CancellationToken cancellationToken);
}

internal sealed class ProductRemoteWebHostFactory(
    ProductRemoteControlBackend backend,
    ProductRemoteCredentialStore credentials,
    ProductRemoteSecurityAuditSink securityAudit) : IProductRemoteWebHostFactory
{
    public async Task<IProductRemoteWebHost> StartAsync(
        Uri publicOrigin,
        int localPort,
        CancellationToken applicationStopping,
        CancellationToken cancellationToken)
    {
        if (localPort != ProductRemoteWebSupervisor.LocalWebPort)
        {
            throw new InvalidOperationException("Remote Web host port is not the product-owned port.");
        }

        var host = await RemoteControlHost.StartAsync(
                backend,
                CreateOptions(publicOrigin, localPort, applicationStopping),
                credentials,
                cancellationToken: cancellationToken,
                securityAuditSink: securityAudit)
            .ConfigureAwait(false);
        return new ProductRemoteWebHost(host);
    }

    internal static RemoteControlOptions CreateOptions(
        Uri publicOrigin,
        int localPort,
        CancellationToken applicationStopping)
        => new()
        {
            Port = localPort,
            PublicOrigin = publicOrigin,
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.TailscaleFunnel,
            OperationCancellationToken = applicationStopping,
            RequireDurableSecurityAudit = true,
        };

    private sealed class ProductRemoteWebHost(RemoteControlHost host) : IProductRemoteWebHost
    {
        public void RevokeAllSessions() => host.RevokeAllSessions();
        public void EnterFailClosedMode() => host.EnterFailClosedMode();
        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
