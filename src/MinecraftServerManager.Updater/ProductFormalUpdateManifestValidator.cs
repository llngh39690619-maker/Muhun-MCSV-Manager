namespace MinecraftServerManager.Updater;

public static class ProductFormalUpdateManifestValidator
{
    public const string GuiEntryPoint = "gui-win-x64/Muhun MCSV Manager.exe";
    public const string ServiceEntryPoint = "service-win-x64/Muhun MCSV Service.exe";
    public const string UpdaterEntryPoint = "updater-win-x64/Muhun MCSV Updater.exe";

    public static void Validate(ProductUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(
                manifest.EntryPoint,
                GuiEntryPoint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Formal update manifest has an unexpected GUI entry point.");
        }

        var files = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Contains(ProductInstalledVersionMetadataStore.FileName))
        {
            throw new InvalidDataException("Formal update package contains a reserved updater metadata file.");
        }

        foreach (var required in new[]
                 {
                     GuiEntryPoint,
                     ServiceEntryPoint,
                     UpdaterEntryPoint,
                 })
        {
            if (!files.Contains(required))
            {
                throw new InvalidDataException("Formal update package is missing a required product executable.");
            }
        }
    }
}
