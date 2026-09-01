using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.App.Services;

internal sealed class PinnedProtectedFormalReleaseVerifier : IProtectedFormalReleaseVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    internal const string PublisherSubjectPublicKeyInfoSha256 =
        "b85078f848fc4245cdbce277327fa0d5cbfd40b459ad907c82daa127d10517b8";
    private const int MaximumReleaseManifestBytes = 1024 * 1024;
    private const int MaximumSignatureBytes = 1024;
    private const int MaximumChecksumBytes = 1024 * 1024;
    private const int MaximumFiles = 4_096;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumReleaseBytes = 4L * 1024 * 1024 * 1024;
    private const string UpdaterRelativePath = "updater-win-x64/Muhun MCSV Updater.exe";
    private static readonly string[] RequiredManifestFiles =
    [
        "publisher.cer",
        "installed-version.v1.json",
        "update-manifest.json",
        "update-manifest.json.sig",
        "update-signing-public-key.json",
        "gui-win-x64/Muhun MCSV Manager.exe",
        "service-win-x64/Muhun MCSV Service.exe",
        UpdaterRelativePath,
    ];

    private readonly IProtectedProductPathSecurityValidator _securityValidator;
    private readonly Action<string, string> _publisherVerifier;
    private readonly TimeProvider _timeProvider;
    private readonly string _publisherCertificateSha256;
    private readonly string _publisherSubjectPublicKeyInfoSha256;

    public PinnedProtectedFormalReleaseVerifier()
        : this(
            new WindowsProtectedProductPathSecurityValidator(),
            WindowsProductPublisherVerifier.Verify,
            TimeProvider.System,
            BundledProductServiceUpdateLauncher.PublisherCertificateSha256,
            PublisherSubjectPublicKeyInfoSha256)
    {
    }

    internal PinnedProtectedFormalReleaseVerifier(
        IProtectedProductPathSecurityValidator securityValidator,
        Action<string, string> publisherVerifier,
        TimeProvider timeProvider,
        string publisherCertificateSha256,
        string publisherSubjectPublicKeyInfoSha256)
    {
        _securityValidator = securityValidator ?? throw new ArgumentNullException(nameof(securityValidator));
        _publisherVerifier = publisherVerifier ?? throw new ArgumentNullException(nameof(publisherVerifier));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _publisherCertificateSha256 = ValidateSha256(publisherCertificateSha256);
        _publisherSubjectPublicKeyInfoSha256 = ValidateSha256(
            publisherSubjectPublicKeyInfoSha256);
    }

    public async Task<string> VerifyAsync(
        string protectedReleaseRoot,
        string expectedProductVersion,
        CancellationToken cancellationToken)
    {
        var root = NormalizeProtectedRoot(protectedReleaseRoot);
        var expectedVersion = WindowsShellProtectedFormalReleaseStager.ValidateVersionPathSegment(
            expectedProductVersion);
        _securityValidator.ValidateTree(root);

        var certificateBytes = await ReadBoundedAsync(
                root,
                "publisher.cer",
                16 * 1024,
                cancellationToken)
            .ConfigureAwait(false);
        VerifyHash(
            certificateBytes,
            _publisherCertificateSha256,
            "publisher certificate");
        using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        using var publisherRsa = certificate.GetRSAPublicKey()
            ?? throw new CryptographicException("The pinned publisher certificate is not RSA.");
        if (publisherRsa.KeySize < 3072)
        {
            throw new CryptographicException("The pinned publisher certificate key is too weak.");
        }

        VerifyHash(
            publisherRsa.ExportSubjectPublicKeyInfo(),
            _publisherSubjectPublicKeyInfoSha256,
            "publisher public key");
        var now = _timeProvider.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            throw new CryptographicException("The pinned publisher certificate is outside its validity window.");
        }

        var manifestBytes = await ReadBoundedAsync(
                root,
                "release-manifest.json",
                MaximumReleaseManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var signatureBytes = await ReadBoundedAsync(
                root,
                "release-manifest.json.sig",
                MaximumSignatureBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (signatureBytes.Length != publisherRsa.KeySize / 8 ||
            !publisherRsa.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss))
        {
            throw new CryptographicException("The protected release manifest signature is invalid.");
        }

        var manifest = ParseManifest(
            manifestBytes,
            expectedVersion,
            _publisherCertificateSha256);
        await VerifyManifestFilesAsync(root, manifest.Files, cancellationToken).ConfigureAwait(false);
        await VerifyChecksumsAsync(root, manifest.Files, cancellationToken).ConfigureAwait(false);
        VerifyExactFileSet(root, manifest.Files);

        var updaterPath = ResolveFile(root, UpdaterRelativePath);
        _publisherVerifier(updaterPath, expectedVersion);
        return updaterPath;
    }

    private static ProtectedReleaseManifest ParseManifest(
        byte[] content,
        string expectedVersion,
        string publisherCertificateSha256)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                GetInt32(root, "schemaVersion") != 1 ||
                !string.Equals(GetString(root, "productId"), "muhun.mcsv.manager", StringComparison.Ordinal) ||
                !string.Equals(GetString(root, "version"), expectedVersion, StringComparison.Ordinal) ||
                GetString(root, "channel") is not ("stable" or "beta") ||
                !string.Equals(GetString(root, "runtimeIdentifier"), "win-x64", StringComparison.Ordinal) ||
                !GetBoolean(root, "installable") ||
                !string.Equals(GetString(root, "signatureAlgorithm"), "rsa-pss-sha256", StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(root, "publisherCertificateSha256"),
                    publisherCertificateSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    GetString(root, "entryPoint"),
                    "gui-win-x64/Muhun MCSV Manager.exe",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(root, "serviceEntryPoint"),
                    "service-win-x64/Muhun MCSV Service.exe",
                    StringComparison.Ordinal) ||
                !string.Equals(GetString(root, "updaterEntryPoint"), UpdaterRelativePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The protected release manifest metadata is invalid.");
            }

            var filesElement = GetArray(root, "files");
            var files = new List<ProtectedReleaseFile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var element in filesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The protected release manifest contains an invalid file entry.");
                }

                var path = ValidateRelativeFilePath(GetString(element, "path"));
                var size = GetInt64(element, "sizeBytes");
                var sha256 = ValidateSha256(GetString(element, "sha256"));
                if (size < 0 || size > MaximumFileBytes || !paths.Add(path))
                {
                    throw new InvalidDataException("The protected release manifest contains an invalid file.");
                }

                total = checked(total + size);
                if (total > MaximumReleaseBytes)
                {
                    throw new InvalidDataException("The protected release exceeds its bounded size limit.");
                }

                files.Add(new ProtectedReleaseFile(path, size, sha256));
            }

            if (files.Count is < 8 or > MaximumFiles ||
                RequiredManifestFiles.Any(required => !paths.Contains(required)))
            {
                throw new InvalidDataException("The protected release manifest is incomplete.");
            }

            return new ProtectedReleaseManifest(files);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The protected release manifest JSON is invalid.", error);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("The protected release manifest size total is invalid.", error);
        }
    }

    private static async Task VerifyManifestFilesAsync(
        string root,
        IReadOnlyList<ProtectedReleaseFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var lease = ProductNoFollowSourceFileReader.Open(root, ResolveFile(root, file.Path));
            if (lease.Length != file.SizeBytes)
            {
                throw new InvalidDataException("A protected release file size does not match its signed manifest.");
            }

            var actual = await SHA256.HashDataAsync(lease.Stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(file.Sha256)))
            {
                throw new CryptographicException("A protected release file failed its signed hash verification.");
            }
        }
    }

    private static async Task VerifyChecksumsAsync(
        string root,
        IReadOnlyList<ProtectedReleaseFile> files,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(
                root,
                "SHA256SUMS.txt",
                MaximumChecksumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        string checksumText;
        try
        {
            checksumText = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The protected release checksum document is not valid UTF-8.",
                error);
        }

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in checksumText
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 67 || line[64] != ' ' || line[65] != '*')
            {
                throw new InvalidDataException("The protected release checksum document is invalid.");
            }

            var hash = ValidateSha256(line[..64]);
            var path = ValidateRelativeFilePath(line[66..]);
            if (!checksums.TryAdd(path, hash))
            {
                throw new InvalidDataException("The protected release checksum document contains a duplicate.");
            }
        }

        if (checksums.Count != files.Count || files.Any(file =>
                !checksums.TryGetValue(file.Path, out var hash) ||
                !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The protected release checksums do not match the signed manifest.");
        }
    }

    private static void VerifyExactFileSet(
        string root,
        IReadOnlyList<ProtectedReleaseFile> files)
    {
        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > MaximumFiles * 3)
                {
                    throw new InvalidDataException("The protected release tree has too many entries.");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The protected release contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative is "release-manifest.json" or "release-manifest.json.sig" or "SHA256SUMS.txt")
                {
                    continue;
                }

                relative = ValidateRelativeFilePath(relative);
                if (!observed.Add(relative))
                {
                    throw new InvalidDataException("The protected release contains a duplicate file path.");
                }
            }
        }

        if (!expected.SetEquals(observed))
        {
            throw new InvalidDataException("The protected release contains an unsigned, missing or unexpected file.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string root,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var lease = ProductNoFollowSourceFileReader.Open(root, ResolveFile(root, relativePath));
        if (lease.Length is < 1 || lease.Length > maximumBytes)
        {
            throw new InvalidDataException("A protected release trust document has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)lease.Length));
        await lease.Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static string NormalizeProtectedRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("The protected formal release root must be absolute.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.IndexOf('"') >= 0 ||
            !Directory.Exists(root))
        {
            throw new InvalidDataException("The protected formal release root is unavailable.");
        }

        WindowsProtectedProductPathSecurityValidator.RejectExistingReparsePoints(root);
        return root;
    }

    private static string ResolveFile(string root, string relativePath)
    {
        var relative = ValidateRelativeFilePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("A protected formal release file is missing.", path);
        }

        return path;
    }

    private static string ValidateRelativeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 ||
            value.StartsWith('/') || value.EndsWith('/') || value.Contains('\\') ||
            value.Contains(':') || value.IndexOf('"') >= 0 || value.Any(char.IsControl) ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("A protected release relative path is unsafe.");
        }

        return value;
    }

    private static string ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("A protected release SHA-256 value is invalid.");
        }

        return value;
    }

    private static void VerifyHash(byte[] content, string expectedHex, string label)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(content),
                Convert.FromHexString(expectedHex)))
        {
            throw new CryptographicException($"The pinned {label} fingerprint does not match.");
        }
    }

    private static JsonElement GetArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The protected release manifest is missing an array.");
        }

        return value;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The protected release manifest is missing a string.");
        }

        return value.GetString()
               ?? throw new InvalidDataException("The protected release manifest string is empty.");
    }

    private static int GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException("The protected release manifest is missing an integer.");
        }

        return result;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException("The protected release manifest is missing an integer.");
        }

        return result;
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("The protected release manifest is missing a Boolean.");
        }

        return value.GetBoolean();
    }

    private sealed record ProtectedReleaseManifest(IReadOnlyList<ProtectedReleaseFile> Files);

    private sealed record ProtectedReleaseFile(string Path, long SizeBytes, string Sha256);
}
