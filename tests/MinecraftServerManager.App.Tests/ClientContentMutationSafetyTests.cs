using System.IO;

namespace MinecraftServerManager.App.Tests;

public sealed class ClientContentMutationSafetyTests
{
    [Fact]
    public void ContentMutations_AreDisabledForRunningInstancesAndRecheckDurableProcessIdentity()
    {
        var source = File.ReadAllText(
                TestRepositoryPaths.AppSource("ViewModels", "ClientWorkspaceViewModel.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "private bool CanMutateSelectedContent() =>\n        !IsBusy && SelectedInstance is { IsRunning: false };",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => CanMutateSelectedContent() && !ShowRecycleBin",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => CanMutateSelectedContent() && SelectedContentItem?.IsActive == true",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => CanMutateSelectedContent() && SelectedContentItem?.IsRecycled == true",
            source,
            StringComparison.Ordinal);

        var guardStart = source.IndexOf(
            "private async Task EnsureContentMutationAllowedAsync(",
            StringComparison.Ordinal);
        Assert.True(guardStart >= 0);
        var guardEnd = source.IndexOf("\n    private ", guardStart + 1, StringComparison.Ordinal);
        var guard = source[guardStart..(guardEnd < 0 ? source.Length : guardEnd)];
        Assert.Contains("instance.IsRunning", guard, StringComparison.Ordinal);
        Assert.Contains("await _registry.LoadAsync(cancellationToken)", guard, StringComparison.Ordinal);
        Assert.Contains("_processRecoveryService.IsMatchingProcessActive(stored)", guard, StringComparison.Ordinal);

        Assert.True(
            CountOccurrences(source, "await EnsureContentMutationAllowedAsync(instance,") >= 4,
            "Every import/toggle/recycle/restore mutation must recheck immediately after acquiring the content gate.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
