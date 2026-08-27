using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Statically inspects installed Forge/NeoForge server packs. Launch scripts are treated only as
/// untrusted text: the detector accepts a deliberately small grammar and never executes them.
/// </summary>
public sealed class ServerPackDetector
{
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const long MaximumScriptBytes = 256L * 1024;
    private const long MaximumArgumentFileBytes = 2L * 1024 * 1024;
    private readonly IHostPlatformProbe _platformProbe;

    public ServerPackDetector(IHostPlatformProbe? platformProbe = null)
    {
        _platformProbe = platformProbe ?? new SystemHostPlatformProbe();
    }

    public async Task<ServerPackDetectionResult> DetectAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Server-pack directory was not found: {root}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var hostOs = _platformProbe.OperatingSystem;
        var evidence = new List<string>
        {
            $"Host platform: {hostOs} ({_platformProbe.OSArchitecture}).",
        };
        var warnings = new List<string>();
        var errors = new List<string>();

        var manifest = await ReadManifestAsync(root, evidence, warnings, cancellationToken)
            .ConfigureAwait(false);
        var installations = DiscoverLoaderInstallations(root, warnings, cancellationToken);
        foreach (var installation in installations)
        {
            evidence.Add(
                $"Installed {installation.CoreType} libraries found at "
                + $"'{installation.RelativeDirectory}'.");
        }

        var scriptFileName = hostOs switch
        {
            HostOperatingSystem.Windows => "run.bat",
            HostOperatingSystem.Linux => "run.sh",
            _ => null,
        };
        string? sourceScriptPath = scriptFileName is null
            ? null
            : Path.Combine(root, scriptFileName);
        ParsedLaunchScript? parsedScript = null;

        if (hostOs == HostOperatingSystem.Unsupported)
        {
            errors.Add("Only Windows and Linux installed server packs are supported.");
        }
        else if (sourceScriptPath is null || !File.Exists(sourceScriptPath))
        {
            errors.Add(
                $"The {hostOs} launch script '{scriptFileName}' is missing. "
                + "The other operating-system script will not be substituted.");
        }
        else
        {
            try
            {
                parsedScript = await ParseLaunchScriptAsync(
                        sourceScriptPath,
                        hostOs,
                        cancellationToken)
                    .ConfigureAwait(false);
                evidence.Add($"Safely parsed '{scriptFileName}' without executing it.");
            }
            catch (Exception error) when (
                error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add(error.Message);
            }
        }

        var argumentFilePaths = new List<string>();
        var serverArguments = new List<string>();
        string? javaExecutablePath = null;
        LoaderInstallation? selectedInstallation = null;

        if (parsedScript is not null)
        {
            serverArguments.AddRange(parsedScript.ServerArguments);
            foreach (var scriptPath in parsedScript.ArgumentFilePaths)
            {
                try
                {
                    var normalized = ValidateRelativeFile(root, scriptPath, "Java argument file");
                    argumentFilePaths.Add(normalized);
                }
                catch (Exception error) when (
                    error is ArgumentException or UnauthorizedAccessException or FileNotFoundException)
                {
                    errors.Add(error.Message);
                }
            }

            if (argumentFilePaths.Count == parsedScript.ArgumentFilePaths.Count)
            {
                selectedInstallation = SelectLoaderInstallation(
                    argumentFilePaths,
                    installations,
                    hostOs,
                    errors);
            }

            try
            {
                javaExecutablePath = ResolveJavaExecutable(
                    root,
                    parsedScript.JavaToken,
                    manifest?.JavaDistributionVersion,
                    hostOs,
                    warnings);
                if (Path.IsPathRooted(javaExecutablePath))
                {
                    evidence.Add("The launch script selects a bundled Java executable.");
                }
                else
                {
                    evidence.Add($"The launch script selects Java from PATH ('{javaExecutablePath}').");
                }
            }
            catch (Exception error) when (
                error is ArgumentException or UnauthorizedAccessException or FileNotFoundException
                    or InvalidDataException)
            {
                errors.Add(error.Message);
            }
        }

        var coreType = selectedInstallation?.CoreType
            ?? manifest?.CoreType
            ?? (installations.Count == 1 ? installations[0].CoreType : CoreType.Unknown);
        if (manifest?.CoreType is not null
            && selectedInstallation is not null
            && manifest.CoreType != selectedInstallation.CoreType)
        {
            errors.Add(
                $"Ambiguous mod loader: the manifest identifies {manifest.CoreType}, but the "
                + $"selected argument file identifies {selectedInstallation.CoreType}.");
        }

        LoaderArgumentMetadata loaderArgumentMetadata;
        try
        {
            loaderArgumentMetadata = await ReadLoaderArgumentMetadataAsync(
                    root,
                    argumentFilePaths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add($"Could not safely inspect the loader argument file: {error.Message}");
            loaderArgumentMetadata = new LoaderArgumentMetadata(null, null);
        }
        var minecraftVersion = manifest?.MinecraftVersion
            ?? loaderArgumentMetadata.MinecraftVersion
            ?? InferMinecraftVersion(selectedInstallation);
        var loaderVersion = manifest?.ModLoaderVersion
            ?? loaderArgumentMetadata.ModLoaderVersion
            ?? selectedInstallation?.Version;
        var javaMajor = ParseJavaMajor(manifest?.JavaDistributionVersion);
        if (javaMajor is null && minecraftVersion is not null)
        {
            javaMajor = new JavaVersionRecommendationService()
                .GetRecommendation(minecraftVersion, coreType)
                .MajorVersion;
        }

        (int minimumMemoryMb, int maximumMemoryMb, string? memoryError) = (1024, 4096, null);
        try
        {
            (minimumMemoryMb, maximumMemoryMb, memoryError) = await DetectMemoryAsync(
                    root,
                    argumentFilePaths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            memoryError = $"Could not safely inspect JVM memory arguments: {error.Message}";
        }
        if (memoryError is not null)
        {
            errors.Add(memoryError);
        }

        var packName = manifest?.PackName;
        var packVersion = manifest?.PackVersion;
        var fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var suggestedName = SafePath.SanitizeFileName(
            string.Join(
                ' ',
                new[] { packName ?? fallbackName, packVersion }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
        var isRecognized = manifest is not null
            || installations.Count > 0
            || sourceScriptPath is not null && File.Exists(sourceScriptPath);
        var confidence = 0;
        if (manifest is not null)
        {
            confidence += 35;
        }

        if (selectedInstallation is not null)
        {
            confidence += 30;
        }
        else if (installations.Count > 0)
        {
            confidence += 15;
        }

        if (parsedScript is not null)
        {
            confidence += 20;
        }

        if (argumentFilePaths.Count > 0 && argumentFilePaths.Count == parsedScript?.ArgumentFilePaths.Count)
        {
            confidence += 10;
        }

        if (javaExecutablePath is not null)
        {
            confidence += 5;
        }

        if (coreType is CoreType.Forge or CoreType.NeoForge)
        {
            evidence.Add($"Detected installed {coreType} argument-file launch layout.");
        }

        var errorText = errors.Count == 0
            ? null
            : string.Join(" ", errors.Distinct(StringComparer.Ordinal));
        var runnable = isRecognized
            && errorText is null
            && hostOs is HostOperatingSystem.Windows or HostOperatingSystem.Linux
            && parsedScript is not null
            && javaExecutablePath is not null
            && argumentFilePaths.Count > 0
            && selectedInstallation is not null;

        return new ServerPackDetectionResult
        {
            DirectoryPath = root,
            IsRecognized = isRecognized,
            IsRunnable = runnable,
            Error = errorText,
            SuggestedName = suggestedName,
            PackName = packName,
            PackVersion = packVersion,
            CoreType = coreType,
            MinecraftVersion = minecraftVersion,
            ModLoaderVersion = loaderVersion,
            JavaMajorVersion = javaMajor,
            JavaExecutablePath = javaExecutablePath,
            SourceLaunchScriptPath = sourceScriptPath is not null && File.Exists(sourceScriptPath)
                ? sourceScriptPath
                : null,
            JavaArgumentFilePaths = argumentFilePaths.ToArray(),
            ServerArguments = serverArguments.ToArray(),
            MinimumMemoryMb = minimumMemoryMb,
            MaximumMemoryMb = maximumMemoryMb,
            ConfidencePercent = Math.Clamp(confidence, 0, 100),
            Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            HostOperatingSystem = hostOs,
        };
    }

    private static async Task<ManifestMetadata?> ReadManifestAsync(
        string root,
        ICollection<string> evidence,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileInfo(path);
        if (file.Length > MaximumManifestBytes)
        {
            warnings.Add(
                $"Skipped .manifest.json because it exceeds the {MaximumManifestBytes / 1024 / 1024} MiB safety limit.");
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 64,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("Ignored .manifest.json because its root is not an object.");
                return null;
            }

            var rootElement = document.RootElement;
            var packName = GetString(rootElement, "name");
            var packVersion = GetString(rootElement, "versionName")
                ?? GetString(rootElement, "version");
            string? minecraftVersion = null;
            string? modLoaderName = null;
            string? modLoaderVersion = null;
            string? javaVersion = null;

            if (TryGetProperty(rootElement, "modPackTargets", out var targets)
                && targets.ValueKind == JsonValueKind.Object)
            {
                minecraftVersion = GetString(targets, "mcVersion")
                    ?? GetString(targets, "minecraftVersion");
                javaVersion = GetString(targets, "javaVersion");
                if (TryGetProperty(targets, "modLoader", out var loader)
                    && loader.ValueKind == JsonValueKind.Object)
                {
                    modLoaderName = GetString(loader, "name");
                    modLoaderVersion = GetString(loader, "version");
                }
            }

            var coreType = ParseCoreType(modLoaderName);
            evidence.Add("Read bounded FTB .manifest.json metadata.");
            return new ManifestMetadata(
                packName,
                packVersion,
                minecraftVersion,
                coreType,
                modLoaderVersion,
                javaVersion);
        }
        catch (JsonException error)
        {
            warnings.Add($"Ignored malformed .manifest.json: {error.Message}");
            return null;
        }
        catch (IOException error)
        {
            warnings.Add($"Could not read .manifest.json: {error.Message}");
            return null;
        }
    }

    private static IReadOnlyList<LoaderInstallation> DiscoverLoaderInstallations(
        string root,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var installations = new List<LoaderInstallation>();
        Discover(
            CoreType.NeoForge,
            "libraries/net/neoforged/neoforge",
            root,
            installations,
            warnings,
            cancellationToken);
        Discover(
            CoreType.Forge,
            "libraries/net/minecraftforge/forge",
            root,
            installations,
            warnings,
            cancellationToken);
        return installations;

        static void Discover(
            CoreType coreType,
            string relativeParent,
            string root,
            ICollection<LoaderInstallation> destination,
            ICollection<string> warnings,
            CancellationToken cancellationToken)
        {
            var parent = Path.Combine(root, NormalizeForFileSystem(relativeParent));
            if (!Directory.Exists(parent))
            {
                return;
            }

            var count = 0;
            foreach (var versionDirectory in Directory.EnumerateDirectories(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 128)
                {
                    warnings.Add($"Stopped scanning '{relativeParent}' after 128 version folders.");
                    break;
                }

                if (File.GetAttributes(versionDirectory).HasFlag(FileAttributes.ReparsePoint))
                {
                    warnings.Add($"Skipped reparse-point loader folder '{versionDirectory}'.");
                    continue;
                }

                var relativeDirectory = ToModelRelativePath(root, versionDirectory);
                destination.Add(new LoaderInstallation(
                    coreType,
                    Path.GetFileName(versionDirectory),
                    relativeDirectory,
                    File.Exists(Path.Combine(versionDirectory, "win_args.txt"))
                        ? relativeDirectory + "/win_args.txt"
                        : null,
                    File.Exists(Path.Combine(versionDirectory, "unix_args.txt"))
                        ? relativeDirectory + "/unix_args.txt"
                        : null));
            }
        }
    }

    private static async Task<ParsedLaunchScript> ParseLaunchScriptAsync(
        string path,
        HostOperatingSystem hostOs,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (file.Length > MaximumScriptBytes)
        {
            throw new InvalidDataException(
                $"Rejected '{file.Name}': it exceeds the {MaximumScriptBytes / 1024} KiB script safety limit.");
        }

        var text = await ReadBoundedTextAsync(path, MaximumScriptBytes, cancellationToken)
            .ConfigureAwait(false);
        var launches = new List<ParsedLaunchScript>();
        using var reader = new StringReader(text);
        string? rawLine;
        var lineNumber = 0;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || IsSafeNonCommandLine(line, hostOs))
            {
                continue;
            }

            ValidateNoShellSyntax(line, hostOs, file.Name, lineNumber);
            if (!TryTokenize(line, out var tokens) || tokens.Count == 0)
            {
                throw new InvalidDataException(
                    $"Rejected '{file.Name}' line {lineNumber}: malformed quoting.");
            }

            if (hostOs == HostOperatingSystem.Linux
                && string.Equals(tokens[0], "exec", StringComparison.Ordinal))
            {
                tokens.RemoveAt(0);
            }

            if (tokens.Count == 0 || !IsJavaToken(tokens[0], hostOs))
            {
                throw new InvalidDataException(
                    $"Rejected '{file.Name}' line {lineNumber}: it contains an unapproved command.");
            }

            var argumentFiles = new List<string>();
            var serverArguments = new List<string>();
            for (var index = 1; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (IsPassThroughToken(token, hostOs))
                {
                    if (index != tokens.Count - 1)
                    {
                        throw new InvalidDataException(
                            $"Rejected '{file.Name}' line {lineNumber}: pass-through arguments must be last.");
                    }

                    continue;
                }

                if (token.StartsWith('@'))
                {
                    if (token.Length == 1)
                    {
                        throw new InvalidDataException(
                            $"Rejected '{file.Name}' line {lineNumber}: empty Java argument-file reference.");
                    }

                    argumentFiles.Add(token[1..]);
                }
                else
                {
                    serverArguments.Add(token);
                }
            }

            if (argumentFiles.Count == 0)
            {
                throw new InvalidDataException(
                    $"Rejected '{file.Name}' line {lineNumber}: no Java argument file is referenced.");
            }

            launches.Add(new ParsedLaunchScript(tokens[0], argumentFiles, serverArguments));
        }

        return launches.Count switch
        {
            1 => launches[0],
            0 => throw new InvalidDataException(
                $"Rejected '{file.Name}': no safe Java launch command was found."),
            _ => throw new InvalidDataException(
                $"Rejected '{file.Name}': multiple Java launch commands are ambiguous."),
        };
    }

    private static bool IsSafeNonCommandLine(string line, HostOperatingSystem hostOs)
    {
        if (hostOs == HostOperatingSystem.Windows)
        {
            return line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "REM", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("::", StringComparison.Ordinal)
                || string.Equals(line, "@echo off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "echo off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "pause", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "@pause", StringComparison.OrdinalIgnoreCase);
        }

        return line.StartsWith('#');
    }

    private static void ValidateNoShellSyntax(
        string line,
        HostOperatingSystem hostOs,
        string fileName,
        int lineNumber)
    {
        var forbidden = hostOs == HostOperatingSystem.Windows ? "&|<>^" : ";|&<>`";
        if (line.IndexOfAny(forbidden.ToCharArray()) >= 0
            || hostOs == HostOperatingSystem.Windows
                && ContainsUnapprovedPercentExpansion(line)
            || hostOs == HostOperatingSystem.Linux
                && ContainsUnapprovedDollarExpansion(line))
        {
            throw new InvalidDataException(
                $"Rejected '{fileName}' line {lineNumber}: shell operators or expansions are not allowed.");
        }
    }

    private static bool ContainsUnapprovedPercentExpansion(string line)
    {
        var withoutPassThrough = line.Replace("%*", string.Empty, StringComparison.Ordinal);
        return withoutPassThrough.Contains('%');
    }

    private static bool ContainsUnapprovedDollarExpansion(string line)
    {
        var withoutPassThrough = line
            .Replace("\"$@\"", string.Empty, StringComparison.Ordinal)
            .Replace("$@", string.Empty, StringComparison.Ordinal);
        return withoutPassThrough.Contains('$');
    }

    private static bool TryTokenize(string command, out List<string> tokens)
    {
        tokens = [];
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in command)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quoted)
        {
            tokens.Clear();
            return false;
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return true;
    }

    private static bool IsJavaToken(string token, HostOperatingSystem hostOs)
    {
        var fileName = Path.GetFileName(token.Replace('\\', '/'));
        return hostOs switch
        {
            HostOperatingSystem.Windows =>
                string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "java", StringComparison.OrdinalIgnoreCase),
            HostOperatingSystem.Linux => string.Equals(fileName, "java", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsPassThroughToken(string token, HostOperatingSystem hostOs) =>
        hostOs == HostOperatingSystem.Windows
            ? string.Equals(token, "%*", StringComparison.Ordinal)
            : string.Equals(token, "$@", StringComparison.Ordinal);

    private static string ValidateRelativeFile(string root, string untrustedPath, string description)
    {
        if (string.IsNullOrWhiteSpace(untrustedPath)
            || untrustedPath.Contains('\0')
            || untrustedPath.Contains('\r')
            || untrustedPath.Contains('\n'))
        {
            throw new ArgumentException($"{description} path is blank or contains control characters.");
        }

        var fileSystemPath = NormalizeForFileSystem(untrustedPath);
        var segments = fileSystemPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(fileSystemPath)
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException(
                $"Rejected {description.ToLowerInvariant()} path outside the server-pack root: '{untrustedPath}'.");
        }

        string fullPath;
        try
        {
            fullPath = SafePath.EnsureWithinRoot(root, fileSystemPath, allowRoot: false);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                $"Rejected {description.ToLowerInvariant()} path outside the server-pack root: '{untrustedPath}'.",
                error);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Referenced {description.ToLowerInvariant()} was not found.", fullPath);
        }

        try
        {
            SafePath.EnsureNoReparsePointsUnderRoot(root, fullPath);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                $"Rejected reparse-point {description.ToLowerInvariant()}: '{untrustedPath}'.",
                error);
        }

        return ToModelRelativePath(root, fullPath);
    }

    private static LoaderInstallation? SelectLoaderInstallation(
        IReadOnlyCollection<string> argumentFiles,
        IReadOnlyList<LoaderInstallation> installations,
        HostOperatingSystem hostOs,
        ICollection<string> errors)
    {
        var expectedName = hostOs == HostOperatingSystem.Windows
            ? "win_args.txt"
            : "unix_args.txt";
        var wrongName = hostOs == HostOperatingSystem.Windows
            ? "unix_args.txt"
            : "win_args.txt";
        if (argumentFiles.Any(path =>
                string.Equals(Path.GetFileName(path), wrongName, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(
                $"The selected {hostOs} launch script references the wrong platform argument file '{wrongName}'.");
            return null;
        }

        var loaderArgumentFiles = argumentFiles
            .Where(path => string.Equals(
                Path.GetFileName(path),
                expectedName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (loaderArgumentFiles.Length != 1)
        {
            errors.Add(loaderArgumentFiles.Length == 0
                ? $"The launch script does not reference a {expectedName} loader argument file."
                : $"The launch script references multiple {expectedName} files and is ambiguous.");
            return null;
        }

        var referenced = loaderArgumentFiles[0];
        var matches = installations.Where(installation => string.Equals(
                hostOs == HostOperatingSystem.Windows
                    ? installation.WindowsArgumentFile
                    : installation.LinuxArgumentFile,
                referenced,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            errors.Add(matches.Length == 0
                ? $"The referenced loader argument file is not in a known Forge/NeoForge layout: '{referenced}'."
                : $"The referenced loader argument file maps to multiple installations: '{referenced}'.");
            return null;
        }

        return matches[0];
    }

    private static string ResolveJavaExecutable(
        string root,
        string javaToken,
        string? manifestJavaVersion,
        HostOperatingSystem hostOs,
        ICollection<string> warnings)
    {
        var normalizedToken = NormalizeForFileSystem(javaToken);
        var tokenHasPath = normalizedToken.Contains(Path.DirectorySeparatorChar)
            || normalizedToken.Contains(Path.AltDirectorySeparatorChar);
        if (tokenHasPath)
        {
            return Path.GetFullPath(ValidateRelativeFile(root, javaToken, "Java executable"), root);
        }

        if (hostOs == HostOperatingSystem.Linux)
        {
            return "java";
        }

        if (hostOs != HostOperatingSystem.Windows)
        {
            throw new InvalidDataException("The host operating system has no supported Java executable form.");
        }

        if (!string.IsNullOrWhiteSpace(manifestJavaVersion))
        {
            var exact = Path.Combine(root, "jre", manifestJavaVersion, "bin", "java.exe");
            if (File.Exists(exact))
            {
                SafePath.EnsureNoReparsePointsUnderRoot(root, exact);
                return exact;
            }
        }

        var candidates = FindBundledJavaExecutables(root, "java.exe");
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count > 1)
        {
            throw new InvalidDataException(
                "Multiple bundled Java executables were found and the launch script does not select one unambiguously.");
        }

        warnings.Add("No bundled Windows Java executable was found; Java must be available on PATH.");
        return "java.exe";
    }

    private static IReadOnlyList<string> FindBundledJavaExecutables(string root, string fileName)
    {
        var results = new List<string>();
        foreach (var directoryName in new[] { "jre", "java", "runtime", "runtimes" })
        {
            var searchRoot = Path.Combine(root, directoryName);
            if (!Directory.Exists(searchRoot))
            {
                continue;
            }

            var directories = new Stack<(DirectoryInfo Directory, int Depth)>();
            directories.Push((new DirectoryInfo(searchRoot), 0));
            var inspected = 0;
            while (directories.TryPop(out var item) && inspected++ < 2048)
            {
                if (item.Directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                foreach (var file in item.Directory.EnumerateFiles(fileName))
                {
                    if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        results.Add(file.FullName);
                        if (results.Count > 32)
                        {
                            return results;
                        }
                    }
                }

                if (item.Depth >= 4)
                {
                    continue;
                }

                foreach (var child in item.Directory.EnumerateDirectories())
                {
                    directories.Push((child, item.Depth + 1));
                }
            }
        }

        return results.Distinct(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<LoaderArgumentMetadata> ReadLoaderArgumentMetadataAsync(
        string root,
        IReadOnlyCollection<string> argumentFiles,
        CancellationToken cancellationToken)
    {
        var loaderPath = argumentFiles.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), "win_args.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path), "unix_args.txt", StringComparison.OrdinalIgnoreCase));
        if (loaderPath is null)
        {
            return new LoaderArgumentMetadata(null, null);
        }

        var fullPath = SafePath.EnsureWithinRoot(root, NormalizeForFileSystem(loaderPath));
        var text = await ReadBoundedTextAsync(fullPath, MaximumArgumentFileBytes, cancellationToken)
            .ConfigureAwait(false);
        return new LoaderArgumentMetadata(
            MatchValue(text, @"--fml\.mcVersion\s+(?<value>[^\s#]+)"),
            MatchValue(text, @"--fml\.(?:neoForgeVersion|forgeVersion)\s+(?<value>[^\s#]+)"));
    }

    private static async Task<(int MinimumMb, int MaximumMb, string? Error)> DetectMemoryAsync(
        string root,
        IReadOnlyCollection<string> argumentFiles,
        CancellationToken cancellationToken)
    {
        var minimum = 1024;
        var maximum = 4096;
        foreach (var relativePath in argumentFiles)
        {
            if (!Path.GetFileName(relativePath)
                    .Contains("jvm_args", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = SafePath.EnsureWithinRoot(root, NormalizeForFileSystem(relativePath));
            var text = await ReadBoundedTextAsync(fullPath, MaximumArgumentFileBytes, cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var value = line.Trim();
                if (value.Length == 0 || value.StartsWith('#'))
                {
                    continue;
                }

                var comment = value.IndexOf('#');
                if (comment >= 0)
                {
                    value = value[..comment].TrimEnd();
                }

                var match = MemoryArgumentRegex.Match(value);
                if (!match.Success)
                {
                    continue;
                }

                if (!TryConvertMemoryToMb(
                        match.Groups["amount"].Value,
                        match.Groups["unit"].Value[0],
                        out var megabytes))
                {
                    return (minimum, maximum, $"Invalid or excessive JVM memory value '{value}'.");
                }

                if (string.Equals(match.Groups["kind"].Value, "s", StringComparison.OrdinalIgnoreCase))
                {
                    minimum = megabytes;
                }
                else
                {
                    maximum = megabytes;
                }
            }
        }

        return minimum > maximum
            ? (minimum, maximum, "JVM minimum memory (-Xms) exceeds maximum memory (-Xmx).")
            : (minimum, maximum, null);
    }

    private static bool TryConvertMemoryToMb(string amountText, char unit, out int megabytes)
    {
        megabytes = 0;
        if (!decimal.TryParse(
                amountText,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
            || amount <= 0)
        {
            return false;
        }

        var multiplier = char.ToUpperInvariant(unit) switch
        {
            'K' => 1m / 1024m,
            'M' => 1m,
            'G' => 1024m,
            'T' => 1024m * 1024m,
            _ => 0m,
        };
        var converted = decimal.Ceiling(amount * multiplier);
        if (multiplier == 0 || converted is < 1 or > 2_097_152)
        {
            return false;
        }

        megabytes = decimal.ToInt32(converted);
        return true;
    }

    private static async Task<string> ReadBoundedTextAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (file.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Refused to read oversized text file '{file.Name}' ({file.Length:N0} bytes).");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            16 * 1024,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? InferMinecraftVersion(LoaderInstallation? installation)
    {
        if (installation is null)
        {
            return null;
        }

        if (installation.CoreType == CoreType.Forge)
        {
            var separator = installation.Version.IndexOf('-');
            if (separator > 0)
            {
                return installation.Version[..separator];
            }
        }

        if (installation.CoreType == CoreType.NeoForge)
        {
            var match = Regex.Match(
                installation.Version,
                @"^(?<minor>\d{2})\.(?<patch>\d{1,2})\.",
                RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return $"1.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
            }
        }

        return null;
    }

    private static int? ParseJavaMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = Regex.Match(version, @"^\s*(?<major>\d+)", RegexOptions.CultureInvariant);
        return match.Success
            && int.TryParse(match.Groups["major"].Value, out var major)
            && major >= 8
                ? major
                : null;
    }

    private static CoreType? ParseCoreType(string? loaderName)
    {
        if (loaderName?.Contains("neoforge", StringComparison.OrdinalIgnoreCase) == true)
        {
            return CoreType.NeoForge;
        }

        if (loaderName?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true)
        {
            return CoreType.Forge;
        }

        return null;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? MatchValue(string text, string pattern)
    {
        var match = Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string NormalizeForFileSystem(string path) => path
        .Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);

    private static string ToModelRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath)
            .Replace('\\', '/')
            .Replace(Path.DirectorySeparatorChar, '/');

    private static readonly Regex MemoryArgumentRegex = new(
        @"^-Xm(?<kind>[sx])(?<amount>\d+(?:\.\d+)?)(?<unit>[KMGT])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record ManifestMetadata(
        string? PackName,
        string? PackVersion,
        string? MinecraftVersion,
        CoreType? CoreType,
        string? ModLoaderVersion,
        string? JavaDistributionVersion);

    private sealed record LoaderInstallation(
        CoreType CoreType,
        string Version,
        string RelativeDirectory,
        string? WindowsArgumentFile,
        string? LinuxArgumentFile);

    private sealed record ParsedLaunchScript(
        string JavaToken,
        IReadOnlyList<string> ArgumentFilePaths,
        IReadOnlyList<string> ServerArguments);

    private sealed record LoaderArgumentMetadata(
        string? MinecraftVersion,
        string? ModLoaderVersion);
}
