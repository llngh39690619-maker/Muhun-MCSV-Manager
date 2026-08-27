using MinecraftServerManager.Contracts.Security;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductAuthorizationTests
{
    private static readonly Guid ServerA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ServerB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void EmptyGrantSet_DefaultsToDeny()
    {
        var decision = ProductAuthorization.Evaluate(
            [],
            ProductPermissionCodes.ServerStart,
            ServerA);

        Assert.Equal(ProductAuthorizationDecision.Denied, decision);
    }

    [Fact]
    public void ServerGrant_AppliesOnlyToExactServer()
    {
        var grants = new[]
        {
            new ProductPermissionGrant(
                ProductPermissionCodes.ServerStart,
                ProductPermissionScope.ForServer(ServerA)),
        };

        Assert.Equal(
            ProductAuthorizationDecision.Granted,
            ProductAuthorization.Evaluate(grants, ProductPermissionCodes.ServerStart, ServerA));
        Assert.Equal(
            ProductAuthorizationDecision.Denied,
            ProductAuthorization.Evaluate(grants, ProductPermissionCodes.ServerStart, ServerB));
    }

    [Fact]
    public void GlobalServerGrant_AppliesToAllServers()
    {
        var grants = new[]
        {
            new ProductPermissionGrant(
                ProductPermissionCodes.ConsoleRead,
                ProductPermissionScope.Global),
        };

        Assert.Equal(
            ProductAuthorizationDecision.Granted,
            ProductAuthorization.Evaluate(grants, ProductPermissionCodes.ConsoleRead, ServerA));
        Assert.Equal(
            ProductAuthorizationDecision.Granted,
            ProductAuthorization.Evaluate(grants, ProductPermissionCodes.ConsoleRead, ServerB));
    }

    [Fact]
    public void GlobalOnlyPermission_RejectsServerScopedGrant()
    {
        var grant = new ProductPermissionGrant(
            ProductPermissionCodes.UserManage,
            ProductPermissionScope.ForServer(ServerA));

        Assert.False(ProductAuthorization.TryValidateGrant(grant));
        Assert.Equal(
            ProductAuthorizationDecision.InvalidGrant,
            ProductAuthorization.Evaluate([grant], ProductPermissionCodes.UserManage));
    }

    [Fact]
    public void ServerPermission_RequiresExplicitTarget()
    {
        var decision = ProductAuthorization.Evaluate(
            [new ProductPermissionGrant(ProductPermissionCodes.ServerRead, ProductPermissionScope.Global)],
            ProductPermissionCodes.ServerRead);

        Assert.Equal(ProductAuthorizationDecision.MissingServerScope, decision);
    }

    [Fact]
    public void UnknownPermission_IsNeverGranted()
    {
        var decision = ProductAuthorization.Evaluate(
            [new ProductPermissionGrant(ProductPermissionCodes.ServerRead, ProductPermissionScope.Global)],
            "server.execute-arbitrary-code",
            ServerA);

        Assert.Equal(ProductAuthorizationDecision.UnknownPermission, decision);
    }

    [Fact]
    public void NullScope_IsRejectedWithoutThrowing()
    {
        var grant = new ProductPermissionGrant(ProductPermissionCodes.ServerRead, null!);

        Assert.False(ProductAuthorization.TryValidateGrant(grant));
        Assert.Equal(
            ProductAuthorizationDecision.InvalidGrant,
            ProductAuthorization.Evaluate([grant], ProductPermissionCodes.ServerRead, ServerA));
    }

    [Fact]
    public void InvalidGrantAfterMatchingGrant_FailsClosedRegardlessOfOrder()
    {
        var matching = new ProductPermissionGrant(
            ProductPermissionCodes.ServerStart,
            ProductPermissionScope.ForServer(ServerA));
        var invalid = new ProductPermissionGrant(ProductPermissionCodes.ConsoleRead, null!);

        Assert.Equal(
            ProductAuthorizationDecision.InvalidGrant,
            ProductAuthorization.Evaluate([matching, invalid], ProductPermissionCodes.ServerStart, ServerA));
        Assert.Equal(
            ProductAuthorizationDecision.InvalidGrant,
            ProductAuthorization.Evaluate([invalid, matching], ProductPermissionCodes.ServerStart, ServerA));
    }

    [Fact]
    public void Catalog_HasUniqueCanonicalCodes()
    {
        Assert.NotEmpty(ProductPermissionCatalog.All);
        Assert.All(ProductPermissionCatalog.All, descriptor =>
        {
            Assert.Equal(descriptor.Code.ToLowerInvariant(), descriptor.Code);
            Assert.True(ProductPermissionCatalog.TryGet(descriptor.Code, out _));
        });
        Assert.Equal(
            ProductPermissionCatalog.All.Count,
            ProductPermissionCatalog.All.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
    }
}
