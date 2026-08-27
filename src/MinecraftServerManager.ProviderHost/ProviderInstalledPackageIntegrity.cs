using System.Security.Cryptography;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost;

public static class ProviderPackageIntegrityVerifier
{
    public static async ValueTask VerifyAsync(
        ProviderHostLayout layout,
        ProviderRegistration registration,
        CancellationToken cancellationToken = default)
        => _ = await ProviderInstalledPackageIntegrity.VerifyAsync(
                layout,
                registration,
                cancellationToken)
            .ConfigureAwait(false);
}

internal sealed record VerifiedProviderPackage(string InstallDirectory, string ExecutablePath);

/// <summary>
/// Revalidates the complete signed payload immediately before execution. This prevents an
/// installed executable or dependency from being silently replaced after package installation.
/// </summary>
internal static class ProviderInstalledPackageIntegrity
{
    public static VerifiedProviderPackage Verify(
        ProviderHostLayout layout,
        ProviderRegistration registration)
    {
        var plan = CreatePlan(layout, registration);
        foreach (var file in plan.PayloadFiles)
        {
            using var input = OpenPayload(file.FullPath);
            var actual = SHA256.HashData(input);
            VerifyDigest(actual, file.ExpectedSha256);
        }

        return plan.Package;
    }

    public static async ValueTask<VerifiedProviderPackage> VerifyAsync(
        ProviderHostLayout layout,
        ProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        var plan = CreatePlan(layout, registration);
        foreach (var file in plan.PayloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = OpenPayload(file.FullPath);
            var actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            VerifyDigest(actual, file.ExpectedSha256);
        }

        return plan.Package;
    }

    private static IntegrityPlan CreatePlan(
        ProviderHostLayout layout,
        ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(registration);
        ProviderRegistrationValidator.ValidateAndThrow(registration, layout);

        var installDirectory = ProviderPathSafety.ResolveOwnedRelativePath(
            layout.Root,
            registration.InstallRelativePath);
        ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(layout.Root, installDirectory);
        ProviderPathSafety.EnsureTreeHasNoReparsePoints(
            installDirectory,
            ProductProviderManifestValidator.MaximumFiles + 1);

        var manifestPath = Path.Combine(installDirectory, ProviderPackageInstaller.ManifestFileName);
        if (!File.Exists(manifestPath) ||
            File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Installed provider manifest is missing or unsafe.");
        }

        var files = Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(installDirectory, path).Replace('\\', '/'),
            })
            .ToArray();
        if (files.Length != registration.Manifest.FileSha256.Count + 1)
        {
            throw new InvalidDataException("Installed provider payload differs from its signed file table.");
        }

        var payloadFiles = new List<PayloadFile>(registration.Manifest.FileSha256.Count);
        foreach (var file in files)
        {
            ProviderPathSafety.EnsureExistingPathHasNoReparsePoints(installDirectory, file.FullPath);
            if (file.RelativePath.Equals(
                    ProviderPackageInstaller.ManifestFileName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var expectedHex = registration.Manifest.FileSha256
                .Where(pair => pair.Key.Equals(file.RelativePath, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .SingleOrDefault()
                ?? throw new InvalidDataException(
                    "Installed provider payload differs from its signed file table.");
            payloadFiles.Add(new PayloadFile(file.FullPath, expectedHex));
        }

        var executable = ProviderPathSafety.ResolveOwnedRelativePath(
            installDirectory,
            registration.Manifest.EntryPoint);
        var executableInfo = new FileInfo(executable);
        if (!executableInfo.Exists || executableInfo.Length < 1 ||
            executableInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Provider executable is missing or unsafe.");
        }

        return new IntegrityPlan(
            new VerifiedProviderPackage(installDirectory, executable),
            payloadFiles);
    }

    private static FileStream OpenPayload(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void VerifyDigest(byte[] actual, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(expected);
        if (!matches)
        {
            throw new CryptographicException(
                "Installed provider payload failed its signed integrity check.");
        }
    }

    private sealed record PayloadFile(string FullPath, string ExpectedSha256);

    private sealed record IntegrityPlan(
        VerifiedProviderPackage Package,
        IReadOnlyList<PayloadFile> PayloadFiles);
}
