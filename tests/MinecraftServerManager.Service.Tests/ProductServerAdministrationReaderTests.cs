using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerAdministrationReaderTests
{
    [Fact]
    public async Task Capture_ReturnsOnlyBoundedTopLevelJarNamesAndAllowlistedJavaMetadata()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = "Mohist",
            JavaRuntimePath = "temurin-jdk-21-test/bin/java.exe",
        };
        var serverRoot = Path.Combine(layout.Servers, registration.ServerDirectory);
        var mods = Path.Combine(serverRoot, "mods");
        var plugins = Path.Combine(serverRoot, "plugins");
        var nested = Path.Combine(mods, "nested");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(plugins);
        File.WriteAllBytes(Path.Combine(mods, "example-mod.jar"), new byte[17]);
        File.WriteAllBytes(Path.Combine(plugins, "example-plugin.JAR"), new byte[23]);
        File.WriteAllText(Path.Combine(mods, "notes.txt"), "not an add-on");
        File.WriteAllBytes(Path.Combine(nested, "not-recursed.jar"), [1]);

        var javaPath = Path.Combine(
            layout.Runtimes,
            registration.JavaRuntimePath.Replace('/', Path.DirectorySeparatorChar));
        var runtimeHome = Directory.GetParent(Path.GetDirectoryName(javaPath)!)!.FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllText(javaPath, "managed java placeholder");
        File.WriteAllText(
            Path.Combine(runtimeHome, "release"),
            "JAVA_VERSION=\"21.0.8+9\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\nOS_ARCH=\"amd64\"\nSECRET_PATH=\"C:\\\\private\"\n");

        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        var reader = new ProductServerAdministrationReader(layout, registry, TimeProvider.System);

        var snapshot = reader.Capture(registration.Id);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.AddonsAvailable);
        Assert.False(snapshot.AddonsTruncated);
        Assert.Collection(
            snapshot.Addons,
            addon =>
            {
                Assert.Equal(ProductServerAddonKind.Mod, addon.Kind);
                Assert.Equal("example-mod.jar", addon.FileName);
                Assert.Equal(17, addon.SizeBytes);
            },
            addon =>
            {
                Assert.Equal(ProductServerAddonKind.Plugin, addon.Kind);
                Assert.Equal("example-plugin.JAR", addon.FileName);
                Assert.Equal(23, addon.SizeBytes);
            });
        Assert.True(snapshot.Java.Configured);
        Assert.True(snapshot.Java.Available);
        Assert.Equal(21, snapshot.Java.MajorVersion);
        Assert.Equal("21.0.8+9", snapshot.Java.Version);
        Assert.Equal("JDK", snapshot.Java.RuntimeKind);
        Assert.Equal("Eclipse Adoptium", snapshot.Java.Vendor);
        Assert.Equal("x64", snapshot.Java.Architecture);

        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(layout.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(registration.ServerDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(registration.JavaRuntimePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET_PATH", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_StopsAtContractLimitAndMarksTheProjectionTruncated()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration() with
        {
            CoreType = "Forge",
        };
        var mods = Path.Combine(layout.Servers, registration.ServerDirectory, "mods");
        Directory.CreateDirectory(mods);
        for (var index = 0;
             index < ProductServerAdministrationContract.MaximumListedAddons + 5;
             index++)
        {
            File.WriteAllBytes(Path.Combine(mods, $"mod-{index:D3}.jar"), [1]);
        }

        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);

        var snapshot = new ProductServerAdministrationReader(layout, registry, TimeProvider.System)
            .Capture(registration.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(ProductServerAdministrationContract.MaximumListedAddons, snapshot.Addons.Count);
        Assert.True(snapshot.AddonsTruncated);
        Assert.All(snapshot.Addons, addon =>
        {
            Assert.DoesNotContain("/", addon.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", addon.FileName, StringComparison.Ordinal);
            Assert.True(addon.FileName.Length <= ProductServerAdministrationContract.MaximumAddonFileNameCharacters);
        });
    }

    [Fact]
    public async Task Capture_MissingManagedTreesReturnsUnavailableWithoutCreatingDirectories()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);

        var snapshot = new ProductServerAdministrationReader(layout, registry, TimeProvider.System)
            .Capture(registration.Id);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.AddonsAvailable);
        Assert.Empty(snapshot.Addons);
        Assert.True(snapshot.Java.Configured);
        Assert.False(snapshot.Java.Available);
        Assert.False(Directory.Exists(Path.Combine(layout.Servers, registration.ServerDirectory)));
        Assert.False(File.Exists(Path.Combine(layout.Runtimes, registration.JavaRuntimePath)));
    }
}
