using System.Windows;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.App.ViewModels;

public sealed class JavaRuntimeItemViewModel : ObservableObject
{
    private readonly string _vendorDisplay;
    private readonly bool _usesInstalledRuntimeFallback;

    public JavaRuntimeItemViewModel(JavaRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        MajorDisplay = $"Java {runtime.MajorVersion}";
        _usesInstalledRuntimeFallback = string.IsNullOrWhiteSpace(runtime.Vendor);
        _vendorDisplay = runtime.Vendor;
        ExecutablePath = runtime.JavaExecutablePath;
        SubscribeToCultureChanges();
    }

    public JavaRuntimeItemViewModel(InstalledJavaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        MajorDisplay = $"Java {runtime.MajorVersion}";
        _vendorDisplay = $"{runtime.Vendor} · {runtime.ImageType}";
        ExecutablePath = runtime.JavaExecutablePath;
        SubscribeToCultureChanges();
    }

    public JavaRuntimeItemViewModel(ProductServerJavaRuntimeSummary runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        MajorDisplay = runtime.MajorVersion is { } major ? $"Java {major}" : "Java —";
        _vendorDisplay = string.IsNullOrWhiteSpace(runtime.Vendor) ? runtime.RuntimeKind : runtime.Vendor;
        ExecutablePath = string.Join(
            " · ",
            new[] { runtime.Version, runtime.RuntimeKind, runtime.Architecture }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        SubscribeToCultureChanges();
    }

    public string MajorDisplay { get; }
    public string VendorDisplay => _usesInstalledRuntimeFallback
        ? LocalizationService.Current.Get("javaRuntime.installed")
        : _vendorDisplay;
    public string ExecutablePath { get; }

    private void SubscribeToCultureChanges()
    {
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.CultureChanged),
            OnCultureChanged);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(VendorDisplay));
}
