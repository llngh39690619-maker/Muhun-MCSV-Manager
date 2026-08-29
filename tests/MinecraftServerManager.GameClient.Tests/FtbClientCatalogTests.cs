using MinecraftServerManager.Core.Models;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class FtbClientCatalogTests
{
    [Fact]
    public void MapAndFilter_UsesOnlyPublicStableReleasesMatchingSameVersionAndLoader()
    {
        var packs = new[]
        {
            Pack(
                10,
                "Stable Pack",
                false,
                Version(101, "release", "1.21.1", "NeoForged"),
                Version(103, "release", "1.21.1", "NeoForge") with { IsPrivate = true },
                Version(100, "beta", "1.21.1", "NeoForge"),
                Version(99, "release", "1.20.1", "Forge")),
            Pack(11, "Private Pack", true, Version(110, "release", "1.21.1", "NeoForge")),
            Pack(12, "Wrong Loader", false, Version(120, "release", "1.21.1", "Fabric")),
        };

        var page = FtbClientCatalog.MapAndFilter(
            packs,
            new FtbClientCatalogRequest(
                GameVersion: "1.21.1",
                Loader: MinecraftClientLoader.NeoForge));

        var project = Assert.Single(page.Projects);
        Assert.Equal(10, project.PackId);
        var version = Assert.Single(project.StableVersions);
        Assert.Equal(101, version.VersionId);
        Assert.Equal("1.21.1", version.GameVersion);
    }

    [Fact]
    public void MapAndFilter_PreservesOfficialOrderAndMapsArtworkAndMetadata()
    {
        var preview = new Uri("https://cdn.feed-the-beast.com/preview.png");
        var icon = new Uri("https://cdn.feed-the-beast.com/icon.png");
        var first = Pack(2, "First", false, Version(20, "release", "1.20.1", "Forge")) with
        {
            Synopsis = "Official synopsis",
            InstallCount = 1234,
            Artwork =
            [
                new FtbArtwork(preview, "splash", 1280, 720),
                new FtbArtwork(icon, "square", 256, 256),
            ],
        };
        var second = Pack(1, "Second", false, Version(10, "release", "1.19.2", "Forge"));

        var page = FtbClientCatalog.MapAndFilter(
            [first, second],
            new FtbClientCatalogRequest(Limit: 20));

        Assert.Equal([2, 1], page.Projects.Select(project => project.PackId));
        Assert.Equal("Official synopsis", page.Projects[0].Description);
        Assert.Equal(1234, page.Projects[0].Installs);
        Assert.Equal(icon, page.Projects[0].IconUri);
        Assert.Equal(preview, page.Projects[0].PreviewImageUri);
    }

    [Fact]
    public void MapAndFilter_LeavesMissingPresentationTextCultureNeutral()
    {
        var page = FtbClientCatalog.MapAndFilter(
            [
                new FtbPack(
                    7,
                    "Metadata-free pack",
                    "metadata-free-pack",
                    false,
                    [
                        new FtbPackVersion(
                            70,
                            "Stable",
                            "release",
                            DateTimeOffset.Parse("2026-08-01T00:00:00Z").ToUnixTimeMilliseconds(),
                            []),
                    ]),
            ],
            new FtbClientCatalogRequest());

        var project = Assert.Single(page.Projects);
        Assert.Equal(string.Empty, project.Description);
        var version = Assert.Single(project.StableVersions);
        Assert.Equal(string.Empty, version.GameVersion);
        Assert.Null(version.LoaderName);
        Assert.DoesNotMatch("[\\u3400-\\u9fff]", project.Description + version.GameVersion);
    }

    [Fact]
    public void AppProtocol_CreatesAndValidatesOnlyExactPositivePackUri()
    {
        var uri = FtbAppProtocol.CreateInstallUri(130);

        Assert.Equal("ftb://modpack/install?packId=130", uri.AbsoluteUri);
        Assert.True(FtbAppProtocol.TryReadInstallPackId(uri, out var packId));
        Assert.Equal(130, packId);
        Assert.False(FtbAppProtocol.TryReadInstallPackId(
            new Uri("https://modpack/install?packId=130"),
            out _));
        Assert.False(FtbAppProtocol.TryReadInstallPackId(
            new Uri("ftb://modpack/install?packId=130&extra=1"),
            out _));
        Assert.False(FtbAppProtocol.TryReadInstallPackId(
            new Uri("ftb://modpack/install?packId=-1"),
            out _));
        Assert.Equal("https://www.feed-the-beast.com/ftb-app", FtbAppProtocol.OfficialDownloadPage.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void Request_RejectsUnboundedInputs()
    {
        var limitError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FtbClientCatalogRequest(Limit: 101).Validate());
        var queryError = Assert.Throws<ArgumentException>(() =>
            new FtbClientCatalogRequest(Query: new string('x', 201)).Validate());
        var gameVersionError = Assert.Throws<ArgumentException>(() =>
            new FtbClientCatalogRequest(GameVersion: new string('1', 65)).Validate());
        var packIdError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            FtbAppProtocol.CreateInstallUri(0));

        AssertValidationFailure(
            limitError,
            FtbClientValidationFailure.ResultLimitOutOfRange);
        AssertValidationFailure(queryError, FtbClientValidationFailure.QueryTooLong);
        AssertValidationFailure(
            gameVersionError,
            FtbClientValidationFailure.GameVersionTooLong);
        AssertValidationFailure(packIdError, FtbClientValidationFailure.InvalidPackId);
    }

    private static void AssertValidationFailure(
        Exception error,
        FtbClientValidationFailure expected)
    {
        Assert.True(FtbClientValidation.TryGetFailure(error, out var actual));
        Assert.Equal(expected, actual);
        Assert.DoesNotMatch("[\\u3400-\\u9fff]", error.Message);
    }

    private static FtbPack Pack(
        int id,
        string name,
        bool isPrivate,
        params FtbPackVersion[] versions)
        => new(id, name, name.ToLowerInvariant(), isPrivate, versions);

    private static FtbPackVersion Version(
        int id,
        string type,
        string gameVersion,
        string loader)
        => new(
            id,
            $"Version {id}",
            type,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z").ToUnixTimeMilliseconds() + id,
            [
                new FtbTarget("game", "minecraft", gameVersion),
                new FtbTarget("modloader", loader, "1.0"),
            ]);
}
