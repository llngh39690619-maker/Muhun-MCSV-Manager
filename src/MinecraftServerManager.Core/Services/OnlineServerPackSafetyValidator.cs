using System.Text;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Applies the additional argument-file restrictions required for server packs obtained online.
/// The general local-import detector intentionally remains more permissive, but an online pack
/// must not turn an apparently standard Forge/NeoForge layout into a wrapper/agent execution path.
/// </summary>
public static class OnlineServerPackSafetyValidator
{
    private const int MaximumArgumentFileBytes = 2 * 1024 * 1024;
    private const int MaximumTokensPerFile = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task ValidateAsync(
        ServerPackDetectionResult detection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detection);
        if (!detection.IsRunnable
            || detection.CoreType is not (CoreType.Forge or CoreType.NeoForge))
        {
            throw new InvalidDataException(
                "線上 Server Pack 必須是可靜態驗證的 Forge 或 NeoForge argument-file 啟動結構。");
        }

        if (string.IsNullOrWhiteSpace(detection.MinecraftVersion)
            || string.IsNullOrWhiteSpace(detection.ModLoaderVersion))
        {
            throw new InvalidDataException("線上 Server Pack 的 argument file 缺少 Minecraft／Loader 版本證據。");
        }

        var root = Path.GetFullPath(detection.DirectoryPath);
        var loaderPrefix = detection.CoreType == CoreType.Forge
            ? "libraries/net/minecraftforge/forge/"
            : "libraries/net/neoforged/neoforge/";
        var loaderFiles = detection.JavaArgumentFilePaths
            .Where(path => Normalize(path).StartsWith(loaderPrefix, StringComparison.OrdinalIgnoreCase)
                           && IsLoaderArgumentFile(path))
            .ToArray();
        if (loaderFiles.Length != 1)
        {
            throw new InvalidDataException(
                "線上 Server Pack 必須明確引用一個標準 Forge／NeoForge loader argument file。");
        }

        IReadOnlyList<string>? loaderTokens = null;
        foreach (var relativePath in detection.JavaArgumentFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = await ReadTokensAsync(root, relativePath, cancellationToken)
                .ConfigureAwait(false);
            RejectUnsafeJavaTokens(tokens, relativePath);
            if (relativePath.Equals(loaderFiles[0], StringComparison.OrdinalIgnoreCase))
            {
                loaderTokens = tokens;
            }
            else
            {
                RejectAuxiliaryLaunchTokens(tokens, relativePath);
            }
        }

        if (loaderTokens is null)
        {
            throw new InvalidDataException("找不到已驗證的 loader argument file。");
        }
        var minecraftVersion = ReadSingleOption(loaderTokens, "--fml.mcVersion");
        if (!minecraftVersion.Equals(detection.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Loader argument file 的 Minecraft 版本與偵測結果不符。");
        }

        var loaderOption = detection.CoreType == CoreType.Forge
            ? "--fml.forgeVersion"
            : "--fml.neoForgeVersion";
        var loaderVersion = ReadSingleOption(loaderTokens, loaderOption);
        var expectedLoaderVersion = NormalizeLoaderVersion(
            detection.MinecraftVersion,
            detection.ModLoaderVersion);
        if (!loaderVersion.Equals(expectedLoaderVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Loader argument file 的 Loader 版本與偵測結果不符。");
        }

        var launchTarget = ReadSingleOption(loaderTokens, "--launchTarget");
        var allowedTargets = detection.CoreType == CoreType.Forge
            ? new[] { "forge_server", "forgeserver" }
            : new[] { "neoforgeserver", "forge_server", "forgeserver" };
        if (!allowedTargets.Contains(launchTarget, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Loader argument file 使用未核准的 server launch target：{launchTarget}");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTokensAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("線上 Server Pack 含有空白 argument-file 路徑。");
        }

        var normalizedPath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = SafePath.EnsureWithinRoot(root, normalizedPath, allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(root, fullPath);
        var info = new FileInfo(fullPath);
        info.Refresh();
        if (!info.Exists || info.Length is < 1 or > MaximumArgumentFileBytes)
        {
            throw new InvalidDataException(
                $"線上 Server Pack argument file 大小無效：{relativePath}");
        }

        byte[] bytes;
        await using (var input = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         32 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (input.Length is < 1 or > MaximumArgumentFileBytes)
            {
                throw new InvalidDataException(
                    $"線上 Server Pack argument file 在開啟後大小無效：{relativePath}");
            }

            bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"線上 Server Pack argument file 在驗證期間發生變更：{relativePath}");
            }
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"線上 Server Pack argument file 不是有效 UTF-8：{relativePath}",
                exception);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return Tokenize(text, relativePath);
    }

    private static IReadOnlyList<string> Tokenize(string text, string context)
    {
        var tokens = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var current = new StringBuilder();
            char quote = '\0';
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                    }
                    else if (character == '\\'
                             && index + 1 < line.Length
                             && line[index + 1] is '\\' or '\'' or '"')
                    {
                        current.Append(line[++index]);
                    }
                    else
                    {
                        current.Append(character);
                    }

                    continue;
                }

                if (character == '#')
                {
                    break;
                }

                if (character is '\'' or '"')
                {
                    quote = character;
                }
                else if (char.IsWhiteSpace(character))
                {
                    AddToken();
                }
                else if (char.IsControl(character))
                {
                    throw new InvalidDataException(
                        $"線上 Server Pack argument file 含有未核准控制字元：{context}");
                }
                else
                {
                    current.Append(character);
                }
            }

            if (quote != '\0')
            {
                throw new InvalidDataException(
                    $"線上 Server Pack argument file 含有未閉合引號：{context}");
            }

            AddToken();

            void AddToken()
            {
                if (current.Length == 0)
                {
                    return;
                }

                if (tokens.Count >= MaximumTokensPerFile)
                {
                    throw new InvalidDataException(
                        $"線上 Server Pack argument file token 數量超過安全上限：{context}");
                }

                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        return tokens;
    }

    private static void RejectUnsafeJavaTokens(IReadOnlyList<string> tokens, string context)
    {
        foreach (var token in tokens)
        {
            if (token.StartsWith('@')
                || token.Equals("-jar", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-jar=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-javaagent=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-agentlib:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-agentpath:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-XX:OnError", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-XX:OnOutOfMemoryError", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-Djava.system.class.loader=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-Xbootclasspath", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--patch-module", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--patch-module=", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"線上 Server Pack argument file 含有未核准的 Java 啟動指令 '{token}'：{context}");
            }
        }
    }

    private static void RejectAuxiliaryLaunchTokens(
        IReadOnlyList<string> tokens,
        string context)
    {
        foreach (var token in tokens)
        {
            if (!token.StartsWith('-')
                || token.Equals("-cp", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-classpath", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--class-path", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--class-path=", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"線上 Server Pack 的輔助 argument file 含有可改變主程式的 token '{token}'：{context}");
            }
        }
    }

    private static string ReadSingleOption(IReadOnlyList<string> tokens, string option)
    {
        var values = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Equals(option, StringComparison.Ordinal))
            {
                if (++index >= tokens.Count || tokens[index].StartsWith('-'))
                {
                    throw new InvalidDataException($"Loader argument file 的 {option} 缺少值。");
                }

                values.Add(tokens[index]);
            }
            else if (token.StartsWith(option + "=", StringComparison.Ordinal))
            {
                values.Add(token[(option.Length + 1)..]);
            }
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new InvalidDataException($"Loader argument file 必須明確指定一次 {option}。");
        }

        return values[0];
    }

    private static string NormalizeLoaderVersion(string minecraftVersion, string loaderVersion)
    {
        var prefix = minecraftVersion + "-";
        return loaderVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[prefix.Length..]
            : loaderVersion;
    }

    private static bool IsLoaderArgumentFile(string path)
    {
        var name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return name.Equals("win_args.txt", StringComparison.OrdinalIgnoreCase)
               || name.Equals("unix_args.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
