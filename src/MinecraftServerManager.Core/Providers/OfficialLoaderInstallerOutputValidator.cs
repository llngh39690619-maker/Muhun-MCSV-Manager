using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Validates launch layouts emitted by an already hash-verified first-party loader installer.
/// The resulting provenance is valid only for the exact files fingerprinted here; ordinary online
/// packs continue to use <see cref="OnlineServerPackSafetyValidator"/> and cannot opt into these
/// installer-specific wrapper rules.
/// </summary>
public static class OfficialLoaderInstallerOutputValidator
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumFingerprintFiles = 200_000;
    private const long MaximumFabricLauncherBytes = 1024 * 1024;
    private const long MaximumForgeShimBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<OfficialLoaderInstallProvenance> ValidateAndCreateAsync(
        ModrinthModpackLoaderInstallRequest request,
        string outputDirectory,
        string installerPath,
        IReadOnlyList<string> installedPaths,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentNullException.ThrowIfNull(installedPaths);
        var root = ValidateRoot(outputDirectory);
        var installer = ValidateRegularFile(installerPath, "官方 Loader installer");

        var layout = request.Kind switch
        {
            ModrinthModpackLoaderKind.Fabric => await ValidateFabricAsync(
                    root,
                    request.LoaderVersion!,
                    cancellationToken)
                .ConfigureAwait(false),
            ModrinthModpackLoaderKind.Forge or ModrinthModpackLoaderKind.NeoForge =>
                await ValidateForgeFamilyAsync(
                        request,
                        root,
                        installer,
                        compareForgeShimWithInstaller: true,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Kind,
                "此 installer output validator 不支援該 Loader。")
        };

        var fingerprints = await FingerprintFilesAsync(
                root,
                installedPaths,
                cancellationToken)
            .ConfigureAwait(false);
        return new OfficialLoaderInstallProvenance(
            request.Kind,
            request.MinecraftVersion,
            request.LoaderVersion!,
            layout,
            fingerprints);
    }

    public static async Task RevalidateAsync(
        OfficialLoaderInstallProvenance provenance,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var request = new ModrinthModpackLoaderInstallRequest(
            provenance.Kind,
            provenance.MinecraftVersion,
            provenance.LoaderVersion);
        ValidateRequest(request);
        if (!Enum.IsDefined(provenance.LaunchLayout))
        {
            throw new InvalidDataException("官方 Loader provenance 含有未知的 launch layout。");
        }

        var root = ValidateRoot(rootDirectory);
        await VerifyFingerprintsAsync(root, provenance.Files, cancellationToken)
            .ConfigureAwait(false);

        var actualLayout = provenance.Kind switch
        {
            ModrinthModpackLoaderKind.Fabric => await ValidateFabricAsync(
                    root,
                    provenance.LoaderVersion,
                    cancellationToken)
                .ConfigureAwait(false),
            ModrinthModpackLoaderKind.Forge or ModrinthModpackLoaderKind.NeoForge =>
                await ValidateForgeFamilyAsync(
                        request,
                        root,
                        installerPath: null,
                        compareForgeShimWithInstaller: false,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InvalidDataException("官方 Loader provenance 的 Loader kind 無效。")
        };
        if (actualLayout != provenance.LaunchLayout)
        {
            throw new InvalidDataException("官方 Loader launch layout 與安裝時 provenance 不一致。");
        }
    }

    private static async Task<OfficialLoaderLaunchLayout> ValidateFabricAsync(
        string root,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        var launcherPath = SafePath.CombineUnderRoot(root, "fabric-server-launch.jar");
        var launcher = new FileInfo(ValidateRegularFile(launcherPath, "Fabric server launcher"));
        if (launcher.Length > MaximumFabricLauncherBytes)
        {
            throw new InvalidDataException("Fabric server launcher 超過安全大小上限。");
        }

        await using var input = new FileStream(
            launcher.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.ToArray();
        if (entries.Length != 2
            || entries.Count(entry => entry.FullName.Equals(
                "META-INF/MANIFEST.MF",
                StringComparison.Ordinal)) != 1
            || entries.Count(entry => entry.FullName.Equals(
                "fabric-server-launch.properties",
                StringComparison.Ordinal)) != 1)
        {
            throw new InvalidDataException(
                "Fabric server launcher 不是 installer 產生的 manifest-only 啟動結構。");
        }

        var manifest = ParseManifest(await ReadZipTextAsync(
                entries.Single(entry => entry.FullName == "META-INF/MANIFEST.MF"),
                cancellationToken)
            .ConfigureAwait(false));
        if (!ReadExactManifestValue(manifest, "Manifest-Version").Equals("1.0", StringComparison.Ordinal)
            || !ReadExactManifestValue(manifest, "Main-Class").Equals(
                "net.fabricmc.loader.impl.launch.server.FabricServerLauncher",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fabric server launcher manifest 的入口類別無效。");
        }

        var properties = (await ReadZipTextAsync(
                entries.Single(entry => entry.FullName == "fabric-server-launch.properties"),
                cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (!properties.Equals(
                "launch.mainClass=net.fabricmc.loader.impl.launch.knot.KnotServer",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fabric server launcher properties 的 server 主類別無效。");
        }

        var classPath = ReadExactManifestValue(manifest, "Class-Path")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (classPath.Length is < 1 or > 128)
        {
            throw new InvalidDataException("Fabric server launcher Class-Path 數量無效。");
        }

        var expectedLoader = $"libraries/net/fabricmc/fabric-loader/{loaderVersion}/"
            + $"fabric-loader-{loaderVersion}.jar";
        if (!classPath.Contains(expectedLoader, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Fabric launcher 未引用選取的 exact Fabric Loader。");
        }

        foreach (var relativePath in classPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!relativePath.StartsWith("libraries/", StringComparison.Ordinal)
                || !relativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains('\\')
                || !(relativePath.StartsWith(
                        "libraries/net/fabricmc/fabric-loader/",
                        StringComparison.Ordinal)
                    || relativePath.StartsWith(
                        "libraries/net/fabricmc/sponge-mixin/",
                        StringComparison.Ordinal)
                    || relativePath.StartsWith(
                        "libraries/org/ow2/asm/",
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Fabric launcher Class-Path 含有非官方 loader dependency 路徑：{relativePath}");
            }

            ValidateRelativeFile(root, relativePath, "Fabric launcher dependency");
        }

        ValidateRegularFile(SafePath.CombineUnderRoot(root, "server.jar"), "Fabric Minecraft server");
        return OfficialLoaderLaunchLayout.FabricManifestLauncher;
    }

    private static async Task<OfficialLoaderLaunchLayout> ValidateForgeFamilyAsync(
        ModrinthModpackLoaderInstallRequest request,
        string root,
        string? installerPath,
        bool compareForgeShimWithInstaller,
        CancellationToken cancellationToken)
    {
        var detector = new ServerPackDetector();
        var detection = await detector.DetectAsync(root, cancellationToken)
            .ConfigureAwait(false);
        var expectedCore = request.Kind == ModrinthModpackLoaderKind.Forge
            ? CoreType.Forge
            : CoreType.NeoForge;
        // Forge 26.x emits a Java-version preflight block before the real argument-file launch.
        // The general detector correctly rejects multi-command shell scripts. Only while the exact
        // shim is still provably identical to the verified installer do we accept that fixed
        // template and replace it with the manager's shell-free-equivalent single launch line.
        if (!detection.IsRunnable && request.Kind == ModrinthModpackLoaderKind.Forge)
        {
            await ValidateForgeEmbeddedShimAsync(
                    request,
                    root,
                    installerPath,
                    compareForgeShimWithInstaller,
                    cancellationToken)
                .ConfigureAwait(false);
            await NormalizeVerifiedForgeRunScriptsAsync(request, root, cancellationToken)
                .ConfigureAwait(false);
            detection = await detector.DetectAsync(root, cancellationToken).ConfigureAwait(false);
            if (!detection.IsRunnable
                || detection.CoreType != CoreType.Forge
                || !request.MinecraftVersion.Equals(
                    detection.MinecraftVersion,
                    StringComparison.OrdinalIgnoreCase)
                || !LoaderVersionsMatch(
                    detection.ModLoaderVersion,
                    request.LoaderVersion!,
                    request.MinecraftVersion))
            {
                throw new InvalidDataException(
                    "Forge installer 輸出在移除已驗證的 Java preflight 後仍無法形成安全 argument-file 啟動結構。");
            }

            var coordinate = request.LoaderVersion!.StartsWith(
                request.MinecraftVersion + "-",
                StringComparison.Ordinal)
                ? request.LoaderVersion
                : $"{request.MinecraftVersion}-{request.LoaderVersion}";
            await ValidateTypedAuxiliaryArgumentFilesAsync(
                    root,
                    detection.JavaArgumentFilePaths,
                    [
                        $"libraries/net/minecraftforge/forge/{coordinate}/win_args.txt",
                        $"libraries/net/minecraftforge/forge/{coordinate}/unix_args.txt"
                    ],
                    cancellationToken)
                .ConfigureAwait(false);

            return OfficialLoaderLaunchLayout.ForgeInstallerEmbeddedShim;
        }

        if (!detection.IsRecognized
            || detection.CoreType != expectedCore
            || !request.MinecraftVersion.Equals(
                detection.MinecraftVersion,
                StringComparison.OrdinalIgnoreCase)
            || !LoaderVersionsMatch(
                detection.ModLoaderVersion,
                request.LoaderVersion!,
                request.MinecraftVersion))
        {
            throw new InvalidDataException(
                $"官方 {request.Kind} installer 輸出無法靜態對應選取的 Minecraft／Loader 版本。"
                + (string.IsNullOrWhiteSpace(detection.Error) ? string.Empty : $" {detection.Error}"));
        }

        if (!detection.IsRunnable)
        {
            throw new InvalidDataException(
                $"官方 {request.Kind} installer 沒有建立可靜態驗證的 server 啟動結構。"
                + (string.IsNullOrWhiteSpace(detection.Error) ? string.Empty : $" {detection.Error}"));
        }

        try
        {
            await OnlineServerPackSafetyValidator.ValidateAsync(detection, cancellationToken)
                .ConfigureAwait(false);
            return OfficialLoaderLaunchLayout.StandardArgumentFiles;
        }
        catch (InvalidDataException) when (request.Kind == ModrinthModpackLoaderKind.NeoForge)
        {
            await ValidateNeoForgeDirectMainClassAsync(
                    request,
                    root,
                    detection,
                    cancellationToken)
                .ConfigureAwait(false);
            return OfficialLoaderLaunchLayout.NeoForgeDirectMainClass;
        }
        catch (InvalidDataException) when (request.Kind == ModrinthModpackLoaderKind.Forge)
        {
            await ValidateForgeEmbeddedShimAsync(
                    request,
                    root,
                    installerPath,
                    compareForgeShimWithInstaller,
                    cancellationToken)
                .ConfigureAwait(false);
            var coordinate = request.LoaderVersion!.StartsWith(
                request.MinecraftVersion + "-",
                StringComparison.Ordinal)
                ? request.LoaderVersion
                : $"{request.MinecraftVersion}-{request.LoaderVersion}";
            await ValidateTypedAuxiliaryArgumentFilesAsync(
                    root,
                    detection.JavaArgumentFilePaths,
                    [
                        $"libraries/net/minecraftforge/forge/{coordinate}/win_args.txt",
                        $"libraries/net/minecraftforge/forge/{coordinate}/unix_args.txt"
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            return OfficialLoaderLaunchLayout.ForgeInstallerEmbeddedShim;
        }
    }

    private static async Task ValidateNeoForgeDirectMainClassAsync(
        ModrinthModpackLoaderInstallRequest request,
        string root,
        ServerPackDetectionResult detection,
        CancellationToken cancellationToken)
    {
        var directory = $"libraries/net/neoforged/neoforge/{request.LoaderVersion}";
        await ValidateNeoForgeArgumentFileAsync(
                request,
                root,
                $"{directory}/win_args.txt",
                ';',
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateNeoForgeArgumentFileAsync(
                request,
                root,
                $"{directory}/unix_args.txt",
                ':',
                cancellationToken)
            .ConfigureAwait(false);
        await ValidateTypedAuxiliaryArgumentFilesAsync(
                root,
                detection.JavaArgumentFilePaths,
                [$"{directory}/win_args.txt", $"{directory}/unix_args.txt"],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateTypedAuxiliaryArgumentFilesAsync(
        string root,
        IReadOnlyList<string> detectedArgumentFiles,
        IReadOnlyList<string> loaderArgumentFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detectedArgumentFiles);
        var loaders = loaderArgumentFiles
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var detected in detectedArgumentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(detected);
            if (loaders.Contains(relativePath))
            {
                continue;
            }

            var tokens = Tokenize(await ReadTextFileAsync(
                    ValidateRelativeFile(root, relativePath, "官方 Loader auxiliary argument file"),
                    cancellationToken)
                .ConfigureAwait(false));
            foreach (var token in tokens)
            {
                if (!token.StartsWith('-')
                    || token.StartsWith('@')
                    || token.Equals("-jar", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-jar=", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-javaagent=", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-agentlib:", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-agentpath:", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-XX:OnError", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-XX:OnOutOfMemoryError", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith(
                        "-Djava.system.class.loader=",
                        StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("-Xbootclasspath", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("-cp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("-classpath", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--class-path", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("--class-path=", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("-p", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--module-path", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("--module-path=", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--upgrade-module-path", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("--upgrade-module-path=", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--patch-module", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("--patch-module=", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("-m", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--module", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("--module=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"官方 Loader auxiliary argument file 含有未核准的 Java token：{token}");
                }
            }
        }
    }

    private static async Task ValidateNeoForgeArgumentFileAsync(
        ModrinthModpackLoaderInstallRequest request,
        string root,
        string relativePath,
        char classPathSeparator,
        CancellationToken cancellationToken)
    {
        var tokens = Tokenize(await ReadTextFileAsync(
                ValidateRelativeFile(root, relativePath, "NeoForge loader argument file"),
                cancellationToken)
            .ConfigureAwait(false));
        const string mainClass = "net.neoforged.fml.startup.Server";
        var mainIndexes = tokens
            .Select((value, index) => (value, index))
            .Where(item => item.value.Equals(mainClass, StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (mainIndexes.Length != 1)
        {
            throw new InvalidDataException(
                "NeoForge direct-main argument file 必須明確指定官方 server 主類別一次。");
        }

        string? classPath = null;
        var mainIndex = mainIndexes[0];
        for (var index = 0; index < mainIndex; index++)
        {
            var token = tokens[index];
            if (token is "--add-opens" or "--add-exports")
            {
                if (++index >= mainIndex || string.IsNullOrWhiteSpace(tokens[index]))
                {
                    throw new InvalidDataException($"NeoForge JVM option {token} 缺少值。");
                }

                continue;
            }

            if (token is "-classpath" or "-cp" or "--class-path")
            {
                if (classPath is not null || ++index >= mainIndex)
                {
                    throw new InvalidDataException("NeoForge direct-main Class-Path 不明確。");
                }

                classPath = tokens[index];
                continue;
            }

            if (token is "-Djava.net.preferIPv6Addresses=system" or "-DlibraryDirectory=libraries")
            {
                continue;
            }

            throw new InvalidDataException(
                $"NeoForge direct-main argument file 含有未核准的 JVM token：{token}");
        }

        if (string.IsNullOrWhiteSpace(classPath))
        {
            throw new InvalidDataException("NeoForge direct-main argument file 缺少 Class-Path。");
        }

        ValidateNeoForgeClassPath(root, classPath, classPathSeparator);
        var programTokens = tokens.Skip(mainIndex + 1).ToArray();
        var values = ParseExactOptionPairs(
            programTokens,
            ["--fml.neoForgeVersion", "--fml.mcVersion", "--fml.neoFormVersion"]);
        if (!values.TryGetValue("--fml.neoForgeVersion", out var loader)
            || !loader.Equals(request.LoaderVersion, StringComparison.OrdinalIgnoreCase)
            || !values.TryGetValue("--fml.mcVersion", out var minecraft)
            || !minecraft.Equals(request.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
            || !values.TryGetValue("--fml.neoFormVersion", out var neoForm)
            || string.IsNullOrWhiteSpace(neoForm))
        {
            throw new InvalidDataException(
                "NeoForge direct-main argument file 的 exact Minecraft／Loader 證據不一致。");
        }
    }

    private static void ValidateNeoForgeClassPath(
        string root,
        string classPath,
        char separator)
    {
        var entries = classPath.Split(separator, StringSplitOptions.None);
        if (entries.Length is < 1 or > 2_048 || entries.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("NeoForge direct-main Class-Path 數量無效。");
        }

        var hasOfficialLoader = false;
        foreach (var entry in entries)
        {
            if (!entry.StartsWith("libraries/", StringComparison.Ordinal)
                || !entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || entry.Contains('\\'))
            {
                throw new InvalidDataException($"NeoForge Class-Path 路徑無效：{entry}");
            }

            ValidateRelativeFile(root, entry, "NeoForge Class-Path dependency");
            hasOfficialLoader |= entry.StartsWith(
                "libraries/net/neoforged/fancymodloader/loader/",
                StringComparison.Ordinal);
        }

        if (!hasOfficialLoader)
        {
            throw new InvalidDataException("NeoForge Class-Path 未引用官方 FancyModLoader server loader。");
        }
    }

    private static async Task ValidateForgeEmbeddedShimAsync(
        ModrinthModpackLoaderInstallRequest request,
        string root,
        string? installerPath,
        bool compareWithInstaller,
        CancellationToken cancellationToken)
    {
        var coordinate = request.LoaderVersion!.StartsWith(
            request.MinecraftVersion + "-",
            StringComparison.Ordinal)
            ? request.LoaderVersion
            : $"{request.MinecraftVersion}-{request.LoaderVersion}";
        var shimName = $"forge-{coordinate}-shim.jar";
        var shimPath = ValidateRelativeFile(root, shimName, "Forge bootstrap shim");
        var shim = new FileInfo(shimPath);
        if (shim.Length > MaximumForgeShimBytes)
        {
            throw new InvalidDataException("Forge bootstrap shim 超過安全大小上限。");
        }

        var argumentDirectory = $"libraries/net/minecraftforge/forge/{coordinate}";
        foreach (var name in new[] { "win_args.txt", "unix_args.txt" })
        {
            var tokens = Tokenize(await ReadTextFileAsync(
                    ValidateRelativeFile(root, $"{argumentDirectory}/{name}", "Forge loader argument file"),
                    cancellationToken)
                .ConfigureAwait(false));
            var jarIndexes = tokens
                .Select((value, index) => (value, index))
                .Where(item => item.value.Equals("-jar", StringComparison.Ordinal))
                .Select(item => item.index)
                .ToArray();
            if (jarIndexes.Length != 1
                || jarIndexes[0] + 2 != tokens.Count
                || !tokens[jarIndexes[0] + 1].Equals(shimName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Forge argument file 未明確引用 installer 內嵌的 exact bootstrap shim。");
            }

            foreach (var token in tokens.Take(jarIndexes[0]))
            {
                if (token is not (
                    "-Djava.net.preferIPv6Addresses=system" or
                    "-XX:+UseCompactObjectHeaders"))
                {
                    throw new InvalidDataException(
                        $"Forge shim argument file 含有未核准的 JVM token：{token}");
                }
            }
        }

        await ValidateForgeShimMetadataAsync(shim.FullName, cancellationToken)
            .ConfigureAwait(false);
        if (compareWithInstaller)
        {
            if (installerPath is null)
            {
                throw new InvalidDataException("Forge shim 驗證缺少已驗證的 installer。");
            }

            await CompareForgeShimWithInstallerAsync(
                    installerPath,
                    shim.FullName,
                    coordinate,
                    shimName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task NormalizeVerifiedForgeRunScriptsAsync(
        ModrinthModpackLoaderInstallRequest request,
        string root,
        CancellationToken cancellationToken)
    {
        var coordinate = request.LoaderVersion!.StartsWith(
            request.MinecraftVersion + "-",
            StringComparison.Ordinal)
            ? request.LoaderVersion
            : $"{request.MinecraftVersion}-{request.LoaderVersion}";
        var shimName = $"forge-{coordinate}-shim.jar";
        var argumentDirectory = $"libraries/net/minecraftforge/forge/{coordinate}";
        var batchPath = ValidateRelativeFile(root, "run.bat", "Forge Windows launch script");
        var shellPath = ValidateRelativeFile(root, "run.sh", "Forge Linux launch script");
        var batch = await ReadTextFileAsync(batchPath, cancellationToken).ConfigureAwait(false);
        var shell = await ReadTextFileAsync(shellPath, cancellationToken).ConfigureAwait(false);
        var batchPreflight = $"java -jar {shimName} --onlyCheckJava";
        var batchLaunch = $"java @user_jvm_args.txt @{argumentDirectory}/win_args.txt %*";
        var shellPreflight = $"java -jar {shimName} --onlyCheckJava || exit 1";
        var shellLaunch = $"java @user_jvm_args.txt @{argumentDirectory}/unix_args.txt \"$@\"";

        var batchLines = ReadTrimmedLines(batch);
        var allowedBatchLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "@echo off",
            batchPreflight,
            "if %ERRORLEVEL% NEQ 0 (",
            "echo.",
            "echo If you're struggling to fix the error above, ask for help on the forums or Discord mentioned in the readme.",
            "goto :exit",
            ")",
            batchLaunch,
            ":exit",
            "pause"
        };
        if (batchLines.Count(line => line.Equals(batchPreflight, StringComparison.OrdinalIgnoreCase)) != 1
            || batchLines.Count(line => line.Equals(batchLaunch, StringComparison.OrdinalIgnoreCase)) != 1
            || batchLines.Any(line => line.Length > 0
                && !line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)
                && !allowedBatchLines.Contains(line)))
        {
            throw new InvalidDataException("Forge Windows Java preflight script 不是核准的 installer 固定模板。");
        }

        var shellLines = ReadTrimmedLines(shell);
        var allowedShellLines = new HashSet<string>(StringComparer.Ordinal)
        {
            "#!/usr/bin/env sh",
            shellPreflight,
            shellLaunch
        };
        if (shellLines.Count(line => line.Equals(shellPreflight, StringComparison.Ordinal)) != 1
            || shellLines.Count(line => line.Equals(shellLaunch, StringComparison.Ordinal)) != 1
            || shellLines.Any(line => line.Length > 0
                && !line.StartsWith('#')
                && !allowedShellLines.Contains(line)))
        {
            throw new InvalidDataException("Forge Linux Java preflight script 不是核准的 installer 固定模板。");
        }

        var normalizedBatch = "@echo off\r\n"
            + "REM Muhun：exact Forge shim 已在安裝時對照官方 installer 驗證。\r\n"
            + batchLaunch + "\r\npause\r\n";
        var normalizedShell = "#!/usr/bin/env sh\n"
            + "# Muhun: the exact Forge shim was verified against the official installer.\n"
            + "exec " + shellLaunch + "\n";
        await File.WriteAllTextAsync(
                batchPath,
                normalizedBatch,
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                shellPath,
                normalizedShell,
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ReadTrimmedLines(string text)
    {
        var result = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            result.Add(line.Trim());
        }

        return result;
    }

    private static async Task ValidateForgeShimMetadataAsync(
        string shimPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            shimPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntries = archive.Entries.Where(entry => entry.FullName.Equals(
                "META-INF/MANIFEST.MF",
                StringComparison.Ordinal))
            .ToArray();
        var propertyEntries = archive.Entries.Where(entry => entry.FullName.Equals(
                "bootstrap-shim.properties",
                StringComparison.Ordinal))
            .ToArray();
        var listEntries = archive.Entries.Where(entry => entry.FullName.Equals(
                "bootstrap-shim.list",
                StringComparison.Ordinal))
            .ToArray();
        if (manifestEntries.Length != 1
            || propertyEntries.Length != 1
            || listEntries.Length != 1
            || !archive.Entries.Any(entry => entry.FullName.Equals(
                "META-INF/FORGE.SF",
                StringComparison.Ordinal))
            || !archive.Entries.Any(entry => entry.FullName.Equals(
                "META-INF/FORGE.RSA",
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Forge bootstrap shim 缺少官方簽署 metadata。");
        }

        var manifest = ParseManifest(await ReadZipTextAsync(
                manifestEntries[0],
                cancellationToken)
            .ConfigureAwait(false));
        if (!ReadExactManifestValue(manifest, "Main-Class").Equals(
                "net.minecraftforge.bootstrap.shim.Main",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Forge bootstrap shim 的入口類別無效。");
        }

        var properties = ParseProperties(await ReadZipTextAsync(
                propertyEntries[0],
                cancellationToken)
            .ConfigureAwait(false));
        if (properties.Count != 3
            || !ReadExactProperty(properties, "Arguments").Equals(
                "--launchTarget forge_server",
                StringComparison.Ordinal)
            || !ReadExactProperty(properties, "Main-Class").Equals(
                "net.minecraftforge.bootstrap.ForgeBootstrap",
                StringComparison.Ordinal)
            || !int.TryParse(ReadExactProperty(properties, "Java-Version"), out var javaVersion)
            || javaVersion is < 8 or > 99)
        {
            throw new InvalidDataException("Forge bootstrap shim properties 不是官方 server 啟動設定。");
        }

        var list = await ReadZipTextAsync(listEntries[0], cancellationToken).ConfigureAwait(false);
        if (!list.Contains("\tnet.minecraftforge:forge:", StringComparison.Ordinal)
            || !list.Contains(":server\tnet/minecraftforge/forge/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Forge bootstrap shim 清單缺少 Forge server artifact。");
        }
    }

    private static async Task CompareForgeShimWithInstallerAsync(
        string installerPath,
        string shimPath,
        string coordinate,
        string shimName,
        CancellationToken cancellationToken)
    {
        await using var installerInput = new FileStream(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(installerInput, ZipArchiveMode.Read, leaveOpen: false);
        var embeddedPath = $"maven/net/minecraftforge/forge/{coordinate}/{shimName}";
        var embedded = archive.Entries
            .Where(entry => entry.FullName.Equals(embeddedPath, StringComparison.Ordinal))
            .ToArray();
        if (embedded.Length != 1 || embedded[0].Length is < 1 or > MaximumForgeShimBytes)
        {
            throw new InvalidDataException("已驗證的 Forge installer 未內嵌 exact bootstrap shim。");
        }

        var shim = new FileInfo(shimPath);
        if (shim.Length != embedded[0].Length)
        {
            throw new InvalidDataException("Forge bootstrap shim 與已驗證 installer 內嵌檔大小不符。");
        }

        await using var embeddedStream = embedded[0].Open();
        var embeddedHash = await SHA256.HashDataAsync(embeddedStream, cancellationToken)
            .ConfigureAwait(false);
        await using var shimStream = new FileStream(
            shim.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var shimHash = await SHA256.HashDataAsync(shimStream, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(embeddedHash, shimHash))
        {
            throw new InvalidDataException("Forge bootstrap shim 與已驗證 installer 內嵌檔不一致。");
        }
    }

    private static async Task<IReadOnlyList<VerifiedInstallFileFingerprint>> FingerprintFilesAsync(
        string root,
        IReadOnlyList<string> installedPaths,
        CancellationToken cancellationToken)
    {
        if (installedPaths.Count is < 1 or > MaximumFingerprintFiles)
        {
            throw new InvalidDataException("官方 Loader provenance 的檔案數量無效。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VerifiedInstallFileFingerprint>(installedPaths.Count);
        foreach (var relativePath in installedPaths.OrderBy(
                     static path => path,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeRelativePath(relativePath);
            if (!seen.Add(normalized))
            {
                throw new InvalidDataException("官方 Loader output 含有重複路徑。");
            }

            var fullPath = ValidateRelativeFile(root, normalized, "官方 Loader output");
            var info = new FileInfo(fullPath);
            await using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            result.Add(new VerifiedInstallFileFingerprint(
                normalized,
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        return result;
    }

    private static async Task VerifyFingerprintsAsync(
        string root,
        IReadOnlyList<VerifiedInstallFileFingerprint> fingerprints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        if (fingerprints.Count is < 1 or > MaximumFingerprintFiles)
        {
            throw new InvalidDataException("官方 Loader provenance 的檔案數量無效。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fingerprint in fingerprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(fingerprint);
            var relativePath = NormalizeRelativePath(fingerprint.RelativePath);
            if (!seen.Add(relativePath)
                || fingerprint.Length < 0
                || fingerprint.Sha256.Length != 64
                || fingerprint.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("官方 Loader provenance 含有無效 fingerprint。");
            }

            var fullPath = ValidateRelativeFile(root, relativePath, "官方 Loader provenance file");
            var info = new FileInfo(fullPath);
            if (info.Length != fingerprint.Length)
            {
                throw new InvalidDataException(
                    $"官方 Loader 檔案大小在安裝驗證後發生變更：{relativePath}");
            }

            await using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            var expected = Convert.FromHexString(fingerprint.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException(
                    $"官方 Loader 檔案 SHA-256 在安裝驗證後發生變更：{relativePath}");
            }
        }
    }

    private static void ValidateRequest(ModrinthModpackLoaderInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (
                ModrinthModpackLoaderKind.Fabric or
                ModrinthModpackLoaderKind.Forge or
                ModrinthModpackLoaderKind.NeoForge)
            || string.IsNullOrWhiteSpace(request.LoaderVersion))
        {
            throw new ArgumentException("官方 Loader installer request 無效。", nameof(request));
        }

        ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
            request.MinecraftVersion,
            nameof(request.MinecraftVersion));
        ModrinthOfficialLoaderArtifactProvider.ValidateVersionArgument(
            request.LoaderVersion,
            nameof(request.LoaderVersion));
    }

    private static string ValidateRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var info = new DirectoryInfo(root);
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("官方 Loader output 根目錄不存在或是 reparse point。");
        }

        return info.FullName;
    }

    private static string ValidateRelativeFile(string root, string relativePath, string context)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullPath = SafePath.EnsureWithinRoot(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar),
            allowRoot: false);
        SafePath.EnsureNoReparsePointsUnderRoot(root, fullPath);
        return ValidateRegularFile(fullPath, context);
    }

    private static string ValidateRegularFile(string path, string context)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists
            || info.Length < 1
            || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"{context} 不存在、是空檔或不是一般檔案。");
        }

        return info.FullName;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\0')
            || relativePath.Contains('\r')
            || relativePath.Contains('\n'))
        {
            throw new InvalidDataException("官方 Loader output 含有無效相對路徑。");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("官方 Loader output 路徑超出根目錄。");
        }

        return normalized;
    }

    private static async Task<string> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaximumMetadataBytes)
        {
            throw new InvalidDataException("官方 Loader 文字 metadata 大小無效。");
        }

        var bytes = new byte[checked((int)info.Length)];
        await using var input = new FileStream(
            info.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return DecodeUtf8(bytes, "官方 Loader 文字 metadata");
    }

    private static async Task<string> ReadZipTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length is < 1 or > MaximumMetadataBytes)
        {
            throw new InvalidDataException($"Loader JAR metadata 大小無效：{entry.FullName}");
        }

        var bytes = new byte[checked((int)entry.Length)];
        await using var input = entry.Open();
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return DecodeUtf8(bytes, entry.FullName);
    }

    private static string DecodeUtf8(byte[] bytes, string context)
    {
        try
        {
            var text = StrictUtf8.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{context} 不是有效 UTF-8。", exception);
        }
    }

    private static Dictionary<string, string> ParseManifest(string text)
    {
        var unfolded = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            // A signed JAR manifest has additional per-entry sections after the first blank
            // line. Only the main section describes the launch contract; parsing later sections
            // as main attributes would both be incorrect and reject their repeated Name/Digest
            // fields.
            if (line.Length == 0 && unfolded.Count > 0)
            {
                break;
            }

            if (line.StartsWith(' ') && unfolded.Count > 0)
            {
                unfolded[^1] += line[1..];
            }
            else if (line.Length > 0)
            {
                unfolded.Add(line);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unfolded)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !result.TryAdd(
                    line[..separator].Trim(),
                    line[(separator + 1)..].TrimStart()))
            {
                throw new InvalidDataException("Loader JAR manifest 格式無效或含有重複欄位。");
            }
        }

        return result;
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            var separator = value.IndexOf('=');
            if (separator <= 0 || !result.TryAdd(
                    value[..separator].Trim(),
                    value[(separator + 1)..].Trim()))
            {
                throw new InvalidDataException("Loader properties 格式無效或含有重複欄位。");
            }
        }

        return result;
    }

    private static string ReadExactManifestValue(
        IReadOnlyDictionary<string, string> manifest,
        string name)
        => manifest.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Loader JAR manifest 缺少 {name}。");

    private static string ReadExactProperty(
        IReadOnlyDictionary<string, string> properties,
        string name)
        => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Loader properties 缺少 {name}。");

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var current = new StringBuilder();
            char quote = '\0';
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                    }
                    else
                    {
                        current.Append(character);
                    }

                    continue;
                }

                if (character == '#')
                {
                    break;
                }

                if (character is '\'' or '"')
                {
                    quote = character;
                }
                else if (char.IsWhiteSpace(character))
                {
                    AddToken();
                }
                else if (char.IsControl(character))
                {
                    throw new InvalidDataException("Loader argument file 含有控制字元。");
                }
                else
                {
                    current.Append(character);
                }
            }

            if (quote != '\0')
            {
                throw new InvalidDataException("Loader argument file 含有未閉合引號。");
            }

            AddToken();

            void AddToken()
            {
                if (current.Length == 0)
                {
                    return;
                }

                if (tokens.Count >= 100_000)
                {
                    throw new InvalidDataException("Loader argument file token 數量超過安全上限。");
                }

                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        return tokens;
    }

    private static IReadOnlyDictionary<string, string> ParseExactOptionPairs(
        IReadOnlyList<string> tokens,
        IReadOnlyCollection<string> allowedOptions)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index += 2)
        {
            if (index + 1 >= tokens.Count
                || !allowedOptions.Contains(tokens[index], StringComparer.Ordinal)
                || tokens[index + 1].StartsWith('-')
                || !result.TryAdd(tokens[index], tokens[index + 1]))
            {
                throw new InvalidDataException("Loader server program arguments 含有未核准或重複的 option。");
            }
        }

        return result;
    }

    private static bool LoaderVersionsMatch(
        string? actual,
        string expected,
        string minecraftVersion)
        => !string.IsNullOrWhiteSpace(actual)
           && (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
               || actual.Equals(
                   $"{minecraftVersion}-{expected}",
                   StringComparison.OrdinalIgnoreCase));
}
