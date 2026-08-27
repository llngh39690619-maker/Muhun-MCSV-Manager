using System.Reflection;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ServerProcessManagerCatalogArtworkSnapshotTests
{
    [Fact]
    public void ProcessSnapshot_PreservesCatalogArtworkWithoutReplacingUserIcon()
    {
        var source = new ServerInstance
        {
            IconImagePath = "themes/icons/user.png",
            CatalogIconImagePath = "cache/modpack-artwork/icons/catalog.png",
            CatalogPreviewImagePath = "cache/modpack-artwork/previews/catalog.png",
            ModpackProviderId = "modrinth",
        };
        var method = typeof(ServerProcessManager).GetMethod(
            "SnapshotInstance",
            BindingFlags.Static | BindingFlags.NonPublic);

        var snapshot = Assert.IsType<ServerInstance>(method?.Invoke(null, [source]));

        Assert.Equal(source.IconImagePath, snapshot.IconImagePath);
        Assert.Equal(source.CatalogIconImagePath, snapshot.CatalogIconImagePath);
        Assert.Equal(source.CatalogPreviewImagePath, snapshot.CatalogPreviewImagePath);
        Assert.Equal(source.ModpackProviderId, snapshot.ModpackProviderId);
    }
}
