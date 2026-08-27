using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class SafePathTests
{
    [Fact]
    public void NoFollowDirectoryLeaseFailsClosedOnUnsupportedPlatform()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var child = Path.Combine(directory.Path, "server-link");
        ReparsePointTestHelper.CreateDirectoryLink(child, outside.Path);
        try
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                SafePath.AcquireNoReparseDirectoryChainLease(directory.Path, child, isWindows: false));
        }
        finally
        {
            Directory.Delete(child);
        }
    }

    [Fact]
    public void NoFollowFileLeaseFailsClosedWithoutCreatingFileOnUnsupportedPlatform()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "server.lock");

        Assert.Throws<PlatformNotSupportedException>(() =>
            SafePath.AcquireNoFollowExclusiveFileLease(file, isWindows: false));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("Paper: 1.21?", "Paper_ 1.21_")]
    [InlineData("CON", "_CON")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("name...   ", "name")]
    [InlineData("  模組   伺服器  ", "模組 伺服器")]
    public void SanitizeFileName_ProducesWindowsSafeName(string input, string expected)
    {
        Assert.Equal(expected, SafePath.SanitizeFileName(input));
    }

    [Fact]
    public void CombineUnderRoot_WithTraversal_Throws()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<UnauthorizedAccessException>(() =>
            SafePath.CombineUnderRoot(directory.Path, "..", "escape"));
    }

    [Fact]
    public void IsWithinRoot_DoesNotAcceptSiblingWithSamePrefix()
    {
        using var directory = new TemporaryDirectory();
        var sibling = directory.Path + "-other";

        Assert.False(SafePath.IsWithinRoot(directory.Path, sibling));
        Assert.True(SafePath.IsWithinRoot(
            directory.Path,
            Path.Combine(directory.Path, "servers", "paper")));
    }

    [Fact]
    public void CreateUniqueDirectoryPath_AddsStableNumericSuffix()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "Paper"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "Paper-2"));

        var result = SafePath.CreateUniqueDirectoryPath(directory.Path, "Paper");

        Assert.Equal(Path.Combine(directory.Path, "Paper-3"), result);
    }

    [Fact]
    public void DeleteTreeWithoutFollowingReparsePoints_RemovesLinkButPreservesItsTarget()
    {
        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "servers");
        var owned = Path.Combine(trustedRoot, ".installing-test");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        ReparsePointTestHelper.CreateDirectoryLink(Path.Combine(owned, "redirect"), outside);

        SafePath.DeleteTreeWithoutFollowingReparsePoints(trustedRoot, owned);

        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void DeleteTreeWithoutFollowingReparsePoints_RejectsRedirectingIntermediateDirectory()
    {
        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "servers");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(trustedRoot);
        Directory.CreateDirectory(Path.Combine(outside, "owned"));
        var redirect = Path.Combine(trustedRoot, "redirect");
        ReparsePointTestHelper.CreateDirectoryLink(redirect, outside);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                SafePath.DeleteTreeWithoutFollowingReparsePoints(
                    trustedRoot,
                    Path.Combine(redirect, "owned")));
            Assert.True(Directory.Exists(Path.Combine(outside, "owned")));
        }
        finally
        {
            if (Directory.Exists(redirect))
            {
                Directory.Delete(redirect, recursive: false);
            }
        }
    }

    [Fact]
    public async Task DeleteTreeWithRetry_RemovesReadOnlyGitPackAndIncompleteTree()
    {
        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "work");
        var owned = Path.Combine(trustedRoot, "buildtools-incomplete");
        var pack = Path.Combine(owned, "BuildData", ".git", "objects", "pack");
        Directory.CreateDirectory(pack);
        var index = Path.Combine(pack, "pack-test.idx");
        await File.WriteAllTextAsync(index, "incomplete");
        File.SetAttributes(index, File.GetAttributes(index) | FileAttributes.ReadOnly);

        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedRoot,
            owned);

        Assert.False(Directory.Exists(owned));
        Assert.False(File.Exists(index));
    }

    [Fact]
    public async Task DeleteTreeWithRetry_RemovesFilesBeyondWindowsMaxPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "work");
        var owned = Path.Combine(trustedRoot, "buildtools-long-path");
        var current = owned;
        var segment = 0;
        while (Path.Combine(current, "checksum.sha1").Length <= 270)
        {
            current = Path.Combine(current, $"maven-segment-{segment++:D2}-abcdefghijklmnop");
        }

        Directory.CreateDirectory(current);
        var checksum = Path.Combine(current, "checksum.sha1");
        await File.WriteAllTextAsync(checksum, new string('a', 40));
        Assert.True(checksum.Length > 260);

        await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedRoot,
            owned);

        Assert.False(Directory.Exists(owned));
        Assert.False(File.Exists(checksum));
    }

    [Fact]
    public void DeleteTree_HandleLeasePreventsDirectoryRenameDuringTraversal()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "servers");
        var owned = Path.Combine(trustedRoot, "Server");
        var replacement = Path.Combine(trustedRoot, "replacement");
        Directory.CreateDirectory(Path.Combine(owned, "world"));
        File.WriteAllText(Path.Combine(owned, "world", "level.dat"), "data");
        var attempted = false;

        SafePath.DeleteTreeWithoutFollowingReparsePoints(
            trustedRoot,
            owned,
            lockedPath =>
            {
                if (attempted || !string.Equals(lockedPath, owned, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                attempted = true;
                Assert.ThrowsAny<IOException>(() => Directory.Move(owned, replacement));
            });

        Assert.True(attempted);
        Assert.False(Directory.Exists(owned));
        Assert.False(Directory.Exists(replacement));
    }

    [Fact]
    public async Task DeleteTree_ExpectedIdentityRejectsRenameSwapAfterConfirmation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var trustedRoot = Path.Combine(directory.Path, "servers");
        var owned = Path.Combine(trustedRoot, "Server");
        var original = Path.Combine(trustedRoot, "original-moved");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "original.txt"), "original");
        var identity = SafePath.GetExistingObjectIdentity(owned);
        Directory.Move(owned, original);
        Directory.CreateDirectory(owned);
        var replacementMarker = Path.Combine(owned, "replacement.txt");
        File.WriteAllText(replacementMarker, "replacement");

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                trustedRoot,
                owned,
                identity));

        Assert.True(File.Exists(replacementMarker));
        Assert.True(File.Exists(Path.Combine(original, "original.txt")));
    }

    [Fact]
    public void CanonicalExistingPath_CollapsesExtendedWindowsAlias()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var extendedAlias = @"\\?\" + directory.Path;

        var canonical = SafePath.GetCanonicalExistingPath(extendedAlias);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Path)),
            canonical,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureTreeContainsNoReparsePoints_WithOrdinaryChinesePath_Succeeds()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "模組包", "伺服器");
        Directory.CreateDirectory(Path.Combine(root, "設定"));
        File.WriteAllText(Path.Combine(root, "設定", "server.properties"), "server-port=25565");

        SafePath.EnsureTreeContainsNoReparsePoints(root);
    }

    [Fact]
    public void EnsureTreeContainsNoReparsePoints_WithNestedLink_RejectsWithoutTouchingTarget()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "staging");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        var redirect = Path.Combine(root, "redirect");
        ReparsePointTestHelper.CreateDirectoryLink(redirect, outside);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                SafePath.EnsureTreeContainsNoReparsePoints(root));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(redirect))
            {
                Directory.Delete(redirect, recursive: false);
            }
        }
    }

    [Fact]
    public void EnsureTreeContainsNoReparsePoints_EnforcesEntryLimit()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "1");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "2");

        Assert.Throws<InvalidDataException>(() =>
            SafePath.EnsureTreeContainsNoReparsePoints(directory.Path, maximumEntries: 1));
    }
}
