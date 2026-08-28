namespace MinecraftServerManager.GameClient;

public static class MinecraftClientInstallationIdentity
{
    public static Guid LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Client identity path has no parent directory.", nameof(path));
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent);

        if (File.Exists(fullPath))
        {
            RejectReparsePoint(fullPath);
            var text = File.ReadAllText(fullPath).Trim();
            return Guid.TryParseExact(text, "D", out var existing) && existing != Guid.Empty
                ? existing
                : throw new InvalidDataException("Minecraft client installation identity is invalid.");
        }

        var created = Guid.NewGuid();
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, created.ToString("D"));
            try
            {
                File.Move(temporaryPath, fullPath);
                return created;
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                RejectReparsePoint(fullPath);
                var text = File.ReadAllText(fullPath).Trim();
                return Guid.TryParseExact(text, "D", out var raced) && raced != Guid.Empty
                    ? raced
                    : throw new InvalidDataException("Minecraft client installation identity is invalid.");
            }
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

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Minecraft client identity paths cannot be reparse points.");
        }
    }
}
