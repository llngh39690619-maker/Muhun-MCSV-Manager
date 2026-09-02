using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.App.Tests;

public sealed class FtbClientInstallFailureDiagnosticsTests
{
    [Fact]
    public void Policy_ClassifiesSupportedFailureFamiliesWithoutReturningExceptionText()
    {
        var cases = new (Exception Error, string Stage, string Code, string LocalizationKey)[]
        {
            (new TimeoutException("timeout-secret"), "game_payload", "network_timeout",
                "client.vm.catalog.ftb.failure.timeout"),
            (new TaskCanceledException("timeout-canceled-secret"), "download_content", "network_timeout",
                "client.vm.catalog.ftb.failure.timeout"),
            (new HttpRequestException("network-secret"), "loader", "network_unavailable",
                "client.vm.catalog.ftb.failure.network"),
            (new HttpRequestException(
                    "http-secret",
                    inner: null,
                    HttpStatusCode.Forbidden),
                "loader", "http_rejected", "client.vm.catalog.ftb.failure.network"),
            (new IOException("disk-secret", unchecked((int)0x80070070)), "pack_content", "disk_full",
                "client.vm.catalog.ftb.failure.storage"),
            (new UnauthorizedAccessException("access-secret"), "pack_content", "access_denied",
                "client.vm.catalog.ftb.failure.storage"),
            (new InvalidDataException("integrity-secret"), "download-content", "integrity_failed",
                "client.vm.catalog.ftb.failure.integrity"),
            (new InvalidDataException("metadata-secret"), "install-game", "game_payload_failed",
                "client.vm.catalog.ftb.failure.compatibility"),
            (new InvalidOperationException("loader-secret"), "neoforge_loader", "loader_failed",
                "client.vm.catalog.ftb.failure.loader"),
            (new InvalidOperationException("payload-secret"), "minecraft_payload", "game_payload_failed",
                "client.vm.catalog.ftb.failure.compatibility"),
            (new InvalidOperationException("java-secret"), "prepare-java", "java_failed",
                "client.vm.catalog.ftb.failure.java"),
            (new InvalidOperationException("unknown-secret"), "pack_content", "unknown",
                "client.vm.catalog.ftb.failure.unknown")
        };

        foreach (var item in cases)
        {
            var classification = FtbClientInstallFailurePolicy.Classify(item.Error, item.Stage);

            Assert.Equal(item.Code, classification.FailureCode);
            Assert.Equal(item.LocalizationKey, classification.LocalizationKey);
            Assert.DoesNotContain("secret", classification.FailureCode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", classification.LocalizationKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Policy_UsesTypedTransactionFailuresInsteadOfGuessingFromStageText()
    {
        var recovery = CreateTransactionFailure(
            typeof(FtbClientInstallRecoveryRequiredException),
            "pending-recovery",
            new MinecraftClientDownloadException(
                4,
                "example.invalid",
                httpStatusCode: null,
                MinecraftClientDownloadFailureKind.Timeout,
                "game-file",
                new TimeoutException("transaction-download-secret")));
        var rollback = CreateTransactionFailure(
            typeof(FtbClientInstallRollbackIncompleteException),
            "rollback");

        var recoveryClassification = FtbClientInstallFailurePolicy.Classify(
            new AggregateException(new InvalidOperationException("wrapper-secret"), recovery),
            "complete");
        var rollbackClassification = FtbClientInstallFailurePolicy.Classify(
            rollback,
            "download-content");
        var stageOnlyClassification = FtbClientInstallFailurePolicy.Classify(
            new InvalidOperationException("ordinary-stage-secret"),
            "rollback");

        Assert.Equal("recovery_required", recoveryClassification.FailureCode);
        Assert.Equal("client.vm.catalog.ftb.failure.recovery", recoveryClassification.LocalizationKey);
        Assert.Equal("game-file", recoveryClassification.DownloadStage);
        Assert.Equal("Timeout", recoveryClassification.DownloadFailureKind);
        Assert.Equal("pending-recovery", recoveryClassification.TransactionStage);
        Assert.Equal("rollback_incomplete", rollbackClassification.FailureCode);
        Assert.Equal("client.vm.catalog.ftb.failure.rollback", rollbackClassification.LocalizationKey);
        Assert.Equal("rollback", rollbackClassification.TransactionStage);
        Assert.Equal("unknown", stageOnlyClassification.FailureCode);
        Assert.Null(stageOnlyClassification.TransactionStage);
    }

    [Fact]
    public void Policy_PrioritizesTypedLoaderProcessOverInnerTimeoutAndNetworkFailures()
    {
        var error = CreateLoaderProcessFailure(
            "loader-process",
            "maven.neoforged.net",
            new AggregateException(
                new TimeoutException("loader-timeout-secret"),
                new HttpRequestException("loader-network-secret")));

        var classification = FtbClientInstallFailurePolicy.Classify(error, "install-loader");

        Assert.Equal("loader_failed", classification.FailureCode);
        Assert.Equal("client.vm.catalog.ftb.failure.loader", classification.LocalizationKey);
        Assert.Equal("loader-process", classification.LoaderStage);
        Assert.Equal("maven.neoforged.net", classification.RemoteHost);
        Assert.Null(classification.DownloadStage);
        Assert.Null(classification.TransactionStage);
    }

    [Theory]
    [InlineData(
        MinecraftClientDownloadFailureKind.InvalidResponse,
        "loader-installer",
        "install-game",
        "loader_failed",
        "client.vm.catalog.ftb.failure.loader")]
    [InlineData(
        MinecraftClientDownloadFailureKind.Unknown,
        "loader-sidecar",
        "install-game",
        "loader_failed",
        "client.vm.catalog.ftb.failure.loader")]
    [InlineData(
        MinecraftClientDownloadFailureKind.InvalidResponse,
        "launcher-metadata",
        "install-game",
        "game_payload_failed",
        "client.vm.catalog.ftb.failure.compatibility")]
    [InlineData(
        MinecraftClientDownloadFailureKind.InvalidResponse,
        "game-file",
        "install-game",
        "integrity_failed",
        "client.vm.catalog.ftb.failure.integrity")]
    [InlineData(
        MinecraftClientDownloadFailureKind.Sha256Mismatch,
        "launcher-metadata",
        "install-game",
        "integrity_failed",
        "client.vm.catalog.ftb.failure.integrity")]
    [InlineData(
        MinecraftClientDownloadFailureKind.Timeout,
        "game-file",
        "install-game",
        "network_timeout",
        "client.vm.catalog.ftb.failure.timeout")]
    public void Policy_UsesManagedDownloadStageAndFailureKindForPreciseClassification(
        MinecraftClientDownloadFailureKind failureKind,
        string downloadStage,
        string outerStage,
        string expectedCode,
        string expectedLocalizationKey)
    {
        var error = new MinecraftClientDownloadException(
            4,
            "example.invalid",
            httpStatusCode: null,
            failureKind,
            downloadStage,
            new InvalidDataException("managed-inner-secret"));

        var classification = FtbClientInstallFailurePolicy.Classify(error, outerStage);

        Assert.Equal(expectedCode, classification.FailureCode);
        Assert.Equal(expectedLocalizationKey, classification.LocalizationKey);
        Assert.Equal(downloadStage, classification.DownloadStage);
        Assert.Equal(failureKind.ToString(), classification.DownloadFailureKind);
    }

    [Fact]
    public void Policy_TraversesAggregateAndInnerExceptionsAndPreservesHttpStatus()
    {
        var http = new HttpRequestException(
            "Authorization: Bearer aggregate-secret",
            new SocketException((int)SocketError.ConnectionRefused),
            HttpStatusCode.TooManyRequests);
        var error = new AggregateException(
            new InvalidOperationException("wrapper-secret"),
            new Exception("inner-secret", http));

        var classification = FtbClientInstallFailurePolicy.Classify(error, "loader");

        Assert.Equal("http_rejected", classification.FailureCode);
        Assert.Equal(429, classification.HttpStatusCode);
        Assert.True(classification.IsRetryable);
        Assert.Equal(typeof(HttpRequestException).FullName, classification.ExceptionType);
    }

    [Fact]
    public void Policy_UsesPrivacySafeManagedDownloadMetadata()
    {
        var error = new MinecraftClientDownloadException(
            4,
            "piston-data.mojang.com",
            HttpStatusCode.ServiceUnavailable,
            MinecraftClientDownloadFailureKind.HttpStatus,
            "game-file",
            new IOException("inner-path-secret"));

        var classification = FtbClientInstallFailurePolicy.Classify(error, "install-game");

        Assert.Equal("http_rejected", classification.FailureCode);
        Assert.Equal(503, classification.HttpStatusCode);
        Assert.Equal(4, classification.AttemptCount);
        Assert.Equal("piston-data.mojang.com", classification.RemoteHost);
        Assert.True(classification.IsRetryable);
    }

    [Fact]
    public async Task Store_WritesOneBoundedAtomicJsonWithOnlyRedactedStructuredDetails()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        var store = new ClientOperationDiagnosticStore(paths);
        var error = new AggregateException(
            "diagnostic-wrapper-secret",
            new MinecraftClientDownloadException(
                4,
                "api.feed-the-beast.com",
                HttpStatusCode.ServiceUnavailable,
                MinecraftClientDownloadFailureKind.HttpStatus,
                "launcher-metadata",
                CreateSecretHttpFailure()));
        var context = new Dictionary<string, string?>
        {
            ["packId"] = "134",
            ["packVersionId"] = "12001",
            ["minecraftVersion"] = "1.21.1",
            ["loader"] = "neoforge",
            ["loaderVersion"] = "21.1.248",
            ["javaVersion"] = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signatureSecret123",
            ["completedFiles"] = "0",
            ["totalFiles"] = "11332",
            ["attempt"] = "4",
            ["maximumAttempts"] = "4",
            ["rollbackAttempted"] = "true",
            ["rollbackSucceeded"] = "true",
            ["recoveryRequired"] = "false",
            ["remoteHost"] = "https://context-user:context-password@context-host-data-secret.example/files?token=context-query-secret#context-fragment-secret",
            ["authorization"] = "Bearer context-bearer-secret",
            ["cookie"] = "session=context-cookie-secret",
            ["instanceName"] = @"C:\Users\Sensitive User\secret-instance"
        };

        var reference = await store.WriteFailureAsync(new ClientOperationDiagnosticWriteRequest(
            "ftb_client_install",
            "game_payload",
            "http_rejected",
            error,
            context));

        Assert.NotNull(reference);
        Assert.Equal(Path.Combine(paths.Logs, "client-operations", "ftb-install"), store.DirectoryPath);
        Assert.True(File.Exists(reference.FilePath));
        Assert.True(new FileInfo(reference.FilePath).Length <= 64 * 1024);
        Assert.Single(Directory.EnumerateFiles(store.DirectoryPath, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.partial"));

        var json = await File.ReadAllTextAsync(reference.FilePath);
        foreach (var secret in new[]
                 {
                     "message-password-secret",
                     "message-bearer-secret",
                     "uri-user",
                     "uri-password",
                     "query-secret",
                     "fragment-secret",
                     "context-user",
                     "context-password",
                     "context-host-data-secret",
                     "context-query-secret",
                     "context-fragment-secret",
                     "context-bearer-secret",
                     "context-cookie-secret",
                     "header-cookie-secret",
                     "eyJhbGciOiJIUzI1NiJ9.jwt-payload-secret.jwt-signature-secret",
                     "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signatureSecret123",
                     "Sensitive User",
                     "secret-instance"
                 })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(reference.DiagnosticId, root.GetProperty("diagnosticId").GetString());
        Assert.Equal("http_rejected", root.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal(503, root.GetProperty("failure").GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(
            "launcher-metadata",
            root.GetProperty("failure").GetProperty("downloadStage").GetString());
        Assert.Equal(
            "HttpStatus",
            root.GetProperty("failure").GetProperty("downloadFailureKind").GetString());
        Assert.Equal(
            "api.feed-the-beast.com",
            root.GetProperty("failure").GetProperty("remoteHost").GetString());
        Assert.False(root.GetProperty("context").TryGetProperty("authorization", out _));
        Assert.False(root.GetProperty("context").TryGetProperty("instanceName", out _));
        Assert.False(root.GetProperty("context").TryGetProperty("remoteHost", out _));
        Assert.All(
            root.GetProperty("failure").GetProperty("exceptionChain").EnumerateArray(),
            descriptor => Assert.False(descriptor.TryGetProperty("message", out _)));
    }

    [Fact]
    public async Task Store_BoundsExceptionDepthAndContextEntryCount()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ClientOperationDiagnosticStore(Path.Combine(temporary.Path, "diagnostics"));
        Exception error = new InvalidOperationException("leaf-secret");
        for (var index = 0; index < 40; index++)
        {
            error = new Exception($"wrapper-secret-{index}", error);
        }

        var context = Enumerable.Range(0, 40)
            .ToDictionary(index => $"unknown-{index}", index => (string?)$"secret-{index}");
        context["packId"] = "134";

        var reference = await store.WriteFailureAsync(new ClientOperationDiagnosticWriteRequest(
            "ftb_client_install",
            "unknown",
            "unknown",
            error,
            context));

        Assert.NotNull(reference);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reference.FilePath));
        Assert.InRange(
            document.RootElement.GetProperty("failure").GetProperty("exceptionChain").GetArrayLength(),
            1,
            12);
        Assert.True(document.RootElement.GetProperty("context").EnumerateObject().Count() <= 16);
        Assert.DoesNotContain("wrapper-secret", await File.ReadAllTextAsync(reference.FilePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host", "host-data-secret.example")]
    [InlineData(
        "Uri",
        "https://uri-user:uri-password@uri-data-secret.example/file?token=uri-query-secret")]
    [InlineData(
        "RequestUri",
        "https://request-user:request-password@request-data-secret.example/file?token=request-query-secret")]
    public async Task Store_DoesNotReadRemoteHostFromArbitraryExceptionData(
        string dataKey,
        string suspiciousValue)
    {
        using var temporary = new TemporaryDirectory();
        var store = new ClientOperationDiagnosticStore(Path.Combine(temporary.Path, "diagnostics"));
        var error = new InvalidOperationException("exception-data-secret");
        error.Data[dataKey] = dataKey.Equals("RequestUri", StringComparison.Ordinal)
            ? new Uri(suspiciousValue)
            : suspiciousValue;

        var reference = await store.WriteFailureAsync(Request(error));

        Assert.NotNull(reference);
        var json = await File.ReadAllTextAsync(reference.FilePath);
        Assert.DoesNotContain("data-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("uri-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("request-user", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("failure").GetProperty("remoteHost").ValueKind);
    }

    [Theory]
    [InlineData(
        typeof(FtbClientInstallRecoveryRequiredException),
        "registry-commit-verification",
        "recovery_required")]
    [InlineData(
        typeof(FtbClientInstallRecoveryRequiredException),
        "post-commit-revocation",
        "recovery_required")]
    [InlineData(
        typeof(FtbClientInstallRecoveryRequiredException),
        "finalization-revocation",
        "recovery_required")]
    [InlineData(
        typeof(FtbClientInstallRecoveryRequiredException),
        "pending-recovery",
        "recovery_required")]
    [InlineData(
        typeof(FtbClientInstallRollbackIncompleteException),
        "rollback",
        "rollback_incomplete")]
    public async Task Store_WritesOnlySafeTypedTransactionStage(
        Type exceptionType,
        string transactionStage,
        string expectedFailureCode)
    {
        using var temporary = new TemporaryDirectory();
        var store = new ClientOperationDiagnosticStore(Path.Combine(temporary.Path, "diagnostics"));
        var error = CreateTransactionFailure(
            exceptionType,
            transactionStage,
            new IOException(
                "transaction-message-secret "
                + @"C:\Users\Transaction Secret\private-file "
                + "https://user:password@example.invalid/file?token=transaction-token-secret"));

        var reference = await store.WriteFailureAsync(new ClientOperationDiagnosticWriteRequest(
            "ftb_client_install",
            "complete",
            "defer_to_policy",
            error,
            new Dictionary<string, string?>()));

        Assert.NotNull(reference);
        var json = await File.ReadAllTextAsync(reference.FilePath);
        Assert.DoesNotContain("transaction-message-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Transaction Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-file", json, StringComparison.Ordinal);
        Assert.DoesNotContain("transaction-token-secret", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var failure = document.RootElement.GetProperty("failure");
        Assert.Equal(expectedFailureCode, failure.GetProperty("code").GetString());
        Assert.Equal(transactionStage, failure.GetProperty("transactionStage").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("loaderStage").ValueKind);
        Assert.All(
            failure.GetProperty("exceptionChain").EnumerateArray(),
            descriptor => Assert.False(descriptor.TryGetProperty("message", out _)));
    }

    [Fact]
    public async Task Store_WritesOnlyTypedLoaderProcessStageAndHost()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ClientOperationDiagnosticStore(Path.Combine(temporary.Path, "diagnostics"));
        var error = CreateLoaderProcessFailureWithMetadata(
            "loader-process",
            "maven.neoforged.net",
            1,
            MinecraftClientLoaderProcessFailureKind.AccessDenied,
            new TimeoutException(
                "loader-process-message-secret "
                + @"C:\Users\Loader Secret\installer.jar "
                + "https://user:password@evil.example/file?token=loader-token-secret"));
        var context = new Dictionary<string, string?>
        {
            ["remoteHost"] = "context-host-secret.example"
        };

        var reference = await store.WriteFailureAsync(new ClientOperationDiagnosticWriteRequest(
            "ftb_client_install",
            "install-loader",
            "loader_failed",
            error,
            context));

        Assert.NotNull(reference);
        var json = await File.ReadAllTextAsync(reference.FilePath);
        Assert.DoesNotContain("loader-process-message-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Loader Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loader-token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("context-host-secret", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var failure = document.RootElement.GetProperty("failure");
        Assert.Equal("loader-process", failure.GetProperty("loaderStage").GetString());
        Assert.Equal("maven.neoforged.net", failure.GetProperty("remoteHost").GetString());
        Assert.Equal(1, failure.GetProperty("loaderProcessExitCode").GetInt32());
        Assert.Equal(
            "AccessDenied",
            failure.GetProperty("loaderProcessFailureKind").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("downloadStage").ValueKind);
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("transactionStage").ValueKind);
        Assert.False(document.RootElement.GetProperty("context").TryGetProperty("remoteHost", out _));
    }

    [Fact]
    public async Task Store_PrunesExpiredAndOverflowReportsWhilePreservingCurrentReport()
    {
        using var temporary = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 31, 4, 5, 6, TimeSpan.Zero);
        var store = new ClientOperationDiagnosticStore(
            Path.Combine(temporary.Path, "diagnostics"),
            () => now,
            maximumRetainedFiles: 3,
            maximumAge: TimeSpan.FromDays(30));
        Directory.CreateDirectory(store.DirectoryPath);
        var expired = Path.Combine(store.DirectoryPath, "expired.json");
        await File.WriteAllTextAsync(expired, "{}");
        File.SetLastWriteTimeUtc(expired, now.UtcDateTime.AddDays(-31));

        ClientOperationDiagnosticReference? latest = null;
        for (var index = 0; index < 6; index++)
        {
            latest = await store.WriteFailureAsync(Request(new InvalidOperationException($"secret-{index}")));
            Assert.NotNull(latest);
        }

        var retained = Directory.EnumerateFiles(store.DirectoryPath, "*.json").ToArray();
        Assert.Equal(3, retained.Length);
        Assert.DoesNotContain(expired, retained, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(latest!.FilePath, retained, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.partial"));
    }

    [Fact]
    public async Task Store_ReturnsNullInsteadOfMaskingTheOriginalFailureWhenWritingFails()
    {
        using var temporary = new TemporaryDirectory();
        var blockingFile = Path.Combine(temporary.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        var store = new ClientOperationDiagnosticStore(Path.Combine(blockingFile, "ftb-install"));

        var writeError = await Record.ExceptionAsync(async () =>
        {
            var result = await store.WriteFailureAsync(Request(
                new InvalidOperationException("original-install-secret")));
            Assert.Null(result);
        });

        Assert.Null(writeError);
    }

    [Fact]
    public async Task Store_RejectsAReparsePointDirectoryWithoutWritingThroughIt()
    {
        using var temporary = new TemporaryDirectory();
        var outside = Path.Combine(temporary.Path, "outside");
        var redirected = Path.Combine(temporary.Path, "redirected-diagnostics");
        Directory.CreateDirectory(outside);
        CreateDirectoryJunction(redirected, outside);
        try
        {
            var store = new ClientOperationDiagnosticStore(redirected);

            var reference = await store.WriteFailureAsync(Request(
                new InvalidOperationException("must-not-write-through-link")));

            Assert.Null(reference);
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            if (Directory.Exists(redirected))
            {
                Directory.Delete(redirected);
            }
        }
    }

    [Fact]
    public async Task Store_RejectsAReparsePointAncestorBelowTheApplicationTrustedRoot()
    {
        using var temporary = new TemporaryDirectory();
        var applicationRoot = Path.Combine(temporary.Path, "application-root");
        var outside = Path.Combine(temporary.Path, "outside-logs");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(outside);
        var paths = new ApplicationPaths(applicationRoot);
        CreateDirectoryJunction(paths.Logs, outside);
        try
        {
            var store = new ClientOperationDiagnosticStore(paths);

            var reference = await store.WriteFailureAsync(Request(
                new InvalidOperationException("must-not-write-through-ancestor-link")));

            Assert.Null(reference);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            if (Directory.Exists(paths.Logs))
            {
                Directory.Delete(paths.Logs);
            }
        }
    }

    private static ClientOperationDiagnosticWriteRequest Request(Exception error)
        => new(
            "ftb_client_install",
            "unknown",
            "unknown",
            error,
            new Dictionary<string, string?>());

    private static Exception CreateSecretHttpFailure()
    {
        var http = new HttpRequestException(
            "password=message-password-secret Authorization: Bearer message-bearer-secret "
            + "Cookie: session=header-cookie-secret "
            + "eyJhbGciOiJIUzI1NiJ9.jwt-payload-secret.jwt-signature-secret "
            + @"C:\Users\Sensitive User\private "
            + "https://uri-user:uri-password@api.feed-the-beast.com/file?token=query-secret#fragment-secret",
            inner: null,
            HttpStatusCode.ServiceUnavailable);
        http.Data["RequestUri"] = new Uri(
            "https://uri-user:uri-password@api.feed-the-beast.com/file?token=query-secret#fragment-secret");
        return new AggregateException("aggregate-secret", new Exception("wrapper-secret", http));
    }

    private static Exception CreateTransactionFailure(
        Type exceptionType,
        string stage,
        Exception? failure = null)
    {
        var constructor = exceptionType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(IEnumerable<Exception>)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (Exception)constructor.Invoke(
            [stage, new[] { failure ?? new IOException("transaction-inner-secret") }]);
    }

    private static Exception CreateLoaderProcessFailure(
        string stage,
        string? host,
        Exception innerException)
    {
        var constructor = typeof(MinecraftClientLoaderProcessException).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string), typeof(Exception)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (Exception)constructor.Invoke([stage, host, innerException]);
    }

    private static Exception CreateLoaderProcessFailureWithMetadata(
        string stage,
        string? host,
        int? exitCode,
        MinecraftClientLoaderProcessFailureKind failureKind,
        Exception innerException)
    {
        var constructor = typeof(MinecraftClientLoaderProcessException).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(string),
                typeof(string),
                typeof(int?),
                typeof(MinecraftClientLoaderProcessFailureKind),
                typeof(Exception),
            ],
            modifiers: null);
        Assert.NotNull(constructor);
        return (Exception)constructor.Invoke(
            [stage, host, exitCode, failureKind, innerException]);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not create test junction.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {standardError}{standardOutput}");
        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mcsv-ftb-diagnostic-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
