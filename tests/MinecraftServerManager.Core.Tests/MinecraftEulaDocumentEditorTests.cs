using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class MinecraftEulaDocumentEditorTests
{
    private static readonly DateTimeOffset AcceptanceTime =
        new(2026, 8, 15, 12, 34, 56, TimeSpan.Zero);

    [Theory]
    [InlineData("eula=true\r\n")]
    [InlineData("  eula \t= \tTRUE\n")]
    [InlineData("eula=false\neula = true\n")]
    public void IsAccepted_UsesLastJavaPropertyValue(string contents)
    {
        Assert.True(MinecraftEulaDocumentEditor.IsAccepted(contents));
        Assert.Same(contents, MinecraftEulaDocumentEditor.EnsureAccepted(
            contents,
            contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            AcceptanceTime));
    }

    [Theory]
    [InlineData("EULA=true\n")]
    [InlineData("eula=false\nEULA=true\n")]
    [InlineData("Eula=true\neula=false\n")]
    public void IsAccepted_RequiresExactLowercasePropertyKey(string contents)
    {
        Assert.False(MinecraftEulaDocumentEditor.IsAccepted(contents));
    }

    [Fact]
    public void EnsureAccepted_DoesNotTreatUppercasePropertyAsMinecraftEulaKey()
    {
        const string contents = "eula=false\nEULA=true\n";

        Assert.Equal(
            "eula=true\nEULA=true\n",
            MinecraftEulaDocumentEditor.EnsureAccepted(contents, "\n", AcceptanceTime));
    }

    [Fact]
    public void IsAccepted_RejectsWhenLastDuplicateIsFalse()
    {
        const string contents = "eula=true\neula = false\n";

        Assert.False(MinecraftEulaDocumentEditor.IsAccepted(contents));
        Assert.Equal(
            "eula=true\neula = true\n",
            MinecraftEulaDocumentEditor.EnsureAccepted(contents, "\n", AcceptanceTime));
    }

    [Fact]
    public void EnsureAccepted_ReplacesPropertyWithoutChangingOtherLinesOrNewlines()
    {
        const string contents = "# Mojang EULA\r\neula = false\r\ncustom=kept\r\n";

        var updated = MinecraftEulaDocumentEditor.EnsureAccepted(
            contents,
            "\r\n",
            AcceptanceTime);

        Assert.Equal("# Mojang EULA\r\neula = true\r\ncustom=kept\r\n", updated);
    }

    [Fact]
    public void EnsureAccepted_AppendsPropertyWhenMissing()
    {
        var updated = MinecraftEulaDocumentEditor.EnsureAccepted(
            "# Existing comment",
            "\r\n",
            AcceptanceTime);

        Assert.Equal(
            "# Existing comment\r\n"
            + "# Accepted by X MCSV after explicit user confirmation at 2026-08-15T12:34:56.0000000+00:00\r\n"
            + "eula=true\r\n",
            updated);
    }

    [Fact]
    public void IsAccepted_DoesNotTreatCommentAsProperty()
    {
        Assert.False(MinecraftEulaDocumentEditor.IsAccepted("# eula=true\neula=false\n"));
    }
}
