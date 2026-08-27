using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Service;

public static class ProductNamedPipeFactory
{
    public const string OperatorsGroupName = "Muhun MCSV Operators";
    public const int MaximumServerInstances = 8;

    public static NamedPipeServerStream CreateServer(bool firstInstance = true)
        => CreateServer(
            layout: null,
            MinecraftServerManager.Contracts.ProductApiProtocol.IpcPackage,
            firstInstance);

    public static NamedPipeServerStream CreateServer(
        ProductDataLayout? layout,
        bool firstInstance = true)
        => CreateServer(
            layout,
            MinecraftServerManager.Contracts.ProductApiProtocol.IpcPackage,
            firstInstance);

    public static NamedPipeServerStream CreateServer(
        ProductDataLayout? layout,
        string pipeName,
        bool firstInstance = true)
    {
        if (!ProductServiceOptionsValidator.IsValidIpcPipeName(pipeName))
        {
            throw new ArgumentException("IPC pipe name is invalid.", nameof(pipeName));
        }

        if (!WindowsServiceHelpers.IsWindowsService())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: MaximumServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous |
                PipeOptions.CurrentUserOnly |
                (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                inBufferSize: 8 * 1024,
                outBufferSize: 8 * 1024);
        }

        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Windows Service identity does not have a SID.");
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier operatorsSid;
        try
        {
            operatorsSid = (SecurityIdentifier)new NTAccount(
                    Environment.MachineName,
                    OperatorsGroupName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException exception)
        {
            throw new InvalidOperationException(
                $"Required local group '{OperatorsGroupName}' is missing. Run the signed product installer first.",
                exception);
        }

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentSid);
        security.AddAccessRule(new PipeAccessRule(
            currentSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administratorsSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            operatorsSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        if (layout is not null)
        {
            var installerSid = ReadInstallerOperatorSid(layout);
            security.AddAccessRule(new PipeAccessRule(
                installerSid,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 8 * 1024,
            outBufferSize: 8 * 1024,
            security,
            HandleInheritability.None,
            additionalAccessRights: (PipeAccessRights)0);
    }

    internal static SecurityIdentifier ReadInstallerOperatorSid(ProductDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = Path.GetFullPath(layout.Root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            layout.Root,
            ProductLocalIpcAccess.InstallerOperatorSidRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Installer operator SID path escaped the product data root.");
        }

        for (FileSystemInfo? cursor = new FileInfo(path); cursor is not null; cursor = cursor switch
             {
                 FileInfo file => file.Directory,
                 DirectoryInfo directory => directory.Parent,
                 _ => null,
             })
        {
            if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Installer operator SID path traverses a reparse point.");
            }
        }

        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException(
                "Installer operator SID binding is missing. Repair the formal installation.",
                path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256,
            FileOptions.SequentialScan);
        if (stream.Length is < 5 or > ProductLocalIpcAccess.MaximumSidFileBytes)
        {
            throw new InvalidDataException("Installer operator SID binding has an invalid size.");
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 256, false);
        var value = reader.ReadToEnd().Trim();
        if (!value.All(character => character <= 0x7f))
        {
            throw new InvalidDataException("Installer operator SID binding is not ASCII.");
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Installer operator SID binding is invalid.", exception);
        }

        if (!sid.IsAccountSid() ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
        {
            throw new InvalidDataException("Installer operator SID must identify one non-broad account.");
        }

        return sid;
    }
}
