using System;
using System.IO;
using System.Text.Json;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class FormalGuiDataRootTests
{
    private const string TestSid = "S-1-5-21-1000-2000-3000-1001";

    [Fact]
    public void ManagedBetaRoot_IsBoundToSelectedInstallRootChannelAndSid()
    {
        using var temporary = new TemporaryDirectory();
        var executable = CreateManagedLayout(temporary.Path, "1.2.9-beta.9");

        var paths = ApplicationPaths.CreateForCurrentInstallation(executable, TestSid);

        Assert.True(paths.IsManagedInstallation);
        Assert.Equal(temporary.Path, paths.InstallRoot, ignoreCase: true);
        Assert.Equal("beta", paths.Channel);
        Assert.Equal(TestSid, paths.CurrentUserSid);
        Assert.Equal(
            Path.Combine(temporary.Path, "users", TestSid, "beta"),
            paths.Root,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(temporary.Path, "exchange", "beta"),
            paths.ProductExchangeRoot,
            ignoreCase: true);

        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.Clients));
        Assert.True(Directory.Exists(paths.Cache));
        Assert.True(Directory.Exists(paths.ProductExchangeRoot));
    }

    [Fact]
    public void ManagedStableVersion_UsesIndependentStableDataRoot()
    {
        using var temporary = new TemporaryDirectory();
        var executable = CreateManagedLayout(temporary.Path, "2.0.0");

        var paths = ApplicationPaths.CreateForCurrentInstallation(executable, TestSid);

        Assert.Equal("stable", paths.Channel);
        Assert.Equal(
            Path.Combine(temporary.Path, "users", TestSid, "stable"),
            paths.Root,
            ignoreCase: true);
    }

    [Fact]
    public void PortableConstructor_RemainsSelfContainedForDiagnostics()
    {
        using var temporary = new TemporaryDirectory();

        var paths = new ApplicationPaths(temporary.Path);

        Assert.False(paths.IsManagedInstallation);
        Assert.Null(paths.InstallRoot);
        Assert.Null(paths.Channel);
        Assert.Equal(
            Path.Combine(temporary.Path, "exchange"),
            paths.ProductExchangeRoot,
            ignoreCase: true);
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.Clients));
    }

    [Fact]
    public void ManagedRoot_RejectsLooseCopiedExecutableWithoutOwnershipBinding()
    {
        using var temporary = new TemporaryDirectory();
        var executable = Path.Combine(temporary.Path, "Muhun MCSV Manager.exe");
        File.WriteAllBytes(executable, "MZ"u8.ToArray());

        var exception = Assert.Throws<InvalidDataException>(() =>
            ApplicationPaths.CreateForCurrentInstallation(executable, TestSid));

        Assert.Contains("Reinstall or repair", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedRoot_RejectsInactiveVersionSlot()
    {
        using var temporary = new TemporaryDirectory();
        var executable = CreateManagedLayout(temporary.Path, "1.2.9-beta.9");
        File.WriteAllText(Path.Combine(temporary.Path, "active-version.v1"), "1.2.9-beta.7\n");

        Assert.Throws<InvalidDataException>(() =>
            ApplicationPaths.CreateForCurrentInstallation(executable, TestSid));
    }

    [Fact]
    public void ManagedRoot_RejectsTamperedInstalledVersionMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var executable = CreateManagedLayout(temporary.Path, "1.2.9-beta.9");
        var metadata = Path.Combine(
            temporary.Path,
            "versions",
            "1.2.9-beta.9",
            "installed-version.v1.json");
        File.WriteAllText(
            metadata,
            "{\"schemaVersion\":1,\"productId\":\"muhun.mcsv.manager\","
            + "\"version\":\"1.2.9-beta.7\","
            + "\"entryPoint\":\"gui-win-x64/Muhun MCSV Manager.exe\"}");

        Assert.Throws<InvalidDataException>(() =>
            ApplicationPaths.CreateForCurrentInstallation(executable, TestSid));
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("../escape")]
    public void ManagedRoot_RejectsInvalidSid(string sid)
    {
        using var temporary = new TemporaryDirectory();
        var executable = CreateManagedLayout(temporary.Path, "1.2.9-beta.9");

        Assert.Throws<InvalidDataException>(() =>
            ApplicationPaths.CreateForCurrentInstallation(executable, sid));
    }

    [Fact]
    public void InteractiveStartup_UsesManagedInstallRootWithoutProfileFallback()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));

        Assert.Contains("ApplicationPaths.CreateForCurrentInstallation()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationPaths.CreateForCurrentUser", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", source, StringComparison.Ordinal);
        Assert.Contains("diagnosticMode ? AppContext.BaseDirectory : paths.Root", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationAcknowledgement_IsSentOnlyAfterViewModelInitialization()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));

        var initialize = source.IndexOf(
            "await viewModel.InitializeAsync",
            StringComparison.Ordinal);
        var acknowledge = source.IndexOf(
            "ProductGuiActivationAcknowledgement.SendReadyAsync",
            StringComparison.Ordinal);

        Assert.True(initialize >= 0);
        Assert.True(acknowledge > initialize);
        Assert.Contains("viewModel.ProductServiceNegotiatedApiVersion", source, StringComparison.Ordinal);
    }

    private static string CreateManagedLayout(string installRoot, string version)
    {
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(
            Path.Combine(installRoot, ".muhun-mcsv-install-root"),
            "muhun.mcsv.manager:1\n");
        File.WriteAllText(Path.Combine(installRoot, "active-version.v1"), version + "\n");
        var versionRoot = Path.Combine(installRoot, "versions", version);
        var guiRoot = Path.Combine(versionRoot, "gui-win-x64");
        Directory.CreateDirectory(guiRoot);
        var executable = Path.Combine(guiRoot, "Muhun MCSV Manager.exe");
        File.WriteAllBytes(executable, "MZ"u8.ToArray());
        File.WriteAllText(
            Path.Combine(versionRoot, "installed-version.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                productId = "muhun.mcsv.manager",
                version,
                entryPoint = "gui-win-x64/Muhun MCSV Manager.exe",
            }));
        return executable;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-gui-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
