using System.Text;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Creates a manager-owned Java argument file containing effective memory flags without changing
/// installer-owned <c>user_jvm_args.txt</c> bytes.
/// </summary>
public sealed class JvmMemoryLaunchOverlayService
{
    public const int MaximumSourceArgumentFileBytes = 1024 * 1024;
    public const string RuntimeArgumentFileRelativePath = ".mcsv-runtime/memory.args";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<string> ApplyAsync(
        ServerInstance launchSnapshot,
        int minimumMemoryMb,
        int maximumMemoryMb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchSnapshot);
        if (launchSnapshot.LaunchKind != ServerLaunchKind.JavaArgumentFiles)
        {
            throw new InvalidOperationException(
                "A JVM memory argument-file overlay can only be applied to JavaArgumentFiles launches.");
        }

        if (minimumMemoryMb <= 0 || maximumMemoryMb < minimumMemoryMb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMemoryMb),
                "Effective JVM memory must be positive and maximum memory cannot be below minimum memory.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(launchSnapshot.DirectoryPath);
        var root = Path.GetFullPath(launchSnapshot.DirectoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Server directory does not exist: '{root}'.");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(root, root);

        var originalPaths = launchSnapshot.JavaArgumentFilePaths?.ToArray() ?? [];
        var normalizedPaths = new string[originalPaths.Length];
        for (var index = 0; index < originalPaths.Length; index++)
        {
            normalizedPaths[index] = ValidateAndNormalizeRelativePath(root, originalPaths[index]);
        }

        var sourceIndex = FindSourceArgumentFileIndex(originalPaths);
        string retainedText;
        if (sourceIndex >= 0)
        {
            var sourceFullPath = SafePath.EnsureWithinRoot(
                root,
                normalizedPaths[sourceIndex],
                allowRoot: false);
            if (!File.Exists(sourceFullPath))
            {
                throw new FileNotFoundException(
                    "The JVM argument file selected for the memory overlay was not found.",
                    sourceFullPath);
            }

            SafePath.EnsureNoReparsePointsUnderRoot(root, sourceFullPath);
            var sourceBytes = await ReadBoundedFileAsync(sourceFullPath, cancellationToken)
                .ConfigureAwait(false);
            retainedText = RemoveMemoryOptionLines(DecodeUtf8(sourceBytes));
        }
        else
        {
            retainedText = string.Empty;
        }

        var generatedText = BuildGeneratedText(
            minimumMemoryMb,
            maximumMemoryMb,
            retainedText);
        var generatedFullPath = await WriteAtomicallyAsync(
                root,
                generatedText,
                cancellationToken)
            .ConfigureAwait(false);

        // Mutate only after every validation and durable write succeeds. The caller passes a
        // launch-only snapshot, so persisted installer metadata remains untouched.
        var updatedPaths = originalPaths.ToList();
        if (sourceIndex >= 0)
        {
            updatedPaths[sourceIndex] = RuntimeArgumentFileRelativePath;
        }
        else
        {
            updatedPaths.Insert(0, RuntimeArgumentFileRelativePath);
        }

        launchSnapshot.JavaArgumentFilePaths = updatedPaths;
        return generatedFullPath;
    }

    private static int FindSourceArgumentFileIndex(IReadOnlyList<string> paths)
    {
        var userIndex = -1;
        var runtimeIndex = -1;
        for (var index = 0; index < paths.Count; index++)
        {
            var normalized = paths[index]
                .Replace('\\', '/')
                .TrimStart('/');
            if (string.Equals(
                    normalized,
                    RuntimeArgumentFileRelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (runtimeIndex >= 0)
                {
                    throw new InvalidDataException(
                        "The launch snapshot contains duplicate manager memory overlays.");
                }

                runtimeIndex = index;
                continue;
            }

            if (string.Equals(
                    Path.GetFileName(normalized),
                    "user_jvm_args.txt",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (userIndex >= 0)
                {
                    throw new InvalidDataException(
                        "The launch snapshot contains more than one user_jvm_args.txt file.");
                }

                userIndex = index;
            }
        }

        if (userIndex >= 0 && runtimeIndex >= 0)
        {
            throw new InvalidDataException(
                "The launch snapshot cannot contain both a user argument file and a memory overlay.");
        }

        return userIndex >= 0 ? userIndex : runtimeIndex;
    }

    private static string ValidateAndNormalizeRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Java argument-file paths cannot be blank.", nameof(path));
        }

        if (path[0] == '@'
            || path.Contains('\0')
            || path.Contains('\r')
            || path.Contains('\n'))
        {
            throw new ArgumentException("Invalid Java argument-file path.", nameof(path));
        }

        var normalized = path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)
            || normalized.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException(
                $"Java argument-file path must stay inside the server root: '{path}'.");
        }

        SafePath.EnsureWithinRoot(root, normalized, allowRoot: false);
        return normalized;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > MaximumSourceArgumentFileBytes)
        {
            throw new InvalidDataException(
                $"JVM argument file exceeds the {MaximumSourceArgumentFileBytes} byte safety limit.");
        }

        using var buffer = new MemoryStream(
            capacity: checked((int)Math.Min(stream.Length, MaximumSourceArgumentFileBytes)));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumSourceArgumentFileBytes)
            {
                throw new InvalidDataException(
                    $"JVM argument file exceeds the {MaximumSourceArgumentFileBytes} byte safety limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string DecodeUtf8(byte[] sourceBytes)
    {
        try
        {
            var start = sourceBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length
                : 0;
            return StrictUtf8.GetString(sourceBytes.AsSpan(start));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "JVM argument file must be valid UTF-8 text.",
                exception);
        }
    }

    private static string RemoveMemoryOptionLines(string text)
    {
        var retained = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var lineStart = position;
            while (position < text.Length && text[position] is not ('\r' or '\n'))
            {
                position++;
            }

            var line = text[lineStart..position];
            var newlineStart = position;
            if (position < text.Length && text[position] == '\r')
            {
                position++;
            }

            if (position < text.Length && text[position] == '\n')
            {
                position++;
            }

            if (!StartsWithMemoryOption(line))
            {
                retained.Append(line);
                retained.Append(text.AsSpan(newlineStart, position - newlineStart));
            }
        }

        return retained.ToString();
    }

    private static bool StartsWithMemoryOption(string line)
    {
        var candidate = line.AsSpan().TrimStart();
        if (candidate.IsEmpty || candidate[0] == '#')
        {
            return false;
        }

        if (candidate[0] == '"')
        {
            candidate = candidate[1..].TrimStart();
        }

        return candidate.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGeneratedText(
        int minimumMemoryMb,
        int maximumMemoryMb,
        string retainedText)
    {
        var builder = new StringBuilder(retainedText.Length + 64);
        builder.Append("-Xms")
            .Append(minimumMemoryMb)
            .Append('M')
            .AppendLine();
        builder.Append("-Xmx")
            .Append(maximumMemoryMb)
            .Append('M')
            .AppendLine();
        builder.Append(retainedText);
        return builder.ToString();
    }

    private static async Task<string> WriteAtomicallyAsync(
        string root,
        string text,
        CancellationToken cancellationToken)
    {
        var runtimeDirectory = SafePath.CombineUnderRoot(root, ".mcsv-runtime");
        if (File.Exists(runtimeDirectory) && !Directory.Exists(runtimeDirectory))
        {
            throw new IOException(
                $"Memory overlay directory path is occupied by a file: '{runtimeDirectory}'.");
        }

        Directory.CreateDirectory(runtimeDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(root, runtimeDirectory);

        var destination = SafePath.CombineUnderRoot(runtimeDirectory, "memory.args");
        if (File.Exists(destination))
        {
            SafePath.EnsureNoReparsePointsUnderRoot(root, destination);
        }

        var temporary = SafePath.CombineUnderRoot(
            runtimeDirectory,
            $"memory.args.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8WithoutBom.GetBytes(text);
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            SafePath.EnsureNoReparsePointsUnderRoot(root, destination);
            return destination;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (FileNotFoundException)
            {
            }
        }
    }
}
