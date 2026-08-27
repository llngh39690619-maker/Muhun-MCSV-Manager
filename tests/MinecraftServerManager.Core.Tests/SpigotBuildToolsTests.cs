using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class SpigotBuildToolsTests
{
    private const string UserAgent = "Muhun-MCSV-Manager.Tests/1.0";

    [Fact]
    public async Task Catalog_ListsOfficialHashAndSourceVerifiedReleaseVersions()
    {
        using var metadata = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/versions/" => Text("""
                <a href="1.8.json">1.8.json</a>
                <a href="1.8.8.json">1.8.8.json</a>
                <a href="1.20.4.json">1.20.4.json</a>
                <a href="1.21.11.json">1.21.11.json</a>
                <a href="26.2.json">26.2.json</a>
                <a href="1.22-pre1.json">1.22-pre1.json</a>
                <a href="27.1.json">27.1.json</a>
                <a href="1000.json">1000.json</a>
                """),
            "/versions/1.8.json" => Json(Legacy18VersionJson()
                .Replace("\"582b\"", "\"1.8\"", StringComparison.Ordinal)),
            "/versions/1.8.8.json" => Json(Legacy18VersionJson()),
            "/versions/1.20.4.json" => Json(VersionJson(includeHashes: false)),
            "/versions/1.21.11.json" => Json(VersionJson(includeHashes: true)),
            "/versions/26.2.json" => Json(
                VersionJson(includeHashes: true)
                    .Replace("[65, 70]", "[69, 70]", StringComparison.Ordinal)),
            _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri)
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var versions = await provider.GetVersionsAsync();

        Assert.Equal(
            ["26.2", "1.21.11", "1.20.4", "1.8.8", "1.8"],
            versions.Select(version => version.MinecraftVersion));
        Assert.Equal([25, 21, 21, 8, 8], versions.Select(version => version.JavaMajorVersion));
        Assert.Equal(
            [
                SpigotBuildOutputVerificationKind.OfficialOutputSha256,
                SpigotBuildOutputVerificationKind.OfficialOutputSha256,
                SpigotBuildOutputVerificationKind.OfficialSourceRefs,
                SpigotBuildOutputVerificationKind.OfficialSourceRefs,
                SpigotBuildOutputVerificationKind.OfficialSourceRefs
            ],
            versions.Select(version => version.VerificationKind));
        Assert.All(versions, version => Assert.True(version.IsSupported));
    }

    [Fact]
    public async Task ResolvePlan_LabelsBukkitAsCraftBukkitAndUsesOfficialTargetHash()
    {
        var requestedPaths = new List<string>();
        using var metadata = Client(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Json(VersionJson(includeHashes: true));
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var resolution = await provider.ResolvePlanAsync(CoreType.CraftBukkit, "1.21.11");

        Assert.True(resolution.IsSupported);
        Assert.Equal("CraftBukkit (Bukkit)", resolution.Plan!.DisplayName);
        Assert.Equal(new string('c', 64), resolution.Plan.ExpectedOutputSha256);
        Assert.Equal(21, resolution.Plan.JavaMajorVersion);
        Assert.Equal("server.jar", resolution.Plan.OutputFileName);
        Assert.Equal("4598", resolution.Plan.VersionIdentity);
        Assert.Equal(["/versions/1.21.11.json", "/versions/4598.json"], requestedPaths);
    }

    [Fact]
    public async Task CatalogIsCachedButEveryPlanFreshlyResolvesAliasAndImmutableIdentity()
    {
        var indexRequests = 0;
        var aliasRequests = 0;
        var identityRequests = 0;
        using var metadata = Client(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/versions/":
                    Interlocked.Increment(ref indexRequests);
                    return Text("""<a href="26.2.json">26.2.json</a>""");
                case "/versions/26.2.json":
                    Interlocked.Increment(ref aliasRequests);
                    return Json(VersionJson(includeHashes: true));
                case "/versions/4598.json":
                    Interlocked.Increment(ref identityRequests);
                    return Json(VersionJson(includeHashes: true));
                default:
                    throw new InvalidOperationException(request.RequestUri.AbsoluteUri);
            }
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        _ = await provider.GetVersionsAsync();
        _ = await provider.GetVersionsAsync();
        _ = await provider.ResolvePlanAsync(CoreType.Spigot, "26.2");
        _ = await provider.ResolvePlanAsync(CoreType.CraftBukkit, "26.2");

        Assert.Equal(1, indexRequests);
        Assert.Equal(3, aliasRequests);
        Assert.Equal(2, identityRequests);
    }

    [Fact]
    public async Task FailedVersionMetadataIsNotCached()
    {
        var requests = 0;
        using var metadata = Client(_ =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return Json(VersionJson(includeHashes: true));
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.ResolvePlanAsync(CoreType.Spigot, "26.2"));
        var resolution = await provider.ResolvePlanAsync(CoreType.Spigot, "26.2");

        Assert.True(resolution.IsSupported);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("refs.BuildData")]
    [InlineData("refs.Bukkit")]
    [InlineData("refs.CraftBukkit")]
    [InlineData("refs.Spigot")]
    [InlineData("hashes.CraftBukkit")]
    [InlineData("hashes.Spigot")]
    [InlineData("toolsVersion")]
    [InlineData("javaVersions")]
    public async Task ResolvePlan_FailsClosedWhenAliasAndImmutableDefinitionDiffer(string field)
    {
        using var metadata = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/versions/26.2.json" => Json(VersionJson(includeHashes: true)),
            "/versions/4598.json" => Json(MismatchedVersionJson(field)),
            _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri)
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ResolvePlanAsync(CoreType.Spigot, "26.2"));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvePlan_RejectsNonCanonicalIdentityBeforeNumericRequest()
    {
        var requests = 0;
        using var metadata = Client(_ =>
        {
            Interlocked.Increment(ref requests);
            return Json(
                VersionJson(includeHashes: true)
                    .Replace("\"4598\"", "\"04598\"", StringComparison.Ordinal));
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ResolvePlanAsync(CoreType.Spigot, "26.2"));

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ResolvePlan_FailsClosedWhenImmutableDefinitionIsMissing()
    {
        using var metadata = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/versions/26.2.json" => Json(VersionJson(includeHashes: true)),
            "/versions/4598.json" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri)
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ResolvePlanAsync(CoreType.Spigot, "26.2"));

        Assert.Contains("4598.json", exception.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public void VersionIdentityUriPolicy_AllowsCanonicalNumericAndHistoricalIdentityPaths()
    {
        Assert.True(SpigotBuildToolsProvider.IsSpigotVersionIdentityUri(
            new Uri("https://hub.spigotmc.org/versions/4598.json")));
        Assert.True(SpigotBuildToolsProvider.IsSpigotVersionIdentityUri(
            new Uri("https://hub.spigotmc.org/versions/582b.json")));
        Assert.True(SpigotBuildToolsProvider.IsSpigotVersionIdentityUri(
            new Uri("https://hub.spigotmc.org/versions/4195-b.json")));

        var rejected = new[]
        {
            "http://hub.spigotmc.org/versions/4598.json",
            "https://example.com/versions/4598.json",
            "https://hub.spigotmc.org:444/versions/4598.json",
            "https://user@hub.spigotmc.org/versions/4598.json",
            "https://hub.spigotmc.org/versions/1.21.11.json",
            "https://hub.spigotmc.org/versions/04598.json",
            "https://hub.spigotmc.org/versions/0.json",
            "https://hub.spigotmc.org/versions/4598.json?next=1",
            "https://hub.spigotmc.org/versions/4598.json#fragment",
            "https://hub.spigotmc.org/versions/nested/4598.json"
        };
        Assert.All(
            rejected,
            value => Assert.False(
                SpigotBuildToolsProvider.IsSpigotVersionIdentityUri(new Uri(value))));
    }

    [Fact]
    public async Task ResolvePlan_UsesOfficialSourceRefsWhenOldDefinitionHasNoOutputHash()
    {
        using var metadata = Client(_ => Json(VersionJson(includeHashes: false)));
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var resolution = await provider.ResolvePlanAsync(CoreType.Spigot, "1.20.4");

        Assert.True(resolution.IsSupported);
        Assert.NotNull(resolution.Plan);
        Assert.Equal(
            SpigotBuildOutputVerificationKind.OfficialSourceRefs,
            resolution.Plan!.OutputVerificationKind);
        Assert.Null(resolution.Plan.ExpectedOutputSha256);
        Assert.Equal("4598", resolution.Plan.BuildRevision);
        Assert.Null(resolution.UnsupportedReason);
    }

    [Fact]
    public async Task ResolvePlan_LegacyAliasWithoutIdentityEndpointUsesFreshAliasAndJava8Recipe()
    {
        var requests = new List<string>();
        using var metadata = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/versions/1.8.8.json" => Json(Legacy18VersionJson()),
                "/versions/582b.json" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri)
            };
        });
        using var artifact = Client(_ => throw new InvalidOperationException());
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);

        var resolution = await provider.ResolvePlanAsync(CoreType.Spigot, "1.8.8");

        Assert.True(resolution.IsSupported);
        Assert.Equal(8, resolution.Plan!.JavaMajorVersion);
        Assert.Equal(1, resolution.Plan.RequiredBuildToolsVersion);
        Assert.Equal("582b", resolution.Plan.VersionIdentity);
        Assert.Equal("1.8.8", resolution.Plan.BuildRevision);
        Assert.Null(resolution.Plan.ExpectedOutputSha256);
        Assert.Equal(
            SpigotBuildOutputVerificationKind.OfficialSourceRefs,
            resolution.Plan.OutputVerificationKind);
        Assert.Equal(["/versions/1.8.8.json", "/versions/582b.json"], requests);
    }

    [Fact]
    public async Task DownloadBuildTools_RejectsJenkinsIdentityBeforeArtifactRequest()
    {
        using var metadata = Client(_ => Json("""
            {
              "number": 199,
              "building": false,
              "inProgress": false,
              "result": "SUCCESS",
              "actions": [],
              "artifacts": []
            }
            """));
        using var artifact = Client(_ => throw new Xunit.Sdk.XunitException(
            "Artifact request must not happen after metadata mismatch."));
        var provider = new SpigotBuildToolsProvider(metadata, artifact, UserAgent);
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DownloadReviewedBuildToolsAsync(
                Path.Combine(directory.Path, "BuildTools.jar")));
    }

    [Fact]
    public async Task Runner_UsesDirectJavaFreshWorkspaceAndPromotesOnlyVerifiedOutput()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var outputBytes = Encoding.UTF8.GetBytes("verified Spigot output");
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(
            CoreType.CraftBukkit,
            trusted,
            Sha256(outputBytes)) with
        {
            MinecraftVersion = "26.2",
            JavaMajorVersion = 25,
            VersionIdentity = "4647"
        };
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.Equal(java, startInfo.FileName);
            Assert.Equal(
                Path.Combine(directory.Path, "jdk"),
                startInfo.Environment["JAVA_HOME"]);
            Assert.Equal(
                string.Join(
                    Path.PathSeparator,
                    managedGit.CommandDirectory,
                    managedGit.MingwBinDirectory,
                    managedGit.UsrBinDirectory,
                    Path.GetDirectoryName(java),
                    Environment.SystemDirectory),
                startInfo.Environment["PATH"]);
            Assert.Equal(managedGit.ShellExecutablePath, startInfo.Environment["SHELL"]);
            Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
            var privateGlobalConfig = startInfo.Environment["GIT_CONFIG_GLOBAL"];
            Assert.NotNull(privateGlobalConfig);
            Assert.Equal(
                Path.Combine(startInfo.WorkingDirectory, "home", ".gitconfig"),
                privateGlobalConfig);
            Assert.Contains(
                "autocrlf = input",
                File.ReadAllText(privateGlobalConfig));
            Assert.Contains("-Xmx1024M", startInfo.Environment["MAVEN_OPTS"]);
            Assert.Contains("-Duser.home=", startInfo.Environment["MAVEN_OPTS"]);
            var javaOptions = startInfo.Environment["_JAVA_OPTIONS"];
            Assert.NotNull(javaOptions);
            Assert.Equal(
                "-XX:TieredStopAtLevel=1 -Dline.separator=\"\n\"",
                javaOptions);
            Assert.DoesNotContain('\r', javaOptions);
            Assert.Single(javaOptions, character => character == '\n');
            Assert.False(startInfo.Environment.ContainsKey("MAVEN_ARGS"));
            Assert.False(startInfo.Environment.ContainsKey("BASH_ENV"));
            Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG_COUNT"));
            Assert.False(startInfo.Environment.ContainsKey("JAVA_TOOL_OPTIONS"));
            Assert.False(startInfo.Environment.ContainsKey("JDK_JAVA_OPTIONS"));
            var arguments = startInfo.ArgumentList.ToArray();
            Assert.StartsWith("-Duser.home=", arguments[0], StringComparison.Ordinal);
            var jarIndex = Array.IndexOf(arguments, "-jar");
            Assert.True(jarIndex >= 0);
            Assert.Equal(tool, arguments[jarIndex + 1]);
            var revisionIndex = Array.IndexOf(arguments, "--rev");
            Assert.True(revisionIndex >= 0);
            Assert.Equal(plan.VersionIdentity, arguments[revisionIndex + 1]);
            Assert.NotEqual(plan.MinecraftVersion, arguments[revisionIndex + 1]);
            Assert.Contains("--compile", arguments);
            Assert.Contains("craftbukkit", arguments);
            Assert.DoesNotContain("--disable-certificate-check", arguments);
            Assert.DoesNotContain("--dev", arguments);
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            File.WriteAllBytes(
                Path.Combine(outputDirectory, $"craftbukkit-{plan.MinecraftVersion}.jar"),
                outputBytes);
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(
                0,
                ["built"],
                []));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var workspace = new RecordingSpigotBuildToolsWorkspace();
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            workspace);
        var staging = Path.Combine(directory.Path, "staging");
        var destination = Path.Combine(staging, "伺服器 空白", "server.jar");

        var result = await runner.BuildAsync(
            plan,
            java,
            tool,
            staging,
            destination);

        Assert.Equal(destination, result.FilePath);
        Assert.Equal(outputBytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, workspace.PrepareCalls);
        Assert.Equal(1, workspace.VerifyCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Theory]
    [InlineData(CoreType.Spigot)]
    [InlineData(CoreType.CraftBukkit)]
    public async Task Runner_HistoricalSourceVerifiedModeRequiresExactCoreStructureAndLocksPromotionHash(
        CoreType coreType)
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(coreType, trusted, new string('f', 64)) with
        {
            MinecraftVersion = "1.8.8",
            JavaMajorVersion = 8,
            ExpectedOutputSha256 = null,
            RequiredBuildToolsVersion = 1,
            VersionIdentity = "582b",
            OutputVerificationKind = SpigotBuildOutputVerificationKind.OfficialSourceRefs,
            BuildRevision = "1.8.8"
        };
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            Assert.Equal("1.8.8", arguments[Array.IndexOf(arguments, "--rev") + 1]);
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            var outputName = coreType == CoreType.CraftBukkit
                ? "craftbukkit-1.8.8.jar"
                : "server.jar";
            WriteHistoricalBuildToolsJar(Path.Combine(outputDirectory, outputName), coreType);
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            Path.Combine(directory.Path, "local-work"),
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");
        var destination = Path.Combine(staging, "server.jar");

        var result = await runner.BuildAsync(plan, java, tool, staging, destination);

        Assert.Equal(Sha256(await File.ReadAllBytesAsync(destination)), result.ActualOutputSha256);
        Assert.True(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(directory.Path, "local-work")));
    }

    [Fact]
    public async Task Runner_HistoricalSourceVerifiedModeRejectsWrongCoreMarker()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('f', 64)) with
        {
            MinecraftVersion = "1.8.8",
            JavaMajorVersion = 8,
            ExpectedOutputSha256 = null,
            RequiredBuildToolsVersion = 1,
            VersionIdentity = "582b",
            OutputVerificationKind = SpigotBuildOutputVerificationKind.OfficialSourceRefs,
            BuildRevision = "1.8.8"
        };
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            WriteHistoricalBuildToolsJar(
                Path.Combine(outputDirectory, "server.jar"),
                CoreType.CraftBukkit);
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            Path.Combine(directory.Path, "local-work"),
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");
        var destination = Path.Combine(staging, "server.jar");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.BuildAsync(plan, java, tool, staging, destination));

        Assert.Contains("不是選取的 Spigot", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Runner_HashMismatchFailsClosedAndCleansWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('f', 64));
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            File.WriteAllText(Path.Combine(outputDirectory, "server.jar"), "wrong output");
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");
        var destination = Path.Combine(staging, "server.jar");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.BuildAsync(plan, java, tool, staging, destination));

        Assert.Contains($"expected={plan.ExpectedOutputSha256}", exception.Message);
        Assert.Contains(
            $"actual={Sha256(Encoding.UTF8.GetBytes("wrong output"))}",
            exception.Message);
        Assert.Contains($"identity={plan.VersionIdentity}", exception.Message);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Fact]
    public async Task Runner_HotSpotFailureIsRelabeledAndRetainsRedactedForensicsBeforeCleanup()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('f', 64)) with
        {
            MinecraftVersion = "26.2",
            JavaMajorVersion = 25,
            VersionIdentity = "4647"
        };
        var managedGit = CreateManagedGit(directory.Path);
        const string replayName = "replay_pid10420.log";
        const string maliciousReplayName = "replay_pid_email-secret@example.invalid.log";
        var standardOutput = new List<string>
        {
            "INFO: Decompiling class net/minecraft/server/dedicated/DedicatedServer",
            "# Compiler replay data is saved as:",
            "placeholder",
            "Authorization: Bearer bearer-secret-must-not-survive",
            "https://user:url-secret@example.invalid/path?token=query-secret",
            @"\\host-secret\share-secret\file.log",
            @"D:\external-secret\file.log",
            "192.0.2.123 host-ip-secret",
            "mail-secret@example.invalid"
        };
        var processResult = new ModrinthLoaderBootstrapProcessResult(
            1,
            standardOutput,
            [
                "# A fatal error has been detected by the Java Runtime Environment:",
                "# Internal Error (compileBroker.cpp:1) token=token-secret-must-not-survive",
                @"# Problematic frame: C [D:\frame-secret\evil.dll+0x123]"
            ],
            OutputTruncated: true);
        var process = new RecordingRunner(startInfo =>
        {
            var replayPath = Path.Combine(startInfo.WorkingDirectory, replayName);
            File.WriteAllText(replayPath, "bounded test replay");
            File.SetAttributes(replayPath, FileAttributes.ReadOnly);
            File.WriteAllText(
                Path.Combine(startInfo.WorkingDirectory, maliciousReplayName),
                "must not be retained");
            standardOutput[2] = replayPath;
            return Task.FromException<ModrinthLoaderBootstrapProcessResult>(
                new ModrinthLoaderBootstrapProcessException(processResult));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");

        var exception = await Assert.ThrowsAsync<SpigotBuildToolsProcessException>(() =>
            runner.BuildAsync(
                plan,
                java,
                tool,
                staging,
                Path.Combine(staging, "server.jar")));

        Assert.Equal(1, exception.ExitCode);
        Assert.Same(plan, exception.Plan);
        Assert.True(exception.OutputTruncated);
        Assert.True(exception.HotSpotFatalDetected);
        Assert.True(exception.JitCompilerFatalDetected);
        Assert.Contains(exception.ReplayFileNames, value => value.StartsWith(replayName));
        Assert.DoesNotContain(
            exception.ReplayFileNames,
            value => value.Contains("email-secret", StringComparison.Ordinal));
        Assert.Empty(exception.HsErrFileNames);
        Assert.Contains(replayName, exception.DeclaredCrashFileNames);
        Assert.Contains("Spigot BuildTools", exception.Message);
        Assert.Contains("HotSpot JVM JIT", exception.Message);
        Assert.Contains("replay=有", exception.Message);
        Assert.Contains("hs_err=無", exception.Message);
        Assert.Contains("stdout/stderr 已依安全上限截斷", exception.Message);
        Assert.DoesNotContain("ModLoader Installer 結束碼", exception.Message);
        Assert.DoesNotContain(localWorkspace, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer-secret-must-not-survive", exception.Message);
        Assert.DoesNotContain("token-secret-must-not-survive", exception.Message);
        Assert.DoesNotContain("url-secret", exception.Message);
        Assert.DoesNotContain("query-secret", exception.Message);
        Assert.DoesNotContain("host-secret", exception.Message);
        Assert.DoesNotContain("share-secret", exception.Message);
        Assert.DoesNotContain("external-secret", exception.Message);
        Assert.DoesNotContain("frame-secret", exception.Message);
        Assert.DoesNotContain("192.0.2.123", exception.Message);
        Assert.DoesNotContain("mail-secret@example.invalid", exception.Message);
        Assert.DoesNotContain("email-secret@example.invalid", exception.Message);
        Assert.Contains("<WORKSPACE>", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Fact]
    public async Task Runner_HsErrWithoutCompilerEvidenceIsGenericHotSpotNotJitAndCleansWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('f', 64)) with
        {
            MinecraftVersion = "26.2",
            JavaMajorVersion = 25,
            VersionIdentity = "4647"
        };
        var managedGit = CreateManagedGit(directory.Path);
        const string hsErrName = "hs_err_pid777.log";
        const string maliciousHsErrName = "hs_err_pid_C--private.log";
        var standardError = new List<string>
        {
            "# A fatal error has been detected by the Java Runtime Environment:",
            "# Internal Error (vmError.cpp:1) generic-secret-must-not-survive",
            "placeholder"
        };
        var processResult = new ModrinthLoaderBootstrapProcessResult(
            1,
            ["ordinary unclassified output must-not-survive"],
            standardError);
        var process = new RecordingRunner(startInfo =>
        {
            var hsErrPath = Path.Combine(startInfo.WorkingDirectory, hsErrName);
            File.WriteAllText(hsErrPath, "bounded test hs_err");
            File.SetAttributes(hsErrPath, FileAttributes.ReadOnly);
            File.WriteAllText(
                Path.Combine(startInfo.WorkingDirectory, maliciousHsErrName),
                "must not be retained");
            standardError[2] = $"# An error report file with more information is saved as: {hsErrPath}";
            return Task.FromException<ModrinthLoaderBootstrapProcessResult>(
                new ModrinthLoaderBootstrapProcessException(processResult));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");

        var exception = await Assert.ThrowsAsync<SpigotBuildToolsProcessException>(() =>
            runner.BuildAsync(
                plan,
                java,
                tool,
                staging,
                Path.Combine(staging, "server.jar")));

        Assert.True(exception.HotSpotFatalDetected);
        Assert.False(exception.JitCompilerFatalDetected);
        Assert.Empty(exception.ReplayFileNames);
        Assert.Contains(exception.HsErrFileNames, value => value.StartsWith(hsErrName));
        Assert.DoesNotContain(
            exception.HsErrFileNames,
            value => value.Contains("C--private", StringComparison.Ordinal));
        Assert.Contains(hsErrName, exception.DeclaredCrashFileNames);
        Assert.Contains("HotSpot JVM 致命錯誤", exception.Message);
        Assert.Contains("不會誤判為 JIT", exception.Message);
        Assert.DoesNotContain("HotSpot JVM JIT 編譯器致命錯誤", exception.Message);
        Assert.DoesNotContain("本次已使用受控 C1 模式", exception.Message);
        Assert.DoesNotContain("ModLoader Installer", exception.Message);
        Assert.DoesNotContain("generic-secret-must-not-survive", exception.Message);
        Assert.DoesNotContain("ordinary unclassified output must-not-survive", exception.Message);
        Assert.DoesNotContain("C--private", exception.Message);
        Assert.DoesNotContain(localWorkspace, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<WORKSPACE>", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Fact]
    public async Task Runner_NonZeroInjectedResultUsesSpigotLabelAndCleansWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.CraftBukkit, trusted, new string('f', 64));
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var javaOptions = startInfo.Environment["_JAVA_OPTIONS"];
            Assert.NotNull(javaOptions);
            Assert.Equal(
                "-Dline.separator=\"\n\"",
                javaOptions);
            Assert.DoesNotContain("TieredStopAtLevel", javaOptions);
            return Task.FromResult(
                new ModrinthLoaderBootstrapProcessResult(
                    7,
                    ["ordinary failure", "Internal Error fake-secret-must-not-survive"],
                    []));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");

        var exception = await Assert.ThrowsAsync<SpigotBuildToolsProcessException>(() =>
            runner.BuildAsync(
                plan,
                java,
                tool,
                staging,
                Path.Combine(staging, "server.jar")));

        Assert.Equal(7, exception.ExitCode);
        Assert.False(exception.HotSpotFatalDetected);
        Assert.False(exception.JitCompilerFatalDetected);
        Assert.Contains("Spigot BuildTools CraftBukkit (Bukkit)", exception.Message);
        Assert.DoesNotContain("ModLoader Installer", exception.Message);
        Assert.DoesNotContain("fake-secret-must-not-survive", exception.Message);
        Assert.Empty(exception.RedactedDiagnosticLines);
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Fact]
    public async Task ManagedGitWorkspace_ConfiguresAutoCrlfBeforeCheckoutAndPostVerifiesAllRepos()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var operation = Path.Combine(directory.Path, "operation");
        Directory.CreateDirectory(operation);
        var managedGit = CreateManagedGit(directory.Path);
        var plan = TestPlan(
            CoreType.Spigot,
            TestArtifact(Encoding.UTF8.GetBytes("tool")),
            new string('f', 64));
        var gitProcess = new RecordingManagedGitWorkspaceRunner(plan);
        var workspace = new SpigotBuildToolsManagedGitWorkspace(gitProcess);

        await workspace.PrepareAsync(plan, operation, managedGit);

        var prepared = gitProcess.Commands.ToArray();
        Assert.Equal(4, prepared.Count(command => command.Arguments[0] == "clone"));
        foreach (var repositoryName in new[] { "BuildData", "Bukkit", "CraftBukkit", "Spigot" })
        {
            var repositoryPath = Path.Combine(operation, repositoryName);
            var cloneIndex = Array.FindIndex(prepared, command =>
                command.Arguments[0] == "clone"
                && command.Arguments[^1].Equals(repositoryPath, StringComparison.Ordinal));
            var setConfigIndex = Array.FindIndex(prepared, command =>
                IsRepositoryCommand(command, repositoryPath, "config", "--replace-all"));
            var readConfigIndex = Array.FindIndex(prepared, command =>
                IsRepositoryCommand(command, repositoryPath, "config", "--get-all"));
            var checkoutIndex = Array.FindIndex(prepared, command =>
                IsRepositoryCommand(command, repositoryPath, "checkout"));
            var refIndex = Array.FindIndex(prepared, command =>
                IsRepositoryCommand(command, repositoryPath, "rev-parse"));

            Assert.True(cloneIndex >= 0);
            Assert.True(cloneIndex < setConfigIndex);
            Assert.True(setConfigIndex < readConfigIndex);
            Assert.True(readConfigIndex < checkoutIndex);
            Assert.True(checkoutIndex < refIndex);
            var cloneArguments = prepared[cloneIndex].Arguments;
            Assert.Contains("--no-checkout", cloneArguments);
            var cloneConfigIndex = Array.IndexOf(cloneArguments, "--config");
            Assert.True(cloneConfigIndex >= 0);
            Assert.Equal("core.autocrlf=input", cloneArguments[cloneConfigIndex + 1]);
            Assert.Equal(
                plan.SourceRefs[repositoryName],
                prepared[checkoutIndex].Arguments[^1],
                ignoreCase: true);
            Assert.Equal(
                [
                    "-C",
                    repositoryPath,
                    "checkout",
                    "--detach",
                    "--force",
                    plan.SourceRefs[repositoryName]
                ],
                prepared[checkoutIndex].Arguments);
            Assert.DoesNotContain("--", prepared[checkoutIndex].Arguments);
        }

        Assert.All(prepared, command =>
        {
            Assert.Equal(managedGit.CommandGitExecutablePath, command.FileName);
            Assert.False(command.UseShellExecute);
            Assert.True(command.CreateNoWindow);
            Assert.True(command.RedirectStandardOutput);
            Assert.True(command.RedirectStandardError);
            Assert.Equal("1", command.Environment["GIT_CONFIG_NOSYSTEM"]);
            Assert.Equal(
                Path.Combine(operation, "home", ".gitconfig"),
                command.Environment["GIT_CONFIG_GLOBAL"]);
            Assert.Equal("0", command.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.False(command.Environment.ContainsKey("GIT_CONFIG_COUNT"));
            Assert.False(command.Environment.ContainsKey("BASH_ENV"));
        });

        var prepareCommandCount = gitProcess.Commands.Count;
        await workspace.VerifyAsync(plan, operation, managedGit);

        var postBuildCommands = gitProcess.Commands.Skip(prepareCommandCount).ToArray();
        Assert.Equal(16, postBuildCommands.Length);
        Assert.Equal(4, postBuildCommands.Count(command =>
            command.Arguments.Contains("--local")
            && command.Arguments.Contains("--get-all")));
        Assert.Equal(4, postBuildCommands.Count(command =>
            command.Arguments.Contains("--global")
            && command.Arguments.Contains("--get-all")));
        Assert.Equal(4, postBuildCommands.Count(command =>
            command.Arguments.Contains("rev-parse")));
        Assert.Equal(4, postBuildCommands.Count(command =>
            command.Arguments.Contains("get-url")));
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("local-config")]
    [InlineData("global-config")]
    public async Task ManagedGitWorkspace_PostBuildVerificationFailsClosedOnDrift(string drift)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var operation = Path.Combine(directory.Path, "operation");
        Directory.CreateDirectory(operation);
        var managedGit = CreateManagedGit(directory.Path);
        var plan = TestPlan(
            CoreType.Spigot,
            TestArtifact(Encoding.UTF8.GetBytes("tool")),
            new string('f', 64));
        var gitProcess = new RecordingManagedGitWorkspaceRunner(plan);
        var workspace = new SpigotBuildToolsManagedGitWorkspace(gitProcess);
        await workspace.PrepareAsync(plan, operation, managedGit);
        if (drift == "ref")
        {
            gitProcess.SpigotRefOverride = new string('e', 40);
        }
        else if (drift == "local-config")
        {
            gitProcess.SpigotAutoCrlf = "true";
        }
        else
        {
            gitProcess.GlobalAutoCrlf = "false";
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            workspace.VerifyAsync(plan, operation, managedGit));

        Assert.Contains($"identity {plan.VersionIdentity}", exception.Message);
        Assert.Contains(
            drift == "global-config" ? "repository BuildData" : "repository Spigot",
            exception.Message);
        if (drift == "ref")
        {
            Assert.Contains($"expected={plan.SourceRefs["Spigot"]}", exception.Message);
            Assert.Contains($"actual={gitProcess.SpigotRefOverride}", exception.Message);
        }
        else
        {
            Assert.Contains("expected=input", exception.Message);
            Assert.Contains(
                drift == "local-config" ? "actual=true" : "actual=false",
                exception.Message);
        }
    }

    [Fact]
    public async Task Runner_CancellationRemovesReadOnlyIncompleteWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('f', 64));
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var incomplete = Path.Combine(startInfo.WorkingDirectory, "BuildData", "partial.idx");
            Directory.CreateDirectory(Path.GetDirectoryName(incomplete)!);
            File.WriteAllText(incomplete, "incomplete");
            File.SetAttributes(incomplete, FileAttributes.ReadOnly);
            return Task.FromException<ModrinthLoaderBootstrapProcessResult>(
                new OperationCanceledException("test cancellation"));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");
        var destination = Path.Combine(staging, "server.jar");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.BuildAsync(plan, java, tool, staging, destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
    }

    [Fact]
    public async Task Runner_RemovesOnlyOldManagerNamedStaleWorkspaceBeforeBuild()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var outputBytes = Encoding.UTF8.GetBytes("verified output");
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, Sha256(outputBytes));
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            File.WriteAllBytes(Path.Combine(outputDirectory, "server.jar"), outputBytes);
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        Directory.CreateDirectory(localWorkspace);
        var stale = Path.Combine(localWorkspace, "buildtools-" + new string('a', 32));
        Directory.CreateDirectory(stale);
        var staleFile = Path.Combine(stale, "partial.tmp");
        File.WriteAllText(staleFile, "partial");
        File.SetAttributes(staleFile, FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        var active = Path.Combine(localWorkspace, "buildtools-" + new string('b', 32));
        Directory.CreateDirectory(active);
        var activeLeasePath = Path.Combine(active, ".manager-operation.lock");
        using var activeLease = new FileStream(
            activeLeasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        Directory.SetLastWriteTimeUtc(active, DateTime.UtcNow - TimeSpan.FromDays(2));
        var unrelated = Path.Combine(localWorkspace, "do-not-delete");
        Directory.CreateDirectory(unrelated);
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var staging = Path.Combine(directory.Path, "staging");

        _ = await runner.BuildAsync(
            plan,
            java,
            tool,
            staging,
            Path.Combine(staging, "server.jar"));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal(
            [active, unrelated],
            Directory.EnumerateDirectories(localWorkspace).Order());
    }

    [Fact]
    public void Preflight_UsesOfficialPortableGitOnWindowsAndRejectsCloudSyncedWorkPath()
    {
        using var directory = new TemporaryDirectory();
        var localRunner = new SpigotBuildToolsRunner(
            localWorkspaceRoot: Path.Combine(directory.Path, "buildtools"));
        var cloudRunner = new SpigotBuildToolsRunner(
            localWorkspaceRoot: Path.Combine(directory.Path, "OneDrive", "buildtools"));

        var local = localRunner.CheckPreflight();
        var cloud = cloudRunner.CheckPreflight();

        if (OperatingSystem.IsWindows())
        {
            Assert.True(local.CanRun);
            Assert.True(local.UsesBuildToolsManagedPortableGit);
            Assert.False(cloud.CanRun);
            Assert.Contains("OneDrive", cloud.UnsupportedReason);
        }
        else
        {
            Assert.False(local.CanRun);
            Assert.Contains("Git", local.UnsupportedReason);
        }
    }

    [Fact]
    public async Task Runner_TargetTrustRootMayBeOneDriveLikeReparseWithChineseAndSpaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var outputBytes = Encoding.UTF8.GetBytes("verified output across target boundary");
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, Sha256(outputBytes));
        var managedGit = CreateManagedGit(directory.Path);
        var process = new RecordingRunner(startInfo =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var outputDirectory = arguments[Array.IndexOf(arguments, "--output-dir") + 1];
            File.WriteAllBytes(Path.Combine(outputDirectory, "server.jar"), outputBytes);
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var localWorkspace = Path.Combine(directory.Path, "local-work");
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            localWorkspace,
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());
        var realTarget = Path.Combine(directory.Path, "真實目標 有空白");
        var linkedStaging = Path.Combine(directory.Path, "OneDrive 模擬");
        Directory.CreateDirectory(realTarget);
        ReparsePointTestHelper.CreateDirectoryLink(linkedStaging, realTarget);
        try
        {
            var destination = Path.Combine(linkedStaging, "中文 Server", "server.jar");

            var result = await runner.BuildAsync(
                plan,
                java,
                tool,
                linkedStaging,
                destination);

            Assert.Equal(destination, result.FilePath);
            Assert.Equal(
                outputBytes,
                await File.ReadAllBytesAsync(
                    Path.Combine(realTarget, "中文 Server", "server.jar")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(localWorkspace));
        }
        finally
        {
            if (Directory.Exists(linkedStaging))
            {
                Directory.Delete(linkedStaging, recursive: false);
            }
        }
    }

    [Fact]
    public async Task Runner_FailsBeforeProcessWhenRuntimeIsJreWithoutJavac()
    {
        using var directory = new TemporaryDirectory();
        var java = CreateTestJdk(directory.Path, includeJavac: false);
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var tool = Path.Combine(directory.Path, "BuildTools.jar");
        await File.WriteAllBytesAsync(tool, toolBytes);
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('a', 64));
        var managedGit = CreateManagedGit(directory.Path);
        var processCalled = false;
        var process = new RecordingRunner(_ =>
        {
            processCalled = true;
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            Path.Combine(directory.Path, "local-work"),
            new StaticManagedMinGitProvider(managedGit),
            new RecordingSpigotBuildToolsWorkspace());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.BuildAsync(
                plan,
                java,
                tool,
                Path.Combine(directory.Path, "staging"),
                Path.Combine(directory.Path, "staging", "server.jar")));

        Assert.Contains("JDK", exception.Message);
        Assert.False(processCalled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("04598")]
    public async Task Runner_RejectsNonCanonicalVersionIdentityBeforeSideEffects(
        string versionIdentity)
    {
        using var directory = new TemporaryDirectory();
        var toolBytes = Encoding.UTF8.GetBytes("reviewed tool");
        var trusted = TestArtifact(toolBytes);
        var plan = TestPlan(CoreType.Spigot, trusted, new string('a', 64)) with
        {
            VersionIdentity = versionIdentity
        };
        var managedGit = CreateManagedGit(directory.Path);
        var processCalled = false;
        var process = new RecordingRunner(_ =>
        {
            processCalled = true;
            return Task.FromResult(new ModrinthLoaderBootstrapProcessResult(0, [], []));
        });
        var workspace = new RecordingSpigotBuildToolsWorkspace();
        var runner = new SpigotBuildToolsRunner(
            process,
            null,
            trusted,
            Path.Combine(directory.Path, "local-work"),
            new StaticManagedMinGitProvider(managedGit),
            workspace);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.BuildAsync(
                plan,
                Path.Combine(directory.Path, "missing-java.exe"),
                Path.Combine(directory.Path, "missing-BuildTools.jar"),
                Path.Combine(directory.Path, "staging"),
                Path.Combine(directory.Path, "staging", "server.jar")));

        Assert.False(processCalled);
        Assert.Equal(0, workspace.PrepareCalls);
        Assert.Equal(0, workspace.VerifyCalls);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "local-work")));
    }

    private static SpigotBuildPlan TestPlan(
        CoreType coreType,
        SpigotBuildToolsArtifactInfo artifact,
        string outputHash)
        => new(
            coreType,
            coreType == CoreType.Spigot ? "Spigot" : "CraftBukkit (Bukkit)",
            "1.21.11",
            21,
            "server.jar",
            outputHash,
            197,
            "4598",
            new Dictionary<string, string>
            {
                ["BuildData"] = new string('a', 40),
                ["Bukkit"] = new string('b', 40),
                ["CraftBukkit"] = new string('c', 40),
                ["Spigot"] = new string('d', 40)
            },
            artifact);

    private static void WriteHistoricalBuildToolsJar(string path, CoreType coreType)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
        {
            writer.Write("Manifest-Version: 1.0\r\n");
            writer.Write("Main-Class: org.bukkit.craftbukkit.Main\r\n\r\n");
        }

        using (archive.CreateEntry("org/bukkit/craftbukkit/Main.class").Open())
        {
        }

        if (coreType == CoreType.Spigot)
        {
            using (archive.CreateEntry("org/spigotmc/SpigotConfig.class").Open())
            {
            }
        }
    }

    private static SpigotBuildToolsArtifactInfo TestArtifact(byte[] bytes)
        => new(
            1,
            new string('a', 40),
            new Uri("https://hub.spigotmc.org/jenkins/job/BuildTools/1/api/json"),
            new Uri("https://hub.spigotmc.org/jenkins/job/BuildTools/1/artifact/target/BuildTools.jar"),
            "BuildTools.jar",
            bytes.Length,
            Sha256(bytes));

    private static string VersionJson(bool includeHashes)
        => $$"""
            {
              "name": "4598",
              "refs": {
                "BuildData": "{{new string('a', 40)}}",
                "Bukkit": "{{new string('b', 40)}}",
                "CraftBukkit": "{{new string('c', 40)}}",
                "Spigot": "{{new string('d', 40)}}"
              },
              {{(includeHashes
                  ? $$"""
                    "hashes": {
                      "CraftBukkit": "{{new string('c', 64)}}",
                      "Spigot": "{{new string('d', 64)}}"
                    },
                    """
                  : string.Empty)}}
              "toolsVersion": 197,
              "javaVersions": [65, 70]
            }
            """;

    private static string Legacy18VersionJson()
        => $$"""
            {
              "name": "582b",
              "refs": {
                "BuildData": "{{new string('a', 40)}}",
                "Bukkit": "{{new string('b', 40)}}",
                "CraftBukkit": "{{new string('c', 40)}}",
                "Spigot": "{{new string('d', 40)}}"
              }
            }
            """;

    private static string MismatchedVersionJson(string field)
    {
        var json = VersionJson(includeHashes: true);
        return field switch
        {
            "name" => json.Replace("\"name\": \"4598\"", "\"name\": \"4599\"", StringComparison.Ordinal),
            "refs.BuildData" => ReplaceMetadataValue(json, "BuildData", 'a', 'e', 40),
            "refs.Bukkit" => ReplaceMetadataValue(json, "Bukkit", 'b', 'e', 40),
            "refs.CraftBukkit" => ReplaceMetadataValue(json, "CraftBukkit", 'c', 'e', 40),
            "refs.Spigot" => ReplaceMetadataValue(json, "Spigot", 'd', 'e', 40),
            "hashes.CraftBukkit" => ReplaceMetadataValue(json, "CraftBukkit", 'c', 'e', 64),
            "hashes.Spigot" => ReplaceMetadataValue(json, "Spigot", 'd', 'e', 64),
            "toolsVersion" => json.Replace("\"toolsVersion\": 197", "\"toolsVersion\": 196", StringComparison.Ordinal),
            "javaVersions" => json.Replace("[65, 70]", "[65, 69]", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    private static string ReplaceMetadataValue(
        string json,
        string property,
        char original,
        char replacement,
        int length)
        => json.Replace(
            $"\"{property}\": \"{new string(original, length)}\"",
            $"\"{property}\": \"{new string(replacement, length)}\"",
            StringComparison.Ordinal);

    private static string Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string CreateTestJdk(string root, bool includeJavac = true)
    {
        var bin = Path.Combine(root, "jdk", "bin");
        Directory.CreateDirectory(bin);
        var java = Write(bin, "java.exe", "java");
        if (includeJavac)
        {
            _ = Write(bin, "javac.exe", "javac");
        }

        return java;
    }

    private static ManagedMinGitInstallation CreateManagedGit(string root)
    {
        var install = Path.Combine(root, "managed-git");
        Directory.CreateDirectory(Path.Combine(install, "cmd"));
        Directory.CreateDirectory(Path.Combine(install, "mingw64", "bin"));
        Directory.CreateDirectory(Path.Combine(install, "usr", "bin"));
        var commandGit = Write(Path.Combine(install, "cmd"), "git.exe", "git");
        var mingwGit = Write(Path.Combine(install, "mingw64", "bin"), "git.exe", "git");
        var shell = Write(Path.Combine(install, "usr", "bin"), "sh.exe", "shell");
        return new ManagedMinGitInstallation(
            "2.45.2.windows.1",
            install,
            commandGit,
            mingwGit,
            shell);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new StubHandler(response));

    private static HttpResponseMessage Json(string value) => Text(value);

    private static HttpResponseMessage Text(string value)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value))
        };

    private static bool IsRepositoryCommand(
        GitCommandSnapshot command,
        string repositoryPath,
        params string[] requiredArguments)
        => command.Arguments.Length >= 3
        && command.Arguments[0].Equals("-C", StringComparison.Ordinal)
        && command.Arguments[1].Equals(repositoryPath, StringComparison.Ordinal)
        && requiredArguments.All(command.Arguments.Contains);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRunner(
        Func<ProcessStartInfo, Task<ModrinthLoaderBootstrapProcessResult>> run)
        : IModrinthLoaderBootstrapProcessRunner
    {
        public Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return run(startInfo);
        }
    }

    private sealed class RecordingSpigotBuildToolsWorkspace : ISpigotBuildToolsWorkspace
    {
        public int PrepareCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public Task PrepareAsync(
            SpigotBuildPlan plan,
            string operationDirectory,
            ManagedMinGitInstallation managedGit,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            return Task.CompletedTask;
        }

        public Task VerifyAsync(
            SpigotBuildPlan plan,
            string operationDirectory,
            ManagedMinGitInstallation managedGit,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingManagedGitWorkspaceRunner(SpigotBuildPlan plan)
        : IModrinthLoaderBootstrapProcessRunner
    {
        private static readonly IReadOnlyDictionary<string, string> Remotes =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BuildData"] = "https://hub.spigotmc.org/stash/scm/spigot/builddata.git",
                ["Bukkit"] = "https://hub.spigotmc.org/stash/scm/spigot/bukkit.git",
                ["CraftBukkit"] = "https://hub.spigotmc.org/stash/scm/spigot/craftbukkit.git",
                ["Spigot"] = "https://hub.spigotmc.org/stash/scm/spigot/spigot.git"
            };

        public List<GitCommandSnapshot> Commands { get; } = [];

        public string? SpigotRefOverride { get; set; }

        public string SpigotAutoCrlf { get; set; } = "input";

        public string GlobalAutoCrlf { get; set; } = "input";

        public Task<ModrinthLoaderBootstrapProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            IProgress<ModrinthLoaderBootstrapOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = startInfo.ArgumentList.ToArray();
            Commands.Add(new GitCommandSnapshot(
                startInfo.FileName,
                startInfo.UseShellExecute,
                startInfo.CreateNoWindow,
                startInfo.RedirectStandardOutput,
                startInfo.RedirectStandardError,
                arguments,
                startInfo.Environment.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)));

            if (arguments[0] == "clone")
            {
                var repositoryPath = arguments[^1];
                Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
                return Success();
            }

            if (arguments.Contains("--global") && arguments.Contains("--get-all"))
            {
                return Success([GlobalAutoCrlf]);
            }

            var repositoryName = Path.GetFileName(arguments[1]);
            if (arguments.Contains("--get-all"))
            {
                var value = repositoryName == "Spigot" ? SpigotAutoCrlf : "input";
                return Success([value]);
            }

            if (arguments.Contains("rev-parse"))
            {
                var value = repositoryName == "Spigot" && SpigotRefOverride is not null
                    ? SpigotRefOverride
                    : plan.SourceRefs[repositoryName];
                return Success([value]);
            }

            if (arguments.Contains("get-url"))
            {
                return Success([Remotes[repositoryName]]);
            }

            return Success();
        }

        private static Task<ModrinthLoaderBootstrapProcessResult> Success(
            IReadOnlyList<string>? output = null)
            => Task.FromResult(new ModrinthLoaderBootstrapProcessResult(
                0,
                output ?? [],
                []));
    }

    private sealed record GitCommandSnapshot(
        string FileName,
        bool UseShellExecute,
        bool CreateNoWindow,
        bool RedirectStandardOutput,
        bool RedirectStandardError,
        string[] Arguments,
        IReadOnlyDictionary<string, string?> Environment);

    private sealed class StaticManagedMinGitProvider(
        ManagedMinGitInstallation installation) : IManagedMinGitProvider
    {
        public Task<ManagedMinGitInstallation> EnsureInstalledAsync(
            IProgress<ManagedMinGitProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ManagedMinGitProgress(
                ManagedMinGitProgressPhase.CheckingCache,
                null));
            progress?.Report(new ManagedMinGitProgress(
                ManagedMinGitProgressPhase.Verifying,
                null));
            return Task.FromResult(installation);
        }
    }
}
