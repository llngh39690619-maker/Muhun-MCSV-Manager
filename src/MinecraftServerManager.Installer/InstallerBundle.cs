using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Installer;

internal sealed record InstallerBundleMetadata(
    int SchemaVersion,
    string ProductId,
    string Version,
    string Channel,
    string PackageFileName,
    long PackageSizeBytes,
    string PackageSha256);

internal readonly record struct InstallerTrailerLocation(
    long Offset,
    long LogicalContentEnd,
    int AuthenticodeAlignmentPaddingBytes);

internal sealed class InstallerBundle : IDisposable
{
    internal const string MetadataEntryName = "installer-bundle.v1.json";
    internal const string ManifestEntryName = "update-manifest.json";
    internal const string SignatureEntryName = "update-manifest.json.sig";
    internal const string PublicKeyEntryName = "update-signing-public-key.json";
    internal const string TrailerMagic = "MCSV-INSTALL-V1!";
    internal const int TrailerLength = 56;
    internal const string PinnedSubjectPublicKeyInfoSha256 =
        "b85078f848fc4245cdbce277327fa0d5cbfd40b459ad907c82daa127d10517b8";
    internal const string PinnedPublisherCertificateSha256 =
        "1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71";

    private const long MaximumBundleBytes = ProductUpdateManifestParser.MaximumPackageBytes + 2 * 1024 * 1024;
    private readonly FileStream _executable;
    private readonly BoundedReadStream _bundleStream;
    private readonly ZipArchive _archive;

    private InstallerBundle(
        FileStream executable,
        BoundedReadStream bundleStream,
        ZipArchive archive,
        InstallerBundleMetadata metadata,
        ProductUpdateManifest manifest)
    {
        _executable = executable;
        _bundleStream = bundleStream;
        _archive = archive;
        Metadata = metadata;
        Manifest = manifest;
    }

    public InstallerBundleMetadata Metadata { get; }
    public ProductUpdateManifest Manifest { get; }

    public static async Task<InstallerBundle> OpenAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var executable = new FileStream(
            Path.GetFullPath(executablePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        BoundedReadStream? bundleStream = null;
        ZipArchive? archive = null;
        try
        {
            var trailerLocation = LocateTrailer(executable);
            var trailer = new byte[TrailerLength];
            executable.Position = trailerLocation.Offset;
            await executable.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);
            var magic = System.Text.Encoding.ASCII.GetString(trailer.AsSpan(40, 16));
            if (!string.Equals(magic, TrailerMagic, StringComparison.Ordinal))
            {
                throw new InvalidDataException("安裝 EXE 的內容標記無效。");
            }

            var bundleLength = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(0, 8));
            if (bundleLength is < 1 or > MaximumBundleBytes || bundleLength > trailerLocation.Offset)
            {
                throw new InvalidDataException("安裝 EXE 的內容長度無效。");
            }

            var bundleOffset = trailerLocation.Offset - bundleLength;
            bundleStream = new BoundedReadStream(executable, bundleOffset, bundleLength, leaveOpen: true);
            var actualHash = await SHA256.HashDataAsync(bundleStream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, trailer.AsSpan(8, 32)))
            {
                throw new CryptographicException("安裝 EXE 內含資料的 SHA-256 驗證失敗。");
            }

            bundleStream.Position = 0;
            archive = new ZipArchive(bundleStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != 5)
            {
                throw new InvalidDataException("安裝內容的檔案數量無效。");
            }

            var entries = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
            var metadataBytes = await ReadBoundedEntryAsync(
                RequireEntry(entries, MetadataEntryName), 32 * 1024, cancellationToken).ConfigureAwait(false);
            var manifestBytes = await ReadBoundedEntryAsync(
                RequireEntry(entries, ManifestEntryName), ProductUpdateManifestParser.MaximumManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var signatureBytes = await ReadBoundedEntryAsync(
                RequireEntry(entries, SignatureEntryName), 1024, cancellationToken).ConfigureAwait(false);
            var publicKeyBytes = await ReadBoundedEntryAsync(
                RequireEntry(entries, PublicKeyEntryName), ProductUpdatePublicKeyLoader.MaximumDocumentBytes,
                cancellationToken).ConfigureAwait(false);

            var metadata = JsonSerializer.Deserialize<InstallerBundleMetadata>(
                    metadataBytes,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    })
                ?? throw new InvalidDataException("安裝內容 metadata 為空白。");
            using var rsa = ProductUpdatePublicKeyLoader.Load(publicKeyBytes, out var keyDocument);
            if (!string.Equals(
                    keyDocument.SubjectPublicKeyInfoSha256,
                    PinnedSubjectPublicKeyInfoSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    keyDocument.PublisherCertificateSha256,
                    PinnedPublisherCertificateSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("安裝內容不是固定的 X MCSV 發布金鑰。");
            }

            using var manifestDocument = JsonDocument.Parse(manifestBytes);
            var packageUrl = manifestDocument.RootElement
                .GetProperty("package")
                .GetProperty("url")
                .GetString();
            if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var packageUri) ||
                !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !packageUri.IsDefaultPort || !string.IsNullOrEmpty(packageUri.UserInfo))
            {
                throw new InvalidDataException("已簽署版本資訊的下載位置無效。");
            }
            var manifest = new SignedProductUpdateManifestVerifier(
                    new Dictionary<string, RSA>(StringComparer.Ordinal)
                    {
                        [keyDocument.KeyId] = rsa,
                    },
                    new HashSet<string>([packageUri.IdnHost], StringComparer.OrdinalIgnoreCase))
                .Verify(manifestBytes, signatureBytes);
            ValidateMetadata(metadata, manifest);

            var packageEntry = RequireEntry(entries, metadata.PackageFileName);
            if (packageEntry.Length != metadata.PackageSizeBytes ||
                archive.Entries.Any(entry => entry.FullName.Contains('\\') ||
                                             entry.FullName.Contains("../", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("安裝套件檔案不符合 bundle metadata。");
            }

            return new InstallerBundle(executable, bundleStream, archive, metadata, manifest);
        }
        catch
        {
            archive?.Dispose();
            bundleStream?.Dispose();
            executable.Dispose();
            throw;
        }
    }

    public async Task CopyPackageToAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var entry = _archive.GetEntry(Metadata.PackageFileName)
            ?? throw new InvalidDataException("安裝 EXE 缺少版本套件。");
        var destination = Path.GetFullPath(destinationPath);
        InstallerLayout.RejectExistingReparsePoints(Path.GetDirectoryName(destination)!);
        await using var input = entry.Open();
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written = checked(written + read);
            if (written > Metadata.PackageSizeBytes)
            {
                throw new InvalidDataException("內含版本套件超過宣告大小。");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (written != Metadata.PackageSizeBytes ||
            !CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(Metadata.PackageSha256)))
        {
            throw new CryptographicException("內含版本套件驗證失敗。");
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
        _bundleStream.Dispose();
        _executable.Dispose();
    }

    internal static InstallerTrailerLocation LocateTrailer(Stream executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        if (!executable.CanRead || !executable.CanSeek || executable.Length <= TrailerLength)
        {
            throw new InvalidDataException("安裝 EXE 未包含產品內容。");
        }

        var certificateTableOffset = ReadAuthenticodeCertificateTableOffset(executable);
        var logicalEnd = certificateTableOffset ?? executable.Length;
        var maximumPadding = certificateTableOffset.HasValue ? 7 : 0;
        Span<byte> candidate = stackalloc byte[TrailerLength];
        for (var padding = 0; padding <= maximumPadding; padding++)
        {
            var trailerOffset = logicalEnd - padding - TrailerLength;
            if (trailerOffset < 1)
            {
                continue;
            }

            ReadExactlyAt(executable, trailerOffset, candidate);
            if (!candidate[40..].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(TrailerMagic)))
            {
                continue;
            }

            if (padding > 0)
            {
                Span<byte> alignment = stackalloc byte[7];
                ReadExactlyAt(executable, trailerOffset + TrailerLength, alignment[..padding]);
                if (alignment[..padding].ContainsAnyExcept((byte)0))
                {
                    continue;
                }
            }

            return new InstallerTrailerLocation(trailerOffset, logicalEnd, padding);
        }

        throw new InvalidDataException("安裝 EXE 的內容標記無效或不在 Authenticode 簽章前。");
    }

    private static long? ReadAuthenticodeCertificateTableOffset(Stream executable)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        ReadExactlyAt(executable, 0, dosHeader);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != 0x5a4d)
        {
            throw new InvalidDataException("安裝檔不是有效的 Windows PE 檔案。");
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
        if (peOffset < dosHeader.Length || peOffset > executable.Length - 24)
        {
            throw new InvalidDataException("安裝檔的 PE header 位置無效。");
        }

        Span<byte> peAndCoffHeader = stackalloc byte[24];
        ReadExactlyAt(executable, peOffset, peAndCoffHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(peAndCoffHeader) != 0x00004550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(peAndCoffHeader[4..]) != 0x8664)
        {
            throw new InvalidDataException("安裝檔不是有效的 Windows x64 PE 檔案。");
        }

        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(peAndCoffHeader[20..]);
        if (optionalHeaderSize is < 152 or > 4096 ||
            peOffset + 24L + optionalHeaderSize > executable.Length)
        {
            throw new InvalidDataException("安裝檔的 PE optional header 大小無效。");
        }

        var optionalHeader = GC.AllocateUninitializedArray<byte>(optionalHeaderSize);
        ReadExactlyAt(executable, peOffset + 24L, optionalHeader);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader);
        var numberOfDirectoriesOffset = magic switch
        {
            0x20b => 108,
            0x10b => 92,
            _ => throw new InvalidDataException("安裝檔的 PE optional header 格式無效。"),
        };
        var dataDirectoriesOffset = numberOfDirectoriesOffset + 4;
        const int securityDirectoryIndex = 4;
        var securityDirectoryOffset = dataDirectoriesOffset + securityDirectoryIndex * 8;
        if (optionalHeader.Length < securityDirectoryOffset + 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(numberOfDirectoriesOffset)) <=
            securityDirectoryIndex)
        {
            throw new InvalidDataException("安裝檔缺少 PE security directory。");
        }

        var certificateOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            optionalHeader.AsSpan(securityDirectoryOffset));
        var certificateSize = BinaryPrimitives.ReadUInt32LittleEndian(
            optionalHeader.AsSpan(securityDirectoryOffset + 4));
        if (certificateOffset == 0 && certificateSize == 0)
        {
            return null;
        }

        if (certificateOffset == 0 || certificateSize < 8 || certificateOffset % 8 != 0 ||
            certificateOffset > executable.Length - certificateSize ||
            certificateOffset + (long)certificateSize != executable.Length)
        {
            throw new InvalidDataException("安裝檔的 Authenticode 憑證表位置無效。");
        }

        return certificateOffset;
    }

    private static void ReadExactlyAt(Stream stream, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
        {
            throw new InvalidDataException("安裝檔資料位置超出範圍。");
        }

        stream.Position = offset;
        stream.ReadExactly(destination);
    }

    private static void ValidateMetadata(InstallerBundleMetadata metadata, ProductUpdateManifest manifest)
    {
        if (metadata.SchemaVersion != 1 ||
            !string.Equals(metadata.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal) ||
            metadata.Channel is not ("beta" or "stable") ||
            !string.Equals(metadata.Version, manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(metadata.Channel, manifest.Channel, StringComparison.Ordinal) ||
            metadata.PackageSizeBytes != manifest.Package.SizeBytes ||
            !string.Equals(metadata.PackageSha256, manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.PackageFileName, $"Muhun-MCSV-{metadata.Version}-win-x64.zip", StringComparison.Ordinal))
        {
            throw new InvalidDataException("安裝 bundle metadata 與已簽署版本資訊不一致。");
        }
    }

    private static ZipArchiveEntry RequireEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string name)
        => entries.TryGetValue(name, out var entry) && !entry.FullName.EndsWith('/')
            ? entry
            : throw new InvalidDataException($"安裝 EXE 缺少必要內容：{name}");

    private static async Task<byte[]> ReadBoundedEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length is < 1 || entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"安裝內容大小無效：{entry.FullName}");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"安裝內容超過宣告大小：{entry.FullName}");
        }

        return bytes;
    }
}

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public BoundedReadStream(Stream inner, long offset, long length, bool leaveOpen)
    {
        if (!inner.CanRead || !inner.CanSeek || offset < 0 || length < 0 || offset > inner.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _inner = inner;
        _offset = offset;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("Bounded stream seek escaped its payload.");
        }

        _position = target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
