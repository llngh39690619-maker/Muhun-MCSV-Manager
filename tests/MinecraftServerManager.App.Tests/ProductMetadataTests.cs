using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductMetadataTests
{
    private const string CurrentVersion = "1.2.5";
    private const string CurrentAssemblyVersion = "1.2.5.0";
    private static readonly string[] VersionedSourceProjects =
    [
        "MinecraftServerManager.App/MinecraftServerManager.App.csproj",
        "MinecraftServerManager.BuiltinProvider/MinecraftServerManager.BuiltinProvider.csproj",
        "MinecraftServerManager.Client/MinecraftServerManager.Client.csproj",
        "MinecraftServerManager.Contracts/MinecraftServerManager.Contracts.csproj",
        "MinecraftServerManager.Core/MinecraftServerManager.Core.csproj",
        "MinecraftServerManager.Data/MinecraftServerManager.Data.csproj",
        "MinecraftServerManager.GameClient.Contracts/MinecraftServerManager.GameClient.Contracts.csproj",
        "MinecraftServerManager.GameClient/MinecraftServerManager.GameClient.csproj",
        "MinecraftServerManager.Notifications/MinecraftServerManager.Notifications.csproj",
        "MinecraftServerManager.ProviderHost/MinecraftServerManager.ProviderHost.csproj",
        "MinecraftServerManager.Remote/MinecraftServerManager.Remote.csproj",
        "MinecraftServerManager.Service/MinecraftServerManager.Service.csproj",
        "MinecraftServerManager.Updater/MinecraftServerManager.Updater.csproj",
    ];

    [Fact]
    public void ApplicationAssembly_UsesXBrandAndKeepsCompatibleOutputName()
    {
        var assembly = typeof(App).Assembly;
        var versionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

        Assert.Equal("Muhun MCSV Manager", assembly.GetName().Name);
        Assert.Equal("1.2.5.0", versionInfo.FileVersion);
        Assert.Equal("1.2.5", versionInfo.ProductVersion);
        Assert.Equal("X MCSV", versionInfo.ProductName);
        Assert.Equal("X MCSV", versionInfo.FileDescription);
        Assert.Equal("Copyright © Muhun 2026", versionInfo.LegalCopyright);
        Assert.Equal(
            "X MCSV",
            assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);

        var coreAssembly = typeof(MinecraftServerManager.Core.Models.ServerInstance).Assembly;
        Assert.Equal("1.2.5.0", coreAssembly.GetName().Version?.ToString());
        Assert.Equal(
            "1.2.5",
            coreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

        var remoteAssembly = typeof(MinecraftServerManager.Remote.RemoteControlOptions).Assembly;
        Assert.Equal("1.2.5.0", remoteAssembly.GetName().Version?.ToString());
        Assert.Equal(
            "1.2.5",
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
        Assert.Equal("1.2.5.0", (string?)identity.Attribute("version"));
    }

    [Fact]
    public void ProjectMetadata_UsesCurrentVersionAndKeepsWindowsFormsTraySupport()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(GetAppSourcePath("."), ".."));
        foreach (var relativePath in VersionedSourceProjects)
        {
            var project = XDocument.Load(Path.Combine(
                sourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var properties = project.Root!.Elements("PropertyGroup").Elements().ToArray();

            Assert.Equal(CurrentVersion, SingleProperty(properties, "Version"));
            Assert.Equal(CurrentAssemblyVersion, SingleProperty(properties, "AssemblyVersion"));
            Assert.Equal(CurrentAssemblyVersion, SingleProperty(properties, "FileVersion"));
            Assert.Equal(CurrentVersion, SingleProperty(properties, "InformationalVersion"));
        }

        var appProject = XDocument.Load(GetAppSourcePath("MinecraftServerManager.App.csproj"));
        var appProperties = appProject.Root!.Elements("PropertyGroup").Elements().ToArray();

        Assert.Equal("Muhun MCSV Manager", SingleProperty(appProperties, "AssemblyName"));
        Assert.Equal("true", SingleProperty(appProperties, "UseWindowsForms"));
        var mailKit = Assert.Single(
            appProject.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "MailKit");
        Assert.Equal("4.17.0", (string?)mailKit.Attribute("Version"));
    }

    [Fact]
    public void MainViewModel_UsesCurrentVersionAndKeepsPortableBackupCompatibility()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));

        Assert.Contains("MuhunMCSVManager/1.0 (Windows; manager)", source, StringComparison.Ordinal);
        Assert.Contains("ProductDisplayVersion", source, StringComparison.Ordinal);
        Assert.Contains("X MCSV {ProductDisplayVersion} · .NET 10 · Windows x64", source, StringComparison.Ordinal);
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
        Assert.Contains("CmlLib.Core 4.0.6", notices, StringComparison.Ordinal);
        Assert.Contains("CmlLib.Core.Commons 4.0.0", notices, StringComparison.Ordinal);
        Assert.Contains("CmlLib.Core.Auth.Microsoft 3.3.1", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("CmlLib.Core.Installer.Forge", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("CmlLib.Core.Installer.NeoForge", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("HtmlAgilityPack", notices, StringComparison.Ordinal);
        Assert.Contains("SharpZipLib 1.4.2", notices, StringComparison.Ordinal);
        Assert.Contains("XboxAuthNet 3.0.4", notices, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2 1.0.1823.32", notices, StringComparison.Ordinal);
        Assert.Contains("MIT License Text", notices, StringComparison.Ordinal);
        Assert.Contains("Microsoft WebView2 License Text", notices, StringComparison.Ordinal);

        var formalBuild = File.ReadAllText(Path.Combine(
            projectRoot,
            "scripts",
            "Build-MuhunMcsvFormalRelease.ps1"));
        Assert.Contains("THIRD-PARTY-NOTICES.txt", formalBuild, StringComparison.Ordinal);
        Assert.Contains("LICENSE.txt", formalBuild, StringComparison.Ordinal);
        Assert.Contains("MinecraftServerManager.GameClient.Tests.csproj", formalBuild, StringComparison.Ordinal);
        Assert.Contains("exact eleven test projects", formalBuild, StringComparison.Ordinal);
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
    public void MainWindowHeader_UsesVersionedXProductBrandResources()
    {
        var document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var headerTexts = document
            .Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains("{DynamicResource L10n.app.brand.mark}", headerTexts);
        Assert.Contains("{DynamicResource L10n.app.brand.name}", headerTexts);
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

    private static string GetMainWindowXamlPath()
        => TestRepositoryPaths.AppSource("MainWindow.xaml");

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private static string GetRemoteSourcePath(string relativePath)
        => TestRepositoryPaths.RemoteSource(relativePath);

    private static string GetProjectRoot()
        => TestRepositoryPaths.RepositoryRoot;
}
