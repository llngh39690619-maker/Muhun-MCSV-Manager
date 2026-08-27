using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductMetadataTests
{
    [Fact]
    public void ApplicationAssembly_UsesMuhunBrandAndVersionedOutputName()
    {
        var assembly = typeof(App).Assembly;
        var versionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

        Assert.Equal("Muhun MCSV Manager", assembly.GetName().Name);
        Assert.Equal("1.0.6.0", versionInfo.FileVersion);
        Assert.Equal("1.0.6", versionInfo.ProductVersion);
        Assert.Equal("Muhun MCSV Manager", versionInfo.ProductName);
        Assert.Equal("Muhun MCSV Manager", versionInfo.FileDescription);
        Assert.Equal("Copyright © Muhun 2026", versionInfo.LegalCopyright);
        Assert.Equal(
            "Muhun MCSV Manager",
            assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);

        var coreAssembly = typeof(MinecraftServerManager.Core.Models.ServerInstance).Assembly;
        Assert.Equal("1.0.6.0", coreAssembly.GetName().Version?.ToString());
        Assert.Equal(
            "1.0.6",
            coreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

        var remoteAssembly = typeof(MinecraftServerManager.Remote.RemoteControlOptions).Assembly;
        Assert.Equal("1.0.6.0", remoteAssembly.GetName().Version?.ToString());
        Assert.Equal(
            "1.0.6",
            remoteAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);
    }

    [Fact]
    public void ApplicationManifest_UsesCurrentAssemblyIdentityVersion()
    {
        var document = XDocument.Load(GetAppSourcePath("app.manifest"));
        XNamespace assembly = "urn:schemas-microsoft-com:asm.v1";
        var identity = Assert.Single(document.Descendants(assembly + "assemblyIdentity"));

        Assert.Equal("Muhun.MCSV.Manager.app", (string?)identity.Attribute("name"));
        Assert.Equal("1.0.6.0", (string?)identity.Attribute("version"));
    }

    [Fact]
    public void ProjectMetadata_UsesCurrentVersionAndKeepsWindowsFormsTraySupport()
    {
        var appProject = XDocument.Load(GetAppSourcePath("MinecraftServerManager.App.csproj"));
        var appProperties = appProject.Root!.Elements("PropertyGroup").Elements().ToArray();

        Assert.Equal("Muhun MCSV Manager", SingleProperty(appProperties, "AssemblyName"));
        Assert.Equal("1.0.6", SingleProperty(appProperties, "Version"));
        Assert.Equal("1.0.6.0", SingleProperty(appProperties, "AssemblyVersion"));
        Assert.Equal("1.0.6.0", SingleProperty(appProperties, "FileVersion"));
        Assert.Equal("1.0.6", SingleProperty(appProperties, "InformationalVersion"));
        Assert.Equal("true", SingleProperty(appProperties, "UseWindowsForms"));
        var mailKit = Assert.Single(
            appProject.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "MailKit");
        Assert.Equal("4.17.0", (string?)mailKit.Attribute("Version"));

        var coreProject = XDocument.Load(GetCoreSourcePath("MinecraftServerManager.Core.csproj"));
        var coreProperties = coreProject.Root!.Elements("PropertyGroup").Elements().ToArray();
        Assert.Equal("1.0.6", SingleProperty(coreProperties, "Version"));
        Assert.Equal("1.0.6.0", SingleProperty(coreProperties, "AssemblyVersion"));
        Assert.Equal("1.0.6.0", SingleProperty(coreProperties, "FileVersion"));
        Assert.Equal("1.0.6", SingleProperty(coreProperties, "InformationalVersion"));

        var remoteProject = XDocument.Load(GetRemoteSourcePath("MinecraftServerManager.Remote.csproj"));
        var remoteProperties = remoteProject.Root!.Elements("PropertyGroup").Elements().ToArray();
        Assert.Equal("1.0.6", SingleProperty(remoteProperties, "Version"));
        Assert.Equal("1.0.6.0", SingleProperty(remoteProperties, "AssemblyVersion"));
        Assert.Equal("1.0.6.0", SingleProperty(remoteProperties, "FileVersion"));
        Assert.Equal("1.0.6", SingleProperty(remoteProperties, "InformationalVersion"));
    }

    [Fact]
    public void MainViewModel_UsesCurrentVersionAndKeepsPortableBackupCompatibility()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));

        Assert.Contains("MuhunMCSVManager/1.0 (Windows; manager)", source, StringComparison.Ordinal);
        Assert.Contains("Muhun MCSV Manager 1.0 · .NET 10 · Windows x64", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.11 Remote Preview 4.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.11 Remote Preview 3.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.11 Remote Preview 2.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.11 Remote Preview 1.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.10.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.9.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 9.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.9.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 8.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.8.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 7.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.7.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 6.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.6.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 5.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.5.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 4.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.4.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 3.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.3.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 2.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.2.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0 Preview 1.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.5.0-preview.1.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.8.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.7.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.6.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.5.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.4.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.3.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.2.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.1.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.4.0.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.3.1.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.3.0.exe\"", source, StringComparison.Ordinal);

        var onlineWorkflowSource = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "Services",
            "OnlineModpackWorkflow.cs")));
        Assert.Contains("MuhunMCSVManager/1.0 (Windows; modpack-installer)", onlineWorkflowSource, StringComparison.Ordinal);
        Assert.Contains("\"Muhun MCSV Manager 0.2.5.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"MinecraftServerManager.exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"remote-security.dat\"", source, StringComparison.Ordinal);
        Assert.Contains("\".remote-security.dat.\"", source, StringComparison.Ordinal);
        Assert.Contains("SingleInstanceGuard.LockFileName", source, StringComparison.Ordinal);

        var coreWorkflowCompositionSource = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "Services",
            "CoreServerCreationWorkflow.Composition.cs")));
        Assert.Contains(
            "MuhunMCSVManager/1.0 (contact: Muhun; Windows core-installer)",
            coreWorkflowCompositionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceWorker_UsesCurrentProductCacheNamespace()
    {
        var source = File.ReadAllText(GetRemoteSourcePath(Path.Combine(
            "Web",
            "service-worker.js")));

        Assert.Contains(
            "const CACHE_NAME = \"mcsv-offline-product-v2\";",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("offline-preview", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveVaultIsExcludedAndThirdPartyNoticesArePresent()
    {
        var projectRoot = GetProjectRoot();
        var gitIgnore = File.ReadAllText(Path.Combine(projectRoot, ".gitignore"));
        Assert.Contains("remote-security.dat", gitIgnore, StringComparison.Ordinal);
        Assert.Contains(".remote-security.dat.*", gitIgnore, StringComparison.Ordinal);
        Assert.Contains(".mcsv-manager.instance.lock", gitIgnore, StringComparison.Ordinal);

        var notices = File.ReadAllText(Path.Combine(projectRoot, "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("MailKit 4.17.0", notices, StringComparison.Ordinal);
        Assert.Contains("MimeKit 4.17.0", notices, StringComparison.Ordinal);
        Assert.Contains("BouncyCastle.Cryptography 2.6.2", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License Text", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalAndroidRelease_PreservesAndReverifiesV4Signature()
    {
        var projectRoot = GetProjectRoot();
        var build = File.ReadAllText(Path.Combine(
            projectRoot,
            "scripts",
            "Build-MuhunMcsvAndroid.ps1"));
        var package = File.ReadAllText(Path.Combine(
            projectRoot,
            "scripts",
            "New-MuhunMcsvRelease.ps1"));
        var verify = File.ReadAllText(Path.Combine(
            projectRoot,
            "scripts",
            "Test-MuhunMcsvRelease.ps1"));

        foreach (var source in new[] { build, package, verify })
        {
            Assert.Contains("Muhun-MCSV-Remote.apk.idsig", source, StringComparison.Ordinal);
            Assert.Contains("APK Signature Scheme v4", source, StringComparison.Ordinal);
            Assert.Contains("v4SignatureSha256", source, StringComparison.Ordinal);
        }

        Assert.Contains("-v4-signature-file", verify, StringComparison.Ordinal);
        Assert.Contains("@('v2', 'v3', 'v4')", package, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowHeader_UsesMuhunProductBrand()
    {
        var document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var headerTexts = document
            .Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains("MUHUN", headerTexts);
        Assert.Contains("MCSV Manager", headerTexts);
        Assert.DoesNotContain("MINECRAFT", headerTexts);
        Assert.DoesNotContain("Server Manager", headerTexts);
    }

    [Fact]
    public void StableSource_DoesNotContainExperimentalIdleWakeContracts()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(GetAppSourcePath("."), ".."));
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        Assert.DoesNotContain("EnableIdleSleep", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IdleTimeoutMinutes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableWakeOnConnect", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MinecraftWakeListener", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerIdleWakeCoordinator", source, StringComparison.Ordinal);
    }

    private static string SingleProperty(IEnumerable<XElement> properties, string name)
        => Assert.Single(properties, element => element.Name.LocalName == name).Value;

    private static string GetMainWindowXamlPath([CallerFilePath] string testFilePath = "")
        => GetAppSourcePath("MainWindow.xaml", testFilePath);

    private static string GetAppSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.App",
            relativePath));

    private static string GetCoreSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.Core",
            relativePath));

    private static string GetRemoteSourcePath(
        string relativePath,
        [CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "MinecraftServerManager.Remote",
            relativePath));

    private static string GetProjectRoot([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            ".."));
}
