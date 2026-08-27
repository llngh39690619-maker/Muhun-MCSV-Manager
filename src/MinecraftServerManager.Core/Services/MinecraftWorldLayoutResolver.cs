using System.Text;

namespace MinecraftServerManager.Core.Services;

public sealed record MinecraftWorldLayout(
    string LevelName,
    string WorldContainerRelativePath,
    IReadOnlyList<string> RelativeWorldDirectories);

/// <summary>
/// Resolves the world directories that must be preserved by a data-only update backup. Metadata
/// reads are bounded and every returned path is confined to the server root without traversing a
/// reparse point. External Bukkit world containers are rejected rather than silently omitted.
/// </summary>
public sealed class MinecraftWorldLayoutResolver
{
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumConfiguredPathLength = 240;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, true, true);

    public async Task<MinecraftWorldLayout> ResolveAsync(
        string serverDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"找不到 Server 資料夾：{root}");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(root, root);
        var propertiesPath = Path.Combine(root, "server.properties");
        var levelName = "world";
        if (File.Exists(propertiesPath))
        {
            var properties = await ReadBoundedTextAsync(
                    root,
                    propertiesPath,
                    allowLatin1Fallback: true,
                    cancellationToken)
                .ConfigureAwait(false);
            levelName = ReadJavaProperty(properties, "level-name")?.Trim() is { Length: > 0 } configured
                ? configured
                : "world";
        }

        var bukkitPath = Path.Combine(root, "bukkit.yml");
        string? configuredContainer = null;
        if (File.Exists(bukkitPath))
        {
            var bukkit = await ReadBoundedTextAsync(
                    root,
                    bukkitPath,
                    allowLatin1Fallback: false,
                    cancellationToken)
                .ConfigureAwait(false);
            configuredContainer = ReadBukkitWorldContainer(bukkit);
        }

        var container = ResolveConfiguredPath(
            root,
            string.IsNullOrWhiteSpace(configuredContainer) || configuredContainer is "." or "~" or "null"
                ? "."
                : configuredContainer,
            allowRoot: true,
            "Bukkit world-container");
        var primaryWorld = ResolveConfiguredPath(
            root,
            Path.IsPathFullyQualified(levelName)
                ? levelName
                : Path.Combine(container, levelName),
            allowRoot: false,
            "server.properties level-name");
        var worldCandidates = new[]
        {
            primaryWorld,
            primaryWorld + "_nether",
            primaryWorld + "_the_end"
        };

        foreach (var candidate in worldCandidates)
        {
            EnsureExistingPathHasNoReparsePoints(root, candidate);
            if (File.Exists(candidate))
            {
                throw new InvalidDataException($"世界路徑不是資料夾：{candidate}");
            }
        }

        var containerRelative = Path.GetRelativePath(root, container);
        return new MinecraftWorldLayout(
            levelName,
            containerRelative == "." ? "." : NormalizeRelativePath(containerRelative),
            worldCandidates
                .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static async Task<string> ReadBoundedTextAsync(
        string root,
        string path,
        bool allowLatin1Fallback,
        CancellationToken cancellationToken)
    {
        SafePath.EnsureNoReparsePointsUnderRoot(root, path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException(
                $"世界設定檔超過 {MaximumMetadataBytes / 1024:N0} KiB 安全上限：{Path.GetFileName(path)}");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"世界設定檔在讀取期間發生變更：{Path.GetFileName(path)}");
        }

        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                return StrictUtf8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
            }

            if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            {
                return StrictUtf16LittleEndian.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length));
            }

            if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            {
                return StrictUtf16BigEndian.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length));
            }

            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException) when (allowLatin1Fallback)
        {
            return Encoding.Latin1.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"世界設定檔不是有效 UTF-8／UTF-16：{Path.GetFileName(path)}",
                exception);
        }
    }

    private static string? ReadJavaProperty(string contents, string requestedKey)
    {
        string? result = null;
        using var reader = new StringReader(contents);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var start = 0;
            while (start < line.Length && line[start] is ' ' or '\t' or '\f')
            {
                start++;
            }

            if (start >= line.Length || line[start] is '#' or '!')
            {
                continue;
            }

            var separator = -1;
            var escaped = false;
            for (var index = start; index < line.Length; index++)
            {
                var character = line[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character is '=' or ':' or ' ' or '\t' or '\f')
                {
                    separator = index;
                    break;
                }
            }

            var rawKey = separator < 0 ? line[start..] : line[start..separator];
            if (!DecodeJavaPropertyToken(rawKey).Equals(requestedKey, StringComparison.Ordinal))
            {
                continue;
            }

            var valueStart = separator < 0 ? line.Length : separator;
            while (valueStart < line.Length && line[valueStart] is ' ' or '\t' or '\f')
            {
                valueStart++;
            }

            if (valueStart < line.Length && line[valueStart] is '=' or ':')
            {
                valueStart++;
            }

            while (valueStart < line.Length && line[valueStart] is ' ' or '\t' or '\f')
            {
                valueStart++;
            }

            var rawValue = line[valueStart..];
            if (EndsWithUnescapedBackslash(rawValue))
            {
                throw new InvalidDataException(
                    $"{requestedKey} 不得使用跨行 continuation，請改成單行相對路徑。");
            }

            result = DecodeJavaPropertyToken(rawValue);
        }

        return result;
    }

    private static string DecodeJavaPropertyToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (++index >= value.Length)
            {
                builder.Append('\\');
                break;
            }

            character = value[index];
            builder.Append(character switch
            {
                't' => '\t',
                'r' => '\r',
                'n' => '\n',
                'f' => '\f',
                'u' => ReadUnicodeEscape(value, ref index),
                _ => character
            });
        }

        return builder.ToString();
    }

    private static char ReadUnicodeEscape(string value, ref int index)
    {
        if (index + 4 >= value.Length)
        {
            throw new InvalidDataException("Java property 含有不完整的 Unicode escape。");
        }

        var span = value.AsSpan(index + 1, 4);
        if (!ushort.TryParse(
                span,
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out var codePoint))
        {
            throw new InvalidDataException("Java property 含有無效的 Unicode escape。");
        }

        index += 4;
        return (char)codePoint;
    }

    private static bool EndsWithUnescapedBackslash(string value)
    {
        var count = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '\\'; index--)
        {
            count++;
        }

        return count % 2 == 1;
    }

    private static string? ReadBukkitWorldContainer(string contents)
    {
        string? result = null;
        using var reader = new StringReader(contents);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            const string key = "world-container";
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || trimmed.Length <= key.Length
                || trimmed[key.Length] != ':')
            {
                continue;
            }

            var value = ReadYamlScalar(trimmed[(key.Length + 1)..]);
            if (result is not null && !result.Equals(value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("bukkit.yml 含有多個互相衝突的 world-container。");
            }

            result = value;
        }

        return result;
    }

    private static string ReadYamlScalar(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] is '\'' or '"')
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            if (end < 0)
            {
                throw new InvalidDataException("bukkit.yml 的 world-container 引號未閉合。");
            }

            if (value[(end + 1)..].TrimStart() is { Length: > 0 } tail && tail[0] != '#')
            {
                throw new InvalidDataException("bukkit.yml 的 world-container 後方含有未核准內容。");
            }

            return value[1..end];
        }

        var comment = value.IndexOf('#');
        return (comment < 0 ? value : value[..comment]).TrimEnd();
    }

    private static string ResolveConfiguredPath(
        string root,
        string configuredPath,
        bool allowRoot,
        string description)
    {
        configuredPath = configuredPath.Trim();
        if (configuredPath.Length == 0 || configuredPath.Length > MaximumConfiguredPathLength)
        {
            throw new InvalidDataException($"{description} 長度無效。");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(
                Path.IsPathFullyQualified(configuredPath)
                    ? configuredPath
                    : Path.Combine(root, configuredPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{description} 不是有效路徑。", exception);
        }

        if (!SafePath.IsWithinRoot(root, candidate)
            || (!allowRoot && PathsEqual(root, candidate)))
        {
            throw new InvalidDataException(
                $"{description} 指向 Server 根目錄外部；為避免漏掉地圖資料，已拒絕更新前備份：{configuredPath}");
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (relative != ".")
        {
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.None);
            if (segments.Any(static segment =>
                    segment.Length == 0
                    || segment is "." or ".."
                    || segment.EndsWith(' ')
                    || segment.EndsWith('.')))
            {
                throw new InvalidDataException($"{description} 含有不安全的路徑片段。");
            }
        }

        EnsureExistingPathHasNoReparsePoints(root, candidate);
        return Path.TrimEndingDirectorySeparator(candidate);
    }

    private static void EnsureExistingPathHasNoReparsePoints(string root, string candidate)
    {
        var safeCandidate = SafePath.EnsureWithinRoot(root, candidate);
        var relative = Path.GetRelativePath(root, safeCandidate);
        var current = root;
        RejectReparsePoint(current);
        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                RejectReparsePoint(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }

        static void RejectReparsePoint(string path)
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"世界路徑包含 symbolic link、junction 或其他 reparse point：{path}");
            }
        }
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
