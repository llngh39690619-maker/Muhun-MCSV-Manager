using System.Windows;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Dialogs;

public partial class ImportServerDialog : Window
{
    public ImportServerDialog(DetectionResult detection, JavaVersionRecommendation recommendation)
    {
        InitializeComponent();
        SourcePath = detection.FilePath;
        CoreDisplay = detection.IsRecognized
            ? detection.CoreType.ToString()
            : LocalizationService.Current.Get("importJar.customUnrecognized");
        MinecraftVersion = detection.MinecraftVersion ?? string.Empty;
        ConfidenceDisplay = $"{detection.ConfidencePercent}%";
        ServerName = BuildSuggestedName(detection);
        SelectedJavaMajor = recommendation.MajorVersion;
        Evidence = detection.Evidence;
        WarningText = BuildWarning(detection, recommendation);
        DataContext = this;
    }

    public string SourcePath { get; }
    public string CoreDisplay { get; }
    public string MinecraftVersion { get; set; }
    public string ConfidenceDisplay { get; }
    public string ServerName { get; set; }
    public int SelectedJavaMajor { get; set; }
    public IReadOnlyList<int> JavaChoices { get; } = [8, 11, 16, 17, 21, 25];
    public IReadOnlyList<string> Evidence { get; }
    public string WarningText { get; }

    private static string BuildSuggestedName(DetectionResult detection)
    {
        var core = detection.IsRecognized ? detection.CoreType.ToString() : "Custom";
        return string.IsNullOrWhiteSpace(detection.MinecraftVersion) ? core : $"{core}-{detection.MinecraftVersion}";
    }

    private static string BuildWarning(DetectionResult detection, JavaVersionRecommendation recommendation)
    {
        var warnings = new List<string>();
        if (detection.ConfidencePercent < 70)
        {
            warnings.Add(LocalizationService.Current.Get("importJar.warning.lowConfidence"));
        }

        if (detection.CoreType is CoreType.Forge or CoreType.NeoForge
            && Path.GetFileName(detection.FilePath).Contains("installer", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(LocalizationService.Current.Get("importJar.warning.installer"));
        }

        if (recommendation.RequiresUserConfirmation)
        {
            warnings.Add(LocalizationService.Current.Get("importJar.warning.java"));
        }

        return warnings.Count == 0
            ? LocalizationService.Current.Get("importJar.status.ready")
            : string.Join(" ", warnings);
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
