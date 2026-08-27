using System.Windows;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class ImportServerFolderDialog : Window
{
    public ImportServerFolderDialog(ServerPackDetectionResult detection, bool autoDetected)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(detection);

        SourcePath = detection.DirectoryPath;
        PackDisplay = string.IsNullOrWhiteSpace(detection.PackVersion)
            ? detection.PackName ?? detection.SuggestedName
            : $"{detection.PackName ?? detection.SuggestedName} {detection.PackVersion}";
        CoreDisplay = $"{detection.CoreType} {detection.ModLoaderVersion} · Minecraft {detection.MinecraftVersion}";
        HostDisplay = detection.HostOperatingSystem switch
        {
            HostOperatingSystem.Windows => LocalizationService.Current.Get("importFolder.host.windows"),
            HostOperatingSystem.Linux => LocalizationService.Current.Get("importFolder.host.linux"),
            _ => LocalizationService.Current.Get("importFolder.host.unsupported")
        };
        ScriptDisplay = detection.SourceLaunchScriptPath
                        ?? LocalizationService.Current.Get("importFolder.script.generated");
        ArgumentFilesDisplay = string.Join(Environment.NewLine, detection.JavaArgumentFilePaths.Select(path => "@" + path));
        JavaDisplay = string.IsNullOrWhiteSpace(detection.JavaExecutablePath)
            ? LocalizationService.Current.Get("importFolder.java.required", detection.JavaMajorVersion)
            : $"Java {detection.JavaMajorVersion} · {detection.JavaExecutablePath}";
        MemoryDisplay = detection.MaximumMemoryMb is { } maximum
            ? LocalizationService.Current.Get("importFolder.memory.maximum", maximum)
            : LocalizationService.Current.Get("importFolder.memory.arguments");
        ServerName = detection.SuggestedName;
        Evidence = detection.Evidence;
        WarningText = BuildWarning(detection);
        IntroText = autoDetected
            ? LocalizationService.Current.Get("importFolder.intro.autoDetected")
            : LocalizationService.Current.Get("importFolder.intro.selected");
        DataContext = this;
    }

    public string SourcePath { get; }
    public string PackDisplay { get; }
    public string CoreDisplay { get; }
    public string HostDisplay { get; }
    public string ScriptDisplay { get; }
    public string ArgumentFilesDisplay { get; }
    public string JavaDisplay { get; }
    public string MemoryDisplay { get; }
    public string IntroText { get; }
    public string ServerName { get; set; }
    public IReadOnlyList<string> Evidence { get; }
    public string WarningText { get; }

    private static string BuildWarning(ServerPackDetectionResult detection)
    {
        var warnings = detection.Warnings.ToList();
        warnings.Add(LocalizationService.Current.Get("importFolder.warning.directJava"));
        warnings.Add(LocalizationService.Current.Get("importFolder.warning.port"));
        return string.Join(" ", warnings);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            DarkMessageBox.Show(
                this,
                LocalizationService.Current.Get("core.validation.serverName"),
                LocalizationService.Current.Get("common.incompleteData"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
