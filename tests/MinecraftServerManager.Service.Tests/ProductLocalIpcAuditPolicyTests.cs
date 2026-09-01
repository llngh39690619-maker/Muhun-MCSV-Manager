using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Security;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductLocalIpcAuditPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-mcsv-local-ipc-audit-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mutation_IsAuditedBeforeAndAfterWithoutSecretPayload()
    {
        var (policy, store) = await CreateAsync();
        var request = Request(ProductIpcProtocol.ServerCommandMethod) with
        {
            ServerId = Guid.NewGuid(),
            Command = "op should-never-enter-audit",
        };
        var invoked = false;

        var response = await policy.ExecuteAsync(
            request,
            "WORKSTATION\\Administrator",
            (received, _) =>
            {
                invoked = true;
                Assert.Same(request, received);
                return Task.FromResult(Success(received.RequestId));
            },
            CancellationToken.None);

        Assert.True(invoked);
        Assert.True(response.Success);
        var entries = await store.ReadRecentAsync(10);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(request.RequestId, entry.CorrelationId);
            Assert.Equal(request.ServerId, entry.ServerId);
            Assert.Equal("console.write", entry.PermissionCode);
            Assert.DoesNotContain("should-never", entry.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(entries, entry => entry.OutcomeCode == "accepted");
        Assert.Contains(entries, entry => entry.OutcomeCode == "succeeded");
    }

    [Fact]
    public async Task ReadOnlyRequest_DoesNotCreateAuditNoise()
    {
        var (policy, store) = await CreateAsync();
        var request = Request(ProductIpcProtocol.ServerListMethod);

        var response = await policy.ExecuteAsync(
            request,
            null,
            (received, _) => Task.FromResult(Success(received.RequestId)),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Empty(await store.ReadRecentAsync(10));
    }

    [Theory]
    [InlineData(ProductIpcProtocol.RemoteAccountPinRevealMethod)]
    [InlineData(ProductIpcProtocol.NotificationDiscordSetMethod)]
    [InlineData(ProductIpcProtocol.UpdateScheduleMethod)]
    [InlineData(ProductIpcProtocol.ServerImportCommitMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupCreateMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupRestoreMethod)]
    [InlineData(ProductIpcProtocol.ServerSettingsUpdateMethod)]
    [InlineData(ProductIpcProtocol.ServerAdministrationMethod)]
    [InlineData(ProductIpcProtocol.ServerPropertiesReadMethod)]
    [InlineData(ProductIpcProtocol.ServerPropertiesUpdateMethod)]
    [InlineData(ProductIpcProtocol.ServerModpackUpdateBeginMethod)]
    [InlineData(ProductIpcProtocol.ServerModpackUpdateCommitMethod)]
    [InlineData(ProductIpcProtocol.ServerModpackUpdateCancelMethod)]
    [InlineData(ProductIpcProtocol.ProviderSetEnabledMethod)]
    [InlineData(ProductIpcProtocol.ProviderHealthMethod)]
    [InlineData(ProductIpcProtocol.ProviderUninstallMethod)]
    [InlineData(ProductIpcProtocol.ProviderInstallMethod)]
    [InlineData(ProductIpcProtocol.ProviderPublisherPinMethod)]
    [InlineData(ProductIpcProtocol.ProviderPublisherRemoveMethod)]
    public void SensitiveAdministrationMethods_AreClassified(string method)
    {
        var descriptor = ProductLocalIpcAuditPolicy.Describe(Request(method));
        Assert.NotNull(descriptor);
        Assert.StartsWith("ipc.", descriptor.ActionCode, StringComparison.Ordinal);
        Assert.NotEmpty(descriptor.PermissionCode);
    }

    [Fact]
    public void ServerPropertiesAudit_DistinguishesReadFromWritePermission()
    {
        Assert.Equal(
            ProductPermissionCodes.FileRead,
            ProductLocalIpcAuditPolicy.Describe(
                Request(ProductIpcProtocol.ServerPropertiesReadMethod))!.PermissionCode);
        Assert.Equal(
            ProductPermissionCodes.FileWrite,
            ProductLocalIpcAuditPolicy.Describe(
                Request(ProductIpcProtocol.ServerPropertiesUpdateMethod))!.PermissionCode);
    }

    [Theory]
    [InlineData(ProductIpcProtocol.ServerRegistrationMethod)]
    [InlineData(ProductIpcProtocol.ServerBackupListMethod)]
    [InlineData(ProductIpcProtocol.ProviderListMethod)]
    [InlineData(ProductIpcProtocol.ProviderPublisherListMethod)]
    public void ProviderReads_DoNotCreatePrivilegedMutationAudit(string method)
        => Assert.Null(ProductLocalIpcAuditPolicy.Describe(Request(method)));

    [Fact]
    public void OversizedOrWhitespaceIdentity_IsPseudonymizedDeterministically()
    {
        var unsafeIdentity = new string('A', 100) + " secret name";
        var first = ProductLocalIpcAuditPolicy.NormalizeIdentity(unsafeIdentity);
        var second = ProductLocalIpcAuditPolicy.NormalizeIdentity(unsafeIdentity);

        Assert.Equal(first, second);
        Assert.StartsWith("local-operator-", first, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", first, StringComparison.OrdinalIgnoreCase);
        Assert.True(first.Length <= 64);
    }

    private async Task<(ProductLocalIpcAuditPolicy Policy, ProductSecurityAuditStore Store)> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        var database = new ProductDatabase(Path.Combine(_root, "product.v1.db"));
        await database.InitializeAsync();
        var store = new ProductSecurityAuditStore(database);
        return (new ProductLocalIpcAuditPolicy(store, TimeProvider.System), store);
    }

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

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
