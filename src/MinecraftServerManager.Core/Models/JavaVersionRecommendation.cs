namespace MinecraftServerManager.Core.Models;

public sealed record JavaVersionRecommendation
{
    public required int MajorVersion { get; init; }

    public required string Reason { get; init; }

    public bool IsOverride { get; init; }

    public bool RequiresUserConfirmation { get; init; }
}
