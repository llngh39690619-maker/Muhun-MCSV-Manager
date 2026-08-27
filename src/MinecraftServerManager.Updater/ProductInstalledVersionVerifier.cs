using System.Security.Cryptography;

namespace MinecraftServerManager.Updater;

/// <summary>
/// Revalidates an installed A/B slot against the already signature-verified manifest. Existing
/// version directories are never trusted merely because their metadata names the expected version.
/// </summary>
public static class ProductInstalledVersionVerifier
{
    public static async Task VerifyAsync(
        string versionRoot,
        ProductUpdateManifest manifest,
        CancellationToken cancellationToken = default,
        bool requireVersionDirectoryName = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        var root = Path.GetFullPath(versionRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) ||
            (requireVersionDirectoryName &&
             !string.Equals(Path.GetFileName(root), manifest.Version, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Installed version directory does not match the signed manifest.");
        }

        RejectReparse(root);
        var metadata = ProductInstalledVersionMetadataStore.Read(root);
        if (!string.Equals(metadata.Version, manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(metadata.EntryPoint, manifest.EntryPoint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installed version metadata does not match the signed manifest.");
        }

        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedFiles = 0;
        foreach (var path in EnumerateFilesNoFollow(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            observedFiles++;
            if (observedFiles > expected.Count + 1)
            {
                throw new InvalidDataException("Installed version contains too many files.");
            }

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            ProductUpdatePath.ValidateRelativeFilePath(relative);
            if (string.Equals(
                    relative,
                    ProductInstalledVersionMetadataStore.FileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!expected.TryGetValue(relative, out var expectedFile) || !verified.Add(relative))
            {
                throw new InvalidDataException("Installed version contains an unsigned or duplicate file.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            RejectReparse(path);
            if (stream.Length != expectedFile.SizeBytes)
            {
                throw new InvalidDataException("Installed version file size does not match its signed manifest.");
            }

            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    Convert.FromHexString(expectedFile.Sha256)))
            {
                throw new InvalidDataException("Installed version file hash does not match its signed manifest.");
            }
        }

        if (verified.Count != expected.Count || expected.Keys.Any(path => !verified.Contains(path)))
        {
            throw new InvalidDataException("Installed version is missing one or more signed files.");
        }
    }

    private static IEnumerable<string> EnumerateFilesNoFollow(string root)
    {
        var pending = new Stack<string>();
        var observedEntries = 0;
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparse(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                observedEntries++;
                if (observedEntries > ProductUpdateManifestParser.MaximumFiles * 2 + 1)
                {
                    throw new InvalidDataException("Installed version tree exceeds its bounded entry limit.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Installed version cannot contain a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Installed version cannot contain a reparse point.");
        }
    }
}
