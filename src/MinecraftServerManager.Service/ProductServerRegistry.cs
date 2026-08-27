using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

/// <summary>Atomic, bounded registry for Service-owned server definitions.</summary>
public sealed class ProductServerRegistry(ProductDataLayout layout)
{
    public const string FileName = "server-registry.v1.json";
    public const int MaximumServers = 256;
    private const long MaximumFileBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ProductServerRegistration> _servers = [];
    private bool _loaded;

    public string FilePath => Path.Combine(layout.Data, FileName);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            layout.EnsureCreated();
            if (File.Exists(FilePath))
            {
                await using var stream = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is < 2 or > MaximumFileBytes)
                {
                    throw new InvalidDataException("Server registry size is outside the allowed range.");
                }

                var document = await JsonSerializer.DeserializeAsync<RegistryDocument>(
                        stream,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("Server registry document is empty.");
                if (document.SchemaVersion != 1)
                {
                    throw new InvalidDataException("Server registry schema is unsupported.");
                }

                if (document.Servers.Count > MaximumServers)
                {
                    throw new InvalidDataException("Server registry exceeds its server limit.");
                }

                foreach (var server in document.Servers)
                {
                    ProductServerRegistrationValidator.ValidateAndThrow(server, layout);
                    if (!_servers.TryAdd(server.Id, Clone(server)))
                    {
                        throw new InvalidDataException("Server registry contains a duplicate server id.");
                    }
                }
            }

            _loaded = true;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Server registry JSON is invalid.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ProductServerRegistration> GetAll()
    {
        EnsureLoaded();
        lock (_servers)
        {
            return _servers.Values
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.Id)
                .Select(Clone)
                .ToArray();
        }
    }

    public bool TryGet(Guid id, out ProductServerRegistration registration)
    {
        EnsureLoaded();
        lock (_servers)
        {
            if (_servers.TryGetValue(id, out var stored))
            {
                registration = Clone(stored);
                return true;
            }
        }

        registration = null!;
        return false;
    }

    public async Task UpsertAsync(
        ProductServerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ProductServerRegistrationValidator.ValidateAndThrow(registration, layout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            Dictionary<Guid, ProductServerRegistration> snapshot;
            lock (_servers)
            {
                if (!_servers.ContainsKey(registration.Id) && _servers.Count >= MaximumServers)
                {
                    throw new InvalidOperationException("Server registry has reached its server limit.");
                }

                snapshot = _servers.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                snapshot[registration.Id] = Clone(registration);
            }

            await SaveAsync(snapshot.Values, cancellationToken).ConfigureAwait(false);
            lock (_servers)
            {
                _servers.Clear();
                foreach (var pair in snapshot)
                {
                    _servers.Add(pair.Key, pair.Value);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Commits only the launch fields selected immediately before process start. Keeping this
    /// update inside the registry gate prevents a concurrent settings write from being replaced
    /// by an older full registration snapshot.
    /// </summary>
    internal async Task<ProductServerRegistration> UpdateLaunchConfigurationAsync(
        Guid id,
        int port,
        CoreType expectedCoreType,
        bool updateVelocityPortArgument,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(id));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            Dictionary<Guid, ProductServerRegistration> snapshot;
            ProductServerRegistration updated;
            lock (_servers)
            {
                if (!_servers.TryGetValue(id, out var stored))
                {
                    throw new KeyNotFoundException($"Server '{id}' is not registered.");
                }

                if (!Enum.TryParse<CoreType>(stored.CoreType, ignoreCase: true, out var storedCoreType) ||
                    storedCoreType != expectedCoreType)
                {
                    throw new InvalidOperationException(
                        "The server core type changed while its launch port was being prepared.");
                }

                var currentArguments = stored.ServerArguments.ToList();
                if (updateVelocityPortArgument)
                {
                    if (storedCoreType != CoreType.Velocity)
                    {
                        throw new InvalidOperationException(
                            "Velocity port arguments cannot be applied to a non-Velocity server.");
                    }

                    VelocityPortArgumentEditor.SetPort(currentArguments, port);
                }

                updated = stored with
                {
                    Port = port,
                    ServerArguments = currentArguments.ToArray(),
                };
                ProductServerRegistrationValidator.ValidateAndThrow(updated, layout);
                snapshot = _servers.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                snapshot[id] = Clone(updated);
            }

            await SaveAsync(snapshot.Values, cancellationToken).ConfigureAwait(false);
            lock (_servers)
            {
                _servers.Clear();
                foreach (var pair in snapshot)
                {
                    _servers.Add(pair.Key, pair.Value);
                }
            }

            return Clone(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(id));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            Dictionary<Guid, ProductServerRegistration> snapshot;
            lock (_servers)
            {
                if (!_servers.ContainsKey(id))
                {
                    return false;
                }

                snapshot = _servers.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                snapshot.Remove(id);
            }

            await SaveAsync(snapshot.Values, cancellationToken).ConfigureAwait(false);
            lock (_servers)
            {
                _servers.Clear();
                foreach (var pair in snapshot)
                {
                    _servers.Add(pair.Key, pair.Value);
                }
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(
        IEnumerable<ProductServerRegistration> registrations,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.Data);
        var document = new RegistryDocument(
            1,
            registrations.OrderBy(server => server.Id).Select(Clone).ToArray());
        var temporaryPath = Path.Combine(
            layout.Data,
            $".{FileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Server registry exceeds its file size limit.");
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Preserve the commit result; a private stale temp file cannot become registry data.
            }
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Server registry has not been loaded.");
        }
    }

    private static ProductServerRegistration Clone(ProductServerRegistration value) => value with
    {
        JavaArgumentFilePaths = value.JavaArgumentFilePaths.ToArray(),
        JvmArguments = value.JvmArguments.ToArray(),
        ServerArguments = value.ServerArguments.ToArray(),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };

    private sealed record RegistryDocument(
        int SchemaVersion,
        IReadOnlyList<ProductServerRegistration> Servers);
}

public static class ProductServerRegistrationValidator
{
    private const int MaximumArgumentCount = 128;
    private const int MaximumArgumentLength = 2048;

    public static void ValidateAndThrow(
        ProductServerRegistration registration,
        ProductDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(layout);
        if (registration.Id == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(registration));
        }

        ValidateText(registration.Name, 128, "Server name");
        if (!Enum.IsDefined(registration.LaunchKind))
        {
            throw new ArgumentException("Server launch kind is unsupported.", nameof(registration));
        }

        ResolveOwnedPath(layout.Servers, registration.ServerDirectory, allowRoot: false);
        ResolveOwnedPath(layout.Runtimes, registration.JavaRuntimePath, allowRoot: false);
        ValidateRelativePath(registration.ServerJarPath, "Server JAR path");
        if (registration.LaunchKind == ProductServerLaunchKind.ExecutableJar &&
            !string.Equals(Path.GetExtension(registration.ServerJarPath), ".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Executable JAR launch requires a .jar path.", nameof(registration));
        }

        ValidateArguments(registration.JavaArgumentFilePaths, "Java argument-file paths", paths: true);
        if (registration.LaunchKind == ProductServerLaunchKind.JavaArgumentFiles &&
            registration.JavaArgumentFilePaths.Count == 0)
        {
            throw new ArgumentException("Java argument-file launch requires at least one argument file.");
        }

        if (!Enum.TryParse<CoreType>(registration.CoreType, ignoreCase: true, out var coreType) ||
            !Enum.IsDefined(coreType))
        {
            throw new ArgumentException("Server core type is unsupported.", nameof(registration));
        }

        if (registration.MinecraftVersion is not null)
        {
            ValidateText(registration.MinecraftVersion, 64, "Minecraft version");
        }

        if (registration.MinimumMemoryMb is < 128 or > 1_048_576 ||
            registration.MaximumMemoryMb < registration.MinimumMemoryMb ||
            registration.MaximumMemoryMb > 1_048_576)
        {
            throw new ArgumentException("Server memory range is invalid.", nameof(registration));
        }

        ValidateArguments(registration.JvmArguments, "JVM arguments", paths: false);
        ValidateArguments(registration.ServerArguments, "Server arguments", paths: false);
        if (registration.StopCommand is not null)
        {
            ValidateText(registration.StopCommand, 128, "Stop command");
        }

        if (registration.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Server port is invalid.", nameof(registration));
        }

        if (!Enum.IsDefined(registration.ModpackSource))
        {
            throw new ArgumentException("Modpack source is unsupported.", nameof(registration));
        }

        ValidateOptionalText(registration.ModpackProviderId, 64, "Modpack provider id");
        ValidateOptionalText(registration.ModpackProjectId, 256, "Modpack project id");
        ValidateOptionalText(registration.ModpackVersionId, 256, "Modpack version id");
        ValidateOptionalText(registration.ModpackVersionName, 256, "Modpack version name");
        if (registration.ModpackSource != ProductModpackSourceKind.None &&
            (string.IsNullOrWhiteSpace(registration.ModpackProjectId) ||
             string.IsNullOrWhiteSpace(registration.ModpackVersionId)))
        {
            throw new ArgumentException(
                "Verified modpack registrations require project and version ids.",
                nameof(registration));
        }
    }

    public static string ResolveOwnedPath(string root, string relativePath, bool allowRoot)
    {
        ValidateRelativePath(relativePath, "Owned path");
        return SafePath.EnsureWithinRoot(root, relativePath, allowRoot);
    }

    private static void ValidateArguments(
        IReadOnlyList<string>? values,
        string label,
        bool paths)
    {
        if (values is null || values.Count > MaximumArgumentCount)
        {
            throw new ArgumentException($"{label} exceed the item limit.");
        }

        foreach (var value in values)
        {
            if (paths)
            {
                ValidateRelativePath(value, label);
            }
            else
            {
                ValidateText(value, MaximumArgumentLength, label);
            }
        }
    }

    private static void ValidateRelativePath(string value, string label)
    {
        ValidateText(value, 512, label);
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"{label} must be a root-confined relative path.");
        }
    }

    private static void ValidateText(string value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
        {
            throw new ArgumentException($"{label} is invalid.");
        }
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string label)
    {
        if (value is not null)
        {
            ValidateText(value, maximumLength, label);
        }
    }
}
