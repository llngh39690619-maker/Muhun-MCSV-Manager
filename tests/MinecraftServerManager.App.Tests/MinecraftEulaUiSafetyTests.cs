using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Tests;

public sealed class MinecraftEulaUiSafetyTests
{
    [Fact]
    public void ExistingInstancePrompt_HasClickableOfficialLinkAndSafeCancelDefault()
    {
        var document = XDocument.Load(TestRepositoryPaths.AppSource(
            Path.Combine("Dialogs", "MinecraftEulaConfirmationDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var link = Assert.Single(document.Descendants(presentation + "Hyperlink"));
        Assert.Equal(
            MinecraftEulaLinkOpener.OfficialEulaUri,
            (string?)link.Attribute("NavigateUri"));

        var cancel = Assert.Single(
            document.Descendants(presentation + "Button"),
            button => (string?)button.Attribute("Content") == "{DynamicResource L10n.common.cancel}");
        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));

        var agree = Assert.Single(
            document.Descendants(presentation + "Button"),
            button => (string?)button.Attribute("Content")
                      == "{DynamicResource L10n.main.vm.confirm.minecraftEulaAgree}");
        Assert.Equal("False", (string?)agree.Attribute("IsDefault"));
    }

    [Fact]
    public void LinkOpener_UsesShellForOfficialHttpsUri()
    {
        ProcessStartInfo? observed = null;

        var opened = MinecraftEulaLinkOpener.TryOpen(
            owner: null,
            processStarter: startInfo =>
            {
                observed = startInfo;
                return null;
            });

        Assert.True(opened);
        Assert.NotNull(observed);
        Assert.True(observed.UseShellExecute);
        Assert.Equal(MinecraftEulaLinkOpener.OfficialEulaUri, observed.FileName);
    }

    [Fact]
    public void LinkOpener_SwallowsLaunchAndFailurePresentationErrors()
    {
        var presentationAttempts = 0;

        var error = Record.Exception(() =>
        {
            var opened = MinecraftEulaLinkOpener.TryOpen(
                owner: null,
                processStarter: _ => throw new Win32Exception("simulated shell failure"),
                failurePresenter: (_, _, _) =>
                {
                    presentationAttempts++;
                    throw new InvalidOperationException("simulated dialog shutdown");
                });
            Assert.False(opened);
        });

        Assert.Null(error);
        Assert.Equal(1, presentationAttempts);
    }

    [Fact]
    public void CreationPage_UsesSafeSharedEulaLinkOpener()
    {
        var code = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("Dialogs", "CoreServerCreationDialog.xaml.cs")));

        Assert.Contains("MinecraftEulaLinkOpener.TryOpen(this)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingInstanceCancellation_RestoresStatusAndUsesLaunchSnapshotDirectory()
    {
        var code = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("ViewModels", "MainWindowViewModel.cs")));

        Assert.Contains(
            "SetStatus(\"main.vm.status.serverState\", server.Name, server.StateText)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsureEulaAcceptedUnderLockAsync(\n                launchSnapshot.DirectoryPath",
            code.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }
}
