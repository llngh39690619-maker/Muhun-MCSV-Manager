using MinecraftServerManager.Core.Models;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientSettingsContractTests
{
    [Fact]
    public void ManagerSettings_ExposeAutomaticNewClientDefaults()
    {
        var settings = new ManagerSettings();

        Assert.Equal(13, ManagerSettings.CurrentSchemaVersion);
        Assert.Equal(MinecraftClientMemoryMode.Automatic, settings.NewClientDefaults.MemoryMode);
        Assert.Equal(2048, settings.NewClientDefaults.MinimumMemoryMb);
        Assert.Equal(4096, settings.NewClientDefaults.MaximumMemoryMb);
    }

    [Fact]
    public void ClientInstance_DefaultsToGlobalMemoryAndVanillaJava()
    {
        var instance = new MinecraftClientInstance();

        Assert.Equal(MinecraftClientEdition.Java, instance.Edition);
        Assert.Equal(MinecraftClientLoader.Vanilla, instance.Loader);
        Assert.Equal(MinecraftClientMemoryMode.UseGlobalDefault, instance.MemoryMode);
    }
}
