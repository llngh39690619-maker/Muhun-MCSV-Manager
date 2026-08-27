using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

internal static class ModrinthModpackTestFixtures
{
    public static byte[] CreateMrpack(string manifest, params (string Path, byte[] Content, int? Attributes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "modrinth.index.json", Encoding.UTF8.GetBytes(manifest), null);
            foreach (var entry in entries) WriteEntry(archive, entry.Path, entry.Content, entry.Attributes);
        }

        return output.ToArray();
    }

    public static (string Sha512, string Sha1) Hashes(byte[] content)
        => (Convert.ToHexString(SHA512.HashData(content)).ToLowerInvariant(),
            Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant());

    public static string FileJson(string path, byte[] content, string server = "required", string? url = null)
    {
        var hashes = Hashes(content);
        return $$"""
        {
          "path": {{System.Text.Json.JsonSerializer.Serialize(path)}},
          "hashes": { "sha512": "{{hashes.Sha512}}", "sha1": "{{hashes.Sha1}}" },
          "env": { "client": "required", "server": "{{server}}" },
          "downloads": ["{{url ?? "https://files.test/" + Uri.EscapeDataString(path)}}"],
          "fileSize": {{content.LongLength}}
        }
        """;
    }

    public static string Manifest(string files, string dependencies = "\"minecraft\":\"1.20.1\",\"fabric-loader\":\"0.16.9\"")
        => $$"""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "fixture-1",
          "name": "Fixture Pack",
          "summary": "offline fixture",
          "files": [{{files}}],
          "dependencies": { {{dependencies}} }
        }
        """;

    public static void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content, int? attributes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        if (attributes is not null) entry.ExternalAttributes = attributes.Value;
        using var stream = entry.Open();
        stream.Write(content);
    }
}

internal sealed class TestUriPolicy : IModrinthModpackUriPolicy
{
    public List<(Uri Uri, bool Redirect)> Checks { get; } = [];

    public void EnsureAllowed(Uri uri, bool isRedirect)
    {
        Checks.Add((uri, isRedirect));
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("files.test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("test URI rejected");
        }
    }
}

internal sealed class FixtureTransport : IModrinthModpackHttpTransport
{
    private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _response;

    public FixtureTransport(Func<Uri, CancellationToken, Task<HttpResponseMessage>> response) => _response = response;

    public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
        => _response(uri, cancellationToken);

    public static HttpResponseMessage Bytes(byte[] content)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
}

internal sealed class FixtureHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var result = response(request);
        result.RequestMessage ??= request;
        return Task.FromResult(result);
    }
}
