using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Atomic registry for Bedrock display shortcuts. Removing a shortcut edits only this JSON
/// document; Bedrock packages, Store state, and filesystem installations are never managed here.
/// </summary>
public sealed class BedrockClientShortcutRegistry : IDisposable
{
    internal const int MaximumDisplayNameLength = 128;

    private readonly JsonSettingsStore<BedrockClientShortcutRegistryDocument> _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private int _disposeState;

    public BedrockClientShortcutRegistry(string registryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        _store = new JsonSettingsStore<BedrockClientShortcutRegistryDocument>(registryPath);
    }

    internal string RegistryPath => _store.FilePath;

    public async Task<BedrockClientShortcutRegistryDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SaveAsync(
        BedrockClientShortcutRegistryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Adds and durably persists a normalized copy of one shortcut.</summary>
    public Task<BedrockClientShortcut> AddAsync(
        BedrockClientShortcut shortcut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        var storedShortcut = Copy(shortcut);
        return UpdateAsync(
            document =>
            {
                document.Shortcuts.Add(storedShortcut);
                return storedShortcut;
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes only the registry entry and returns it. This method never deletes an installation,
    /// package, directory, or user file.
    /// </summary>
    public Task<BedrockClientShortcut> RemoveAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default)
    {
        if (shortcutId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty shortcut id is required.", nameof(shortcutId));
        }

        return UpdateAsync(
            document =>
            {
                var index = document.Shortcuts.FindIndex(shortcut => shortcut.Id == shortcutId);
                if (index < 0)
                {
                    throw new KeyNotFoundException($"Bedrock shortcut '{shortcutId}' was not found.");
                }

                var removed = document.Shortcuts[index];
                document.Shortcuts.RemoveAt(index);
                return removed;
            },
            cancellationToken);
    }

    /// <summary>
    /// Performs one in-process read/modify/validate/atomic-save transaction. The callback is
    /// synchronous so it cannot retain the registry lock across arbitrary asynchronous work.
    /// </summary>
    public async Task<TResult> UpdateAsync<TResult>(
        Func<BedrockClientShortcutRegistryDocument, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfDisposingOrDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            var document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var result = update(document);
            cancellationToken.ThrowIfCancellationRequested();
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        // Complete a commit already in progress before closing its atomic JSON store.
        _mutationGate.Wait();
        try
        {
            _store.Dispose();
        }
        finally
        {
            // Leave the gate usable so operations queued during the dispose race can wake and
            // observe the explicit disposed state instead of failing from SemaphoreSlim.Release.
            _mutationGate.Release();
        }
    }

    internal static void NormalizeAndValidate(BedrockClientShortcutRegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != BedrockClientShortcutRegistryDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Bedrock shortcut registry schema {document.SchemaVersion}.");
        }

        if (document.Shortcuts is null || document.Shortcuts.Count > 1_024)
        {
            throw new InvalidDataException("Bedrock shortcut registry has an invalid shortcut list.");
        }

        var ids = new HashSet<Guid>();
        foreach (var shortcut in document.Shortcuts)
        {
            if (shortcut is null)
            {
                throw new InvalidDataException("Bedrock shortcut registry contains a null shortcut.");
            }

            if (shortcut.Id == Guid.Empty || !ids.Add(shortcut.Id))
            {
                throw new InvalidDataException(
                    "Bedrock shortcut registry contains an invalid or duplicate id.");
            }

            var displayName = shortcut.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName) ||
                displayName.Length > MaximumDisplayNameLength ||
                displayName.Any(char.IsControl))
            {
                throw new InvalidDataException("Bedrock shortcut display name is invalid.");
            }

            if (!Enum.IsDefined(shortcut.Channel))
            {
                throw new InvalidDataException("Bedrock shortcut channel is invalid.");
            }

            if (shortcut.CreatedAtUtc == default)
            {
                throw new InvalidDataException("Bedrock shortcut creation time is invalid.");
            }

            shortcut.DisplayName = displayName;
            shortcut.CreatedAtUtc = shortcut.CreatedAtUtc.ToUniversalTime();
        }
    }

    private async Task<BedrockClientShortcutRegistryDocument> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? new BedrockClientShortcutRegistryDocument();
        NormalizeAndValidate(document);
        return document;
    }

    private async Task SaveCoreAsync(
        BedrockClientShortcutRegistryDocument document,
        CancellationToken cancellationToken)
    {
        NormalizeAndValidate(document);
        document.SchemaVersion = BedrockClientShortcutRegistryDocument.CurrentSchemaVersion;
        await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
    }

    private static BedrockClientShortcut Copy(BedrockClientShortcut shortcut) => new()
    {
        Id = shortcut.Id,
        DisplayName = shortcut.DisplayName,
        Channel = shortcut.Channel,
        CreatedAtUtc = shortcut.CreatedAtUtc,
    };

    private void ThrowIfDisposingOrDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}
