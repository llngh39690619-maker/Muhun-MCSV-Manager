using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Runtime localization source for WPF. Values are projected into application dynamic resources,
/// so every open window updates in place without recreating its view model or restarting MCSV.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string ResourcePrefix = "L10n.";
    private readonly object _sync = new();
    private string? _settingsFile;
    private string _cultureName = ProductLocalizationCatalog.FallbackCulture;

    private LocalizationService()
    {
    }

    public static LocalizationService Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    public string CultureName
    {
        get
        {
            lock (_sync)
            {
                return _cultureName;
            }
        }
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(CultureName);

    public string this[string key] => Get(key);

    public void Initialize(string settingsFile, CultureInfo? detectedCulture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        var normalizedPath = Path.GetFullPath(settingsFile);
        var persistedCulture = TryReadPersistedCulture(normalizedPath, out var requiresRepair);
        var selected = persistedCulture
                       ?? ProductLocalizationCatalog.NormalizeCulture(
                           (detectedCulture ?? CultureInfo.CurrentUICulture).Name);
        lock (_sync)
        {
            _settingsFile = normalizedPath;
        }

        // Repair a corrupt, oversized, unsupported, or legacy language state atomically.
        ApplyCulture(selected, persist: requiresRepair);
    }

    public void SetCulture(string? cultureName) =>
        ApplyCulture(ProductLocalizationCatalog.NormalizeCulture(cultureName), persist: true);

    public string Get(string key, params object?[] arguments)
    {
        return ProductLocalizationCatalog.Format(CultureName, key, arguments);
    }

    internal void ApplyResources(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var strings = ProductLocalizationCatalog.GetDocument(CultureName).Strings;
        foreach (var key in ProductLocalizationCatalog.Keys)
        {
            resources[$"{ResourcePrefix}{key}"] = strings[key];
        }
    }

    private void ApplyCulture(string cultureName, bool persist)
    {
        var normalized = ProductLocalizationCatalog.NormalizeCulture(cultureName);
        var changed = false;
        lock (_sync)
        {
            if (!_cultureName.Equals(normalized, StringComparison.Ordinal))
            {
                _cultureName = normalized;
                changed = true;
            }
        }

        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        void UpdateResources()
        {
            if (Application.Current?.Resources is { } resources)
            {
                ApplyResources(resources);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(UpdateResources);
        }
        else
        {
            UpdateResources();
        }

        if (persist)
        {
            TryPersist(normalized);
        }

        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CultureName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string? TryReadPersistedCulture(string path, out bool requiresRepair)
    {
        requiresRepair = true;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 4096)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = JsonSerializer.Deserialize<LanguageState>(stream);
            if (string.IsNullOrWhiteSpace(state?.Culture)
                || !ProductLocalizationCatalog.TryNormalizeCulture(state.Culture, out var normalized))
            {
                return null;
            }

            requiresRepair = !string.Equals(state.Culture, normalized, StringComparison.Ordinal);
            return normalized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void TryPersist(string cultureName)
    {
        string? path;
        lock (_sync)
        {
            path = _settingsFile;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path)
                            ?? throw new IOException("Language settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, new LanguageState(cultureName));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Localization remains usable for this process even when a removable/portable folder
            // becomes read-only. Persistence is retried the next time the user changes language.
        }
    }

    private sealed record LanguageState(string Culture);
}
