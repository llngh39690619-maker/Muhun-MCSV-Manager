using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class OnlineModpackProvenanceTests
{
    [Fact]
    public void ApplyVerifiedModpackProvenance_MapsFtbIdentityAndInstalledVersionName()
    {
        var instance = new ServerInstance();
        var request = CreateRequest(
            OnlineModpackProvider.Ftb,
            projectId: "42",
            versionId: "314",
            versionName: "Catalog label");

        OnlineModpackWorkflow.ApplyVerifiedModpackProvenance(
            instance,
            request,
            OnlineModpackProvider.Ftb,
            verifiedProjectId: "42",
            verifiedVersionId: "314",
            verifiedVersionName: "1.7.0");

        Assert.Equal(ModpackSourceKind.Ftb, instance.ModpackSource);
        Assert.Equal("42", instance.ModpackProjectId);
        Assert.Equal("314", instance.ModpackVersionId);
        Assert.Equal("1.7.0", instance.ModpackVersionName);
    }

    [Fact]
    public void ApplyVerifiedModpackProvenance_MapsModrinthIdentityAndApiVersionNumber()
    {
        var instance = new ServerInstance();
        var request = CreateRequest(
            OnlineModpackProvider.Modrinth,
            projectId: "project-id",
            versionId: "version-id",
            versionName: "Pretty title (1.7.0)");

        OnlineModpackWorkflow.ApplyVerifiedModpackProvenance(
            instance,
            request,
            OnlineModpackProvider.Modrinth,
            verifiedProjectId: "project-id",
            verifiedVersionId: "version-id",
            verifiedVersionName: "1.7.0");

        Assert.Equal(ModpackSourceKind.Modrinth, instance.ModpackSource);
        Assert.Equal("project-id", instance.ModpackProjectId);
        Assert.Equal("version-id", instance.ModpackVersionId);
        Assert.Equal("1.7.0", instance.ModpackVersionName);
    }

    [Fact]
    public void ApplyVerifiedModpackProvenance_FallsBackToCatalogLabelWhenArtifactHasNoLabel()
    {
        var instance = new ServerInstance();
        var request = CreateRequest(
            OnlineModpackProvider.Ftb,
            projectId: "7",
            versionId: "9",
            versionName: "  1.6.0  ");

        OnlineModpackWorkflow.ApplyVerifiedModpackProvenance(
            instance,
            request,
            OnlineModpackProvider.Ftb,
            verifiedProjectId: "7",
            verifiedVersionId: "9",
            verifiedVersionName: null);

        Assert.Equal("1.6.0", instance.ModpackVersionName);
    }

    [Fact]
    public void ApplyVerifiedModpackProvenance_IdentityMismatchFailsBeforeMutation()
    {
        var instance = new ServerInstance();
        var request = CreateRequest(
            OnlineModpackProvider.Modrinth,
            projectId: "selected-project",
            versionId: "selected-version",
            versionName: "1.6.0");

        Assert.Throws<InvalidDataException>(() =>
            OnlineModpackWorkflow.ApplyVerifiedModpackProvenance(
                instance,
                request,
                OnlineModpackProvider.Modrinth,
                verifiedProjectId: "different-project",
                verifiedVersionId: "selected-version",
                verifiedVersionName: "1.7.0"));

        Assert.Equal(ModpackSourceKind.None, instance.ModpackSource);
        Assert.Null(instance.ModpackProjectId);
        Assert.Null(instance.ModpackVersionId);
        Assert.Null(instance.ModpackVersionName);
    }

    [Fact]
    public void ApplyVerifiedModpackProvenance_MapsCurseForgeIdentity()
    {
        var instance = new ServerInstance();
        var request = CreateRequest(
            OnlineModpackProvider.CurseForge,
            projectId: "12",
            versionId: "34",
            versionName: "1.7.0");

        OnlineModpackWorkflow.ApplyVerifiedModpackProvenance(
            instance,
            request,
            OnlineModpackProvider.CurseForge,
            verifiedProjectId: "12",
            verifiedVersionId: "34",
            verifiedVersionName: "1.7.0");

        Assert.Equal(ModpackSourceKind.CurseForge, instance.ModpackSource);
        Assert.Equal("12", instance.ModpackProjectId);
        Assert.Equal("34", instance.ModpackVersionId);
        Assert.Equal("1.7.0", instance.ModpackVersionName);
    }

    private static OnlineModpackInstallRequest CreateRequest(
        OnlineModpackProvider provider,
        string projectId,
        string versionId,
        string versionName)
    {
        var project = new OnlineModpackSearchResult(
            provider,
            projectId,
            "Example Pack",
            "Summary",
            "Author");
        var version = new OnlineModpackVersion(
            provider,
            projectId,
            versionId,
            versionName,
            "1.21.1",
            "NeoForge",
            "release",
            DateTimeOffset.UnixEpoch,
            HasOfficialServerPack: true);
        return new OnlineModpackInstallRequest(project, version, "Example Server");
    }
}
