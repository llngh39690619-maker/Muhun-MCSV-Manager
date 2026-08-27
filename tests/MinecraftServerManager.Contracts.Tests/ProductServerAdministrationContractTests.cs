using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductServerAdministrationContractTests
{
    [Fact]
    public void RemoteEligibleSnapshot_IsPathFreeAndHasExplicitBounds()
    {
        var propertyNames = typeof(ProductServerAdministrationSnapshot)
            .GetProperties()
            .Concat(typeof(ProductServerAddonSummary).GetProperties())
            .Concat(typeof(ProductServerJavaRuntimeSummary).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Directory", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(ProductServerAdministrationContract.MaximumListedAddons, 1, 200);
        Assert.InRange(
            ProductServerAdministrationContract.MaximumScannedEntries,
            ProductServerAdministrationContract.MaximumListedAddons,
            4096);
        Assert.InRange(ProductServerAdministrationContract.MaximumAddonFileNameCharacters, 32, 200);
        Assert.InRange(ProductServerAdministrationContract.MaximumJavaMetadataCharacters, 16, 128);
        Assert.InRange(ProductServerAdministrationContract.MaximumJavaReleaseFileBytes, 1024, 64 * 1024);
    }
}
