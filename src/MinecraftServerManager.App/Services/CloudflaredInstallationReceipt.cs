using System.Security.Cryptography;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// DPAPI-protected evidence that the managed cloudflared executable was produced by the
/// first-party bootstrap flow and matched GitHub's release size and SHA-256 metadata.
/// </summary>
internal sealed record CloudflaredInstallationReceipt(
    string ReleaseTag,
    string AssetIdentity,
    long Size,
    string Sha256,
    DateTimeOffset InstalledAtUtc)
{
    internal const string CanonicalAssetIdentity =
        "github.com/cloudflare/cloudflared:cloudflared-windows-amd64.exe";

    public static CloudflaredInstallationReceipt Create(
        CloudflaredBootstrapResult result,
        DateTimeOffset installedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        var receipt = new CloudflaredInstallationReceipt(
            result.Version?.Trim() ?? string.Empty,
            CanonicalAssetIdentity,
            result.Size,
            result.Sha256?.Trim().ToLowerInvariant() ?? string.Empty,
            installedAt.ToUniversalTime());
        ValidateAndThrow(receipt);
        return receipt;
    }

    internal static bool IsValid(CloudflaredInstallationReceipt? receipt)
    {
        if (receipt is null ||
            string.IsNullOrWhiteSpace(receipt.ReleaseTag) ||
            receipt.ReleaseTag.Length > 100 ||
            !string.Equals(receipt.ReleaseTag, receipt.ReleaseTag.Trim(), StringComparison.Ordinal) ||
            receipt.ReleaseTag.Any(char.IsControl) ||
            !string.Equals(
                receipt.AssetIdentity,
                CanonicalAssetIdentity,
                StringComparison.Ordinal) ||
            receipt.Size is < 1 or > CloudflaredBootstrapService.MaximumExecutableBytes ||
            receipt.Sha256 is not { Length: 64 } sha256 ||
            sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            receipt.InstalledAtUtc == default ||
            receipt.InstalledAtUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        return true;
    }

    internal static void ValidateAndThrow(CloudflaredInstallationReceipt? receipt)
    {
        if (!IsValid(receipt))
        {
            throw new InvalidDataException("cloudflared 安裝收據格式無效。");
        }
    }

    public override string ToString() => $"cloudflared {ReleaseTag} ({AssetIdentity})";
}

/// <summary>
/// Keeps the exact verified executable open without write/delete sharing until the named
/// connector has started. Returning only a path would reintroduce a verify-to-execute race.
/// </summary>
internal sealed class CloudflaredExecutableVerificationLease : IAsyncDisposable
{
    private FileStream? _lockedStream;

    internal CloudflaredExecutableVerificationLease(
        string executablePath,
        FileStream? lockedStream = null)
    {
        ExecutablePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(executablePath)
                ? throw new ArgumentException("Verified executable path is required.", nameof(executablePath))
                : executablePath);
        _lockedStream = lockedStream;
    }

    public string ExecutablePath { get; }

    public ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _lockedStream, null);
        return stream is null ? ValueTask.CompletedTask : stream.DisposeAsync();
    }
}

/// <summary>
/// Verifies the Named Tunnel executable immediately before launch. Quick Tunnel retains its
/// existing explicit-path behavior; fixed-domain credentials may run only the MCSV-managed,
/// receipt-bound executable.
/// </summary>
internal static class CloudflaredNamedTunnelExecutableVerifier
{
    internal static string GetManagedExecutablePath(string applicationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        var root = Path.GetFullPath(applicationRoot);
        return SafePath.CombineUnderRoot(
            root,
            "tools",
            "cloudflared",
            "cloudflared.exe");
    }

    internal static async Task<CloudflaredExecutableVerificationLease> VerifyAsync(
        string applicationRoot,
        string executablePath,
        CloudflaredInstallationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        CloudflaredInstallationReceipt.ValidateAndThrow(receipt);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(applicationRoot);
        var expectedPath = GetManagedExecutablePath(root);
        var candidatePath = Path.GetFullPath(executablePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(candidatePath, expectedPath, comparison))
        {
            throw new UnauthorizedAccessException(
                "Cloudflare Named Tunnel 只能使用 MCSV 管理的 tools\\cloudflared\\cloudflared.exe。");
        }

        string verifiedPath;
        try
        {
            verifiedPath = SafePath.EnsureNoReparsePointsUnderRoot(root, candidatePath);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            UnauthorizedAccessException or
            IOException)
        {
            throw new InvalidDataException(
                "MCSV 管理的 cloudflared.exe 不存在或路徑包含連結／重新解析點。",
                exception);
        }

        byte[]? expectedDigest = null;
        byte[]? actualDigest = null;
        FileStream? stream = null;
        try
        {
            expectedDigest = Convert.FromHexString(receipt.Sha256);
            stream = new FileStream(
                verifiedPath,
                FileMode.Open,
                FileAccess.Read,
                // Deny write/delete sharing so the verified file cannot be replaced between
                // this digest and Process.Start. Other readers, including the Windows image
                // loader, remain allowed.
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != receipt.Size)
            {
                throw new InvalidDataException(
                    "cloudflared.exe 大小與受保護的安裝收據不一致；請重新安全下載。");
            }

            actualDigest = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (stream.Length != receipt.Size ||
                !CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException(
                    "cloudflared.exe SHA-256 與受保護的安裝收據不一致；請重新安全下載。");
            }

            // Re-check every managed component after hashing. The open handle prevents a normal
            // writer or rename from replacing the file during the size/digest calculation.
            SafePath.EnsureNoReparsePointsUnderRoot(root, verifiedPath);
            var lease = new CloudflaredExecutableVerificationLease(verifiedPath, stream);
            stream = null;
            return lease;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("cloudflared 安裝收據的 SHA-256 無效。", exception);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (expectedDigest is not null) CryptographicOperations.ZeroMemory(expectedDigest);
            if (actualDigest is not null) CryptographicOperations.ZeroMemory(actualDigest);
        }
    }
}
