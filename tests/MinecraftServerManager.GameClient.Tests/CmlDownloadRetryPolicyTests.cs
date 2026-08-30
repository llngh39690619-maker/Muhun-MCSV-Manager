using System.Net;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class CmlDownloadRetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void IsRetryable_TransientHttpStatusReturnsTrue(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("transient", null, statusCode);

        Assert.True(CmlDownloadRetryPolicy.IsRetryable(exception, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void IsRetryable_PermanentHttpStatusReturnsFalse(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("permanent", null, statusCode);

        Assert.False(CmlDownloadRetryPolicy.IsRetryable(exception, CancellationToken.None));
    }

    [Fact]
    public void IsRetryable_DiskAndPermissionErrorsReturnFalse()
    {
        Assert.False(CmlDownloadRetryPolicy.IsRetryable(
            new IOException("disk write failed"),
            CancellationToken.None));
        Assert.False(CmlDownloadRetryPolicy.IsRetryable(
            new UnauthorizedAccessException("denied"),
            CancellationToken.None));
    }

    [Fact]
    public void IsRetryable_CallerCancellationAlwaysReturnsFalse()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(CmlDownloadRetryPolicy.IsRetryable(
            new TimeoutException("timeout"),
            cancellation.Token));
    }

    [Fact]
    public void IsRetryable_AggregateRequiresEveryFailureToBeTransient()
    {
        var transient = new HttpRequestException(
            "temporary",
            null,
            HttpStatusCode.ServiceUnavailable);
        var permanent = new HttpRequestException(
            "missing",
            null,
            HttpStatusCode.NotFound);

        Assert.True(CmlDownloadRetryPolicy.IsRetryable(
            new AggregateException(transient, new TimeoutException()),
            CancellationToken.None));
        Assert.False(CmlDownloadRetryPolicy.IsRetryable(
            new AggregateException(transient, permanent),
            CancellationToken.None));
    }

    [Fact]
    public void Options_RejectUnboundedOrInvalidSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (CmlDownloadReliabilityOptions.Default with { MaximumFileAttempts = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (CmlDownloadReliabilityOptions.Default with { MaximumConcurrentDownloads = 9 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (CmlDownloadReliabilityOptions.Default with
            {
                RetryDelays = [TimeSpan.FromMinutes(2)],
            }).Validate());
    }
}
