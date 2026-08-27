using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ServerLaunchDefinitionTests
{
    [Fact]
    public void ExecutableJar_ChineseRootNestedSpaces_UsesRootRelativeJarArgument()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serverRoot = Path.Combine(temporaryDirectory.Path, "中文 Server 資料夾");
        var nestedRelativePath = Path.Combine("核心 files", "Paper 伺服器.jar");
        var jarPath = Path.Combine(serverRoot, nestedRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        File.WriteAllBytes(jarPath, []);
        var instance = CreateInstance(serverRoot, jarPath);

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(Path.GetFullPath(serverRoot), definition.WorkingDirectory);
        Assert.Equal("-jar", definition.Arguments[^3]);
        Assert.Equal(nestedRelativePath, definition.Arguments[^2]);
        Assert.Equal("nogui", definition.Arguments[^1]);
        Assert.False(Path.IsPathRooted(definition.Arguments[^2]));
        Assert.DoesNotContain(serverRoot, definition.Arguments[^2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutableJar_RelativeNestedPath_IsCanonicalizedAndKeptRelative()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedRelativePath = Path.Combine("cores", "stable", "server.jar");
        var jarPath = Path.Combine(temporaryDirectory.Path, nestedRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        File.WriteAllBytes(jarPath, []);
        var configuredPath = Path.Combine(
            "cores",
            "obsolete",
            "..",
            "stable",
            ".",
            "server.jar");
        var instance = CreateInstance(temporaryDirectory.Path, configuredPath);

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(nestedRelativePath, definition.Arguments[^2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecutableJar_PathOutsideServerRoot_IsRejected(bool useRootedPath)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serverRoot = Path.Combine(temporaryDirectory.Path, "server");
        var outsideJar = Path.Combine(temporaryDirectory.Path, "outside.jar");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllBytes(outsideJar, []);
        var configuredPath = useRootedPath
            ? outsideJar
            : Path.Combine("..", "outside.jar");
        var instance = CreateInstance(serverRoot, configuredPath);

        var error = Assert.Throws<ArgumentException>(
            () => new JavaJarLaunchDefinitionResolver().Resolve(instance));

        Assert.Contains("inside the server directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
    }

    [Fact]
    public void ExecutableJar_MissingJarInsideServerRoot_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var instance = CreateInstance(
            temporaryDirectory.Path,
            Path.Combine("missing", "server.jar"));

        var error = Assert.Throws<FileNotFoundException>(
            () => new JavaJarLaunchDefinitionResolver().Resolve(instance));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(temporaryDirectory.Path, "missing", "server.jar")),
            error.FileName);
    }

    [Fact]
    public void ExecutableJar_PathThroughDirectoryJunctionOrSymlink_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serverRoot = Path.Combine(temporaryDirectory.Path, "server");
        var outsideRoot = Path.Combine(temporaryDirectory.Path, "outside");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllBytes(Path.Combine(outsideRoot, "server.jar"), []);
        var linkPath = Path.Combine(serverRoot, "linked");
        ReparsePointTestHelper.CreateDirectoryLink(linkPath, outsideRoot);
        var instance = CreateInstance(serverRoot, Path.Combine("linked", "server.jar"));

        try
        {
            var error = Assert.Throws<ArgumentException>(
                () => new JavaJarLaunchDefinitionResolver().Resolve(instance));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    [Fact]
    public void ExecutableJar_ServerRootJunctionOrSymlink_IsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var physicalRoot = Path.Combine(temporaryDirectory.Path, "physical");
        var aliasRoot = Path.Combine(temporaryDirectory.Path, "alias");
        Directory.CreateDirectory(physicalRoot);
        File.WriteAllBytes(Path.Combine(physicalRoot, "server.jar"), []);
        ReparsePointTestHelper.CreateDirectoryLink(aliasRoot, physicalRoot);
        var instance = CreateInstance(aliasRoot, "server.jar");

        try
        {
            var error = Assert.Throws<ArgumentException>(
                () => new JavaJarLaunchDefinitionResolver().Resolve(instance));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(aliasRoot);
        }
    }

    [Theory]
    [InlineData(CoreType.Forge)]
    [InlineData(CoreType.NeoForge)]
    public void ExecutableJar_InstallerArtifactIsRejectedBeforeFilesystemLookup(CoreType coreType)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var instance = CreateInstance(temporaryDirectory.Path, "missing-installer.jar");
        instance.CoreType = coreType;

        var error = Assert.Throws<InvalidOperationException>(
            () => new JavaJarLaunchDefinitionResolver().Resolve(instance));

        Assert.Contains("installer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JavaArgumentFiles_BlankServerJarPath_RemainsSupported()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "args.txt"), "-version");
        var instance = CreateInstance(temporaryDirectory.Path, string.Empty);
        instance.LaunchKind = ServerLaunchKind.JavaArgumentFiles;
        instance.JavaArgumentFilePaths = ["args.txt"];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(["@args.txt", "nogui"], definition.Arguments);
    }

    [Theory]
    [InlineData(CoreType.Vanilla)]
    [InlineData(CoreType.Paper)]
    [InlineData(CoreType.Fabric)]
    public void ExecutableJar_DedicatedMinecraftCoreWithoutPersistedHeadlessArgument_AppendsNogui(
        CoreType coreType)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(temporaryDirectory.Path, "server.jar"), []);
        var instance = CreateInstance(temporaryDirectory.Path, "server.jar");
        instance.CoreType = coreType;
        instance.ServerArguments = ["--demo"];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(
            ["-Xms1024M", "-Xmx2048M", "-jar", "server.jar", "--demo", "nogui"],
            definition.Arguments);
        Assert.Equal(["--demo"], instance.ServerArguments);
    }

    [Theory]
    [InlineData(CoreType.Forge)]
    [InlineData(CoreType.NeoForge)]
    public void JavaArgumentFiles_OfficialLoaderPassThroughWithoutServerArguments_AppendsNogui(
        CoreType coreType)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string userArguments = "user_jvm_args.txt";
        const string loaderArguments = "libraries/loader/win_args.txt";
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, userArguments), "-Xmx8G");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "libraries", "loader"));
        File.WriteAllText(
            Path.Combine(temporaryDirectory.Path, loaderArguments.Replace(
                '/',
                Path.DirectorySeparatorChar)),
            "--launchTarget server");
        var instance = CreateInstance(temporaryDirectory.Path, string.Empty);
        instance.CoreType = coreType;
        instance.LaunchKind = ServerLaunchKind.JavaArgumentFiles;
        instance.JavaArgumentFilePaths = [userArguments, loaderArguments];
        instance.JvmArguments = ["-Dmust.not.be.injected=true"];
        instance.ServerArguments = [];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(
            ["@user_jvm_args.txt", "@libraries/loader/win_args.txt", "nogui"],
            definition.Arguments);
        Assert.DoesNotContain("-Dmust.not.be.injected=true", definition.Arguments);
        Assert.Empty(instance.ServerArguments);
    }

    [Fact]
    public void JavaArgumentFiles_ExistingMixedCaseHeadlessArgument_IsPreservedWithoutDuplication()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "args.txt"), "--launchTarget server");
        var instance = CreateInstance(temporaryDirectory.Path, string.Empty);
        instance.CoreType = CoreType.Forge;
        instance.LaunchKind = ServerLaunchKind.JavaArgumentFiles;
        instance.JavaArgumentFilePaths = ["args.txt"];
        instance.ServerArguments = ["--demo", "NoGui"];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(["@args.txt", "--demo", "NoGui"], definition.Arguments);
        Assert.Single(
            definition.Arguments,
            argument => argument.Equals("nogui", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["--demo", "NoGui"], instance.ServerArguments);
    }

    [Theory]
    [InlineData(CoreType.Velocity)]
    [InlineData(CoreType.Unknown)]
    [InlineData(CoreType.CustomJar)]
    public void JavaArgumentFiles_ProxyOrUnknownCore_DoesNotInjectMinecraftHeadlessArgument(
        CoreType coreType)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "args.txt"), "--port 25565");
        var instance = CreateInstance(temporaryDirectory.Path, string.Empty);
        instance.CoreType = coreType;
        instance.LaunchKind = ServerLaunchKind.JavaArgumentFiles;
        instance.JavaArgumentFilePaths = ["args.txt"];
        instance.ServerArguments = ["--port", "25565"];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(["@args.txt", "--port", "25565"], definition.Arguments);
        Assert.DoesNotContain("nogui", definition.Arguments);
    }

    [Theory]
    [InlineData(CoreType.Velocity)]
    [InlineData(CoreType.Waterfall)]
    [InlineData(CoreType.BungeeCord)]
    [InlineData(CoreType.Unknown)]
    [InlineData(CoreType.CustomJar)]
    public void ExecutableJar_ProxyOrUnknownCore_DoesNotInjectMinecraftHeadlessArgument(
        CoreType coreType)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(temporaryDirectory.Path, "server.jar"), []);
        var instance = CreateInstance(temporaryDirectory.Path, "server.jar");
        instance.CoreType = coreType;
        instance.ServerArguments = ["--port", "25565"];

        var definition = new JavaJarLaunchDefinitionResolver().Resolve(instance);

        Assert.Equal(
            ["-Xms1024M", "-Xmx2048M", "-jar", "server.jar", "--port", "25565"],
            definition.Arguments);
        Assert.DoesNotContain("nogui", definition.Arguments);
    }

    private static ServerInstance CreateInstance(string serverRoot, string jarPath) => new()
    {
        Name = "launch-definition-test",
        DirectoryPath = serverRoot,
        ServerJarPath = jarPath,
        LaunchKind = ServerLaunchKind.ExecutableJar,
        JavaExecutablePath = "java.exe",
        MinimumMemoryMb = 1024,
        MaximumMemoryMb = 2048,
        ServerArguments = ["nogui"],
    };
}
