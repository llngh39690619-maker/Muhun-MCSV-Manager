using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace MinecraftServerManager.Contracts;

public sealed record ProductGuiActivationAcknowledgementRequest(
    string PipeName,
    string Nonce,
    string Version);

public sealed record ProductGuiReadyAcknowledgement(
    int SchemaVersion,
    int ProcessId,
    int SessionId,
    string Version,
    string Nonce,
    ProductApiVersion ApiVersion,
    bool Ready);

/// <summary>
/// One-shot GUI side of the signed updater's A/B readiness handshake. The App must call
/// <see cref="SendReadyAsync"/> only after its Service handshake reports Ready and a compatible
/// API version, and only after the main view model has completed initialization.
/// </summary>
public static class ProductGuiActivationAcknowledgement
{
    public const int SchemaVersion = 1;
    public const string PipeArgument = "--activation-ack-pipe";
    public const string NonceArgument = "--activation-nonce";
    public const string VersionArgument = "--activation-version";
    public const string PipePrefix = "Muhun.MCSV.GuiReady.v1.";
    public const int MaximumFrameBytes = 8 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static bool TryParseRequest(
        IReadOnlyList<string>? arguments,
        out ProductGuiActivationAcknowledgementRequest? request)
    {
        request = null;
        if (arguments is null)
        {
            return false;
        }

        var pipe = ReadUniqueValue(arguments, PipeArgument);
        var nonce = ReadUniqueValue(arguments, NonceArgument);
        var version = ReadUniqueValue(arguments, VersionArgument);
        var present = new[] { pipe.Present, nonce.Present, version.Present };
        if (!present.Any(value => value))
        {
            return false;
        }

        if (present.Any(value => !value) ||
            !IsValidPipeName(pipe.Value) ||
            !IsHex(nonce.Value, 64) ||
            !IsSemanticVersion(version.Value))
        {
            throw new InvalidDataException("GUI activation acknowledgement arguments are invalid.");
        }

        request = new ProductGuiActivationAcknowledgementRequest(
            pipe.Value!,
            nonce.Value!,
            version.Value!);
        return true;
    }

    public static async Task SendReadyAsync(
        ProductGuiActivationAcknowledgementRequest request,
        string runningVersion,
        bool serviceReady,
        ProductApiVersion negotiatedApiVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows() ||
            !serviceReady ||
            negotiatedApiVersion.CompareTo(ProductApiProtocol.MinimumSupportedVersion) < 0 ||
            negotiatedApiVersion.CompareTo(ProductApiProtocol.CurrentVersion) > 0 ||
            !IsValidPipeName(request.PipeName) ||
            !IsHex(request.Nonce, 64) ||
            !IsSemanticVersion(request.Version) ||
            !string.Equals(request.Version, runningVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "GUI cannot acknowledge activation before exact version and Service readiness are proven.");
        }

        using var process = Process.GetCurrentProcess();
        if (process.SessionId <= 0)
        {
            throw new InvalidOperationException("GUI activation acknowledgement requires an interactive session.");
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            request.PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectTimeout);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new ProductGuiReadyAcknowledgement(
                    SchemaVersion,
                    process.Id,
                    process.SessionId,
                    runningVersion,
                    request.Nonce,
                    negotiatedApiVersion,
                    Ready: true),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload.Length is < 2 or > MaximumFrameBytes)
            {
                throw new InvalidDataException("GUI activation acknowledgement has an invalid size.");
            }

            await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), deadline.Token)
                .ConfigureAwait(false);
            await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GUI activation acknowledgement pipe was unavailable.");
        }
    }

    private static (bool Present, string? Value) ReadUniqueValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        var found = false;
        string? value = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found || index + 1 >= arguments.Count)
            {
                throw new InvalidDataException("GUI activation acknowledgement argument is duplicated or incomplete.");
            }

            found = true;
            value = arguments[++index];
        }

        return (found, value);
    }

    private static bool IsValidPipeName(string? value)
        => value is not null && value.Length == PipePrefix.Length + 32 &&
           value.StartsWith(PipePrefix, StringComparison.Ordinal) &&
           IsHex(value[PipePrefix.Length..], 32);

    private static bool IsHex(string? value, int length)
        => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    private static bool IsSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        var mainAndPreRelease = value.Split('-', 2);
        var parts = mainAndPreRelease[0].Split('.');
        if (parts.Length != 3 || parts.Any(part =>
                part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        return mainAndPreRelease.Length == 1 ||
               (mainAndPreRelease[1].Length > 0 &&
                mainAndPreRelease[1].Split('.').All(identifier =>
                    identifier.Length > 0 &&
                    identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')));
    }
}
