namespace MinecraftServerManager.Core.Models;

/// <summary>
/// Desktop-only visual metadata keyed by a Service server id. These fields never cross the local
/// Service IPC boundary and never grant access to a Service-owned server directory.
/// </summary>
public sealed class ServerAppearancePreference
{
    public string? BackgroundImagePath { get; set; }

    public double BackgroundImageOpacity { get; set; } = 0.25;

    public string? IconImagePath { get; set; }

    public string? CatalogIconImagePath { get; set; }

    public string? CatalogPreviewImagePath { get; set; }

    public ServerAppearancePreference Copy() => new()
    {
        BackgroundImagePath = BackgroundImagePath,
        BackgroundImageOpacity = BackgroundImageOpacity,
        IconImagePath = IconImagePath,
        CatalogIconImagePath = CatalogIconImagePath,
        CatalogPreviewImagePath = CatalogPreviewImagePath,
    };

    public static ServerAppearancePreference From(ServerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new ServerAppearancePreference
        {
            BackgroundImagePath = instance.BackgroundImagePath,
            BackgroundImageOpacity = instance.BackgroundImageOpacity,
            IconImagePath = instance.IconImagePath,
            CatalogIconImagePath = instance.CatalogIconImagePath,
            CatalogPreviewImagePath = instance.CatalogPreviewImagePath,
        };
    }

    public void ApplyTo(ServerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        instance.BackgroundImagePath = BackgroundImagePath;
        instance.BackgroundImageOpacity = BackgroundImageOpacity;
        instance.IconImagePath = IconImagePath;
        instance.CatalogIconImagePath = CatalogIconImagePath;
        instance.CatalogPreviewImagePath = CatalogPreviewImagePath;
    }
}
