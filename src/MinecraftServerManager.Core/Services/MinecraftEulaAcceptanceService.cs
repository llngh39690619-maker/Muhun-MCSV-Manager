using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>
/// Raised before Java starts when a Minecraft server has no accepted EULA document and the
/// current operation did not carry an explicit user confirmation.
/// </summary>
public sealed class MinecraftEulaAcceptanceRequiredException : InvalidOperationException
{
    public MinecraftEulaAcceptanceRequiredException()
        : base("Minecraft EULA acceptance must be confirmed before this server can start.")
    {
    }
}

/// <summary>
/// Verifies and, only after explicit confirmation, atomically updates one server root's
/// <c>eula.txt</c>. The supplied root is always authoritative; the process working directory is
/// never used to infer the file location.
/// </summary>
public sealed class MinecraftEulaAcceptanceService
{
    private readonly ServerPropertiesPortService _documents;
    private readonly TimeProvider _timeProvider;

    public MinecraftEulaAcceptanceService()
        : this(new ServerPropertiesPortService(), TimeProvider.System)
    {
    }

    public MinecraftEulaAcceptanceService(
        ServerPropertiesPortService documents,
        TimeProvider? timeProvider = null)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool IsRequired(CoreType coreType)
        => coreType is not (CoreType.Velocity or CoreType.Waterfall or CoreType.BungeeCord);

    /// <summary>
    /// Performs a read-only, best-effort preflight suitable for checking before a restart stops a
    /// live process. The authoritative start hook must still call <see cref="EnsureAcceptedAsync"/>
    /// while holding the server directory lease.
    /// </summary>
    public async Task<bool> IsAcceptedAsync(
        string serverRoot,
        CancellationToken cancellationToken = default)
    {
        var (_, path) = ResolveEulaPath(serverRoot, cancellationToken);
        var document = await _documents.ReadDocumentAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return document is not null && MinecraftEulaDocumentEditor.IsAccepted(document.Text);
    }

    /// <summary>
    /// Returns <see langword="true"/> when this call changed the file. An already accepted file
    /// is left byte-for-byte unchanged. A missing or false document is never modified unless
    /// <paramref name="userConfirmedAcceptance"/> is true.
    /// </summary>
    public async Task<bool> EnsureAcceptedAsync(
        string serverRoot,
        bool userConfirmedAcceptance,
        CancellationToken cancellationToken = default)
    {
        var (root, path) = ResolveEulaPath(serverRoot, cancellationToken);

        var document = await _documents.ReadDocumentAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (document is not null && MinecraftEulaDocumentEditor.IsAccepted(document.Text))
        {
            return false;
        }

        if (!userConfirmedAcceptance)
        {
            throw new MinecraftEulaAcceptanceRequiredException();
        }

        var newline = document?.Text.Contains("\r\n", StringComparison.Ordinal) == true
            ? "\r\n"
            : Environment.NewLine;
        var accepted = MinecraftEulaDocumentEditor.EnsureAccepted(
            document?.Text ?? string.Empty,
            newline,
            _timeProvider.GetUtcNow());
        await _documents.SaveDocumentAsync(
                path,
                accepted,
                document?.FormatToken,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        path = SafePath.EnsureNoReparsePointsUnderRoot(root, path);
        var verified = await _documents.ReadDocumentAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (verified is null || !MinecraftEulaDocumentEditor.IsAccepted(verified.Text))
        {
            throw new IOException("The Minecraft EULA document could not be verified after writing.");
        }

        return true;
    }

    private static (string Root, string Path) ResolveEulaPath(
        string serverRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The Minecraft server directory was not found: {root}");
        }

        root = SafePath.EnsureNoReparsePointsUnderRoot(root, root);
        var path = SafePath.EnsureWithinRoot(
            root,
            Path.Combine(root, "eula.txt"),
            allowRoot: false);
        if (Directory.Exists(path))
        {
            throw new IOException("The Minecraft EULA path is a directory instead of a file.");
        }

        path = File.Exists(path)
            ? SafePath.EnsureNoReparsePointsUnderRoot(root, path)
            : path;
        return (root, path);
    }
}
