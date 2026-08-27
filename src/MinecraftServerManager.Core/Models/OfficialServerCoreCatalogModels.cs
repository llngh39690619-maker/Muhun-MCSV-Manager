namespace MinecraftServerManager.Core.Models;

/// <summary>How an artifact selected from the official server-core catalog must be installed.</summary>
public enum OfficialServerInstallStrategy
{
    DirectServerJar,
    FabricInstaller,
    ForgeInstaller,
    NeoForgeInstaller
}

/// <summary>A server product for which this application has a first-party catalog contract.</summary>
public sealed record OfficialServerCoreDescriptor(
    CoreType CoreType,
    string DisplayName,
    bool IsProxy);

/// <summary>
/// A stable product/game version exposed by a first-party catalog. Velocity is not tied to one
/// Minecraft release, so its product version is also placed in <see cref="MinecraftVersion"/>.
/// </summary>
public sealed record OfficialServerCoreVersionInfo(
    CoreType CoreType,
    string DisplayName,
    string MinecraftVersion,
    string ProductVersion,
    OfficialServerInstallStrategy InstallStrategy,
    int JavaMajorVersion);

/// <summary>
/// A concrete official build. Direct JAR sources include their download integrity metadata.
/// Installer sources deliberately leave integrity fields empty when the first-party Maven
/// checksum and response size are resolved by the existing verified installer downloader.
/// </summary>
public sealed record OfficialServerCoreBuildInfo(
    CoreType CoreType,
    string DisplayName,
    string MinecraftVersion,
    string ProductVersion,
    string? LoaderVersion,
    string BuildVersion,
    OfficialServerInstallStrategy InstallStrategy,
    bool IsStable,
    Uri? DownloadUri,
    string? FileName,
    long? Size,
    string? HashAlgorithm,
    string? Hash);
