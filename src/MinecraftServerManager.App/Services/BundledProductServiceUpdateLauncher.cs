using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MinecraftServerManager.App.Services;

internal enum BundledProductServiceUpdateOutcome
{
    Completed = 0,
    Cancelled,
    ReleaseLayoutUnavailable,
    PublisherVerificationFailed,
    UpdateFailed,
}

internal sealed record BundledProductServiceUpdateResult(
    BundledProductServiceUpdateOutcome Outcome,
    int? ExitCode = null)
{
    public bool Succeeded => Outcome == BundledProductServiceUpdateOutcome.Completed;
}

internal interface IBundledProductServiceUpdateLauncher
{
    Task<BundledProductServiceUpdateResult> UpdateAsync(
        CancellationToken cancellationToken = default);
}

internal interface IElevatedProductUpdaterProcessRunner
{
    Task<int> RunAsync(
        string updaterPath,
        string releaseRoot,
        CancellationToken cancellationToken);
}

internal sealed record ProtectedFormalReleaseStage(string ReleaseRoot);

internal interface IProtectedFormalReleaseStager
{
    Task<ProtectedFormalReleaseStage> StageAsync(
        string sourceReleaseRoot,
        string expectedProductVersion,
        CancellationToken cancellationToken);

    Task TryCleanupAsync(ProtectedFormalReleaseStage stage);
}

internal interface IProtectedFormalReleaseVerifier
{
    Task<string> VerifyAsync(
        string protectedReleaseRoot,
        string expectedProductVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Locates the currently running loose formal release, asks the Windows Shell file-operation
/// broker to copy the complete release into a protected Program Files staging directory, verifies
/// that protected copy, and only then elevates the protected updater. No executable beneath the
/// user-writable source release is ever elevated.
/// </summary>
internal sealed class BundledProductServiceUpdateLauncher : IBundledProductServiceUpdateLauncher
{
    internal const string PublisherCertificateSha256 =
        "1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71";
    internal const string RepairCommand = "--repair-product-service";
    internal const string ReleaseRootArgument = "--release-root";

    private readonly Func<string?> _processPathResolver;
    private readonly IProtectedFormalReleaseStager _releaseStager;
    private readonly IProtectedFormalReleaseVerifier _protectedReleaseVerifier;
    private readonly IElevatedProductUpdaterProcessRunner _processRunner;
    private readonly string _expectedProductVersion;

    public BundledProductServiceUpdateLauncher(string expectedProductVersion)
        : this(
            () => Environment.ProcessPath,
            new WindowsShellProtectedFormalReleaseStager(),
            new PinnedProtectedFormalReleaseVerifier(),
            new ElevatedProductUpdaterProcessRunner(),
            expectedProductVersion)
    {
    }

    internal BundledProductServiceUpdateLauncher(
        Func<string?> processPathResolver,
        IProtectedFormalReleaseStager releaseStager,
        IProtectedFormalReleaseVerifier protectedReleaseVerifier,
        IElevatedProductUpdaterProcessRunner processRunner,
        string expectedProductVersion)
    {
        _processPathResolver = processPathResolver
            ?? throw new ArgumentNullException(nameof(processPathResolver));
        _releaseStager = releaseStager ?? throw new ArgumentNullException(nameof(releaseStager));
        _protectedReleaseVerifier = protectedReleaseVerifier
            ?? throw new ArgumentNullException(nameof(protectedReleaseVerifier));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (string.IsNullOrWhiteSpace(expectedProductVersion))
        {
            throw new ArgumentException("Product version is required.", nameof(expectedProductVersion));
        }

        _expectedProductVersion = expectedProductVersion.Trim();
    }

    public async Task<BundledProductServiceUpdateResult> UpdateAsync(
        CancellationToken cancellationToken = default)
    {
        string sourceReleaseRoot;
        try
        {
            (_, sourceReleaseRoot) = ResolveLooseReleaseLayout(_processPathResolver());
        }
        catch (Exception error) when (error is
            ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.ReleaseLayoutUnavailable);
        }

        ProtectedFormalReleaseStage stage;
        try
        {
            stage = await _releaseStager.StageAsync(
                    sourceReleaseRoot,
                    _expectedProductVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsUserCancellation(error))
        {
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.Cancelled);
        }
        catch (Exception error) when (error is
            ArgumentException or IOException or InvalidDataException or
            UnauthorizedAccessException or Win32Exception or COMException)
        {
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.UpdateFailed);
        }

        string updaterPath;
        try
        {
            updaterPath = await _protectedReleaseVerifier.VerifyAsync(
                    stage.ReleaseRoot,
                    _expectedProductVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _releaseStager.TryCleanupAsync(stage).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            await _releaseStager.TryCleanupAsync(stage).ConfigureAwait(false);
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.PublisherVerificationFailed);
        }

        try
        {
            var exitCode = await _processRunner
                .RunAsync(updaterPath, stage.ReleaseRoot, cancellationToken)
                .ConfigureAwait(false);
            return exitCode == 0
                ? new BundledProductServiceUpdateResult(
                    BundledProductServiceUpdateOutcome.Completed,
                    exitCode)
                : new BundledProductServiceUpdateResult(
                    BundledProductServiceUpdateOutcome.UpdateFailed,
                    exitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsUserCancellation(error))
        {
            await _releaseStager.TryCleanupAsync(stage).ConfigureAwait(false);
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.Cancelled);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            await _releaseStager.TryCleanupAsync(stage).ConfigureAwait(false);
            return new BundledProductServiceUpdateResult(
                BundledProductServiceUpdateOutcome.UpdateFailed);
        }
    }

    private static bool IsUserCancellation(Exception error)
        => error is Win32Exception { NativeErrorCode: 1223 }
           || error is COMException { HResult: unchecked((int)0x800704C7) };

    internal static (string UpdaterPath, string ReleaseRoot) ResolveLooseReleaseLayout(
        string? guiExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(guiExecutablePath) ||
            !Path.IsPathFullyQualified(guiExecutablePath))
        {
            throw new InvalidDataException("The GUI executable path is unavailable.");
        }

        var guiPath = Path.GetFullPath(guiExecutablePath);
        RequireRegularFile(guiPath, "Muhun MCSV Manager.exe");
        var guiRoot = Path.GetDirectoryName(guiPath)
            ?? throw new InvalidDataException("The GUI payload directory is unavailable.");
        if (!string.Equals(
                Path.GetFileName(guiRoot),
                "gui-win-x64",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The GUI is not running from a formal release payload.");
        }

        var releaseRoot = Directory.GetParent(guiRoot)?.FullName
            ?? throw new InvalidDataException("The formal release root is unavailable.");
        RejectExistingReparsePoints(releaseRoot);
        // Installed A/B slots deliberately omit the signed raw-release repair inputs. Never
        // reinterpret a managed slot as a loose release or try to mutate it in place.
        if (string.Equals(
                Directory.GetParent(releaseRoot)?.Name,
                "versions",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A managed version slot is not a loose formal release.");
        }

        var updaterPath = Path.Combine(
            releaseRoot,
            "updater-win-x64",
            "Muhun MCSV Updater.exe");
        RequireRegularFile(updaterPath, "Muhun MCSV Updater.exe");
        foreach (var required in new[]
                 {
                     "publisher.cer",
                     "release-manifest.json",
                     "release-manifest.json.sig",
                     "update-manifest.json",
                     "update-manifest.json.sig",
                     "update-signing-public-key.json",
                 })
        {
            RequireRegularFile(Path.Combine(releaseRoot, required), required);
        }

        return (updaterPath, releaseRoot);
    }

    private static void RequireRegularFile(string path, string requiredName)
    {
        var fullPath = Path.GetFullPath(path);
        RejectExistingReparsePoints(fullPath);
        if (!string.Equals(Path.GetFileName(fullPath), requiredName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A required formal-release file is missing or unsafe.");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Formal-release paths cannot traverse a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }
}

internal sealed class ElevatedProductUpdaterProcessRunner : IElevatedProductUpdaterProcessRunner
{
    public async Task<int> RunAsync(
        string updaterPath,
        string releaseRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(BundledProductServiceUpdateLauncher.RepairCommand);
        startInfo.ArgumentList.Add(BundledProductServiceUpdateLauncher.ReleaseRootArgument);
        startInfo.ArgumentList.Add(releaseRoot);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated product updater could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

internal static class WindowsProductPublisherVerifier
{
    private const int CertificateUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertificateChainUnavailable = unchecked((int)0x800B010A);
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void Verify(string executablePath, string expectedProductVersion)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Product publisher verification requires Windows.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        var fileInfo = FileVersionInfo.GetVersionInfo(fullPath);
        var productVersion = fileInfo.ProductVersion?.Split('+', 2)[0] ?? string.Empty;
        if (!string.Equals(productVersion, expectedProductVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The bundled updater version does not match the GUI version.");
        }

#pragma warning disable SYSLIB0057
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
        var signerHash = Convert.ToHexString(SHA256.HashData(signer.RawData));
        if (!string.Equals(
                signerHash,
                BundledProductServiceUpdateLauncher.PublisherCertificateSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The bundled updater publisher does not match X MCSV.");
        }

        using var file = new WinTrustFileInfo(fullPath);
        var data = new WinTrustData(file.Pointer);
        var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        var pinnedSelfSignedPublisher = signer.SubjectName.RawData.AsSpan()
            .SequenceEqual(signer.IssuerName.RawData);
        if (!IsAcceptableTrustResult(result, pinnedSelfSignedPublisher))
        {
            throw new Win32Exception(result, "The bundled updater Authenticode signature is invalid.");
        }
    }

    /// <summary>
    /// The caller has already verified the updater byte-for-byte through the pinned RSA-PSS
    /// release manifest. A clean PC can therefore accept the pinned self-signed certificate when
    /// Windows reports only a missing private trust root. Bad digests and every other policy error
    /// remain fail-closed.
    /// </summary>
    internal static bool IsAcceptableTrustResult(int result, bool pinnedSelfSignedPublisher)
        => result == 0 ||
           pinnedSelfSignedPublisher &&
           result is CertificateUntrustedRoot or CertificateChainUnavailable;

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UserInterfaceChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UserContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>());
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UserInterfaceChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0;
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            UserContext = 0;
        }
    }

    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly IntPtr _filePath;
        public IntPtr Pointer { get; }

        public WinTrustFileInfo(string filePath)
        {
            _filePath = Marshal.StringToCoTaskMemUni(filePath);
            var native = new NativeWinTrustFileInfo
            {
                StructureSize = checked((uint)Marshal.SizeOf<NativeWinTrustFileInfo>()),
                FilePath = _filePath,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeWinTrustFileInfo>());
            Marshal.StructureToPtr(native, Pointer, fDeleteOld: false);
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(Pointer);
            }
            if (_filePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_filePath);
            }
        }
    }
}
