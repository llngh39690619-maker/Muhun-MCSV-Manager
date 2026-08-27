using System.IO.Compression;
using System.Security.Cryptography;

namespace MinecraftServerManager.Updater;

public sealed class SafeProductPackageExtractor
{
    private const int CopyBufferSize = 128 * 1024;
    private const long MaximumCompressionRatio = 1_000;

    public async Task ExtractAndVerifyAsync(
        Stream packageStream,
        string destinationDirectory,
        ProductUpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!packageStream.CanRead || !packageStream.CanSeek)
        {
            throw new ArgumentException("Update package stream must be readable and seekable.", nameof(packageStream));
        }

        if (packageStream.Length != manifest.Package.SizeBytes)
        {
            throw new InvalidDataException("Update package size does not match its signed manifest.");
        }

        packageStream.Position = 0;
        var actualPackageHash = await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
        var expectedPackageHash = Convert.FromHexString(manifest.Package.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(expectedPackageHash, actualPackageHash))
        {
            throw new InvalidDataException("Update package hash does not match its signed manifest.");
        }

        packageStream.Position = 0;

        var destination = Path.GetFullPath(destinationDirectory);
        RejectExistingReparsePoints(Path.GetDirectoryName(destination)
                                    ?? throw new InvalidDataException("Update staging parent is invalid."));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("Update staging directory must be empty.");
        }

        Directory.CreateDirectory(destination);
        RejectExistingReparsePoints(destination);
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalExtracted = 0;

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > ProductUpdateManifestParser.MaximumFiles * 2)
        {
            throw new InvalidDataException("Update package contains too many entries.");
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = entry.FullName;
            if (entryPath.EndsWith("/", StringComparison.Ordinal))
            {
                RejectLinkOrReparseEntry(entry);
                var directoryPath = entryPath.TrimEnd('/');
                if (directoryPath.Length == 0)
                {
                    continue;
                }

                ProductUpdatePath.ValidateRelativeFilePath(directoryPath + "/placeholder");
                var resolvedDirectory = ProductUpdatePath.ResolveUnderRoot(destination, directoryPath + "/placeholder");
                Directory.CreateDirectory(Path.GetDirectoryName(resolvedDirectory)!);
                RejectExistingReparsePoints(Path.GetDirectoryName(resolvedDirectory)!);
                continue;
            }

            ProductUpdatePath.ValidateRelativeFilePath(entryPath);
            RejectLinkOrReparseEntry(entry);
            if (!extracted.Add(entryPath) || !expected.TryGetValue(entryPath, out var expectedFile))
            {
                throw new InvalidDataException("Update package contains an unexpected or duplicate file.");
            }

            if (entry.Length != expectedFile.SizeBytes || entry.Length > ProductUpdateManifestParser.MaximumPackageBytes)
            {
                throw new InvalidDataException("Update package file size does not match its manifest.");
            }

            if (entry.Length > 1024 * 1024 &&
                (entry.CompressedLength == 0 || entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio))
            {
                throw new InvalidDataException("Update package compression ratio is unsafe.");
            }

            totalExtracted = checked(totalExtracted + entry.Length);
            if (totalExtracted > ProductUpdateManifestParser.MaximumPackageBytes)
            {
                throw new InvalidDataException("Update package extracted size exceeds the product limit.");
            }

            var outputPath = ProductUpdatePath.ResolveUnderRoot(destination, entryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            RejectExistingReparsePoints(Path.GetDirectoryName(outputPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = GC.AllocateUninitializedArray<byte>(CopyBufferSize);
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > expectedFile.SizeBytes)
                {
                    throw new InvalidDataException("Update package expanded beyond its declared size.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (written != expectedFile.SizeBytes ||
                !string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()),
                    expectedFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Update package file hash does not match its manifest.");
            }
        }

        if (extracted.Count != expected.Count || expected.Keys.Any(path => !extracted.Contains(path)))
        {
            throw new InvalidDataException("Update package is missing one or more manifest files.");
        }

        var entryPoint = ProductUpdatePath.ResolveUnderRoot(destination, manifest.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new InvalidDataException("Update package entry point was not extracted.");
        }
    }

    private static void RejectLinkOrReparseEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Update package links and reparse points are forbidden.");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Update staging paths must not traverse a reparse point.");
            }
        }
    }
}
