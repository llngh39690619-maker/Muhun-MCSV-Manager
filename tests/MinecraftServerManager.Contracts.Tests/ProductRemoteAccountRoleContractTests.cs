using System.Text.Json;
using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductRemoteAccountRoleContractTests
{
    [Fact]
    public void RoleValues_AreStableAndSummaryRoundTripsRoleWithoutSecrets()
    {
        Assert.Equal(1, (int)ProductRemoteAccountRole.Owner);
        Assert.Equal(2, (int)ProductRemoteAccountRole.Admin);
        Assert.Equal(3, (int)ProductRemoteAccountRole.Operator);
        Assert.Equal(4, (int)ProductRemoteAccountRole.Viewer);

        var summary = new ProductRemoteAccountSummary(
            "operator1",
            "mcsv-local-approved-account",
            null,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            [new ProductPermissionGrant(
                ProductPermissionCodes.ServerRead,
                ProductPermissionScope.ForServer(Guid.Parse("d68450bb-c034-4f1f-90f2-2a2ddd049c3d")))],
            ProductRemoteAccountRole.Operator);

        var json = JsonSerializer.Serialize(summary);
        var restored = JsonSerializer.Deserialize<ProductRemoteAccountSummary>(json);

        Assert.Equal(ProductRemoteAccountRole.Operator, restored?.Role);
        Assert.DoesNotContain("pin", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutationRequests_AllowRoleToBeOmittedForProtocolCompatibility()
    {
        var create = JsonSerializer.Deserialize<ProductCreateRemoteAccountRequest>(
            """
            {"Username":"viewer1","CredentialSubject":"subject","Email":null,"Pin":"1234","Grants":[]}
            """);
        var update = JsonSerializer.Deserialize<ProductUpdateRemoteAccountAuthorizationRequest>(
            """
            {"Enabled":true,"Grants":[]}
            """);

        Assert.Null(create?.Role);
        Assert.Null(update?.Role);
    }
}
