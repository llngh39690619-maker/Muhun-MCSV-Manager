using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class SafeModpackArchiveTests
{
    [Fact]
    public async Task InspectStrictFormatReturnsOnlyServerApplicableFilesAndLayers()
    {
        using var temp = new TemporaryDirectory();
        var required = "required"u8.ToArray();
        var optional = "optional"u8.ToArray();
        var unsupported = "client"u8.ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Join(',',
            ModrinthModpackTestFixtures.FileJson("mods/required.jar", required),
            ModrinthModpackTestFixtures.FileJson("mods/optional.jar", optional, "optional"),
            ModrinthModpackTestFixtures.FileJson("mods/client.jar", unsupported, "unsupported")));
        var pack = ModrinthModpackTestFixtures.CreateMrpack(
            manifest,
            ("overrides/config/value.txt", "base"u8.ToArray(), null),
            ("server-overrides/config/value.txt", "server"u8.ToArray(), null),
            ("client-overrides/client-secret.txt", "ignored"u8.ToArray(), null));
        var path = Path.Combine(temp.Path, "valid.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, pack);

        var plan = await SafeModpackArchive.InspectAsync(path, new TestUriPolicy());

        Assert.Equal("Fixture Pack", plan.Name);
        Assert.Equal("1.20.1", plan.MinecraftVersion);
        Assert.Equal(ModrinthModpackLoaderKind.Fabric, plan.LoaderInstallRequest.Kind);
        Assert.Equal("0.16.9", plan.LoaderInstallRequest.LoaderVersion);
        Assert.Equal(2, plan.Files.Count);
        Assert.Single(plan.OptionalFiles);
        Assert.Equal(1, plan.SkippedUnsupportedFiles);
        Assert.Single(plan.Overrides);
        Assert.Single(plan.ServerOverrides);
        Assert.DoesNotContain(plan.Overrides.Concat(plan.ServerOverrides), entry => entry.ArchivePath.StartsWith("client-overrides/"));
    }

    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("mods/a.jar:payload")]
    [InlineData("mods/CON.txt")]
    [InlineData("mods/trailing. ")]
    [InlineData("mods\\backslash.jar")]
    public async Task InspectRejectsUnsafeManifestPaths(string unsafePath)
    {
        using var temp = new TemporaryDirectory();
        var content = "x"u8.ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(
            ModrinthModpackTestFixtures.FileJson(unsafePath, content));
        var path = Path.Combine(temp.Path, "unsafe.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(manifest));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(path, new TestUriPolicy()));
    }

    [Fact]
    public async Task InspectRejectsUnicodeAndCaseFoldedDuplicatePaths()
    {
        using var temp = new TemporaryDirectory();
        var content = "x"u8.ToArray();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Join(',',
            ModrinthModpackTestFixtures.FileJson("mods/e\u0301.jar", content),
            ModrinthModpackTestFixtures.FileJson("MODS/é.jar", content)));
        var path = Path.Combine(temp.Path, "duplicate.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(manifest));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(path, new TestUriPolicy()));
    }

    [Fact]
    public async Task InspectRejectsTraversalAndSymlinkZipEntries()
    {
        using var temp = new TemporaryDirectory();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Empty);
        var traversal = Path.Combine(temp.Path, "traversal.mrpack");
        ModrinthModpackTestFixtures.WriteFile(traversal, ModrinthModpackTestFixtures.CreateMrpack(
            manifest, ("overrides/../escape.txt", "x"u8.ToArray(), null)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(traversal, new TestUriPolicy()));

        var symlink = Path.Combine(temp.Path, "symlink.mrpack");
        var linkAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
        ModrinthModpackTestFixtures.WriteFile(symlink, ModrinthModpackTestFixtures.CreateMrpack(
            manifest, ("overrides/link", "target"u8.ToArray(), linkAttributes)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(symlink, new TestUriPolicy()));
    }

    [Theory]
    [InlineData("\"minecraft\":\"1.20.1\",\"fabric-loader\":\"1\",\"forge\":\"2\"")]
    [InlineData("\"minecraft\":\"1.20.1\",\"unknown-loader\":\"1\"")]
    [InlineData("\"fabric-loader\":\"1\"")]
    public async Task InspectRejectsAmbiguousUnknownOrMinecraftLessDependencies(string dependencies)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "deps.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(
            ModrinthModpackTestFixtures.Manifest(string.Empty, dependencies)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(path, new TestUriPolicy()));
    }

    [Fact]
    public async Task InspectEnforcesConfiguredZipBombLimits()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "large.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(
            ModrinthModpackTestFixtures.Manifest(string.Empty),
            ("overrides/large.txt", new byte[1024], null)));
        var limits = new SafeModpackArchiveLimits(MaxEntryUncompressedBytes: 512);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(path, new TestUriPolicy(), limits));
    }

    [Fact]
    public async Task InspectRejectsDuplicateJsonPropertiesAndMalformedHashes()
    {
        using var temp = new TemporaryDirectory();
        var duplicateJson = """
        {"formatVersion":1,"formatVersion":1,"game":"minecraft","versionId":"v","name":"n",
         "files":[],"dependencies":{"minecraft":"1.20.1"}}
        """;
        var duplicatePath = Path.Combine(temp.Path, "duplicate-json.mrpack");
        ModrinthModpackTestFixtures.WriteFile(
            duplicatePath, ModrinthModpackTestFixtures.CreateMrpack(duplicateJson));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(duplicatePath, new TestUriPolicy()));

        var content = "x"u8.ToArray();
        var malformed = ModrinthModpackTestFixtures.FileJson("mods/x.jar", content)
            .Replace(ModrinthModpackTestFixtures.Hashes(content).Sha1, "xyz", StringComparison.Ordinal);
        var malformedPath = Path.Combine(temp.Path, "malformed-hash.mrpack");
        ModrinthModpackTestFixtures.WriteFile(malformedPath, ModrinthModpackTestFixtures.CreateMrpack(
            ModrinthModpackTestFixtures.Manifest(malformed)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(malformedPath, new TestUriPolicy()));
    }

    [Theory]
    [InlineData("\"minecraft\":\"1.21.1\"", ModrinthModpackLoaderKind.Vanilla, null)]
    [InlineData("\"minecraft\":\"1.21.1\",\"fabric-loader\":\"0.16.9\"", ModrinthModpackLoaderKind.Fabric, "0.16.9")]
    [InlineData("\"minecraft\":\"1.21.1\",\"forge\":\"52.0.1\"", ModrinthModpackLoaderKind.Forge, "52.0.1")]
    [InlineData("\"minecraft\":\"1.21.1\",\"neoforge\":\"21.1.1\"", ModrinthModpackLoaderKind.NeoForge, "21.1.1")]
    [InlineData("\"minecraft\":\"1.21.1\",\"quilt-loader\":\"0.27.1\"", ModrinthModpackLoaderKind.Quilt, "0.27.1")]
    public async Task InspectReturnsExplicitLoaderInstallHandoff(
        string dependencies,
        ModrinthModpackLoaderKind expectedKind,
        string? expectedLoaderVersion)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, expectedKind + ".mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(
            ModrinthModpackTestFixtures.Manifest(string.Empty, dependencies)));

        var plan = await SafeModpackArchive.InspectAsync(path, new TestUriPolicy());

        Assert.Equal(expectedKind, plan.LoaderInstallRequest.Kind);
        Assert.Equal("1.21.1", plan.LoaderInstallRequest.MinecraftVersion);
        Assert.Equal(expectedLoaderVersion, plan.LoaderInstallRequest.LoaderVersion);
    }

    [Theory]
    [InlineData("\"formatVersion\": 2", "\"formatVersion\": 1")]
    [InlineData("\"game\": \"other\"", "\"game\": \"minecraft\"")]
    public async Task InspectRejectsUnsupportedFormatOrGame(string replacement, string original)
    {
        using var temp = new TemporaryDirectory();
        var manifest = ModrinthModpackTestFixtures.Manifest(string.Empty).Replace(original, replacement, StringComparison.Ordinal);
        var path = Path.Combine(temp.Path, "unsupported.mrpack");
        ModrinthModpackTestFixtures.WriteFile(path, ModrinthModpackTestFixtures.CreateMrpack(manifest));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeModpackArchive.InspectAsync(path, new TestUriPolicy()));
    }
}
