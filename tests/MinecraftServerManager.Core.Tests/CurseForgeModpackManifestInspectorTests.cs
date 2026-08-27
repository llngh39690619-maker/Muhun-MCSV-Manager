using System.IO.Compression;
using System.Text;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class CurseForgeModpackManifestInspectorTests
{
    [Theory]
    [InlineData("forge-47.2.0", ModrinthModpackLoaderKind.Forge, "47.2.0")]
    [InlineData("fabric-0.16.9", ModrinthModpackLoaderKind.Fabric, "0.16.9")]
    [InlineData("neoforge-21.1.248", ModrinthModpackLoaderKind.NeoForge, "21.1.248")]
    [InlineData("quilt-0.26.4", ModrinthModpackLoaderKind.Quilt, "0.26.4")]
    public async Task Inspect_ValidGeneratedManifest_ReturnsExactPrimaryLoader(
        string loaderId,
        ModrinthModpackLoaderKind expectedKind,
        string expectedVersion)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", Manifest(loaderId))]);

        var result = await new CurseForgeModpackManifestInspector().InspectAsync(archive);

        Assert.Equal("Fixture Pack", result.Name);
        Assert.Equal("1.0.0", result.PackVersion);
        Assert.Equal("1.20.1", result.MinecraftVersion);
        Assert.Equal(expectedKind, result.LoaderInstallRequest.Kind);
        Assert.Equal(expectedVersion, result.LoaderInstallRequest.LoaderVersion);
        Assert.Equal("1.20.1", result.LoaderInstallRequest.MinecraftVersion);
    }

    [Fact]
    public async Task Inspect_EmptyLoaderArray_ProducesVanillaRequest()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", ManifestWithLoaderJson("[]"))]);

        var result = await new CurseForgeModpackManifestInspector().InspectAsync(archive);

        Assert.Equal(ModrinthModpackLoaderKind.Vanilla, result.LoaderInstallRequest.Kind);
        Assert.Null(result.LoaderInstallRequest.LoaderVersion);
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public async Task Inspect_InvalidOrAmbiguousManifest_IsRejected(string json, string expectedText)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(directory.Path, [("manifest.json", json)]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));

        Assert.Contains(expectedText, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> InvalidManifests => new()
    {
        { ManifestWithLoaderJson("[{\"id\":\"forge-47.2.0\",\"primary\":false}]"), "primary" },
        {
            ManifestWithLoaderJson(
                "[{\"id\":\"forge-47.2.0\",\"primary\":true},"
                + "{\"id\":\"fabric-0.16.9\",\"primary\":true}]"),
            "多個"
        },
        { Manifest("liteloader-1.0"), "不支援" },
        { Manifest("forge-../../evil"), "不安全" },
        { Manifest("forge-"), "缺少版本" },
        { Manifest("forge-47.2.0").Replace("minecraftModpack", "other", StringComparison.Ordinal), "manifestType" },
        { Manifest("forge-47.2.0").Replace("\"manifestVersion\": 1", "\"manifestVersion\": 2", StringComparison.Ordinal), "manifestVersion" },
        { Manifest("forge-47.2.0").Replace("\"1.20.1\"", "\"../1.20.1\"", StringComparison.Ordinal), "不安全" }
    };

    [Fact]
    public async Task Inspect_NestedOrWrongCaseManifest_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var inspector = new CurseForgeModpackManifestInspector();
        var nested = CreateArchive(
            directory.Path,
            [("pack/manifest.json", Manifest("forge-47.2.0"))],
            "nested.zip");
        var wrongCase = CreateArchive(
            directory.Path,
            [("Manifest.json", Manifest("forge-47.2.0"))],
            "case.zip");

        var nestedError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(nested));
        var caseError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(wrongCase));

        Assert.Contains("根目錄", nestedError.Message, StringComparison.Ordinal);
        Assert.Contains("精確名稱", caseError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_DuplicateManifestNames_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("manifest.json", Manifest("fabric-0.16.9"))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));

        Assert.Contains("重複", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xA000 << 16)]
    [InlineData(0x1000 << 16)]
    [InlineData((int)FileAttributes.ReparsePoint)]
    public async Task Inspect_LinkReparseOrSpecialManifestEntry_IsRejected(int externalAttributes)
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, $"special-{externalAttributes}.zip");
        using (var stream = new FileStream(
                   archivePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            entry.ExternalAttributes = externalAttributes;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false, true));
            writer.Write(Manifest("forge-47.2.0"));
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archivePath));

        Assert.Contains("特殊檔案", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_EntryCountAndManifestSizeLimits_AreEnforced()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("extra.txt", "extra")
            ]);
        var inspector = new CurseForgeModpackManifestInspector();

        var entryError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxEntries: 1)));
        var sizeError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxManifestBytes: 32)));

        Assert.Contains("entries", entryError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", sizeError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("上限", sizeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_ArchiveAndCompressionRatioLimits_AreEnforced()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", Manifest("forge-47.2.0"))]);
        var archiveLength = new FileInfo(archive).Length;
        var inspector = new CurseForgeModpackManifestInspector();

        var archiveError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxArchiveBytes: archiveLength - 1)));
        var ratioError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxCompressionRatio: 1d)));

        Assert.Contains("client pack", archiveError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("壓縮比例", ratioError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_Cancellation_IsObservedBeforeManifestRead()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", Manifest("forge-47.2.0"))]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(
                archive,
                cancellationToken: cancellation.Token));
    }

    private static string Manifest(string loaderId)
        => ManifestWithLoaderJson($$"""[{"id":"{{loaderId}}","primary":true}]""");

    private static string ManifestWithLoaderJson(string loaderJson)
        => $$"""
           {
             "minecraft": {
               "version": "1.20.1",
               "modLoaders": {{loaderJson}}
             },
             "manifestType": "minecraftModpack",
             "manifestVersion": 1,
             "name": "Fixture Pack",
             "version": "1.0.0",
             "author": "Tests",
             "files": [],
             "overrides": "overrides"
           }
           """;

    private static string CreateArchive(
        string directory,
        IReadOnlyList<(string Path, string Contents)> entries,
        string fileName = "pack.zip")
    {
        var path = Path.Combine(directory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryPath, contents) in entries)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false, true));
            writer.Write(contents);
        }

        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MuhunMCSVManager-CurseManifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
