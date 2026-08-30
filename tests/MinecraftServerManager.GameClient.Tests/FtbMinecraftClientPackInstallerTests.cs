using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class FtbMinecraftClientPackInstallerTests : IDisposable
{
    private static readonly Uri FileMirror =
        new("https://files.feed-the-beast.com/blob/client-file");
    private static readonly Uri FilePrimary =
        new("https://edge.forgecdn.net/files/1/2/client.jar");
    private static readonly Uri EmptyFile =
        new("https://files.feed-the-beast.com/blob/empty-file");
    private static readonly Uri ServerFile =
        new("https://files.feed-the-beast.com/blob/server-file");
    private static readonly Uri OptionalFile =
        new("https://files.feed-the-beast.com/blob/optional-file");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-ftb-client-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _instances;
    private readonly string _staging;
    private readonly string _registryPath;

    public FtbMinecraftClientPackInstallerTests()
    {
        _instances = Path.Combine(_root, "instances");
        _staging = Path.Combine(_root, "staging");
        _registryPath = Path.Combine(_root, "registry.json");
        Directory.CreateDirectory(_instances);
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public async Task InstallAsync_VerifiesFilesFiltersServerIncludesOptionalAndRaisesAutomaticMemory()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);
        var request = Request();

        var result = await installer.InstallAsync(request, javaExecutablePath: null);

        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        Assert.Equal(finalRoot, result.Instance.DirectoryPath);
        Assert.Equal(fixture.ClientBytes, await File.ReadAllBytesAsync(
            Path.Combine(finalRoot, "mods", "client.jar")));
        Assert.True(File.Exists(Path.Combine(finalRoot, "config", "empty.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(finalRoot, "config", "empty.txt")).Length);
        Assert.False(File.Exists(Path.Combine(finalRoot, "mods", "server.jar")));
        Assert.Equal(fixture.OptionalBytes, await File.ReadAllBytesAsync(
            Path.Combine(finalRoot, "mods", "optional.jar")));
        Assert.Equal(MinecraftClientLoader.NeoForge, result.Instance.Loader);
        Assert.Equal("21.1.209", result.Instance.LoaderVersion);
        Assert.Equal(21, result.Instance.JavaMajorVersion);
        Assert.Equal(5120, result.Instance.MinimumMemoryMb);
        Assert.Equal(6144, result.Instance.MaximumMemoryMb);
        Assert.Equal("ftb", result.Instance.CatalogProvider);
        Assert.Equal("130", result.Instance.CatalogProjectId);
        Assert.Equal("100140", result.Instance.CatalogVersionId);
        Assert.Equal("cdn.feed-the-beast.com", result.Instance.CatalogIconUri?.Host);
        Assert.Equal(3, result.InstalledContentFiles);
        Assert.Equal(1, result.SkippedServerFiles);
        Assert.Equal(0, result.SkippedOptionalFiles);
        Assert.Contains(FileMirror, handler.Requests);
        Assert.DoesNotContain(FilePrimary, handler.Requests);
        var payloadRequest = Assert.Single(payload.Requests);
        Assert.Equal(5120, payloadRequest.MinimumMemoryMb);
        Assert.Equal(6144, payloadRequest.MaximumMemoryMb);
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal(result.Instance.Id, stored.Id);
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_IncludesOptionalClientDependenciesEvenWhenFlagIsFalse()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var result = await installer.InstallAsync(
            Request() with { IncludeOptionalFiles = false },
            javaExecutablePath: null);

        Assert.True(File.Exists(Path.Combine(result.Instance.DirectoryPath, "mods", "optional.jar")));
        Assert.Equal(3, result.InstalledContentFiles);
        Assert.Equal(0, result.SkippedOptionalFiles);
    }

    [Fact]
    public async Task InstallAsync_Sha256MismatchRollsBackPayloadAndRegistry()
    {
        var fixture = CreateFixture();
        var file = fixture.Manifest.Files[0] with
        {
            Hashes = fixture.Manifest.Files[0].Hashes with { Sha256 = new string('0', 64) },
        };
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files = [file, .. fixture.Manifest.Files.Skip(1)],
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        await AssertNoInstallationAsync(registry);
    }

    [Theory]
    [InlineData(true, "release")]
    [InlineData(false, "beta")]
    public async Task InstallAsync_RejectsPrivateOrNonStableManifestBeforePayload(
        bool isPrivate,
        string type)
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with { IsPrivate = isPrivate, Type = type },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        Assert.Empty(handler.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_ManualMemoryBelowPackMinimumFailsBeforePayload()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            Request() with
            {
                MemoryMode = MinecraftClientMemoryMode.Manual,
                MaximumMemoryMb = 4096,
            },
            javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RejectsTraversalAndProtectedPathsBeforePayload()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files =
                [
                    fixture.Manifest.Files[0] with { Path = "../outside.jar" },
                    fixture.Manifest.Files[1] with { Path = "versions/evil.json" },
                ],
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_AcceptsCurrentFeaturedPackFileCountAboveTenThousand()
    {
        const int currentFeaturedPackFileCount = 11_332;
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files = Enumerable.Range(0, currentFeaturedPackFileCount)
                    .Select(index => PackFile(
                        $"server-{index}.jar",
                        $"mods/server-{index}.jar",
                        EmptyFile,
                        [],
                        [],
                        serverOnly: true))
                    .ToArray(),
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        var result = await installer.InstallAsync(Request(), javaExecutablePath: null);

        Assert.Equal(currentFeaturedPackFileCount, result.SkippedServerFiles);
        Assert.Equal(0, result.InstalledContentFiles);
        Assert.Empty(handler.Requests);
        Assert.Single(payload.Requests);
    }

    [Fact]
    public async Task InstallAsync_RejectsMoreThanTwentyThousandFilesBeforePayload()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files = Enumerable.Repeat(fixture.Manifest.Files[0], 20_001).ToArray(),
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        Assert.Empty(handler.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_DeduplicatesSameContentCaseOnlyWindowsAlias()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files =
                [
                    PackFile("settings.json", "config/Obscuria/settings.json", EmptyFile, [], []),
                    PackFile("settings.json", "config/obscuria/settings.json", EmptyFile, [], []),
                ],
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var result = await installer.InstallAsync(Request(), javaExecutablePath: null);

        Assert.Equal(1, result.InstalledContentFiles);
        Assert.True(File.Exists(Path.Combine(
            result.Instance.DirectoryPath,
            "config",
            "Obscuria",
            "settings.json")));
        Assert.Equal(1, handler.Requests.Count(uri => uri == EmptyFile));
    }

    [Fact]
    public async Task InstallAsync_RejectsDifferentContentCaseOnlyWindowsAliasBeforePayload()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Manifest = fixture.Manifest with
            {
                Files =
                [
                    PackFile(
                        "settings.json",
                        "config/Obscuria/settings.json",
                        FilePrimary,
                        [],
                        [1]),
                    PackFile(
                        "settings.json",
                        "config/obscuria/settings.json",
                        FileMirror,
                        [],
                        [2]),
                ],
            },
        };
        using var registry = new MinecraftClientRegistry(_registryPath);
        var payload = new FakePayloadInstaller();
        using var artifacts = CreateArtifactClient(fixture, out var handler);
        var installer = CreateInstaller(registry, payload, fixture, artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Empty(payload.Requests);
        Assert.Empty(handler.Requests);
        await AssertNoInstallationAsync(registry);
    }

    [Fact]
    public async Task InstallAsync_RegistryCommitFailureCompletelyRollsBackFinalTreeAndReceipt()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(_registryPath);
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_instances));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task InstallAsync_PostCommitRegistryExceptionReloadsExactOwnershipWithoutRollback()
    {
        var fixture = CreateFixture();
        var throwAfterSave = 1;
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () =>
            {
                if (Interlocked.Exchange(ref throwAfterSave, 0) == 1)
                {
                    throw new IOException("Simulated caller-visible error after durable registry save.");
                }
            });
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var result = await installer.InstallAsync(Request(), javaExecutablePath: null);

        Assert.True(Directory.Exists(result.Instance.DirectoryPath));
        Assert.Equal(result.Instance.Id, Assert.Single((await registry.LoadAsync()).Instances).Id);
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_UncertainRegistryCommitReturnsTypedRecoveryRequiredFailure()
    {
        var fixture = CreateFixture();
        FileStream? registryLock = null;
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () =>
            {
                registryLock = new FileStream(
                    _registryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                throw new IOException($"Simulated commit uncertainty at {_root}?token=secret.");
            });
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        FtbClientInstallRecoveryRequiredException error;
        try
        {
            error = await Assert.ThrowsAsync<FtbClientInstallRecoveryRequiredException>(() =>
                installer.InstallAsync(Request(), javaExecutablePath: null));
        }
        finally
        {
            registryLock?.Dispose();
        }

        Assert.Equal("registry-commit-verification", error.Stage);
        Assert.Equal(2, error.FailureCount);
        Assert.True(error.RecoveryRequired);
        Assert.False(error.RollbackCompleted);
        Assert.DoesNotContain(_root, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, GetTransactionFailures(error).InnerExceptions.Count);
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Single(Directory.EnumerateDirectories(_instances));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_CommitOutOfMemoryBubblesOriginalAndRetainsCommittedReceipt()
    {
        var fixture = CreateFixture();
        var expected = new OutOfMemoryException($"Sensitive commit failure at {_root}.");
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () => throw expected);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Same(expected, error);
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Single(Directory.EnumerateDirectories(_instances));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_UncertainPostCommitRevocationReturnsTypedRecoveryRequiredFailure()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var movedOriginal = Path.Combine(_instances, "post-commit-original");
        var replacement = Path.Combine(_root, "post-commit-replacement");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "replacement-must-survive.txt"),
            "replacement-content");
        FileStream? registryLock = null;
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () =>
            {
                Directory.Move(finalRoot, movedOriginal);
                Directory.Move(replacement, finalRoot);
                registryLock = new FileStream(
                    _registryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            });
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        FtbClientInstallRecoveryRequiredException error;
        try
        {
            error = await Assert.ThrowsAsync<FtbClientInstallRecoveryRequiredException>(() =>
                installer.InstallAsync(request, javaExecutablePath: null));
        }
        finally
        {
            registryLock?.Dispose();
        }

        Assert.Equal("post-commit-revocation", error.Stage);
        Assert.Equal(2, error.FailureCount);
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(movedOriginal));
        Assert.Equal(
            "replacement-content",
            await File.ReadAllTextAsync(Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_UncertainFinalizationRevocationReturnsTypedRecoveryRequiredFailure()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var movedOriginal = Path.Combine(_instances, "finalization-original");
        var replacement = Path.Combine(_root, "finalization-replacement-locked");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "replacement-must-survive.txt"),
            "replacement-content");
        FileStream? registryLock = null;
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            duringCommittedFinalization: promoted =>
            {
                Directory.Move(promoted, movedOriginal);
                Directory.Move(replacement, promoted);
                registryLock = new FileStream(
                    _registryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            });

        FtbClientInstallRecoveryRequiredException error;
        try
        {
            error = await Assert.ThrowsAsync<FtbClientInstallRecoveryRequiredException>(() =>
                installer.InstallAsync(request, javaExecutablePath: null));
        }
        finally
        {
            registryLock?.Dispose();
        }

        Assert.Equal("finalization-revocation", error.Stage);
        Assert.Equal(2, error.FailureCount);
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(movedOriginal));
        Assert.Equal(
            "replacement-content",
            await File.ReadAllTextAsync(Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_FinalRootSwapAcrossRegistryCommitIsRevokedWithoutDeletingReplacement()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var movedRoot = Path.Combine(_instances, "commit-time-swap");
        var replacement = Path.Combine(_root, "commit-time-replacement");
        Directory.CreateDirectory(replacement);
        var sentinel = Path.Combine(replacement, "replacement-must-survive.txt");
        await File.WriteAllTextAsync(sentinel, "replacement-content");
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () =>
            {
                Directory.Move(finalRoot, movedRoot);
                Directory.Move(replacement, finalRoot);
            });
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var error = await Assert.ThrowsAsync<FtbClientInstallRollbackIncompleteException>(() =>
            installer.InstallAsync(request, javaExecutablePath: null));

        Assert.Equal("rollback", error.Stage);
        Assert.True(error.RecoveryRequired);
        Assert.False(error.RollbackCompleted);
        Assert.Contains(
            GetTransactionFailures(error).InnerExceptions,
            exception => exception is InvalidDataException &&
                         exception.Message.Contains("changed during registry commit", StringComparison.Ordinal));
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(movedRoot));
        Assert.True(Directory.Exists(finalRoot));
        Assert.Equal("replacement-content", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_PostCommitJunctionInjectionRevokesRegistryAndPreservesTarget()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var outside = Path.Combine(_root, "junction-target");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "must-survive.txt");
        await File.WriteAllTextAsync(sentinel, "outside-content");
        var injectOnce = 1;
        using var registry = new MinecraftClientRegistry(
            _registryPath,
            () =>
            {
                if (Interlocked.Exchange(ref injectOnce, 0) == 1)
                {
                    CreateDirectoryJunction(Path.Combine(finalRoot, "commit-time-link"), outside);
                }
            });
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(request, javaExecutablePath: null));

        Assert.Contains("changed during registry commit", error.Message, StringComparison.Ordinal);
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.False(Directory.Exists(finalRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
        Assert.Equal("outside-content", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task InstallAsync_PreLeaseRootReplacementFailsClosedWithoutDeletingReplacement()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var capturedOriginal = Path.Combine(_root, "captured-promoted-tree");
        var replacement = Path.Combine(_root, "replacement-tree");
        Directory.CreateDirectory(replacement);
        var sentinel = Path.Combine(replacement, "replacement-must-survive.txt");
        await File.WriteAllTextAsync(sentinel, "replacement-content");
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            afterPromotionBeforeLease: promoted =>
            {
                Directory.Move(promoted, capturedOriginal);
                Directory.Move(replacement, promoted);
            });

        var error = await Assert.ThrowsAsync<FtbClientInstallRollbackIncompleteException>(() =>
            installer.InstallAsync(request, javaExecutablePath: null));

        Assert.Equal("rollback", error.Stage);
        Assert.True(error.RecoveryRequired);
        Assert.False(error.RollbackCompleted);
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(capturedOriginal));
        Assert.Equal("replacement-content", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_SwapDuringFinalizationRevokesRegistryAndRetainsOwnershipReceipt()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var movedOriginal = Path.Combine(_instances, "finalize-moved-original");
        var replacement = Path.Combine(_root, "finalize-replacement");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "replacement-must-survive.txt"),
            "replacement-content");
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            duringCommittedFinalization: promoted =>
            {
                Directory.Move(promoted, movedOriginal);
                Directory.Move(replacement, promoted);
            });

        var error = await Assert.ThrowsAsync<FtbClientInstallRollbackIncompleteException>(() =>
            installer.InstallAsync(request, javaExecutablePath: null));

        Assert.Equal("rollback", error.Stage);
        Assert.Contains(
            GetTransactionFailures(error).InnerExceptions,
            exception => exception is InvalidDataException &&
                         exception.Message.Contains("changed during finalization", StringComparison.Ordinal));
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(movedOriginal));
        Assert.Equal("replacement-content", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task InstallAsync_CompletionProgressSwapThenThrowStillRunsFinalInvariant()
    {
        var fixture = CreateFixture();
        var request = Request();
        var finalRoot = Path.Combine(_instances, request.InstanceId.ToString("N"));
        var movedOriginal = Path.Combine(_instances, "progress-moved-original");
        var replacement = Path.Combine(_root, "progress-replacement");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "replacement-must-survive.txt"),
            "replacement-content");
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);
        var swapOnce = 1;
        var progress = new SynchronousProgress<FtbClientPackInstallProgress>(value =>
        {
            if (value.Stage == "complete" && Interlocked.Exchange(ref swapOnce, 0) == 1)
            {
                Directory.Move(finalRoot, movedOriginal);
                Directory.Move(replacement, finalRoot);
                throw new InvalidOperationException("Simulated progress observer failure after swap.");
            }
        });

        var error = await Assert.ThrowsAsync<FtbClientInstallRollbackIncompleteException>(() =>
            installer.InstallAsync(request, javaExecutablePath: null, progress));

        Assert.Equal("rollback", error.Stage);
        Assert.Contains(
            GetTransactionFailures(error).InnerExceptions,
            exception => exception is InvalidDataException &&
                         exception.Message.Contains("changed during finalization", StringComparison.Ordinal));
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.True(Directory.Exists(movedOriginal));
        Assert.Equal("replacement-content", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task FtbInstalledInstance_RejectsManagedLoaderReplacementAndRecoveryRemainsValid()
    {
        var fixture = CreateFixture();
        var request = Request();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var ftbInstaller = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);
        var installed = await ftbInstaller.InstallAsync(request, javaExecutablePath: null);
        var identityBefore = SafePath.GetExistingObjectIdentity(installed.Instance.DirectoryPath);
        var switchPayload = new FakePayloadInstaller();
        var manager = new MinecraftClientInstanceManager(
            _instances,
            _staging,
            registry,
            new FakeReleaseCatalog("1.21.1"),
            switchPayload);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.SwitchLoaderAsync(
                request.InstanceId,
                MinecraftClientLoader.Fabric,
                "0.16.10",
                javaExecutablePath: null));

        Assert.Contains("official pack manifest", error.Message, StringComparison.Ordinal);
        Assert.Empty(switchPayload.Requests);
        Assert.Equal(identityBefore, SafePath.GetExistingObjectIdentity(installed.Instance.DirectoryPath));
        await ftbInstaller.RecoverPendingPromotionsAsync();
        var stored = Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Equal(MinecraftClientLoader.NeoForge, stored.Loader);
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task RecoverPendingPromotionsAsync_RemovesIdentityBoundOrphanAfterCleanupLockClears()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(_registryPath);
        using var failingRegistry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            failingRegistry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            (trustedRoot, path, identity, cancellationToken) =>
            {
                if (PathsEqual(trustedRoot, _instances))
                {
                    throw new IOException("Simulated Windows sharing violation during orphan cleanup.");
                }

                return SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    trustedRoot,
                    path,
                    identity,
                    protectedObjectIdentities: null,
                    cancellationToken);
            });

        var error = await Assert.ThrowsAsync<FtbClientInstallRollbackIncompleteException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Equal("rollback", error.Stage);
        Assert.Contains(
            GetTransactionFailures(error).InnerExceptions,
            exception => exception is IOException &&
                         exception.Message.Contains("sharing violation", StringComparison.Ordinal));
        var finalRoot = Path.Combine(_instances, Request().InstanceId.ToString("N"));
        Assert.True(Directory.Exists(finalRoot));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));

        failingRegistry.Dispose();
        Directory.Delete(_registryPath);
        using var recoveredRegistry = new MinecraftClientRegistry(_registryPath);
        var recoveredInstaller = CreateInstaller(
            recoveredRegistry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);

        await recoveredInstaller.RecoverPendingPromotionsAsync();

        Assert.False(Directory.Exists(finalRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
        Assert.Empty((await recoveredRegistry.LoadAsync()).Instances);
    }

    [Fact]
    public async Task InstallAsync_RollbackCleanupOutOfMemoryBubblesOriginalAndRetainsReceipt()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(_registryPath);
        using var failingRegistry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var expected = new OutOfMemoryException($"Sensitive cleanup failure at {_root}.");
        var installer = CreateInstaller(
            failingRegistry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            (trustedRoot, path, identity, cancellationToken) =>
            {
                if (PathsEqual(trustedRoot, _instances))
                {
                    throw expected;
                }

                return SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    trustedRoot,
                    path,
                    identity,
                    protectedObjectIdentities: null,
                    cancellationToken);
            });

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            installer.InstallAsync(Request(), javaExecutablePath: null));

        Assert.Same(expected, error);
        Assert.Single(Directory.EnumerateDirectories(_instances));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task RecoverPendingPromotionsAsync_KeepsRegisteredFinalAndDurableOwnershipReceipt()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            (trustedRoot, path, identity, cancellationToken) =>
            {
                if (PathsEqual(trustedRoot, _staging))
                {
                    throw new IOException("Simulated committed-receipt cleanup lock.");
                }

                return SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                    trustedRoot,
                    path,
                    identity,
                    protectedObjectIdentities: null,
                    cancellationToken);
            });

        var result = await installer.InstallAsync(Request(), javaExecutablePath: null);

        Assert.True(Directory.Exists(result.Instance.DirectoryPath));
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));

        var recoveredInstaller = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);
        await recoveredInstaller.RecoverPendingPromotionsAsync();

        Assert.True(Directory.Exists(result.Instance.DirectoryPath));
        Assert.Single((await registry.LoadAsync()).Instances);
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task RecoverPendingPromotionsAsync_OutOfMemoryBubblesOriginalAndRetainsReceipt()
    {
        var fixture = CreateFixture();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);
        var installed = await installer.InstallAsync(Request(), javaExecutablePath: null);
        var expected = new OutOfMemoryException($"Sensitive recovery failure at {_root}.");
        var recovery = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            duringRegisteredRecovery: _ => throw expected);

        var error = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            recovery.RecoverPendingPromotionsAsync());

        Assert.Same(expected, error);
        Assert.Equal(installed.Instance.Id, Assert.Single((await registry.LoadAsync()).Instances).Id);
        Assert.True(Directory.Exists(installed.Instance.DirectoryPath));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    [Fact]
    public async Task RecoverPendingPromotionsAsync_SwapDuringCleanupFailsClosedAndRetainsReceipt()
    {
        var fixture = CreateFixture();
        var request = Request();
        using var registry = new MinecraftClientRegistry(_registryPath);
        using var artifacts = CreateArtifactClient(fixture, out _);
        var installer = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts);
        var installed = await installer.InstallAsync(request, javaExecutablePath: null);
        var finalRoot = installed.Instance.DirectoryPath;
        var movedOriginal = Path.Combine(_instances, "recovery-moved-original");
        var replacement = Path.Combine(_root, "recovery-replacement");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "replacement-must-survive.txt"),
            "replacement-content");
        var recovery = CreateInstaller(
            registry,
            new FakePayloadInstaller(),
            fixture,
            artifacts,
            duringRegisteredRecovery: registered =>
            {
                Directory.Move(registered, movedOriginal);
                Directory.Move(replacement, registered);
            });

        var error = await Assert.ThrowsAsync<FtbClientInstallRecoveryRequiredException>(() =>
            recovery.RecoverPendingPromotionsAsync());

        Assert.Equal("pending-recovery", error.Stage);
        Assert.True(error.RecoveryRequired);
        Assert.False(error.RollbackCompleted);
        Assert.Contains(
            GetTransactionFailures(error).InnerExceptions,
            exception => exception.InnerException is UnauthorizedAccessException);
        Assert.Equal(installed.Instance.Id, Assert.Single((await registry.LoadAsync()).Instances).Id);
        Assert.True(Directory.Exists(movedOriginal));
        Assert.Equal("replacement-content", await File.ReadAllTextAsync(
            Path.Combine(finalRoot, "replacement-must-survive.txt")));
        Assert.Single(Directory.EnumerateFiles(_staging, ".ftb-client-promotion-*.json"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private FtbMinecraftClientPackInstaller CreateInstaller(
        MinecraftClientRegistry registry,
        IMinecraftClientPayloadInstaller payload,
        Fixture fixture,
        HttpClient artifactClient,
        Func<string, string, SafePathObjectIdentity, CancellationToken, Task>? deleteOwnedTree = null,
        Action<string>? afterPromotionBeforeLease = null,
        Action<string>? duringCommittedFinalization = null,
        Action<string>? duringRegisteredRecovery = null)
    {
        var releases = new FakeReleaseCatalog("1.21.1");
        var catalog = new FakeCatalog(fixture.Pack, fixture.Manifest);
        deleteOwnedTree ??= static (trustedRoot, path, identity, cancellationToken) =>
            SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                trustedRoot,
                path,
                identity,
                protectedObjectIdentities: null,
                cancellationToken);
        return new FtbMinecraftClientPackInstaller(
            _instances,
            _staging,
            registry,
            releases,
            payload,
            catalog,
            artifactClient,
            deleteOwnedTree,
            afterPromotionBeforeLease,
            duringCommittedFinalization,
            duringRegisteredRecovery);
    }

    private static bool PathsEqual(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static AggregateException GetTransactionFailures(Exception exception) =>
        Assert.IsType<AggregateException>(exception.InnerException);

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a test junction.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create test junction (exit {process.ExitCode}): {error}{output}");
        }
    }

    private static HttpClient CreateArtifactClient(Fixture fixture, out ArtifactHandler handler)
    {
        handler = new ArtifactHandler(request =>
        {
            var bytes = request.RequestUri switch
            {
                var uri when uri == FileMirror || uri == FilePrimary => fixture.ClientBytes,
                var uri when uri == EmptyFile => [],
                var uri when uri == ServerFile => fixture.ServerBytes,
                var uri when uri == OptionalFile => fixture.OptionalBytes,
                _ => null,
            };
            return bytes is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                : BytesResponse(request, bytes);
        });
        return new HttpClient(handler);
    }

    private static HttpResponseMessage BytesResponse(HttpRequestMessage request, byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentLength = bytes.LongLength;
        return response;
    }

    private static Fixture CreateFixture()
    {
        var client = Encoding.UTF8.GetBytes("verified-ftb-client-mod");
        var server = Encoding.UTF8.GetBytes("server-only");
        var optional = Encoding.UTF8.GetBytes("optional-client");
        var targets = new FtbTarget[]
        {
            new("game", "minecraft", "1.21.1"),
            new("modloader", "neoforge", "21.1.209"),
            new("runtime", "java", "21.0.4+7-LTS"),
        };
        var version = new FtbPackVersion(
            100140,
            "Stable",
            "release",
            DateTimeOffset.Parse("2026-08-20T00:00:00Z").ToUnixTimeMilliseconds(),
            targets);
        var pack = new FtbPack(
            130,
            "FTB Test Pack",
            "ftb-test-pack",
            false,
            [version],
            Artwork:
            [
                new FtbArtwork(
                    new Uri("https://cdn.feed-the-beast.com/blob/icon.png"),
                    "square",
                    256,
                    256),
            ]);
        var files = new FtbPackFile[]
        {
            PackFile("client.jar", "mods/client.jar", FilePrimary, [FileMirror], client),
            PackFile("empty.txt", "config/empty.txt", EmptyFile, [], []),
            PackFile("server.jar", "mods/server.jar", ServerFile, [], server, serverOnly: true),
            PackFile("optional.jar", "mods/optional.jar", OptionalFile, [], optional, optional: true),
        };
        var manifest = new FtbPackVersionManifest(
            130,
            100140,
            "Stable",
            "release",
            false,
            version.Updated,
            targets,
            new FtbPackMemorySpecs(5120, 6144),
            files);
        return new Fixture(pack, manifest, client, server, optional);
    }

    private static FtbPackFile PackFile(
        string name,
        string path,
        Uri uri,
        IReadOnlyList<Uri> mirrors,
        byte[] bytes,
        bool serverOnly = false,
        bool optional = false) => new(
        1,
        name,
        path,
        uri,
        mirrors,
        bytes.LongLength,
        ClientOnly: false,
        ServerOnly: serverOnly,
        Optional: optional,
        Type: "mod",
        new FtbPackFileHashes(Sha1(bytes), Sha256(bytes), Sha512(bytes)));

    private static FtbClientPackInstallRequest Request() => new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        "FTB Test Pack",
        130,
        100140,
        MinecraftClientMemoryMode.Automatic,
        2048,
        4096,
        1280,
        720,
        false,
        JavaMajorVersion: 21);

    private async Task AssertNoInstallationAsync(MinecraftClientRegistry registry)
    {
        Assert.Empty((await registry.LoadAsync()).Instances);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_instances));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    private static string Sha1(byte[] bytes) =>
        Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha512(byte[] bytes) =>
        Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private sealed record Fixture(
        FtbPack Pack,
        FtbPackVersionManifest Manifest,
        byte[] ClientBytes,
        byte[] ServerBytes,
        byte[] OptionalBytes);

    private sealed class FakeCatalog(
        FtbPack pack,
        FtbPackVersionManifest manifest) : IFtbClientPackCatalog
    {
        public Task<FtbPack> GetPackAsync(
            int packId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(pack);

        public Task<FtbPackVersionManifest> GetVersionManifestAsync(
            int packId,
            int versionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(manifest);
    }

    private sealed class FakeReleaseCatalog(params string[] versions) : IMinecraftReleaseCatalog
    {
        public Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
            return Task.FromResult(new MinecraftReleaseCatalogSnapshot(
                versions[0],
                now,
                versions.Select(version => new MinecraftReleaseInfo(
                    version,
                    now,
                    new Uri($"https://piston-meta.mojang.com/v1/packages/a/{version}.json"),
                    new string('a', 40),
                    1)).ToArray()));
        }
    }

    private sealed class FakePayloadInstaller : IMinecraftClientPayloadInstaller
    {
        public List<MinecraftClientInstallRequest> Requests { get; } = [];

        public async Task<string> InstallAsync(
            MinecraftClientInstallRequest request,
            string stagingDirectory,
            string? javaExecutablePath,
            IProgress<MinecraftClientInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var marker = Path.Combine(stagingDirectory, "versions", "fake-profile", "installed.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "installed", cancellationToken);
            return "fake-profile";
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ArtifactHandler(
        Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(factory(request));
        }
    }
}
