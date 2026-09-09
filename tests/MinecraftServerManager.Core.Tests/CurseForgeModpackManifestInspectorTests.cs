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

    [Fact]
    public async Task Inspect_ManifestFilesAndOverrides_AreParsedExactly()
    {
        using var directory = new TemporaryDirectory();
        var manifest = ManifestWithFilesJson(
            "forge-14.23.5.2860",
            """
            [
              { "projectID": 285109, "fileID": 4612979, "required": true },
              { "projectID": 238222, "fileID": 4472389, "required": false }
            ]
            """,
            minecraftVersion: "1.12.2",
            name: "RLCraft",
            packVersion: "v2.9.3");
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", manifest),
                ("overrides/config/options.txt", "difficulty=hard"),
                ("docs/readme.txt", "not an override")
            ]);

        var result = await new CurseForgeModpackManifestInspector().InspectAsync(archive);

        Assert.Equal("RLCraft", result.Name);
        Assert.Equal("v2.9.3", result.PackVersion);
        Assert.Equal("1.12.2", result.MinecraftVersion);
        Assert.Equal(ModrinthModpackLoaderKind.Forge, result.LoaderInstallRequest.Kind);
        Assert.Equal("14.23.5.2860", result.LoaderInstallRequest.LoaderVersion);
        Assert.Equal(
            [
                new CurseForgeModpackManifestFile(285109, 4612979, true),
                new CurseForgeModpackManifestFile(238222, 4472389, false)
            ],
            result.Files);
        Assert.Equal("overrides", result.OverridesDirectory);
        var applied = Assert.Single(result.OverrideEntries);
        Assert.Equal("overrides/config/options.txt", applied.ArchivePath);
        Assert.Equal("config/options.txt", applied.RelativePath);
        Assert.Equal(15, applied.Length);
    }

    [Theory]
    [InlineData("{}", "陣列")]
    [InlineData("[{\"projectID\":0,\"fileID\":2,\"required\":true}]", "正整數")]
    [InlineData("[{\"projectID\":1,\"fileID\":0,\"required\":true}]", "正整數")]
    [InlineData("[{\"projectID\":1,\"fileID\":2,\"required\":\"true\"}]", "boolean")]
    [InlineData("[{\"projectID\":1,\"fileID\":2,\"required\":true},{\"projectID\":1,\"fileID\":2,\"required\":false}]", "重複")]
    public async Task Inspect_InvalidManifestFileReferences_AreRejected(
        string filesJson,
        string expectedText)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", ManifestWithFilesJson("forge-47.2.0", filesJson))]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));

        Assert.Contains(expectedText, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_ManifestFileCountLimit_IsEnforced()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", ManifestWithFilesJson(
                "forge-47.2.0",
                "[{\"projectID\":1,\"fileID\":2,\"required\":true},"
                + "{\"projectID\":3,\"fileID\":4,\"required\":true}]"))]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(
                archive,
                new CurseForgeManifestInspectionLimits(MaxManifestFiles: 1)));

        Assert.Contains("files", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("上限", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../overrides")]
    [InlineData("/overrides")]
    [InlineData("C:/overrides")]
    [InlineData("overrides\\nested")]
    [InlineData("overrides/../payload")]
    public async Task Inspect_UnsafeOverridesManifestDirectory_IsRejected(string overridesDirectory)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [("manifest.json", ManifestWithFilesJson(
                "forge-47.2.0",
                "[]",
                overridesDirectory))]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("overrides\\config\\options.txt")]
    [InlineData("overrides/../../outside.txt")]
    public async Task Inspect_UnsafeArchivePath_IsRejected(string archiveEntryPath)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                (archiveEntryPath, "payload")
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));
    }

    [Theory]
    [InlineData("versions")]
    [InlineData("libraries")]
    [InlineData("assets")]
    [InlineData("runtime")]
    [InlineData("runtimes")]
    [InlineData("jre")]
    [InlineData("natives")]
    [InlineData("launcher")]
    [InlineData(".x-mcsv-content")]
    [InlineData(".x-mcsv")]
    [InlineData("installation.id")]
    [InlineData("launcher_accounts.json")]
    [InlineData("launcher_profiles.json")]
    public async Task Inspect_ProtectedOverrideTopLevel_IsRejected(string protectedName)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ($"overrides/{protectedName}/payload.bin", "payload")
            ]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));

        Assert.Contains("受保護", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overrides/config/options.txt", "overrides/CONFIG/OPTIONS.TXT")]
    [InlineData("overrides/caf\u00e9.txt", "overrides/cafe\u0301.txt")]
    [InlineData("overrides/config", "overrides/config/options.txt")]
    public async Task Inspect_DuplicateOrConflictingOverrideTargets_AreRejected(
        string firstPath,
        string secondPath)
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                (firstPath, "first"),
                (secondPath, "second")
            ]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archive));

        Assert.True(
            exception.Message.Contains("重複", StringComparison.Ordinal)
            || exception.Message.Contains("衝突", StringComparison.Ordinal),
            exception.Message);
    }

    [Fact]
    public async Task Inspect_SymbolicLinkOverrideEntry_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "symlink-override.zip");
        using (var stream = new FileStream(
                   archivePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteArchiveEntry(archive, "manifest.json", Manifest("forge-47.2.0"));
            var link = archive.CreateEntry("overrides/config/options.txt", CompressionLevel.Fastest);
            link.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(link.Open(), new UTF8Encoding(false, true));
            writer.Write("../../outside.txt");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CurseForgeModpackManifestInspector().InspectAsync(archivePath));

        Assert.Contains("符號連結", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_ExpandedSizeAndOverrideCompressionLimits_AreEnforced()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("overrides/config/options.txt", new string('A', 32 * 1024))
            ]);
        var inspector = new CurseForgeModpackManifestInspector();

        var entryError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxEntryUncompressedBytes: 16 * 1024)));
        var totalError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxArchiveUncompressedBytes: 16 * 1024)));
        var ratioError = await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(
            archive,
            new CurseForgeManifestInspectionLimits(MaxCompressionRatio: 50d)));

        Assert.Contains("解壓縮大小", entryError.Message, StringComparison.Ordinal);
        Assert.Contains("解壓縮大小", totalError.Message, StringComparison.Ordinal);
        Assert.Contains("壓縮比例", ratioError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractOverrides_ValidLayer_OverwritesOnlyRelativePayloadFiles()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("overrides/config/options.txt", "new-value"),
                ("overrides/resourcepacks/example/readme.txt", "pack"),
                ("not-overrides/ignored.txt", "ignored")
            ]);
        var staging = Path.Combine(directory.Path, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "config"));
        await File.WriteAllTextAsync(Path.Combine(staging, "config", "options.txt"), "old-value");
        await File.WriteAllTextAsync(Path.Combine(staging, "keep.txt"), "keep");

        await new CurseForgeModpackManifestInspector().ExtractOverridesAsync(archive, staging);

        Assert.Equal("new-value", await File.ReadAllTextAsync(Path.Combine(staging, "config", "options.txt")));
        Assert.Equal(
            "pack",
            await File.ReadAllTextAsync(Path.Combine(staging, "resourcepacks", "example", "readme.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(staging, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(staging, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(staging, "ignored.txt")));
    }

    [Fact]
    public async Task ExtractOverrides_ReparseStagingRoot_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("overrides/config/options.txt", "payload")
            ]);
        var actual = Path.Combine(directory.Path, "actual");
        var link = Path.Combine(directory.Path, "staging-link");
        Directory.CreateDirectory(actual);
        ReparsePointTestHelper.CreateDirectoryLink(link, actual);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgeModpackManifestInspector().ExtractOverridesAsync(archive, link));

            Assert.False(File.Exists(Path.Combine(actual, "config", "options.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task ExtractOverrides_ReparseParentFailsPreflightBeforeAnyWrite()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            directory.Path,
            [
                ("manifest.json", Manifest("forge-47.2.0")),
                ("overrides/first.txt", "first"),
                ("overrides/config/options.txt", "must-not-escape")
            ]);
        var staging = Path.Combine(directory.Path, "staging");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(staging, "config");
        ReparsePointTestHelper.CreateDirectoryLink(link, outside);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgeModpackManifestInspector().ExtractOverridesAsync(archive, staging));

            Assert.False(File.Exists(Path.Combine(staging, "first.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "options.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
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

    private static string ManifestWithFilesJson(
        string loaderId,
        string filesJson,
        string overrides = "overrides",
        string minecraftVersion = "1.20.1",
        string name = "Fixture Pack",
        string packVersion = "1.0.0")
        => $$"""
           {
             "minecraft": {
               "version": "{{minecraftVersion}}",
               "modLoaders": [{ "id": "{{loaderId}}", "primary": true }]
             },
             "manifestType": "minecraftModpack",
             "manifestVersion": 1,
             "name": "{{name}}",
             "version": "{{packVersion}}",
             "author": "Tests",
             "files": {{filesJson}},
             "overrides": "{{overrides}}"
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

    private static void WriteArchiveEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false, true));
        writer.Write(contents);
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
