using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.Updater;

internal sealed record ProductLocalRepairTrustPolicy(
    string SubjectPublicKeyInfoSha256,
    string PublisherCertificateSha256,
    IReadOnlySet<string> AllowedPackageHosts)
{
    public static ProductLocalRepairTrustPolicy Production { get; } = new(
        "b85078f848fc4245cdbce277327fa0d5cbfd40b459ad907c82daa127d10517b8",
        "1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "muhun.tailafea21.ts.net",
        });
}

internal sealed record VerifiedProductLocalRelease(
    string ReleaseRoot,
    ProductUpdateManifest UpdateManifest,
    ProductFormalActivationLayout Layout);

internal static class ProductLocalFormalReleaseVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private const int MaximumReleaseManifestBytes = 1024 * 1024;
    private const int MaximumSignatureBytes = 1024;
    private const int MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumFormalReleaseBytes = 4L * 1024 * 1024 * 1024;
    private static readonly string[] RequiredRootFiles =
    [
        "release-manifest.json",
        "release-manifest.json.sig",
        "SHA256SUMS.txt",
        "publisher.cer",
        "update-manifest.json",
        "update-manifest.json.sig",
        "update-signing-public-key.json",
        "installed-version.v1.json",
    ];
    private static readonly string[] RequiredAuthenticodeFiles =
    [
        ProductFormalUpdateManifestValidator.ServiceEntryPoint,
        ProductFormalUpdateManifestValidator.GuiEntryPoint,
        ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
        "Install-MuhunMcsv.ps1",
        "Uninstall-MuhunMcsv.ps1",
        "Test-MuhunMcsvRelease.ps1",
        "tools/Uninstall-MuhunMcsv.ps1",
    ];
    private static readonly string[] RequiredNestedPayloadFiles =
    [
        ProductFormalUpdateManifestValidator.ServiceEntryPoint,
        ProductFormalUpdateManifestValidator.GuiEntryPoint,
        ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
        "tools/Uninstall-MuhunMcsv.ps1",
        "service-win-x64/update-signing-public-key.json",
        "providers/muhun.catalog/deployment.v1.json",
        "providers/muhun.catalog/muhun.catalog.mcsvp",
        "providers/muhun.catalog/publisher-public.pem",
    ];

    public static async Task<VerifiedProductLocalRelease> VerifyAsync(
        string releaseRoot,
        ProductLocalRepairTrustPolicy trustPolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default,
        Action<ProductFormalActivationLayout>? executableVersionValidator = null,
        Action<string, string>? executableSignerValidator = null,
        bool requireRunningFromReleaseUpdater = true)
    {
        ArgumentNullException.ThrowIfNull(trustPolicy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateSha256(trustPolicy.SubjectPublicKeyInfoSha256);
        ValidateSha256(trustPolicy.PublisherCertificateSha256);
        if (trustPolicy.AllowedPackageHosts.Count is < 1 or > 8)
        {
            throw new InvalidDataException("The local repair package-host trust set is invalid.");
        }

        var root = NormalizeReleaseRoot(releaseRoot);
        foreach (var relative in RequiredRootFiles)
        {
            _ = ResolveExistingFile(root, relative);
        }

        var certificateBytes = ReadBounded(
            ResolveExistingFile(root, "publisher.cer"),
            ProductUpdatePublicKeyLoader.MaximumDocumentBytes);
        VerifyHash(certificateBytes, trustPolicy.PublisherCertificateSha256, "publisher certificate");
        using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        using var publisherRsa = certificate.GetRSAPublicKey()
            ?? throw new CryptographicException("The publisher certificate is not RSA.");
        if (publisherRsa.KeySize < 3072)
        {
            throw new CryptographicException("The publisher certificate key is too weak.");
        }

        var publisherSpki = publisherRsa.ExportSubjectPublicKeyInfo();
        VerifyHash(publisherSpki, trustPolicy.SubjectPublicKeyInfoSha256, "publisher public key");
        var now = timeProvider.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            throw new CryptographicException("The pinned publisher certificate is outside its validity window.");
        }

        var releaseManifestBytes = ReadBounded(
            ResolveExistingFile(root, "release-manifest.json"),
            MaximumReleaseManifestBytes);
        var releaseSignature = ReadBounded(
            ResolveExistingFile(root, "release-manifest.json.sig"),
            MaximumSignatureBytes);
        VerifySignature(publisherRsa, releaseManifestBytes, releaseSignature, "release manifest");
        var release = ParseReleaseManifest(releaseManifestBytes);
        if (!string.Equals(
                release.PublisherCertificateSha256,
                trustPolicy.PublisherCertificateSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The release publisher binding is not pinned.");
        }

        await VerifyReleaseFilesAsync(root, release.Files, cancellationToken).ConfigureAwait(false);
        VerifyChecksums(root, release.Files);
        VerifyExactReleaseFileSet(root, release.Files);

        var publicKeyBytes = ReadBounded(
            ResolveExistingFile(root, "update-signing-public-key.json"),
            ProductUpdatePublicKeyLoader.MaximumDocumentBytes);
        using var updateRsa = ProductUpdatePublicKeyLoader.Load(publicKeyBytes, out var publicKeyDocument);
        if (!string.Equals(
                publicKeyDocument.SubjectPublicKeyInfoSha256,
                trustPolicy.SubjectPublicKeyInfoSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                publicKeyDocument.PublisherCertificateSha256,
                trustPolicy.PublisherCertificateSha256,
                StringComparison.OrdinalIgnoreCase) ||
            now < publicKeyDocument.NotBeforeUtc || now > publicKeyDocument.NotAfterUtc ||
            !CryptographicOperations.FixedTimeEquals(
                updateRsa.ExportSubjectPublicKeyInfo(),
                publisherSpki))
        {
            throw new CryptographicException("The update public-key document is not pinned to the publisher.");
        }

        var updateManifestBytes = ReadBounded(
            ResolveExistingFile(root, "update-manifest.json"),
            ProductUpdateManifestParser.MaximumManifestBytes);
        var updateSignature = ReadBounded(
            ResolveExistingFile(root, "update-manifest.json.sig"),
            MaximumSignatureBytes);
        var updateVerifier = new SignedProductUpdateManifestVerifier(
            new Dictionary<string, RSA>(StringComparer.Ordinal)
            {
                [publicKeyDocument.KeyId] = updateRsa,
            },
            trustPolicy.AllowedPackageHosts,
            timeProvider);
        var updateManifest = updateVerifier.Verify(updateManifestBytes, updateSignature);
        ProductFormalUpdateManifestValidator.Validate(updateManifest);
        ValidateManifestBinding(release, updateManifest, publicKeyDocument);

        var releaseFiles = release.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in updateManifest.Files)
        {
            if (!releaseFiles.TryGetValue(file.Path, out var formalFile) ||
                formalFile.SizeBytes != file.SizeBytes ||
                !string.Equals(formalFile.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update payload is not identically bound by the formal release.");
            }
        }

        foreach (var required in RequiredNestedPayloadFiles)
        {
            if (!updateManifest.Files.Any(file =>
                    string.Equals(file.Path, required, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The signed local repair payload is incomplete.");
            }
        }

        ValidateInstalledMetadata(root, updateManifest);
        var layout = ResolveLooseFormalLayout(root, updateManifest.Version);
        (executableVersionValidator ?? ProductActivationPathPolicy.ValidateMatchingProductVersions)(layout);
        var signerValidator = executableSignerValidator ?? ValidateExecutableSigner;
        foreach (var executable in new[] { layout.GuiPath, layout.ServicePath, layout.UpdaterPath })
        {
            signerValidator(executable, trustPolicy.PublisherCertificateSha256);
        }

        if (requireRunningFromReleaseUpdater)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) ||
                !string.Equals(
                    Path.GetFullPath(processPath),
                    layout.UpdaterPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Local repair must run from the verified release Updater.");
            }
        }

        return new VerifiedProductLocalRelease(root, updateManifest, layout);
    }

    internal static void ValidateExecutableSigner(string path, string expectedCertificateSha256)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var actual = signer.GetCertHashString(HashAlgorithmName.SHA256);
            if (!string.Equals(actual, expectedCertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("A formal product executable has an unexpected signer.");
            }
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException("A formal product executable signer could not be read.", exception);
        }
    }

    private static FormalReleaseManifest ParseReleaseManifest(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                GetInt32(root, "schemaVersion") != 1 ||
                !string.Equals(GetString(root, "productId"), "muhun.mcsv.manager", StringComparison.Ordinal) ||
                !GetBoolean(root, "installable") ||
                !string.Equals(GetString(root, "runtimeIdentifier"), "win-x64", StringComparison.Ordinal) ||
                GetString(root, "channel") is not ("stable" or "beta") ||
                !string.Equals(GetString(root, "signatureAlgorithm"), "rsa-pss-sha256", StringComparison.Ordinal) ||
                GetString(root, "publisherTrustMode") is not ("self-signed-local" or "public-ca") ||
                !string.Equals(
                    GetString(root, "entryPoint"),
                    ProductFormalUpdateManifestValidator.GuiEntryPoint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(root, "serviceEntryPoint"),
                    ProductFormalUpdateManifestValidator.ServiceEntryPoint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(root, "updaterEntryPoint"),
                    ProductFormalUpdateManifestValidator.UpdaterEntryPoint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The formal release manifest metadata is invalid.");
            }

            var version = GetString(root, "version");
            ProductUpdateManifestParser.ValidateVersion(version);
            var channel = GetString(root, "channel");
            if ((string.Equals(channel, "stable", StringComparison.Ordinal) && version.Contains('-')) ||
                version.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries).Any(identifier =>
                    identifier.Equals("preview", StringComparison.OrdinalIgnoreCase) ||
                    identifier.Equals("alpha", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The formal release channel and version are inconsistent.");
            }
            var publisherCertificateSha256 = GetString(root, "publisherCertificateSha256");
            ValidateSha256(publisherCertificateSha256);
            var keyId = GetString(root, "keyId");

            var updatePublicKey = GetObject(root, "updatePublicKey");
            var updateManifest = GetObject(root, "updateManifest");
            if (!string.Equals(
                    GetString(updatePublicKey, "path"),
                    "update-signing-public-key.json",
                    StringComparison.Ordinal) ||
                !string.Equals(GetString(updateManifest, "path"), "update-manifest.json", StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(updateManifest, "signaturePath"),
                    "update-manifest.json.sig",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The formal release update-document paths are invalid.");
            }

            var authenticode = GetArray(root, "authenticodeFiles")
                .EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : throw new InvalidDataException("The Authenticode list is invalid."))
                .ToArray();
            if (authenticode.Length != RequiredAuthenticodeFiles.Length ||
                authenticode.Distinct(StringComparer.OrdinalIgnoreCase).Count() != authenticode.Length ||
                RequiredAuthenticodeFiles.Any(required =>
                    !authenticode.Contains(required, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The formal release Authenticode list is incomplete.");
            }

            var filesElement = GetArray(root, "files");
            var files = new List<FormalReleaseFile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The formal release file list is invalid.");
                }

                var path = GetString(item, "path");
                ProductUpdatePath.ValidateRelativeFilePath(path);
                var size = GetInt64(item, "sizeBytes");
                var sha256 = GetString(item, "sha256");
                ValidateSha256(sha256);
                if (size < 0 || size > ProductUpdateManifestParser.MaximumPackageBytes || !paths.Add(path))
                {
                    throw new InvalidDataException("The formal release contains an invalid or duplicate file.");
                }

                totalSize = checked(totalSize + size);
                if (totalSize > MaximumFormalReleaseBytes)
                {
                    throw new InvalidDataException("The formal release exceeds the bounded size limit.");
                }

                files.Add(new FormalReleaseFile(path, size, sha256));
            }

            if (files.Count is < 8 or > ProductUpdateManifestParser.MaximumFiles)
            {
                throw new InvalidDataException("The formal release file list is missing or too large.");
            }

            return new FormalReleaseManifest(
                version,
                channel,
                keyId,
                publisherCertificateSha256,
                files);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The formal release manifest JSON is invalid.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The formal release total size is invalid.", exception);
        }
    }

    private static async Task VerifyReleaseFilesAsync(
        string root,
        IReadOnlyList<FormalReleaseFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveExistingFile(root, entry.Path);
            await using var stream = OpenVerifiedRead(path, entry.SizeBytes);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(entry.Sha256)))
            {
                throw new CryptographicException("A formal release file failed its signed hash verification.");
            }
        }
    }

    private static void VerifyChecksums(string root, IReadOnlyList<FormalReleaseFile> files)
    {
        var checksumBytes = ReadBounded(ResolveExistingFile(root, "SHA256SUMS.txt"), MaximumChecksumBytes);
        string checksumText;
        try
        {
            checksumText = StrictUtf8.GetString(checksumBytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The release checksum document is not valid UTF-8.",
                error);
        }

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in checksumText
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Length < 67 || rawLine[64] != ' ' || rawLine[65] != '*')
            {
                throw new InvalidDataException("The release checksum document format is invalid.");
            }

            var hash = rawLine[..64];
            var path = rawLine[66..];
            ValidateSha256(hash);
            ProductUpdatePath.ValidateRelativeFilePath(path);
            if (!checksums.TryAdd(path, hash))
            {
                throw new InvalidDataException("The release checksum document contains a duplicate path.");
            }
        }

        if (checksums.Count != files.Count || files.Any(file =>
                !checksums.TryGetValue(file.Path, out var hash) ||
                !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The release checksum document does not match the signed manifest.");
        }
    }

    private static void VerifyExactReleaseFileSet(string root, IReadOnlyList<FormalReleaseFile> files)
    {
        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var entryCount = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectExistingReparsePoints(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entryCount > ProductUpdateManifestParser.MaximumFiles * 3)
                {
                    throw new InvalidDataException("The formal release tree exceeds its bounded entry limit.");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The formal release cannot contain a reparse point.");
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

                ProductUpdatePath.ValidateRelativeFilePath(relative);
                if (!observed.Add(relative))
                {
                    throw new InvalidDataException("The formal release contains a duplicate file path.");
                }
            }
        }

        if (!expected.SetEquals(observed))
        {
            throw new InvalidDataException("The formal release contains an unsigned, missing or unexpected file.");
        }
    }

    private static void ValidateManifestBinding(
        FormalReleaseManifest release,
        ProductUpdateManifest update,
        ProductUpdatePublicKeyDocument publicKey)
    {
        if (!string.Equals(update.Version, release.Version, StringComparison.Ordinal) ||
            !string.Equals(update.Channel, release.Channel, StringComparison.Ordinal) ||
            !string.Equals(update.KeyId, release.KeyId, StringComparison.Ordinal) ||
            !string.Equals(update.KeyId, publicKey.KeyId, StringComparison.Ordinal) ||
            !string.Equals(
                update.EntryPoint,
                ProductFormalUpdateManifestValidator.GuiEntryPoint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update manifest does not match the formal release manifest.");
        }
    }

    private static void ValidateInstalledMetadata(string root, ProductUpdateManifest manifest)
    {
        var bytes = ReadBounded(ResolveExistingFile(root, "installed-version.v1.json"), 16 * 1024);
        try
        {
            var metadata = JsonSerializer.Deserialize<ProductInstalledVersionMetadata>(
                    bytes,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("The formal installed-version metadata is empty.");
            if (metadata.SchemaVersion != 1 ||
                !string.Equals(metadata.ProductId, "muhun.mcsv.manager", StringComparison.Ordinal) ||
                !string.Equals(metadata.Version, manifest.Version, StringComparison.Ordinal) ||
                !string.Equals(metadata.EntryPoint, manifest.EntryPoint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The formal installed-version metadata is inconsistent.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The formal installed-version metadata JSON is invalid.", exception);
        }
    }

    private static ProductFormalActivationLayout ResolveLooseFormalLayout(string root, string version)
    {
        var gui = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(root, "gui-win-x64", "Muhun MCSV Manager.exe"),
            "Muhun MCSV Manager.exe");
        var service = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(root, "service-win-x64", "Muhun MCSV Service.exe"),
            "Muhun MCSV Service.exe");
        var updater = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(root, "updater-win-x64", "Muhun MCSV Updater.exe"),
            "Muhun MCSV Updater.exe");
        return new ProductFormalActivationLayout(root, version, gui, service, updater);
    }

    private static string NormalizeReleaseRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("The formal release root must be absolute.");
        }

        var root = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.IndexOf('"') >= 0 || !Directory.Exists(root))
        {
            throw new InvalidDataException("The formal release root must be an existing safe local directory.");
        }

        RejectExistingReparsePoints(root);
        return root;
    }

    private static string ResolveExistingFile(string root, string relativePath)
    {
        var path = ProductUpdatePath.ResolveUnderRoot(root, relativePath);
        RejectExistingReparsePoints(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("A required formal release file is missing.", path);
        }

        return path;
    }

    private static FileStream OpenVerifiedRead(string path, long expectedSize)
    {
        RejectExistingReparsePoints(path);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            stream.Dispose();
            throw new InvalidDataException("A formal release file size does not match its signed manifest.");
        }

        return stream;
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        RejectExistingReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 || stream.Length > maximumBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A local repair input file has an invalid size or identity.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void VerifySignature(RSA rsa, byte[] content, byte[] signature, string label)
    {
        if (rsa.KeySize < 3072 || signature.Length != rsa.KeySize / 8 ||
            !rsa.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException($"The {label} RSA-PSS signature is invalid.");
        }
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

    private static void ValidateSha256(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("A required SHA-256 value is invalid.");
        }
    }

    private static JsonElement GetObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The formal release manifest is missing an object.");
        }

        return value;
    }

    private static JsonElement GetArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The formal release manifest is missing an array.");
        }

        return value;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The formal release manifest is missing a string.");
        }

        return value.GetString() ?? throw new InvalidDataException("The formal release manifest string is empty.");
    }

    private static int GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException("The formal release manifest is missing an integer.");
        }

        return result;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException("The formal release manifest is missing an integer.");
        }

        return result;
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("The formal release manifest is missing a Boolean.");
        }

        return value.GetBoolean();
    }

    private static void RejectExistingReparsePoints(string path)
        => ProductActivationPathPolicy.RejectExistingReparsePoints(path);

    private sealed record FormalReleaseManifest(
        string Version,
        string Channel,
        string KeyId,
        string PublisherCertificateSha256,
        IReadOnlyList<FormalReleaseFile> Files);

    private sealed record FormalReleaseFile(string Path, long SizeBytes, string Sha256);
}
