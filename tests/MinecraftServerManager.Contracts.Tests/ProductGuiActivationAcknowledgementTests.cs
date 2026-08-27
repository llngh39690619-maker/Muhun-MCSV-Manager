namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductGuiActivationAcknowledgementTests
{
    [Fact]
    public void CompleteBoundRequest_ParsesExactly()
    {
        var pipe = ProductGuiActivationAcknowledgement.PipePrefix + new string('a', 32);
        var nonce = new string('b', 64);

        var parsed = ProductGuiActivationAcknowledgement.TryParseRequest(
            [
                "--ordinary-option",
                "value",
                ProductGuiActivationAcknowledgement.PipeArgument,
                pipe,
                ProductGuiActivationAcknowledgement.NonceArgument,
                nonce,
                ProductGuiActivationAcknowledgement.VersionArgument,
                "1.2.3",
            ],
            out var request);

        Assert.True(parsed);
        Assert.Equal(pipe, request?.PipeName);
        Assert.Equal(nonce, request?.Nonce);
        Assert.Equal("1.2.3", request?.Version);
    }

    [Fact]
    public void PartialDuplicateOrMalformedBinding_FailsClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProductGuiActivationAcknowledgement.TryParseRequest(
                [ProductGuiActivationAcknowledgement.PipeArgument, "bad"],
                out _));
        Assert.Throws<InvalidDataException>(() =>
            ProductGuiActivationAcknowledgement.TryParseRequest(
                [
                    ProductGuiActivationAcknowledgement.PipeArgument,
                    ProductGuiActivationAcknowledgement.PipePrefix + new string('a', 32),
                    ProductGuiActivationAcknowledgement.PipeArgument,
                    ProductGuiActivationAcknowledgement.PipePrefix + new string('b', 32),
                ],
                out _));
        Assert.False(ProductGuiActivationAcknowledgement.TryParseRequest(["--normal"], out _));
    }
}
