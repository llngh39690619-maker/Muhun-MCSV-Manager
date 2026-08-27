using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductOperationErrorPolicyTests
{
    public static TheoryData<Exception, string, int> ExpectedErrors => new()
    {
        { new FileNotFoundException("missing C:\\Users\\private\\secret.jar"), "server.launch_path_not_found", 409 },
        { new DirectoryNotFoundException("missing D:\\private-world"), "server.launch_path_not_found", 409 },
        { new UnauthorizedAccessException("account DOMAIN\\private-user denied C:\\secret"), "server.path_rejected", 403 },
        { new InvalidOperationException("process C:\\secret\\java.exe rejected"), "server.operation_rejected", 409 },
        { new InvalidDataException("invalid token=hunter2"), "server.data_invalid", 422 },
        { new ArgumentException("request contained C:\\secret"), "request.invalid", 400 },
        { new KeyNotFoundException("server at C:\\secret not found"), "server.not_found", 404 },
    };

    [Theory]
    [MemberData(nameof(ExpectedErrors))]
    public void ExpectedFailures_AreMappedWithoutLeakingExceptionDetails(
        Exception exception,
        string expectedCode,
        int expectedStatus)
    {
        Assert.True(ProductOperationErrorPolicy.IsExpected(exception));

        var result = ProductOperationErrorPolicy.ToPublic(exception);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedFailure_IsNotClassifiedForPublicHandling()
    {
        Assert.False(ProductOperationErrorPolicy.IsExpected(new NullReferenceException("sensitive")));
    }
}
