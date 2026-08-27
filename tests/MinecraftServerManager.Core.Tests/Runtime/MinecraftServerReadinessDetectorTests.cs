using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class MinecraftServerReadinessDetectorTests
{
    [Theory]
    [InlineData("[Server thread/INFO]: Done (0.315s)! For help, type \"help\"")]
    [InlineData("[18:30:52] [Server thread/INFO] [minecraft/DedicatedServer]: Done (12.844s)! For help, type 'help'")]
    [InlineData("Done (1s)! For help")]
    public void IsReadyLine_AcceptsAuthoritativeMinecraftCompletion(string line)
        => Assert.True(MinecraftServerReadinessDetector.IsReadyLine(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Listening on 0.0.0.0:25565")]
    [InlineData("Plugin update done (1.2s)! For help")]
    [InlineData("Done loading recipes")]
    [InlineData("Done (1.2s)! not actually ready")]
    public void IsReadyLine_RejectsNonAuthoritativeOutput(string? line)
        => Assert.False(MinecraftServerReadinessDetector.IsReadyLine(line));

    [Fact]
    public void IsReadyLine_RejectsOversizedInputBeforeRegexWork()
        => Assert.False(MinecraftServerReadinessDetector.IsReadyLine(
            new string('x', 5000) + " Done (1.0s)! For help"));
}
