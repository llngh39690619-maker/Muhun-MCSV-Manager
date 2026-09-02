using System.Security.Principal;
using MinecraftServerManager.Contracts;
using Microsoft.Win32;

namespace MinecraftServerManager.Updater;

internal sealed record ProductManagedInstallation(
    string InstallRoot,
    string DataRoot,
    string ExchangeRoot,
    string ActiveVersion,
    string ServiceVersion,
    string ActiveServicePath);

internal static class ProductManagedInstallationResolver
{
    private const string ProductId = "muhun.mcsv.manager";
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\MuhunMCSV";
    private const string ArpRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuhunMCSV";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static ProductManagedInstallation Resolve(string publisherCertificateSha256)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Product Service repair requires Windows.");
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(ServiceRegistryPath, writable: false)
            ?? throw new InvalidOperationException("The managed Muhun MCSV Service is not installed.");
        if (!string.Equals(
                service.GetValue("ObjectName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                @"NT SERVICE\MuhunMCSV",
                StringComparison.OrdinalIgnoreCase) ||
            service.GetValue("Start", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int start ||
            start != 2)
        {
            throw new InvalidDataException("The installed Service identity is not managed by X MCSV.");
        }

        var imagePath = service.GetValue(
                "ImagePath",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new InvalidDataException("The installed Service image path is missing.");
        var (servicePath, dataRoot, exchangeRoot) = ParseServiceImagePath(imagePath);
        var formalLayout = ResolveInstalledLayoutFromService(servicePath);
        var installRoot = Directory.GetParent(Directory.GetParent(formalLayout.VersionRoot)!.FullName)!.FullName;
        installRoot = ProductGuiActivationBroker.ValidateInstallRoot(installRoot);
        ValidateSafeLocalInstallRoot(installRoot);

        var activeVersion = ProductUpdateActivator.ReadActiveVersion(installRoot);
        var metadata = ProductInstalledVersionMetadataStore.Read(formalLayout.VersionRoot);
        if (!string.Equals(metadata.Version, formalLayout.Version, StringComparison.Ordinal) ||
            !string.Equals(
                metadata.EntryPoint,
                ProductFormalUpdateManifestValidator.GuiEntryPoint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The active installed-version metadata is invalid.");
        }

        ValidateOptionalArpRegistration(machine, installRoot, publisherCertificateSha256);
        var normalizedDataRoot = ProductActivationCredentialReader.ValidateDataRoot(dataRoot);
        _ = ProductActivationCredentialReader.Read(normalizedDataRoot);
        return new ProductManagedInstallation(
            installRoot,
            normalizedDataRoot,
            Path.GetFullPath(exchangeRoot),
            activeVersion,
            formalLayout.Version,
            formalLayout.ServicePath);
    }

    internal static (string ServicePath, string DataRoot, string ExchangeRoot) ParseServiceImagePath(
        string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imagePath.Length > 4_096 || imagePath[0] != '"')
        {
            throw new InvalidDataException("The installed Service command line is invalid.");
        }

        var executableEnd = imagePath.IndexOf('"', 1);
        if (executableEnd <= 1)
        {
            throw new InvalidDataException("The installed Service executable path is invalid.");
        }

        var executable = imagePath[1..executableEnd];
        var arguments = imagePath[(executableEnd + 1)..].Trim();
        const string dataPrefix = "\"--Mcsv:Service:DataRoot=";
        const string separator = "\" \"--Mcsv:Service:ExchangeRoot=";
        if (!arguments.StartsWith(dataPrefix, StringComparison.Ordinal) ||
            !arguments.EndsWith('"'))
        {
            throw new InvalidDataException("The installed Service storage-root binding is invalid.");
        }

        var separatorIndex = arguments.IndexOf(separator, dataPrefix.Length, StringComparison.Ordinal);
        if (separatorIndex <= dataPrefix.Length ||
            separatorIndex + separator.Length >= arguments.Length - 1)
        {
            throw new InvalidDataException("The installed Service exchange-root binding is missing.");
        }

        var dataRoot = arguments[dataPrefix.Length..separatorIndex];
        var exchangeRoot = arguments[(separatorIndex + separator.Length)..^1];
        if (dataRoot.IndexOf('"') >= 0 || dataRoot.Any(char.IsControl) ||
            exchangeRoot.IndexOf('"') >= 0 || exchangeRoot.Any(char.IsControl))
        {
            throw new InvalidDataException("The installed Service storage-root binding is unsafe.");
        }

        ProductManagedStorageLayout.ValidateCanonicalSiblingRoots(dataRoot, exchangeRoot);

        return (
            ProductActivationPathPolicy.ValidateExecutable(executable, "Muhun MCSV Service.exe"),
            dataRoot,
            exchangeRoot);
    }

    private static ProductFormalActivationLayout ResolveInstalledLayoutFromService(string servicePath)
    {
        var serviceDirectory = Directory.GetParent(servicePath)?.FullName
            ?? throw new InvalidDataException("The managed Service directory is invalid.");
        if (!string.Equals(Path.GetFileName(serviceDirectory), "service-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Service is not in a formal service-win-x64 payload directory.");
        }

        var versionRoot = Directory.GetParent(serviceDirectory)?.FullName
            ?? throw new InvalidDataException("The managed version directory is invalid.");
        var versionsRoot = Directory.GetParent(versionRoot)?.FullName
            ?? throw new InvalidDataException("The managed versions directory is invalid.");
        if (!string.Equals(Path.GetFileName(versionsRoot), "versions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Service is not in a managed versions directory.");
        }

        var version = Path.GetFileName(versionRoot);
        ProductUpdateManifestParser.ValidateVersion(version);
        var guiPath = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(versionRoot, "gui-win-x64", "Muhun MCSV Manager.exe"),
            "Muhun MCSV Manager.exe");
        var updaterPath = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(versionRoot, "updater-win-x64", "Muhun MCSV Updater.exe"),
            "Muhun MCSV Updater.exe");
        return new ProductFormalActivationLayout(versionRoot, version, guiPath, servicePath, updaterPath);
    }

    internal static string ValidateSafeLocalInstallRoot(string installRoot)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Directory.Exists(candidate))
        {
            throw new InvalidDataException("The managed install root must be an existing local directory.");
        }

        var volumeRoot = Path.GetPathRoot(candidate)
            ?? throw new InvalidDataException("The managed install root has no local volume.");
        if (string.Equals(
                candidate,
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The managed product cannot use a volume root directly.");
        }

        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
            !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The managed install root must use a local fixed NTFS volume.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(candidate);
        return candidate;
    }

    private static void ValidateOptionalArpRegistration(
        RegistryKey machine,
        string installRoot,
        string publisherCertificateSha256)
    {
        using var arp = machine.OpenSubKey(ArpRegistryPath, writable: false);
        if (arp is null)
        {
            // Early managed installations predate Apps & Features registration. Their Service
            // virtual account, exact formal ImagePath, active pointer and protected root markers
            // remain the ownership proof used by this narrowly scoped repair path.
            return;
        }

        var registeredRoot = arp.GetValue(
            "InstallLocation",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var registeredProduct = arp.GetValue(
            "ProductId",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var registeredPublisher = arp.GetValue(
            "PublisherCertificateSha256",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (arp.SubKeyCount != 0 ||
            !string.Equals(registeredProduct, ProductId, StringComparison.Ordinal) ||
            !string.Equals(registeredPublisher, publisherCertificateSha256, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(registeredRoot) ||
            !string.Equals(
                Path.GetFullPath(registeredRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                installRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Apps & Features registration does not match the managed installation.");
        }
    }
}
