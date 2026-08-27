using System.Diagnostics;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductUpdateRetentionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-UpdateRetentionTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _installRoot;
    private readonly string _updatesRoot;
    private readonly string _versionsRoot;

    public ProductUpdateRetentionManagerTests()
    {
        _installRoot = Path.Combine(_root, "product");
        _updatesRoot = Path.Combine(_root, "data", "updates");
        _versionsRoot = Path.Combine(_installRoot, "versions");
        Directory.CreateDirectory(_versionsRoot);
        Directory.CreateDirectory(_updatesRoot);
        File.WriteAllText(
            Path.Combine(_installRoot, ".muhun-mcsv-install-root"),
            "muhun.mcsv.manager:1\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Run_BoundsManagedArtifactsWhilePreservingEveryDurableProtection()
    {
        foreach (var version in Versions("1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6"))
        {
            CreateVersion(version);
            CreatePackage(version);
            CreateVerifiedCache(version);
        }

        WriteActivePointer("1.0.6");
        WriteJournal("1.0.5", "1.0.6", ProductUpdateActivationState.Committed);
        WritePending("1.0.4");
        var staging = Path.Combine(
            _versionsRoot,
            $".1.0.7.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "partial.bin"), "partial");
        var verification = Path.Combine(
            _updatesRoot,
            "verification",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verification);
        File.WriteAllText(Path.Combine(verification, "expanded.bin"), "expanded");
        Directory.CreateDirectory(Path.Combine(_versionsRoot, "manual-version"));
        Directory.CreateDirectory(Path.Combine(_updatesRoot, "packages"));
        File.WriteAllText(Path.Combine(_updatesRoot, "packages", "manual.zip"), "operator-owned");

        var serversSentinel = Path.Combine(_root, "data", "servers", "server-a", "world", "level.dat");
        var backupsSentinel = Path.Combine(_root, "data", "backups", "server-a", "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(serversSentinel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backupsSentinel)!);
        File.WriteAllText(serversSentinel, "world");
        File.WriteAllText(backupsSentinel, "backup");

        var result = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.6",
            new ProductUpdateRetentionPolicy(1, 1, 1))
            .Run(["1.0.3"]);

        Assert.False(result.SkippedBecauseUpdaterLeaseUnavailable);
        Assert.Equal(2, result.InstalledVersionsRemoved);
        Assert.Equal(2, result.PackagesRemoved);
        Assert.Equal(2, result.VerifiedManifestCachesRemoved);
        Assert.Equal(1, result.StagingDirectoriesRemoved);
        Assert.Equal(1, result.VerificationDirectoriesRemoved);
        Assert.Equal(0, result.FailedArtifacts);
        Assert.Equal(
            Versions("1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6"),
            ExistingManagedVersions());
        AssertArtifactVersions(
            Path.Combine(_updatesRoot, "packages"),
            ".zip",
            Versions("1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6"));
        Assert.Equal(
            Versions("1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6"),
            ExistingVersionDirectories(Path.Combine(_updatesRoot, "verified")));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "manual-version")));
        Assert.True(File.Exists(Path.Combine(_updatesRoot, "packages", "manual.zip")));
        Assert.True(File.Exists(Path.Combine(_installRoot, "active-version.v1")));
        Assert.True(File.Exists(Path.Combine(
            _installRoot,
            ProductUpdateActivator.ActivationStateDirectoryName,
            "activation-journal.v1.json")));
        Assert.True(File.Exists(Path.Combine(
            _updatesRoot,
            ProductUpdatePendingActivationProtocol.FileName)));
        Assert.Equal("world", File.ReadAllText(serversSentinel));
        Assert.Equal("backup", File.ReadAllText(backupsSentinel));
    }

    [Fact]
    public void Run_NonterminalJournalKeepsBothSidesOfPointerSwitch()
    {
        CreateVersion("1.0.0");
        CreateVersion("1.0.1");
        CreateVersion("1.0.2");
        WriteActivePointer("1.0.2");
        WriteJournal("1.0.1", "1.0.2", ProductUpdateActivationState.HealthChecking);

        var result = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0)).Run();

        Assert.Equal(1, result.InstalledVersionsRemoved);
        Assert.False(Directory.Exists(Path.Combine(_versionsRoot, "1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.1")));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.2")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_MalformedDurableStateFailsClosedBeforeAnyMutation(bool malformedJournal)
    {
        CreateVersion("1.0.0");
        CreateVersion("1.0.1");
        WriteActivePointer("1.0.1");
        var staging = Path.Combine(
            _versionsRoot,
            $".1.1.0.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        if (malformedJournal)
        {
            var activationRoot = Path.Combine(
                _installRoot,
                ProductUpdateActivator.ActivationStateDirectoryName);
            Directory.CreateDirectory(activationRoot);
            File.WriteAllText(Path.Combine(activationRoot, "activation-journal.v1.json"), "{bad-json");
        }
        else
        {
            File.WriteAllText(
                Path.Combine(_updatesRoot, ProductUpdatePendingActivationProtocol.FileName),
                "{bad-json");
        }

        var manager = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0));

        Assert.Throws<InvalidDataException>(() => manager.Run());
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.0")));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void Run_UpdaterLeaseHeldSkipsWithoutMutation()
    {
        CreateVersion("1.0.0");
        CreateVersion("1.0.1");
        WriteActivePointer("1.0.1");
        var activationRoot = Path.Combine(
            _installRoot,
            ProductUpdateActivator.ActivationStateDirectoryName);
        Directory.CreateDirectory(activationRoot);
        using var lease = new FileStream(
            Path.Combine(activationRoot, ".updater.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0)).Run();

        Assert.True(result.SkippedBecauseUpdaterLeaseUnavailable);
        Assert.Equal(0, result.TotalRemoved);
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.0")));
    }

    [Fact]
    public void Run_CrashAfterQuarantineIsIdempotentlyCompletedOnRestart()
    {
        CreateVersion("1.0.0");
        CreateVersion("1.0.1");
        WriteActivePointer("1.0.1");
        var interrupted = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0),
            checkpoint => throw new ProductUpdateRetentionInterruptionException(checkpoint.ToString()));

        Assert.Throws<ProductUpdateRetentionInterruptionException>(() => interrupted.Run());
        Assert.False(Directory.Exists(Path.Combine(_versionsRoot, "1.0.0")));
        Assert.Single(Directory.EnumerateDirectories(_versionsRoot, ".retention-version-*"));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.1")));

        var recovered = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0)).Run();

        Assert.Equal(1, recovered.InstalledVersionsRemoved);
        Assert.Empty(Directory.EnumerateDirectories(_versionsRoot, ".retention-version-*"));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.1")));
    }

    [Fact]
    public void Run_PackageCrashAfterQuarantineIsIdempotentlyCompletedOnRestart()
    {
        CreateVersion("1.0.1");
        CreatePackage("1.0.0");
        WriteActivePointer("1.0.1");
        var packagesRoot = Path.Combine(_updatesRoot, "packages");
        var interrupted = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0),
            checkpoint => throw new ProductUpdateRetentionInterruptionException(checkpoint.ToString()));

        Assert.Throws<ProductUpdateRetentionInterruptionException>(() => interrupted.Run());
        Assert.False(File.Exists(Path.Combine(packagesRoot, "1.0.0.zip")));
        Assert.Single(Directory.EnumerateFiles(packagesRoot, ".retention-package-*"));

        var recovered = new ProductUpdateRetentionManager(
            _installRoot,
            _updatesRoot,
            "1.0.1",
            new ProductUpdateRetentionPolicy(0, 0, 0)).Run();

        Assert.Equal(1, recovered.PackagesRemoved);
        Assert.Empty(Directory.EnumerateFiles(packagesRoot, ".retention-package-*"));
        Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.1")));
    }

    [Fact]
    public void Run_ReparsePointInStaleVersionIsNeverFollowedOrDeleted()
    {
        CreateVersion("1.0.0");
        CreateVersion("1.0.1");
        WriteActivePointer("1.0.1");
        var external = Path.Combine(Path.GetTempPath(), "MuhunMCSV-RetentionExternal", Guid.NewGuid().ToString("N"));
        var sentinel = Path.Combine(external, "must-survive.txt");
        Directory.CreateDirectory(external);
        File.WriteAllText(sentinel, "external");
        var junction = Path.Combine(_versionsRoot, "1.0.0", "external-link");
        CreateDirectoryJunction(junction, external);
        try
        {
            var result = new ProductUpdateRetentionManager(
                _installRoot,
                _updatesRoot,
                "1.0.1",
                new ProductUpdateRetentionPolicy(0, 0, 0)).Run();

            Assert.Equal(1, result.FailedArtifacts);
            Assert.True(Directory.Exists(Path.Combine(_versionsRoot, "1.0.0")));
            Assert.Equal("external", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            Directory.Delete(external, recursive: true);
        }
    }

    private void CreateVersion(string version)
    {
        var path = Path.Combine(_versionsRoot, version);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "payload.bin"), version);
    }

    private void CreatePackage(string version)
    {
        var root = Path.Combine(_updatesRoot, "packages");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, version + ".zip"), version);
    }

    private void CreateVerifiedCache(string version)
    {
        var root = Path.Combine(_updatesRoot, "verified", version);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "manifest.v1.json"), version);
    }

    private void WriteActivePointer(string version)
        => File.WriteAllText(Path.Combine(_installRoot, "active-version.v1"), version + "\n");

    private void WriteJournal(
        string previous,
        string target,
        ProductUpdateActivationState state)
    {
        var root = Path.Combine(_installRoot, ProductUpdateActivator.ActivationStateDirectoryName);
        Directory.CreateDirectory(root);
        var journal = new ProductUpdateActivationJournal(
            1,
            Guid.NewGuid(),
            previous,
            target,
            state,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(root, "activation-journal.v1.json"),
            JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private void WritePending(string version)
        => ProductUpdatePendingActivationProtocol.Write(
            _updatesRoot,
            new ProductUpdatePendingActivation(
                ProductUpdatePendingActivationProtocol.CurrentSchemaVersion,
                ProductUpdateChannel.Stable,
                version,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                new string('A', 64),
                new string('B', 64),
                "release-key",
                new string('C', 64),
                new string('D', 64)));

    private string[] ExistingManagedVersions()
        => ExistingVersionDirectories(_versionsRoot);

    private static string[] ExistingVersionDirectories(string root)
        => Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && char.IsAsciiDigit(name[0]))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertArtifactVersions(
        string root,
        string extension,
        IReadOnlyList<string> expected)
        => Assert.Equal(
            expected,
            Directory.EnumerateFiles(root, "*" + extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && char.IsAsciiDigit(name[0]))
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static string[] Versions(params string[] values)
        => values.Order(StringComparer.Ordinal).ToArray();

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("Could not create retention test junction.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0 && File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint),
            $"Could not create retention test junction: {error}{output}");
    }
}
