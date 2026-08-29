namespace MinecraftServerManager.App.ViewModels;

public sealed record ClientResolutionChoice(int Width, int Height)
{
    public string DisplayName => $"{Width} × {Height}";
}

internal static class ClientResolutionCatalog
{
    private static readonly ClientResolutionChoice[] CommonChoices =
    [
        new(854, 480),
        new(1280, 720),
        new(1366, 768),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440),
        new(3840, 2160),
    ];

    public static IReadOnlyList<ClientResolutionChoice> CreateChoices(
        int currentWidth,
        int currentHeight)
    {
        if (!IsValid(currentWidth, currentHeight) ||
            CommonChoices.Any(choice =>
                choice.Width == currentWidth && choice.Height == currentHeight))
        {
            return CommonChoices;
        }

        return CommonChoices
            .Append(new ClientResolutionChoice(currentWidth, currentHeight))
            .OrderBy(choice => (long)choice.Width * choice.Height)
            .ThenBy(choice => choice.Width)
            .ThenBy(choice => choice.Height)
            .ToArray();
    }

    public static ClientResolutionChoice? Find(
        IReadOnlyList<ClientResolutionChoice> choices,
        int width,
        int height) => choices.FirstOrDefault(choice =>
            choice.Width == width && choice.Height == height);

    public static bool IsValid(int width, int height) =>
        width is >= 640 and <= 16_384 && height is >= 360 and <= 16_384;
}
