using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Service;

/// <summary>
/// Stores only the operator's durable on/off intent. Runtime state, URLs, process identifiers,
/// and secrets are deliberately never persisted here.
/// </summary>
public sealed class ProductRemoteWebIntentStore(ProductDataLayout layout)
{
    internal const string FileName = "remote-web.intent.v1.json";
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(layout.Operations, FileName);

    public bool ReadDesiredEnabled()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                // The formal product defaults to remote Web enabled. An explicit operator stop
                // writes false and survives both Service and machine restarts.
                return true;
            }

            RejectReparsePoint(_path);
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is 0 or > 1_024)
            {
                throw new InvalidDataException("Remote Web intent file has an invalid size.");
            }

            try
            {
                var value = JsonSerializer.Deserialize(bytes, ProductRemoteWebIntentJsonContext.Default.IntentDocument)
                    ?? throw new InvalidDataException("Remote Web intent file is empty.");
                if (value.SchemaVersion != SchemaVersion)
                {
                    throw new InvalidDataException("Remote Web intent schema is unsupported.");
                }

                return value.DesiredEnabled;
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Remote Web intent file is invalid.", error);
            }
        }
    }

    public void WriteDesiredEnabled(bool desiredEnabled)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(layout.Operations);
            RejectReparsePoint(layout.Operations);
            if (File.Exists(_path))
            {
                RejectReparsePoint(_path);
            }

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new IntentDocument(SchemaVersion, desiredEnabled),
                ProductRemoteWebIntentJsonContext.Default.IntentDocument);
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4_096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Remote Web intent path cannot be a reparse point.");
        }
    }

    internal sealed record IntentDocument(int SchemaVersion, bool DesiredEnabled);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProductRemoteWebIntentStore.IntentDocument))]
internal sealed partial class ProductRemoteWebIntentJsonContext : JsonSerializerContext;
