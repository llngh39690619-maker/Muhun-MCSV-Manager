using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Verifies the downloaded FTB Windows installer with the native Authenticode policy provider and
/// then pins the public signer identity and Code Signing EKU. It never imports certificates or
/// changes a Windows trust store.
/// </summary>
public sealed class WindowsFtbExecutableSignatureVerifier : IFtbExecutableSignatureVerifier
{
    private const string ExpectedSignerName = "Feed The Beast Ltd";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public Task VerifyAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("FTB Installer Authenticode 驗證僅支援 Windows。");
        }

        var fullPath = Path.GetFullPath(executablePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("找不到 FTB Installer。", fullPath);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("FTB Installer 不得是 reparse point。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fileInfo = new WinTrustFileInfo(fullPath);
        try
        {
            var trustData = new WinTrustData(fileInfo.Pointer);
            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            if (result != 0)
            {
                throw new InvalidDataException(
                    $"FTB Installer 的 Authenticode 驗證失敗：0x{result:X8} ({new Win32Exception(result).Message})。");
            }
        }
        finally
        {
            fileInfo.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        X509Certificate2 signer;
        try
        {
#pragma warning disable SYSLIB0057
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
        }
        catch (Exception exception) when (exception is CryptographicException or IOException)
        {
            throw new InvalidDataException("無法讀取 FTB Installer 的簽章憑證。", exception);
        }

        using (signer)
        {
            var signerName = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!signerName.Equals(ExpectedSignerName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"FTB Installer 簽章者不符：預期 {ExpectedSignerName}，實際 {signerName}。");
            }

            var hasCodeSigningEku = signer.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == CodeSigningEku);
            if (!hasCodeSigningEku)
            {
                throw new InvalidDataException("FTB Installer 的簽章憑證不含 Code Signing EKU。");
            }
        }

        return Task.CompletedTask;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustData>());
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0; // WTD_REVOKE_NONE; Windows policy may still enforce chain policy.
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            UiContext = 0;
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
                StructSize = checked((uint)Marshal.SizeOf<NativeWinTrustFileInfo>()),
                FilePath = _filePath,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeWinTrustFileInfo>());
            Marshal.StructureToPtr(native, Pointer, fDeleteOld: false);
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer);
            if (_filePath != IntPtr.Zero) Marshal.FreeCoTaskMem(_filePath);
        }
    }
}
