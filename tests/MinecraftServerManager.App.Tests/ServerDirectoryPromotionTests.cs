using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ServerDirectoryPromotionTests
{
    [Fact]
    public void DestinationClaimedAfterProposal_RetriesWithoutTouchingWinner()
    {
        var root = CreateTempRoot();
        try
        {
            var staging = Path.Combine(root, ".staging-one");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "server.jar"), "owned-stage");
            var proposedCount = 0;
            string? collision = null;

            var promoted = ServerDirectoryPromotion.PromoteToUniqueDirectory(
                root,
                staging,
                "Same Name",
                candidate =>
                {
                    if (Interlocked.Increment(ref proposedCount) != 1)
                    {
                        return;
                    }

                    collision = candidate;
                    Directory.CreateDirectory(candidate);
                    File.WriteAllText(Path.Combine(candidate, "winner.txt"), "do-not-touch");
                });

            Assert.NotNull(collision);
            Assert.NotEqual(collision, promoted);
            Assert.Equal("do-not-touch", File.ReadAllText(Path.Combine(collision!, "winner.txt")));
            Assert.Equal("owned-stage", File.ReadAllText(Path.Combine(promoted, "server.jar")));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentPromotions_WithSamePreferredName_BothCompleteToDistinctDirectories()
    {
        var root = CreateTempRoot();
        try
        {
            var firstStage = Path.Combine(root, ".staging-first");
            var secondStage = Path.Combine(root, ".staging-second");
            Directory.CreateDirectory(firstStage);
            Directory.CreateDirectory(secondStage);
            File.WriteAllText(Path.Combine(firstStage, "first.txt"), "first");
            File.WriteAllText(Path.Combine(secondStage, "second.txt"), "second");

            var firstTask = Task.Run(() => ServerDirectoryPromotion.PromoteToUniqueDirectory(
                root,
                firstStage,
                "Concurrent"));
            var secondTask = Task.Run(() => ServerDirectoryPromotion.PromoteToUniqueDirectory(
                root,
                secondStage,
                "Concurrent"));
            var destinations = await Task.WhenAll(firstTask, secondTask);

            Assert.Equal(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Single(destinations, path => File.Exists(Path.Combine(path, "first.txt")));
            Assert.Single(destinations, path => File.Exists(Path.Combine(path, "second.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"msm-promotion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
