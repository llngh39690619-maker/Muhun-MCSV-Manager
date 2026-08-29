using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class ModrinthClientContentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-modrinth-content-tests",
        Guid.NewGuid().ToString("N"));

    public ModrinthClientContentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SearchAsync_UsesContentVersionLoaderAndTextFilters()
    {
        var handler = new RecordingHandler(request => Json(request, """
            {
              "hits": [{
                "project_id": "ProjectA",
                "project_type": "mod",
                "slug": "project-a",
                "title": "Project A",
                "description": "A test mod",
                "author": "Author",
                "client_side": "required",
                "icon_url": "https://cdn.modrinth.com/data/ProjectA/icon.png",
                "versions": ["1.21.1"],
                "categories": ["neoforge"],
                "downloads": 42,
                "date_modified": "2026-08-01T00:00:00Z"
              }],
              "offset": 0,
              "limit": 20,
              "total_hits": 1
            }
            """));
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientContentCatalog(client, "X-MCSV-Tests/1.0");

        var result = await catalog.SearchAsync(new ModrinthClientContentSearchRequest(
            MinecraftClientContentKind.Mod,
            "storage",
            "1.21.1",
            MinecraftClientLoader.NeoForge,
            ModrinthClientContentSort.Updated));

        var project = Assert.Single(result.Projects);
        Assert.Equal(MinecraftClientContentKind.Mod, project.Kind);
        Assert.Equal("https://modrinth.com/mod/project-a", project.ProjectPageUri.AbsoluteUri);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("query=storage", query, StringComparison.Ordinal);
        Assert.Contains("project_type:mod", query, StringComparison.Ordinal);
        Assert.Contains("versions:1.21.1", query, StringComparison.Ordinal);
        Assert.Contains("categories:neoforge", query, StringComparison.Ordinal);
        Assert.Contains("index=updated", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectStableVersionAsync_RejectsBetaAndPostFiltersCompatibility()
    {
        var handler = new RecordingHandler(request => Json(request, """
            [{
              "project_id": "ProjectA",
              "id": "WrongLoader",
              "name": "Wrong Loader",
              "version_number": "1",
              "version_type": "release",
              "status": "listed",
              "game_versions": ["1.21.1"],
              "loaders": ["fabric"],
              "date_published": "2026-08-03T00:00:00Z",
              "files": []
            }, {
              "project_id": "ProjectA",
              "id": "BetaVersion",
              "name": "Beta",
              "version_number": "2-beta",
              "version_type": "beta",
              "status": "listed",
              "game_versions": ["1.21.1"],
              "loaders": ["forge"],
              "date_published": "2026-08-04T00:00:00Z",
              "files": []
            }, {
              "project_id": "ProjectA",
              "id": "StableVersion",
              "name": "Stable",
              "version_number": "1.0.0",
              "version_type": "release",
              "status": "listed",
              "game_versions": ["1.21.1"],
              "loaders": ["forge"],
              "date_published": "2026-08-02T00:00:00Z",
              "files": []
            }]
            """));
        using var client = new HttpClient(handler);
        var catalog = new ModrinthClientContentCatalog(client, "X-MCSV-Tests/1.0");

        var selected = await catalog.SelectStableVersionAsync(
            "ProjectA",
            "1.21.1",
            MinecraftClientLoader.Forge);

        Assert.Equal("StableVersion", selected.VersionId);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("game_versions=[\"1.21.1\"]", query, StringComparison.Ordinal);
        Assert.Contains("loaders=[\"forge\"]", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_ResolvesRequiredDependenciesRecursivelyDeduplicatesAndBreaksCycles()
    {
        var sha = new string('a', 128);
        var projects = new[]
        {
            Project("RootProject", "root", "Root", MinecraftClientContentKind.Mod),
            Project("DependencyB", "dependency-b", "Dependency B", MinecraftClientContentKind.Mod),
            Project("DependencyC", "dependency-c", "Dependency C", MinecraftClientContentKind.Mod),
        };
        var versions = new[]
        {
            Version("RootProject", "RootVersion", "root.jar", sha,
                [Required(projectId: "DependencyB"), Required(versionId: "VersionC")]),
            Version("DependencyB", "VersionB", "dependency-b.jar", sha,
                [Required(projectId: "DependencyC")]),
            Version("DependencyC", "VersionC", "dependency-c.jar", sha,
                [Required(projectId: "RootProject")]),
        };
        var catalog = new FakeCatalog(projects, versions);
        using var installer = CreateInstaller(
            catalog,
            new DownloadHandler(new Dictionary<string, byte[]>()));

        var plan = await installer.PlanAsync(
            "RootProject",
            MinecraftClientContentKind.Mod,
            "1.21.1",
            MinecraftClientLoader.Forge);

        Assert.True(plan.CanInstallAutomatically);
        Assert.Equal(MinecraftClientLoader.Forge, plan.RequiredLoader);
        Assert.Equal(
            new[] { "DependencyC", "DependencyB", "RootProject" },
            plan.Artifacts.Select(static artifact => artifact.ProjectId));
        Assert.Equal(3, plan.Artifacts.Select(static artifact => artifact.ProjectId).Distinct().Count());
        Assert.Empty(plan.Fallbacks);
    }

    [Fact]
    public async Task PlanAsync_ReturnsOfficialFallbackWhenFileCannotBeVerified()
    {
        var project = Project("RootProject", "root", "Root", MinecraftClientContentKind.Mod);
        var version = Version("RootProject", "RootVersion", "root.jar", sha512: null);
        var catalog = new FakeCatalog([project], [version]);
        using var installer = CreateInstaller(
            catalog,
            new DownloadHandler(new Dictionary<string, byte[]>()));

        var plan = await installer.PlanAsync(
            project.ProjectId,
            project.Kind,
            "1.21.1",
            MinecraftClientLoader.Forge);

        Assert.False(plan.CanInstallAutomatically);
        var fallback = Assert.Single(plan.Fallbacks);
        Assert.Equal(ModrinthClientContentFallbackReason.MissingVerifiedFile, fallback.Reason);
        Assert.Equal(
            "https://modrinth.com/mod/root/version/RootVersion",
            fallback.VersionPageUri.AbsoluteUri);
        Assert.Equal("cdn.modrinth.com", Assert.IsType<Uri>(fallback.DirectDownloadUri).Host);
    }

    [Fact]
    public async Task InstallAsync_VerifiesHashesAndAtomicallyInstallsModWithDependency()
    {
        var rootPayload = Encoding.UTF8.GetBytes("root mod payload");
        var dependencyPayload = Encoding.UTF8.GetBytes("dependency payload");
        var project = Project("RootProject", "root", "Root", MinecraftClientContentKind.Mod);
        var dependency = Project(
            "DependencyB",
            "dependency-b",
            "Dependency B",
            MinecraftClientContentKind.Mod);
        var rootVersion = Version(
            project.ProjectId,
            "RootVersion",
            "root.jar",
            Sha512(rootPayload),
            [Required(projectId: dependency.ProjectId)],
            rootPayload.Length,
            Sha1(rootPayload));
        var dependencyVersion = Version(
            dependency.ProjectId,
            "DependencyVersion",
            "dependency.jar",
            Sha512(dependencyPayload),
            size: dependencyPayload.Length,
            sha1: Sha1(dependencyPayload));
        var catalog = new FakeCatalog([project, dependency], [rootVersion, dependencyVersion]);
        var handler = new DownloadHandler(new Dictionary<string, byte[]>
        {
            ["root.jar"] = rootPayload,
            ["dependency.jar"] = dependencyPayload,
        });
        using var installer = CreateInstaller(catalog, handler);
        var instance = Path.Combine(_root, "mod-instance");
        Directory.CreateDirectory(instance);

        var result = await installer.InstallAsync(new ModrinthClientContentInstallRequest(
            instance,
            project.ProjectId,
            project.Kind,
            "1.21.1",
            MinecraftClientLoader.Forge));

        Assert.True(result.Installed);
        Assert.Equal(2, result.InstalledEntries.Count);
        Assert.Equal(rootPayload, await File.ReadAllBytesAsync(Path.Combine(instance, "mods", "root.jar")));
        Assert.Equal(
            dependencyPayload,
            await File.ReadAllBytesAsync(Path.Combine(instance, "mods", "dependency.jar")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "staging")));
    }

    [Theory]
    [InlineData(MinecraftClientContentKind.ResourcePack, "resourcepacks", "pack.zip")]
    [InlineData(MinecraftClientContentKind.ShaderPack, "shaderpacks", "shader.zip")]
    public async Task InstallAsync_WritesZipContentToTheCorrectInstanceDirectory(
        MinecraftClientContentKind kind,
        string directoryName,
        string fileName)
    {
        var payload = Encoding.UTF8.GetBytes($"{kind} payload");
        var project = Project("ContentProject", "content", "Content", kind);
        var version = Version(
            project.ProjectId,
            "ContentVersion",
            fileName,
            Sha512(payload),
            size: payload.Length,
            sha1: Sha1(payload),
            loaders: []);
        var catalog = new FakeCatalog([project], [version]);
        using var installer = CreateInstaller(
            catalog,
            new DownloadHandler(new Dictionary<string, byte[]> { [fileName] = payload }));
        var instance = Path.Combine(_root, $"{kind}-instance");
        Directory.CreateDirectory(instance);

        var result = await installer.InstallAsync(new ModrinthClientContentInstallRequest(
            instance,
            project.ProjectId,
            kind,
            "1.21.1"));

        Assert.True(result.Installed);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(instance, directoryName, fileName)));
    }

    [Fact]
    public async Task InstallAsync_HashMismatchLeavesInstanceUntouchedAndReturnsDirectFallback()
    {
        var payload = Encoding.UTF8.GetBytes("tampered payload");
        var project = Project("RootProject", "root", "Root", MinecraftClientContentKind.Mod);
        var version = Version(
            project.ProjectId,
            "RootVersion",
            "root.jar",
            new string('0', 128),
            size: payload.Length,
            sha1: new string('0', 40));
        var catalog = new FakeCatalog([project], [version]);
        using var installer = CreateInstaller(
            catalog,
            new DownloadHandler(new Dictionary<string, byte[]> { ["root.jar"] = payload }));
        var instance = Path.Combine(_root, "failed-instance");
        Directory.CreateDirectory(instance);

        var result = await installer.InstallAsync(new ModrinthClientContentInstallRequest(
            instance,
            project.ProjectId,
            project.Kind,
            "1.21.1",
            MinecraftClientLoader.Forge));

        Assert.False(result.Installed);
        Assert.Empty(result.InstalledEntries);
        Assert.False(Directory.Exists(Path.Combine(instance, "mods")));
        var fallback = Assert.Single(result.Fallbacks);
        Assert.Equal(ModrinthClientContentFallbackReason.DownloadFailed, fallback.Reason);
        Assert.Equal("cdn.modrinth.com", Assert.IsType<Uri>(fallback.DirectDownloadUri).Host);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task InstallAsync_ModWithoutKnownInstanceLoaderFailsClosedBeforeNetworkOrDiskMutation()
    {
        var project = Project("RootProject", "root", "Root", MinecraftClientContentKind.Mod);
        var version = Version("RootProject", "RootVersion", "root.jar", new string('a', 128));
        var catalog = new FakeCatalog([project], [version]);
        using var installer = CreateInstaller(
            catalog,
            new DownloadHandler(new Dictionary<string, byte[]>()));
        var instance = Path.Combine(_root, "unknown-loader-instance");
        Directory.CreateDirectory(instance);

        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            new ModrinthClientContentInstallRequest(
                instance,
                project.ProjectId,
                project.Kind,
                "1.21.1")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(instance));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ModrinthClientContentInstaller CreateInstaller(
        IModrinthClientContentCatalog catalog,
        HttpMessageHandler handler)
        => new(
            Path.Combine(_root, "staging"),
            catalog,
            new HttpClient(handler, disposeHandler: true));

    private static ModrinthClientContentProject Project(
        string id,
        string slug,
        string title,
        MinecraftClientContentKind kind)
        => new(
            id,
            slug,
            kind,
            title,
            string.Empty,
            "Author",
            null,
            ["1.21.1"],
            kind == MinecraftClientContentKind.Mod ? ["forge"] : [],
            0,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            new Uri($"https://modrinth.com/{ProjectType(kind)}/{slug}"));

    private static ModrinthClientContentVersion Version(
        string projectId,
        string versionId,
        string fileName,
        string? sha512,
        IReadOnlyList<ModrinthClientContentDependency>? dependencies = null,
        long size = 1,
        string? sha1 = null,
        IReadOnlyList<string>? loaders = null)
        => new(
            projectId,
            versionId,
            versionId,
            "1.0.0",
            ["1.21.1"],
            loaders ?? ["forge"],
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            [new ModrinthClientContentFile(
                fileName,
                new Uri($"https://cdn.modrinth.com/data/{projectId}/versions/{versionId}/{fileName}"),
                size,
                sha512,
                sha1,
                true)],
            dependencies ?? []);

    private static ModrinthClientContentDependency Required(
        string? projectId = null,
        string? versionId = null)
        => new(projectId, versionId, null, ModrinthClientDependencyKind.Required);

    private static string ProjectType(MinecraftClientContentKind kind) => kind switch
    {
        MinecraftClientContentKind.Mod => "mod",
        MinecraftClientContentKind.ResourcePack => "resourcepack",
        MinecraftClientContentKind.ShaderPack => "shader",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Sha512(byte[] payload)
        => Convert.ToHexString(SHA512.HashData(payload)).ToLowerInvariant();

    private static string Sha1(byte[] payload)
        => Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

    private static HttpResponseMessage Json(HttpRequestMessage request, string json)
        => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeCatalog(
        IEnumerable<ModrinthClientContentProject> projects,
        IEnumerable<ModrinthClientContentVersion> versions)
        : IModrinthClientContentCatalog
    {
        private readonly IReadOnlyDictionary<string, ModrinthClientContentProject> _projects =
            projects.ToDictionary(static project => project.ProjectId, StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, ModrinthClientContentVersion> _versions =
            versions.ToDictionary(static version => version.VersionId, StringComparer.Ordinal);

        public Task<ModrinthClientContentSearchPage> SearchAsync(
            ModrinthClientContentSearchRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModrinthClientContentProject> GetProjectAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_projects[projectId]);
        }

        public Task<IReadOnlyList<ModrinthClientContentVersion>> GetStableVersionsAsync(
            string projectId,
            string gameVersion,
            MinecraftClientLoader? loader = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ModrinthClientContentVersion> result = _versions.Values
                .Where(version => string.Equals(version.ProjectId, projectId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<ModrinthClientContentVersion> GetStableVersionAsync(
            string versionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_versions[versionId]);
        }

        public async Task<ModrinthClientContentVersion> SelectStableVersionAsync(
            string projectId,
            string gameVersion,
            MinecraftClientLoader? loader = null,
            CancellationToken cancellationToken = default)
            => (await GetStableVersionsAsync(projectId, gameVersion, loader, cancellationToken))[0];
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class DownloadHandler(IReadOnlyDictionary<string, byte[]> payloads)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Uri.UnescapeDataString(Path.GetFileName(request.RequestUri!.AbsolutePath));
            var response = payloads.TryGetValue(fileName, out var payload)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
