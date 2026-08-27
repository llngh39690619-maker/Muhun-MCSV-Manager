using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteWebIntentStoreTests
{
    [Fact]
    public void MissingIntent_DefaultsEnabledAndExplicitStopSurvivesNewStore()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var first = new ProductRemoteWebIntentStore(layout);

        Assert.True(first.ReadDesiredEnabled());

        first.WriteDesiredEnabled(false);
        var second = new ProductRemoteWebIntentStore(layout);

        Assert.False(second.ReadDesiredEnabled());
        Assert.Equal(
            "{\"schemaVersion\":1,\"desiredEnabled\":false}",
            File.ReadAllText(Path.Combine(layout.Operations, ProductRemoteWebIntentStore.FileName)));
    }

    [Fact]
    public void CorruptOrFutureIntent_FailsClosedInsteadOfResettingToEnabled()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        var path = Path.Combine(layout.Operations, ProductRemoteWebIntentStore.FileName);
        File.WriteAllText(path, "{\"schemaVersion\":2,\"desiredEnabled\":true}");

        var store = new ProductRemoteWebIntentStore(layout);

        Assert.Throws<InvalidDataException>(() => store.ReadDesiredEnabled());
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(File.ReadAllText(path))
            .RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void UnknownIntentMember_IsRejected()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        layout.EnsureCreated();
        File.WriteAllText(
            Path.Combine(layout.Operations, ProductRemoteWebIntentStore.FileName),
            "{\"schemaVersion\":1,\"desiredEnabled\":true,\"command\":\"reset\"}");

        Assert.Throws<InvalidDataException>(
            () => new ProductRemoteWebIntentStore(layout).ReadDesiredEnabled());
    }
}
