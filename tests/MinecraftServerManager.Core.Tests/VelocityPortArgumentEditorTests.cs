using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class VelocityPortArgumentEditorTests
{
    [Theory]
    [InlineData("--port", "25566")]
    [InlineData("-p", "25567")]
    public void TryReadPort_ReadsSeparatedOfficialForms(string option, string value)
    {
        Assert.True(VelocityPortArgumentEditor.TryReadPort([option, value], out var port));
        Assert.Equal(int.Parse(value), port);
    }

    [Theory]
    [InlineData("--port=25568", 25568)]
    [InlineData("-p=25569", 25569)]
    public void TryReadPort_ReadsEqualsForms(string argument, int expected)
    {
        Assert.True(VelocityPortArgumentEditor.TryReadPort([argument], out var port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void SetPort_NormalizesDuplicatesAndPreservesUnrelatedArguments()
    {
        var arguments = new List<string>
        {
            "--add-server", "lobby:127.0.0.1:25566", "-p", "25570", "--port=25571"
        };

        VelocityPortArgumentEditor.SetPort(arguments, 25572);

        Assert.Equal(
            ["--add-server", "lobby:127.0.0.1:25566", "--port", "25572"],
            arguments);
        Assert.True(VelocityPortArgumentEditor.TryReadPort(arguments, out var port));
        Assert.Equal(25572, port);
    }

    [Fact]
    public void SetPort_RemovesMalformedSeparatedValuesButPreservesFollowingOptions()
    {
        var arguments = new List<string>
        {
            "--port", "not-a-port", "--show-plugins", "-p", "-1", "--help"
        };

        VelocityPortArgumentEditor.SetPort(arguments, 25573);

        Assert.Equal(["--show-plugins", "--help", "--port", "25573"], arguments);
    }

    [Fact]
    public void SetPort_WhenSeparatedOptionHasNoValue_PreservesNextNamedOption()
    {
        var arguments = new List<string> { "--port", "--help" };

        VelocityPortArgumentEditor.SetPort(arguments, 25574);

        Assert.Equal(["--help", "--port", "25574"], arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void SetPort_RejectsOutOfRangePort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VelocityPortArgumentEditor.SetPort([], port));
    }
}
