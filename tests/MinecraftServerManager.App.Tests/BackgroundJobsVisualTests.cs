using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.App.Tests;

public sealed class BackgroundJobsVisualTests
{
    [Fact]
    public void MainFooter_UsesOneWayAggregateProgressAndWorkCenterButton()
    {
        var document = XDocument.Load(GetSourcePath("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(document.Descendants(presentation + "ProgressBar"), element =>
            (string?)element.Attribute("Value") == "{Binding BackgroundJobProgress, Mode=OneWay}"
            && (string?)element.Attribute("IsIndeterminate")
            == "{Binding IsBackgroundJobProgressIndeterminate, Mode=OneWay}");
        Assert.Contains(document.Descendants(presentation + "Button"), element =>
            (string?)element.Attribute("Content") == "☰"
            && (string?)element.Attribute("Command") == "{Binding OpenBackgroundJobsCommand}");
    }

    [Fact]
    public void WorkCenter_ReadOnlyRunAndProgressBindingsAreExplicitlyOneWay()
    {
        var document = XDocument.Load(GetSourcePath(Path.Combine("Dialogs", "BackgroundJobsWindow.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.All(document.Descendants(presentation + "Run")
                .Where(element => ((string?)element.Attribute("Text"))?.Contains("Binding") == true),
            element => Assert.Contains("Mode=OneWay", (string?)element.Attribute("Text")));
        Assert.All(document.Descendants(presentation + "ProgressBar"), element =>
        {
            Assert.Contains("Mode=OneWay", (string?)element.Attribute("Value"));
            Assert.Contains("Mode=OneWay", (string?)element.Attribute("IsIndeterminate"));
        });
    }

    [Fact]
    public void WorkCenter_ExplainsAutomaticSuccessCleanupAndRetainedErrors()
    {
        var document = XDocument.Load(GetSourcePath(Path.Combine("Dialogs", "BackgroundJobsWindow.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{DynamicResource L10n.jobs.cleanupHint}");
        var localized = ProductLocalizationCatalog
            .GetDocument(ProductLocalizationCatalog.FallbackCulture)
            .Strings["jobs.cleanupHint"];
        Assert.Contains("成功工作會在 3 秒後自動清除", localized, StringComparison.Ordinal);
        Assert.Contains("失敗或取消紀錄會保留", localized, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkCenter_WithMaterializedJobItem_CompletesStaLayoutWithoutBindingFailure()
    {
        WpfStaTestHost.Run(() =>
        {
            var job = new BackgroundServerJobViewModel(
                Guid.NewGuid(),
                BackgroundServerJobKind.CoreServer,
                "Spigot-1.8",
                "建立 Spigot 1.8",
                _ => { });
            job.MarkRunning();
            job.ApplyProgress("正在下載 BuildTools…", "官方來源", 42);
            var context = new WorkCenterTestContext(job);
            var window = new BackgroundJobsWindow(context);
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = Assert.Single(
                    FindVisualChildren<ListBox>(window),
                    candidate => ReferenceEquals(candidate.ItemsSource, context.BackgroundJobItems));
                list.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
            }
            finally
            {
                window.Close();
                job.DisposeCancellation();
            }
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string GetSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    public sealed class WorkCenterTestContext
    {
        public WorkCenterTestContext(BackgroundServerJobViewModel job)
        {
            BackgroundJobItems = new ObservableCollection<BackgroundServerJobViewModel> { job };
        }

        public ObservableCollection<BackgroundServerJobViewModel> BackgroundJobItems { get; }
        public string BackgroundSchedulingProfile => "測試排程";
        public string BackgroundJobSummary => "1 項工作";
        public string BackgroundJobActivity => "Spigot-1.8 · 正在下載";
        public double BackgroundJobProgress => 42;
        public bool IsBackgroundJobProgressIndeterminate => false;
        public RelayCommand CancelAllBackgroundJobsCommand { get; } = new(() => { });
        public RelayCommand ClearFinishedBackgroundJobsCommand { get; } = new(() => { });
    }
}
