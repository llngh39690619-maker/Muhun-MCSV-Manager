using System.Globalization;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>Core-aware Java recommendations with an explicit conservative fallback.</summary>
public sealed class JavaVersionRecommendationService
{
    public static IReadOnlyList<int> SupportedMajorVersions { get; } =
        Array.AsReadOnly([8, 11, 16, 17, 21, 25]);

    public JavaVersionRecommendation GetRecommendation(
        string? minecraftVersion,
        CoreType coreType = CoreType.Unknown,
        int? overrideMajorVersion = null)
    {
        if (overrideMajorVersion is not null)
        {
            if (overrideMajorVersion < 8)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overrideMajorVersion),
                    "An overridden Java major version must be 8 or newer.");
            }

            return new JavaVersionRecommendation
            {
                MajorVersion = overrideMajorVersion.Value,
                IsOverride = true,
                RequiresUserConfirmation = false,
                Reason = $"Java {overrideMajorVersion.Value} was explicitly selected for this instance."
            };
        }

        if (!TryParseMinecraftVersion(minecraftVersion, out var version))
        {
            return new JavaVersionRecommendation
            {
                MajorVersion = 17,
                RequiresUserConfirmation = true,
                Reason = "Minecraft version is unknown; Java 17 is a conservative fallback and must be confirmed."
            };
        }

        var isUnknownCore = coreType is CoreType.Unknown or CoreType.CustomJar;
        var major = IsPaperFamily(coreType)
            ? RecommendForPaperFamily(version)
            : RecommendForMinecraft(version);
        var futureOrAmbiguous = version.Major is > 1 and < 26 ||
                                version.Major > 26 ||
                                version is { Major: 1, Minor: > 21 };
        var confirmationRequired = isUnknownCore || futureOrAmbiguous;

        return new JavaVersionRecommendation
        {
            MajorVersion = major,
            RequiresUserConfirmation = confirmationRequired,
            Reason = BuildReason(coreType, version, major, confirmationRequired)
        };
    }

    private static int RecommendForPaperFamily(GameVersion version)
    {
        if (version.Major >= 26)
        {
            return 25;
        }

        if (version.Major > 1)
        {
            return 21;
        }

        return version.Minor switch
        {
            <= 11 => 8,
            <= 15 => 11,
            16 when version.Patch <= 4 => 11,
            16 => 16,
            <= 19 => 17,
            _ => 21
        };
    }

    private static int RecommendForMinecraft(GameVersion version)
    {
        if (version.Major >= 26)
        {
            return 25;
        }

        if (version.Major > 1)
        {
            return 21;
        }

        if (version.Minor <= 16)
        {
            return 8;
        }

        if (version.Minor == 17)
        {
            return 16;
        }

        if (version.Minor <= 19)
        {
            return 17;
        }

        if (version.Minor == 20 && version.Patch <= 4)
        {
            return 17;
        }

        return 21;
    }

    private static bool IsPaperFamily(CoreType coreType) =>
        coreType is CoreType.Paper or CoreType.Purpur or CoreType.Folia or
            CoreType.Spigot or CoreType.CraftBukkit;

    private static string BuildReason(
        CoreType coreType,
        GameVersion version,
        int javaMajor,
        bool confirmationRequired)
    {
        var coreDescription = coreType == CoreType.Unknown ? "an unknown core" : coreType.ToString();
        var suffix = confirmationRequired
            ? " The core or game version is not fully known, so confirmation is required."
            : string.Empty;
        return $"Java {javaMajor} is recommended for {coreDescription} on Minecraft {version}.{suffix}";
    }

    private static bool TryParseMinecraftVersion(string? value, out GameVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        var separator = normalized.IndexOfAny(['-', '+', ' ']);
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            (parts.Length > 2 &&
             !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var patch = parts.Length > 2
            ? int.Parse(parts[2], CultureInfo.InvariantCulture)
            : 0;
        version = new GameVersion(major, minor, patch);
        return true;
    }

    private readonly record struct GameVersion(int Major, int Minor, int Patch)
    {
        public override string ToString() => Patch == 0
            ? $"{Major}.{Minor}"
            : $"{Major}.{Minor}.{Patch}";
    }
}
