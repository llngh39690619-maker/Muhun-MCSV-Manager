using System;
using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class FormalGuiDataRootTests
{
    [Fact]
    public void CurrentUserRoot_IsStableWritableLocationOutsideVersionDirectory()
    {
        using var temporary = new TemporaryDirectory();

        var paths = ApplicationPaths.CreateForCurrentUser(temporary.Path);

        Assert.Equal(
            Path.Combine(temporary.Path, "Muhun", "MCSV"),
            paths.Root,
            ignoreCase: true);
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.Cache));
        Assert.True(Directory.Exists(paths.Themes));
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData(@"\\server\share\profile")]
    public void CurrentUserRoot_RejectsNonLocalOrRelativeRoots(string root)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApplicationPaths.CreateForCurrentUser(root));
    }

    [Fact]
    public void InteractiveStartup_UsesPerUserRootForDataAndCrossVersionSingleInstance()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));

        Assert.Contains("ApplicationPaths.CreateForCurrentUser()", source, StringComparison.Ordinal);
        Assert.Contains("diagnosticMode ? AppContext.BaseDirectory : paths.Root", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationAcknowledgement_IsSentOnlyAfterViewModelInitialization()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource("App.xaml.cs"));

        var initialize = source.IndexOf(
            "await viewModel.InitializeAsync",
            StringComparison.Ordinal);
        var acknowledge = source.IndexOf(
            "ProductGuiActivationAcknowledgement.SendReadyAsync",
            StringComparison.Ordinal);

        Assert.True(initialize >= 0);
        Assert.True(acknowledge > initialize);
        Assert.Contains("viewModel.ProductServiceNegotiatedApiVersion", source, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-gui-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
