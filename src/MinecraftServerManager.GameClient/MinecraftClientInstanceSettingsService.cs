using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Reads and atomically updates the user-editable portion of an installed client instance.
/// Installation identity and catalog-owned fields are copied from the registry record and can
/// never be supplied by the caller.
/// </summary>
public sealed class MinecraftClientInstanceSettingsService
{
    public const int MaximumIconFileBytes = 16 * 1024 * 1024;
    public const int MaximumJvmArgumentCount = 256;
    public const int MaximumJvmArgumentLength = 2_048;
    public const int MaximumJvmArgumentsTotalLength = 65_536;

    private static readonly HashSet<string> SupportedIconExtensions = new(
        [".png", ".jpg", ".jpeg", ".bmp", ".ico"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ManagedMemoryArgumentPrefixes =
    [
        "-Xms",
        "-Xmx",
        "-XX:InitialHeapSize",
        "-XX:MaxHeapSize",
        "-XX:InitialRAMPercentage",
        "-XX:MinRAMPercentage",
        "-XX:MaxRAMPercentage",
        "-XX:MaxRAM",
    ];

    private readonly MinecraftClientRegistry _registry;
    private readonly IMinecraftClientJavaExecutableProbe _javaExecutableProbe;
    private readonly string? _instancesRoot;

    public MinecraftClientInstanceSettingsService(MinecraftClientRegistry registry)
        : this(registry, new MinecraftClientJavaExecutableProbe(), null)
    {
    }

    public MinecraftClientInstanceSettingsService(
        MinecraftClientRegistry registry,
        string instancesRoot)
        : this(registry, new MinecraftClientJavaExecutableProbe(), instancesRoot)
    {
    }

    internal MinecraftClientInstanceSettingsService(
        MinecraftClientRegistry registry,
        IMinecraftClientJavaExecutableProbe javaExecutableProbe,
        string? instancesRoot = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _javaExecutableProbe = javaExecutableProbe
            ?? throw new ArgumentNullException(nameof(javaExecutableProbe));
        _instancesRoot = string.IsNullOrWhiteSpace(instancesRoot)
            ? null
            : Path.GetFullPath(instancesRoot);
    }

    public async Task<MinecraftClientInstanceSettingsUpdate> GetSettingsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        var document = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var instance = document.Instances.SingleOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new KeyNotFoundException("The Minecraft client instance was not found.");
        return ToSettings(instance);
    }

    public async Task<int> ValidateJavaExecutableAsync(
        Guid instanceId,
        string javaExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = NormalizeJavaExecutablePath(
            javaExecutablePath,
            nameof(javaExecutablePath))
            ?? throw new ArgumentException(
                "A Java executable must be selected for validation.",
                nameof(javaExecutablePath));
        var document = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var instance = document.Instances.SingleOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new KeyNotFoundException("The Minecraft client instance was not found.");
        if (MinecraftClientProcessRecoveryService.HasPersistedIdentity(instance))
        {
            throw new InvalidOperationException(
                "Client Java cannot be changed while a process identity is active.");
        }

        return await ProbeCompatibleJavaAsync(instance, normalizedPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MinecraftClientInstance> UpdateAsync(
        Guid instanceId,
        MinecraftClientInstanceSettingsUpdate settings,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ValidateAndNormalize(settings);
        var snapshotDocument = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = snapshotDocument.Instances.SingleOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new KeyNotFoundException("The Minecraft client instance was not found.");
        if (MinecraftClientProcessRecoveryService.HasPersistedIdentity(snapshot))
        {
            throw new InvalidOperationException(
                "Client settings cannot be changed while a process identity is active.");
        }

        int? detectedJavaMajorVersion = null;
        if (normalized.JavaExecutablePath is not null)
        {
            detectedJavaMajorVersion = await ProbeCompatibleJavaAsync(
                    snapshot,
                    normalized.JavaExecutablePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var ownedIcon = PersistIconIntoOwnedInstance(
            snapshot,
            normalized.IconImagePath,
            cancellationToken);
        try
        {
            var updatedInstance = await _registry.UpdateAsync(
                document =>
                {
                var index = document.Instances.FindIndex(candidate => candidate.Id == instanceId);
                if (index < 0)
                {
                    throw new KeyNotFoundException("The Minecraft client instance was not found.");
                }

                var current = document.Instances[index];
                if (MinecraftClientProcessRecoveryService.HasPersistedIdentity(current))
                {
                    throw new InvalidOperationException(
                        "Client settings cannot be changed while a process identity is active.");
                }

                var updated = Clone(current);
                updated.Name = normalized.Name;
                updated.IconImagePath = ownedIcon.Path;
                updated.WindowWidth = normalized.WindowWidth;
                updated.WindowHeight = normalized.WindowHeight;
                updated.FullScreen = normalized.FullScreen;
                updated.EnableQuickLaunch = normalized.EnableQuickLaunch;
                updated.HideLauncherAfterGameStarts = normalized.HideLauncherAfterGameStarts;
                updated.ShowGameLog = normalized.ShowGameLog;
                updated.EnableDedicatedGpu = normalized.EnableDedicatedGpu;
                updated.EnableDiscordPresence = normalized.EnableDiscordPresence;
                updated.MemoryMode = normalized.MemoryMode;
                updated.MinimumMemoryMb = normalized.MinimumMemoryMb;
                updated.MaximumMemoryMb = normalized.MaximumMemoryMb;
                updated.JavaExecutablePath = normalized.JavaExecutablePath;
                updated.JavaMajorVersion = detectedJavaMajorVersion;
                updated.JvmArguments = [.. normalized.JvmArguments];

                document.Instances[index] = updated;
                    return Clone(updated);
                },
                cancellationToken).ConfigureAwait(false);
            DeleteSupersededOwnedIcon(snapshot.DirectoryPath, snapshot.IconImagePath, ownedIcon.Path);
            return updatedInstance;
        }
        catch
        {
            if (ownedIcon.Created && ownedIcon.Path is not null)
            {
                TryDeleteOwnedIcon(snapshot.DirectoryPath, ownedIcon.Path);
            }

            throw;
        }
    }

    private async Task<int> ProbeCompatibleJavaAsync(
        MinecraftClientInstance instance,
        string javaExecutablePath,
        CancellationToken cancellationToken)
    {
        int detectedJavaMajorVersion;
        try
        {
            detectedJavaMajorVersion = await _javaExecutableProbe
                .ProbeMajorVersionAsync(javaExecutablePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException(
                "X MCSV could not validate the selected Java executable with a bounded " +
                "java -version check. Choose a working java.exe/javaw.exe, or clear the " +
                "custom path to use the managed runtime.",
                error);
        }

        MinecraftClientJavaCompatibility.EnsureMatchesMinecraft(
            instance.GameVersion,
            detectedJavaMajorVersion);
        return detectedJavaMajorVersion;
    }

    private static NormalizedSettings ValidateAndNormalize(
        MinecraftClientInstanceSettingsUpdate settings)
    {
        var name = settings.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            throw new ArgumentException("The Minecraft client instance name is invalid.", nameof(settings));
        }

        if (settings.WindowWidth is < 640 or > 16_384 ||
            settings.WindowHeight is < 360 or > 16_384)
        {
            throw new ArgumentException(
                "The Minecraft client resolution must be between 640x360 and 16384x16384.",
                nameof(settings));
        }

        if (!Enum.IsDefined(settings.MemoryMode))
        {
            throw new ArgumentException("The Minecraft client memory mode is unsupported.", nameof(settings));
        }

        if (settings.MinimumMemoryMb is < 512 or > 262_144 ||
            settings.MaximumMemoryMb < settings.MinimumMemoryMb ||
            settings.MaximumMemoryMb > 262_144)
        {
            throw new ArgumentException(
                "The Minecraft client memory range must be a valid 512-262144 MB range.",
                nameof(settings));
        }

        var arguments = ValidateJvmArguments(settings.JvmArguments, nameof(settings));
        return new NormalizedSettings(
            name,
            NormalizeIconPath(settings.IconImagePath, nameof(settings)),
            settings.WindowWidth,
            settings.WindowHeight,
            settings.FullScreen,
            settings.EnableQuickLaunch,
            settings.HideLauncherAfterGameStarts,
            settings.ShowGameLog,
            settings.EnableDedicatedGpu,
            settings.EnableDiscordPresence,
            settings.MemoryMode,
            settings.MinimumMemoryMb,
            settings.MaximumMemoryMb,
            NormalizeJavaExecutablePath(settings.JavaExecutablePath, nameof(settings)),
            arguments);
    }

    private static IReadOnlyList<string> ValidateJvmArguments(
        IReadOnlyList<string>? arguments,
        string parameterName)
    {
        if (arguments is null || arguments.Count > MaximumJvmArgumentCount)
        {
            throw new ArgumentException("There are too many custom JVM arguments.", parameterName);
        }

        var normalized = new List<string>(arguments.Count);
        var totalLength = 0;
        foreach (var rawArgument in arguments)
        {
            var argument = rawArgument?.Trim();
            if (string.IsNullOrWhiteSpace(argument) ||
                argument.Length > MaximumJvmArgumentLength ||
                !argument.StartsWith("-", StringComparison.Ordinal) ||
                argument.Any(character => character is '\0' or '\r' or '\n'))
            {
                throw new ArgumentException("A custom JVM argument is invalid.", parameterName);
            }

            if (ManagedMemoryArgumentPrefixes.Any(prefix =>
                    argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "Heap-size JVM arguments are managed by the selected memory policy.",
                    parameterName);
            }

            totalLength = checked(totalLength + argument.Length);
            if (totalLength > MaximumJvmArgumentsTotalLength)
            {
                throw new ArgumentException("Custom JVM arguments exceed the safe size limit.", parameterName);
            }

            normalized.Add(argument);
        }

        return normalized;
    }

    private static string? NormalizeIconPath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = NormalizeAbsolutePath(path, "icon", parameterName);
        if (!SupportedIconExtensions.Contains(Path.GetExtension(fullPath)))
        {
            throw new ArgumentException(
                "The client icon must be PNG, JPEG, BMP or ICO.",
                parameterName);
        }

        var file = ValidateRegularFile(fullPath, "client icon");
        if (file.Length > MaximumIconFileBytes)
        {
            throw new ArgumentException("The client icon exceeds the 16 MiB safety limit.", parameterName);
        }

        return fullPath;
    }

    private OwnedIconCopy PersistIconIntoOwnedInstance(
        MinecraftClientInstance instance,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (sourcePath is null)
        {
            return new OwnedIconCopy(null, false);
        }

        var instanceRoot = Path.GetFullPath(instance.DirectoryPath);
        if (_instancesRoot is not null)
        {
            SafePath.EnsureWithinRoot(_instancesRoot, instanceRoot, allowRoot: false);
            SafePath.EnsureNoReparsePointsUnderRoot(_instancesRoot, instanceRoot);
        }
        else
        {
            SafePath.EnsureNoReparsePointsUnderRoot(instanceRoot, instanceRoot);
        }

        var assetsDirectory = SafePath.CombineUnderRoot(instanceRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assetsDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(instanceRoot, assetsDirectory);
        var normalizedSource = Path.GetFullPath(sourcePath);
        if (SafePath.IsWithinRoot(assetsDirectory, normalizedSource))
        {
            SafePath.EnsureNoReparsePointsUnderRoot(instanceRoot, normalizedSource);
            return new OwnedIconCopy(normalizedSource, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(normalizedSource).ToLowerInvariant();
        var destination = SafePath.CombineUnderRoot(
            assetsDirectory,
            $"custom-icon-{Guid.NewGuid():N}{extension}");
        var temporary = SafePath.CombineUnderRoot(
            assetsDirectory,
            $".custom-icon-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(
                       normalizedSource,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copied = ValidateRegularFile(temporary, "copied client icon");
            if (copied.Length is <= 0 or > MaximumIconFileBytes)
            {
                throw new InvalidDataException("The copied client icon is outside the safe size limit.");
            }

            File.Move(temporary, destination);
            return new OwnedIconCopy(destination, true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void DeleteSupersededOwnedIcon(
        string instanceRoot,
        string? previousPath,
        string? currentPath)
    {
        if (previousPath is null ||
            string.Equals(previousPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteOwnedIcon(instanceRoot, previousPath);
    }

    private static void TryDeleteOwnedIcon(string instanceRoot, string candidatePath)
    {
        try
        {
            var assetsDirectory = SafePath.CombineUnderRoot(instanceRoot, ".x-mcsv", "assets");
            var candidate = SafePath.EnsureWithinRoot(assetsDirectory, candidatePath, allowRoot: false);
            if (!Path.GetFileName(candidate).StartsWith("custom-icon-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SafePath.EnsureNoReparsePointsUnderRoot(instanceRoot, candidate);
            File.Delete(candidate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? NormalizeJavaExecutablePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = NormalizeAbsolutePath(path, "Java executable", parameterName);
        var fileName = Path.GetFileName(fullPath);
        if (!string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The selected Java executable must be java.exe or javaw.exe.",
                parameterName);
        }

        ValidateRegularFile(fullPath, "Java executable");
        return fullPath;
    }

    private static string NormalizeAbsolutePath(
        string path,
        string label,
        string parameterName)
    {
        if (path.Length > 32_767 || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The {label} path must be absolute.", parameterName);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"The {label} path is invalid.", parameterName, error);
        }
    }

    private static FileInfo ValidateRegularFile(string fullPath, string label)
    {
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The selected {label} does not exist.", fullPath);
        }

        var file = new FileInfo(fullPath);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"The selected {label} cannot be a reparse point.");
        }

        return file;
    }

    private static void ValidateInstanceId(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("The Minecraft client instance id is invalid.", nameof(instanceId));
        }
    }

    private static MinecraftClientInstanceSettingsUpdate ToSettings(MinecraftClientInstance instance) =>
        new()
        {
            Name = instance.Name,
            IconImagePath = instance.IconImagePath,
            WindowWidth = instance.WindowWidth,
            WindowHeight = instance.WindowHeight,
            FullScreen = instance.FullScreen,
            EnableQuickLaunch = instance.EnableQuickLaunch,
            HideLauncherAfterGameStarts = instance.HideLauncherAfterGameStarts,
            ShowGameLog = instance.ShowGameLog,
            EnableDedicatedGpu = instance.EnableDedicatedGpu,
            EnableDiscordPresence = instance.EnableDiscordPresence,
            MemoryMode = instance.MemoryMode,
            MinimumMemoryMb = instance.MinimumMemoryMb,
            MaximumMemoryMb = instance.MaximumMemoryMb,
            JavaExecutablePath = instance.JavaExecutablePath,
            JvmArguments = [.. instance.JvmArguments],
        };

    private static MinecraftClientInstance Clone(MinecraftClientInstance source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Edition = source.Edition,
        DirectoryPath = source.DirectoryPath,
        GameVersion = source.GameVersion,
        InstalledVersionId = source.InstalledVersionId,
        Loader = source.Loader,
        LoaderVersion = source.LoaderVersion,
        LoaderInstallKind = source.LoaderInstallKind,
        JavaMajorVersion = source.JavaMajorVersion,
        JavaExecutablePath = source.JavaExecutablePath,
        MemoryMode = source.MemoryMode,
        MinimumMemoryMb = source.MinimumMemoryMb,
        MaximumMemoryMb = source.MaximumMemoryMb,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        FullScreen = source.FullScreen,
        EnableQuickLaunch = source.EnableQuickLaunch,
        HideLauncherAfterGameStarts = source.HideLauncherAfterGameStarts,
        ShowGameLog = source.ShowGameLog,
        EnableDedicatedGpu = source.EnableDedicatedGpu,
        EnableDiscordPresence = source.EnableDiscordPresence,
        JvmArguments = [.. source.JvmArguments],
        EnvironmentVariables = new Dictionary<string, string>(
            source.EnvironmentVariables,
            StringComparer.OrdinalIgnoreCase),
        AccountId = source.AccountId,
        BackgroundImagePath = source.BackgroundImagePath,
        BackgroundImageOpacity = source.BackgroundImageOpacity,
        IconImagePath = source.IconImagePath,
        CatalogIconImagePath = source.CatalogIconImagePath,
        CatalogPreviewImagePath = source.CatalogPreviewImagePath,
        CatalogProvider = source.CatalogProvider,
        CatalogProjectId = source.CatalogProjectId,
        CatalogVersionId = source.CatalogVersionId,
        CatalogIconUri = source.CatalogIconUri,
        CatalogPreviewUri = source.CatalogPreviewUri,
        LastPlayedAtUtc = source.LastPlayedAtUtc,
        TotalPlayTimeSeconds = source.TotalPlayTimeSeconds,
        ActiveProcessId = source.ActiveProcessId,
        ActiveProcessStartedAtUtc = source.ActiveProcessStartedAtUtc,
        ActiveProcessExecutablePath = source.ActiveProcessExecutablePath,
        CreatedAtUtc = source.CreatedAtUtc,
    };

    private sealed record OwnedIconCopy(string? Path, bool Created);

    private sealed record NormalizedSettings(
        string Name,
        string? IconImagePath,
        int WindowWidth,
        int WindowHeight,
        bool FullScreen,
        bool EnableQuickLaunch,
        bool HideLauncherAfterGameStarts,
        bool ShowGameLog,
        bool EnableDedicatedGpu,
        bool EnableDiscordPresence,
        MinecraftClientMemoryMode MemoryMode,
        int MinimumMemoryMb,
        int MaximumMemoryMb,
        string? JavaExecutablePath,
        IReadOnlyList<string> JvmArguments);
}
