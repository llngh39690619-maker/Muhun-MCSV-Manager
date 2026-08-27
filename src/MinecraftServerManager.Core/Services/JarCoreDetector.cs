using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Identifies a server core using filenames and bounded reads of ZIP metadata.
/// The detector never executes the JAR or loads classes from it.
/// </summary>
public sealed partial class JarCoreDetector : ICoreDetector
{
    private const int MaximumEntries = 100_000;
    private const long MaximumMetadataLength = 1024 * 1024;

    public DetectionResult Detect(string jarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);
        var fullPath = Path.GetFullPath(jarPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The server JAR was not found.", fullPath);
        }

        var scores = new Dictionary<CoreType, int>();
        var evidence = new List<string>();
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        string? minecraftVersion = ExtractMinecraftVersionFromFileName(fileName);
        string? mainClass = null;

        ScoreFileName(fileName, scores, evidence);

        try
        {
            using var file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

            ZipArchiveEntry? manifestEntry = null;
            ZipArchiveEntry? versionEntry = null;
            var entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                if (++entryCount > MaximumEntries)
                {
                    return BuildResult(
                        fullPath,
                        scores,
                        evidence,
                        minecraftVersion,
                        mainClass,
                        $"JAR contains more than the safe limit of {MaximumEntries:N0} entries.");
                }

                var entryName = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                ScoreEntry(entryName, scores, evidence);

                if (entryName.Equals("meta-inf/manifest.mf", StringComparison.Ordinal))
                {
                    manifestEntry = entry;
                }
                else if (entryName.Equals("version.json", StringComparison.Ordinal))
                {
                    versionEntry = entry;
                }
            }

            if (manifestEntry is not null)
            {
                if (TryReadBoundedText(manifestEntry, out var manifest, out var manifestError))
                {
                    var attributes = ParseManifest(manifest);
                    if (attributes.TryGetValue("Main-Class", out var detectedMainClass))
                    {
                        mainClass = detectedMainClass.Trim();
                    }

                    ScoreManifest(attributes, manifest, scores, evidence);
                    minecraftVersion ??= ExtractVersionFromManifest(attributes);
                }
                else
                {
                    evidence.Add(manifestError!);
                }
            }

            if (minecraftVersion is null && versionEntry is not null &&
                TryReadBoundedText(versionEntry, out var versionJson, out _))
            {
                minecraftVersion = ExtractVersionFromVersionJson(versionJson);
            }

            return BuildResult(
                fullPath,
                scores,
                evidence,
                minecraftVersion,
                mainClass,
                error: null);
        }
        catch (InvalidDataException exception)
        {
            return BuildResult(
                fullPath,
                scores,
                evidence,
                minecraftVersion,
                mainClass,
                $"The file is not a valid ZIP/JAR archive: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return BuildResult(
                fullPath,
                scores,
                evidence,
                minecraftVersion,
                mainClass,
                $"The JAR uses an unsupported archive feature: {exception.Message}");
        }
    }

    public Task<DetectionResult> DetectAsync(
        string jarPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Detect(jarPath);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    private static void ScoreFileName(
        string fileName,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        var normalized = fileName.ToLowerInvariant();

        if (normalized.Contains("purpur", StringComparison.Ordinal))
        {
            AddScore(CoreType.Purpur, 65, "Filename contains 'purpur'.", scores, evidence);
        }
        else if (normalized.Contains("folia", StringComparison.Ordinal))
        {
            AddScore(CoreType.Folia, 65, "Filename contains 'folia'.", scores, evidence);
        }
        else if (normalized.Contains("paper", StringComparison.Ordinal))
        {
            AddScore(CoreType.Paper, 60, "Filename contains 'paper'.", scores, evidence);
        }

        if (normalized.Contains("neoforge", StringComparison.Ordinal))
        {
            AddScore(CoreType.NeoForge, 65, "Filename contains 'neoforge'.", scores, evidence);
        }
        else if (normalized.Contains("forge", StringComparison.Ordinal))
        {
            AddScore(CoreType.Forge, 60, "Filename contains 'forge'.", scores, evidence);
        }

        AddFileNameToken(CoreType.Fabric, "fabric", 60, normalized, scores, evidence);
        AddFileNameToken(CoreType.Velocity, "velocity", 65, normalized, scores, evidence);
        AddFileNameToken(CoreType.Waterfall, "waterfall", 65, normalized, scores, evidence);
        AddFileNameToken(CoreType.BungeeCord, "bungeecord", 65, normalized, scores, evidence);
        AddFileNameToken(CoreType.CraftBukkit, "craftbukkit", 60, normalized, scores, evidence);
        AddFileNameToken(CoreType.Spigot, "spigot", 60, normalized, scores, evidence);
        AddFileNameToken(CoreType.Mohist, "mohist", 55, normalized, scores, evidence);
        AddFileNameToken(CoreType.Arclight, "arclight", 55, normalized, scores, evidence);
        AddFileNameToken(CoreType.CatServer, "catserver", 55, normalized, scores, evidence);
        AddFileNameToken(CoreType.Akarin, "akarin", 55, normalized, scores, evidence);

        if (normalized.Equals("server", StringComparison.Ordinal) ||
            normalized.StartsWith("minecraft_server", StringComparison.Ordinal) ||
            normalized.StartsWith("minecraft-server", StringComparison.Ordinal) ||
            normalized.StartsWith("vanilla", StringComparison.Ordinal))
        {
            AddScore(CoreType.Vanilla, 35, "Filename resembles a Vanilla server JAR.", scores, evidence);
        }
    }

    private static void ScoreEntry(
        string entryName,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        if (entryName.StartsWith("com/mohistmc/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Mohist, 100, "Contains Mohist project classes.", scores, evidence);
        }

        if (entryName.StartsWith("io/izzel/arclight/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Arclight, 100, "Contains Arclight project classes.", scores, evidence);
        }

        if (entryName.StartsWith("catserver/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.CatServer, 100, "Contains CatServer project classes.", scores, evidence);
        }

        // The official CatServer 1.18.2 launcher is a small FoxLaunch wrapper. Neither marker is
        // accepted alone; together they cross the online high-confidence threshold.
        if (entryName.Equals("foxlaunch/foxserverlauncher.class", StringComparison.Ordinal))
        {
            AddScore(CoreType.CatServer, 45, "Contains CatServer's FoxLaunch entry point.", scores, evidence);
        }

        if (entryName.Equals("data/server.lzma", StringComparison.Ordinal))
        {
            AddScore(CoreType.CatServer, 40, "Contains CatServer's embedded server payload.", scores, evidence);
        }

        if (entryName.StartsWith("io/akarin/", StringComparison.Ordinal)
            || entryName.StartsWith(
                "meta-inf/maven/com.destroystokyo.paper/akarin/",
                StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Akarin, 100, "Contains Akarin project classes/metadata.", scores, evidence);
        }

        if (entryName.Contains("org/purpurmc/purpur/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Purpur, 85, "Contains Purpur classes.", scores, evidence);
        }
        else if (entryName.Contains("io/papermc/paper/threadedregions/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Folia, 85, "Contains Folia threaded-region classes.", scores, evidence);
        }
        else if (entryName.Contains("io/papermc/paperclip/", StringComparison.Ordinal) ||
                 entryName.Contains("io/papermc/paper/", StringComparison.Ordinal) ||
                 entryName.Contains("com/destroystokyo/paper/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Paper, 80, "Contains Paper classes.", scores, evidence);
        }

        if (entryName.Equals("fabric-server-launch.properties", StringComparison.Ordinal) ||
            entryName.Contains("net/fabricmc/loader/impl/launch/server/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Fabric, 90, "Contains Fabric server launcher metadata.", scores, evidence);
        }

        if (entryName.Contains("net/neoforged/", StringComparison.Ordinal) ||
            entryName.Equals("meta-inf/neoforge.mods.toml", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.NeoForge, 90, "Contains NeoForge metadata or classes.", scores, evidence);
        }
        else if (entryName.Contains("net/minecraftforge/", StringComparison.Ordinal) ||
                 entryName.Equals("meta-inf/mods.toml", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Forge, 85, "Contains Forge metadata or classes.", scores, evidence);
        }

        if (entryName.Contains("com/velocitypowered/proxy/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Velocity, 90, "Contains Velocity proxy classes.", scores, evidence);
        }

        if (entryName.Contains("io/github/waterfallmc/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Waterfall, 85, "Contains Waterfall proxy classes.", scores, evidence);
        }

        if (entryName.Contains("net/md_5/bungee/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.BungeeCord, 70, "Contains BungeeCord proxy classes.", scores, evidence);
        }

        if (entryName.Contains("org/spigotmc/", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Spigot, 80, "Contains Spigot classes.", scores, evidence);
        }

        if (entryName.Equals("org/bukkit/craftbukkit/main.class", StringComparison.Ordinal))
        {
            AddScoreOnce(
                CoreType.CraftBukkit,
                80,
                "Contains the CraftBukkit server entry point.",
                scores,
                evidence);
        }

        if (entryName.Equals("net/minecraft/server/main.class", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Vanilla, 35, "Contains the Vanilla server main class.", scores, evidence);
        }
        else if (entryName.Equals("net/minecraft/bundler/main.class", StringComparison.Ordinal))
        {
            AddScoreOnce(CoreType.Vanilla, 55, "Contains the Mojang server bundler main class.", scores, evidence);
        }
    }

    private static void ScoreManifest(
        IReadOnlyDictionary<string, string> attributes,
        string manifest,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        attributes.TryGetValue("Main-Class", out var mainClass);
        var searchable = $"{mainClass}\n{manifest}".ToLowerInvariant();

        if (string.Equals(mainClass, "com.mohistmc.MohistMCStart", StringComparison.Ordinal))
        {
            AddScore(CoreType.Mohist, 100, "Manifest uses the Mohist entry point.", scores, evidence);
        }

        if (string.Equals(mainClass, "io.izzel.arclight.server.Launcher", StringComparison.Ordinal))
        {
            AddScore(CoreType.Arclight, 100, "Manifest uses the Arclight entry point.", scores, evidence);
        }

        if (string.Equals(mainClass, "catserver.server.CatServerLaunch", StringComparison.Ordinal))
        {
            AddScore(CoreType.CatServer, 100, "Manifest uses the CatServer entry point.", scores, evidence);
        }
        else if (string.Equals(mainClass, "foxlaunch.FoxServerLauncher", StringComparison.Ordinal))
        {
            AddScore(CoreType.CatServer, 15, "Manifest uses CatServer's FoxLaunch entry point.", scores, evidence);
        }

        if (string.Equals(mainClass, "org.bukkit.craftbukkit.Main", StringComparison.Ordinal))
        {
            AddScore(
                CoreType.CraftBukkit,
                20,
                "Manifest uses the CraftBukkit server entry point.",
                scores,
                evidence);
            AddScore(
                CoreType.Spigot,
                20,
                "Spigot uses the CraftBukkit server entry point.",
                scores,
                evidence);
        }

        if (searchable.Contains("purpur", StringComparison.Ordinal))
        {
            AddScore(CoreType.Purpur, 80, "Manifest identifies Purpur.", scores, evidence);
        }
        else if (searchable.Contains("folia", StringComparison.Ordinal))
        {
            AddScore(CoreType.Folia, 80, "Manifest identifies Folia.", scores, evidence);
        }
        else if (searchable.Contains("paperclip", StringComparison.Ordinal) ||
                 searchable.Contains("papermc", StringComparison.Ordinal))
        {
            AddScore(CoreType.Paper, 80, "Manifest identifies Paper/Paperclip.", scores, evidence);
        }

        if (searchable.Contains("neoforge", StringComparison.Ordinal))
        {
            AddScore(CoreType.NeoForge, 85, "Manifest identifies NeoForge.", scores, evidence);
        }
        else if (searchable.Contains("minecraftforge", StringComparison.Ordinal))
        {
            AddScore(CoreType.Forge, 80, "Manifest identifies Forge.", scores, evidence);
        }

        AddManifestToken(CoreType.Fabric, "fabricmc", 85, searchable, scores, evidence);
        AddManifestToken(CoreType.Velocity, "velocitypowered", 90, searchable, scores, evidence);
        AddManifestToken(CoreType.Waterfall, "waterfall", 85, searchable, scores, evidence);
        AddManifestToken(CoreType.BungeeCord, "bungeecord", 80, searchable, scores, evidence);

        if (string.Equals(mainClass, "net.minecraft.server.Main", StringComparison.Ordinal)
            || string.Equals(mainClass, "net.minecraft.bundler.Main", StringComparison.Ordinal))
        {
            AddScore(CoreType.Vanilla, 45, "Manifest uses an official Mojang server main class.", scores, evidence);
        }
    }

    private static DetectionResult BuildResult(
        string fullPath,
        IReadOnlyDictionary<CoreType, int> scores,
        IReadOnlyCollection<string> evidence,
        string? minecraftVersion,
        string? mainClass,
        string? error)
    {
        var winner = scores.Count == 0
            ? new KeyValuePair<CoreType, int>(CoreType.Unknown, 0)
            : scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();

        return new DetectionResult
        {
            FilePath = fullPath,
            CoreType = winner.Key,
            MinecraftVersion = minecraftVersion,
            MainClass = mainClass,
            ConfidencePercent = Math.Clamp(winner.Value, 0, 100),
            Evidence = evidence.ToArray(),
            Error = error
        };
    }

    private static void AddFileNameToken(
        CoreType coreType,
        string token,
        int score,
        string fileName,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        if (fileName.Contains(token, StringComparison.Ordinal))
        {
            AddScore(coreType, score, $"Filename contains '{token}'.", scores, evidence);
        }
    }

    private static void AddManifestToken(
        CoreType coreType,
        string token,
        int score,
        string searchableManifest,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        if (searchableManifest.Contains(token, StringComparison.Ordinal))
        {
            AddScore(coreType, score, $"Manifest identifies {coreType}.", scores, evidence);
        }
    }

    private static void AddScoreOnce(
        CoreType coreType,
        int score,
        string reason,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        if (scores.TryGetValue(coreType, out var existing) && existing >= score)
        {
            return;
        }

        scores[coreType] = Math.Max(existing, score);
        evidence.Add(reason);
    }

    private static void AddScore(
        CoreType coreType,
        int score,
        string reason,
        IDictionary<CoreType, int> scores,
        ICollection<string> evidence)
    {
        scores.TryGetValue(coreType, out var current);
        scores[coreType] = Math.Min(100, current + score);
        evidence.Add(reason);
    }

    private static bool TryReadBoundedText(
        ZipArchiveEntry entry,
        out string text,
        out string? error)
    {
        if (entry.Length > MaximumMetadataLength)
        {
            text = string.Empty;
            error = $"Skipped oversized metadata entry '{entry.FullName}'.";
            return false;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        text = reader.ReadToEnd();
        error = null;
        return true;
    }

    private static Dictionary<string, string> ParseManifest(string manifest)
    {
        var unfolded = new List<string>();
        using var reader = new StringReader(manifest);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith(' ') && unfolded.Count > 0)
            {
                unfolded[^1] += line[1..];
            }
            else
            {
                unfolded.Add(line);
            }
        }

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unfoldedLine in unfolded)
        {
            var separator = unfoldedLine.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            attributes[unfoldedLine[..separator].Trim()] =
                unfoldedLine[(separator + 1)..].TrimStart();
        }

        return attributes;
    }

    private static string? ExtractVersionFromManifest(IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var key in new[]
                 {
                     "Minecraft-Version", "MinecraftVersion", "Game-Version",
                     "Implementation-Version", "Specification-Version", "Bundle-Version"
                 })
        {
            if (attributes.TryGetValue(key, out var value))
            {
                var detected = ExtractMinecraftVersion(value);
                if (detected is not null)
                {
                    return detected;
                }
            }
        }

        return null;
    }

    private static string? ExtractVersionFromVersionJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var propertyName in new[] { "id", "name", "minecraftVersion" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.String)
                {
                    var version = ExtractMinecraftVersion(property.GetString());
                    if (version is not null)
                    {
                        return version;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed optional version.json must not invalidate an otherwise
            // structurally valid server JAR.
        }

        return null;
    }

    private static string? ExtractMinecraftVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MinecraftVersionRegex().Match(text);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string? ExtractMinecraftVersionFromFileName(string fileName)
    {
        // NeoForge release numbers use <minecraft minor>.<minecraft patch>.<build>,
        // for example 21.1.200 targets Minecraft 1.21.1.
        var neoForgeMatch = NeoForgeVersionRegex().Match(fileName);
        if (neoForgeMatch.Success)
        {
            return $"1.{neoForgeMatch.Groups["minor"].Value}.{neoForgeMatch.Groups["patch"].Value}";
        }

        return ExtractMinecraftVersion(fileName);
    }

    [GeneratedRegex(
        @"(?<!\d)(?<version>1\.\d{1,2}(?:\.\d{1,2})?|2\d\.\d{1,2}(?:\.\d{1,2})?)(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftVersionRegex();

    [GeneratedRegex(
        @"neoforge[-_](?<minor>\d{2})\.(?<patch>\d{1,2})\.\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeoForgeVersionRegex();
}
