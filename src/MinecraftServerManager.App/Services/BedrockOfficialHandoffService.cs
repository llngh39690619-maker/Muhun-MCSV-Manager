using System.ComponentModel;
using System.Diagnostics;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.App.Services;

internal enum BedrockOfficialHandoffTarget
{
    Minecraft,
    // Retained for the existing UI fallback; this value is specifically the official
    // Minecraft Launcher Microsoft Store product, not an arbitrary Store URI.
    MicrosoftStore,
    MinecraftForWindowsStore,
    MinecraftPreviewStore,
}

/// <summary>
/// Hands Bedrock Edition back to the official Windows protocol handlers. X MCSV
/// deliberately does not download, unpack, or register a synthetic Bedrock instance.
/// </summary>
internal sealed class BedrockOfficialHandoffService
{
    // Minecraft's documented Bedrock deep-link handler. Keep this URI parameter-free:
    // no caller-controlled value is ever forwarded to the Windows shell.
    internal static readonly Uri MinecraftUri = new("minecraft://", UriKind.Absolute);

    // Official Microsoft Store product IDs. They are a closed compile-time allowlist and are
    // never constructed from shortcut names, version strings, URLs, or other caller input.
    internal static readonly Uri MinecraftForWindowsStoreUri = new(
        "ms-windows-store://pdp/?ProductId=9NBLGGH2JHXJ",
        UriKind.Absolute);

    internal static readonly Uri MinecraftPreviewStoreUri = new(
        "ms-windows-store://pdp/?ProductId=9P5X4QVLC2XR",
        UriKind.Absolute);

    internal static readonly Uri MinecraftLauncherStoreUri = new(
        "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5",
        UriKind.Absolute);

    // Compatibility name used by the existing protocol-to-Launcher fallback.
    internal static readonly Uri MicrosoftStoreUri = MinecraftLauncherStoreUri;

    private readonly Func<ProcessStartInfo, bool> _tryStart;

    public BedrockOfficialHandoffService()
        : this(TryStartWithWindowsShell)
    {
    }

    internal BedrockOfficialHandoffService(Func<ProcessStartInfo, bool> tryStart)
    {
        _tryStart = tryStart ?? throw new ArgumentNullException(nameof(tryStart));
    }

    public bool TryOpen(out BedrockOfficialHandoffTarget target)
    {
        if (_tryStart(CreateStartInfo(BedrockOfficialHandoffTarget.Minecraft)))
        {
            target = BedrockOfficialHandoffTarget.Minecraft;
            return true;
        }

        if (_tryStart(CreateStartInfo(BedrockOfficialHandoffTarget.MicrosoftStore)))
        {
            target = BedrockOfficialHandoffTarget.MicrosoftStore;
            return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// Opens the one fixed official Store product selected by a closed Stable/Preview enum.
    /// Display names and all other shortcut data are intentionally absent from this interface.
    /// </summary>
    public bool TryOpenStore(MinecraftBedrockChannel channel) =>
        _tryStart(CreateStoreStartInfo(channel));

    /// <summary>Opens the fixed official Minecraft Launcher Store product.</summary>
    public bool TryOpenLauncherStore() =>
        _tryStart(CreateStartInfo(BedrockOfficialHandoffTarget.MicrosoftStore));

    internal static ProcessStartInfo CreateStoreStartInfo(MinecraftBedrockChannel channel) =>
        CreateStartInfo(GetStoreTarget(channel));

    internal static BedrockOfficialHandoffTarget GetStoreTarget(MinecraftBedrockChannel channel) =>
        channel switch
        {
            MinecraftBedrockChannel.Stable =>
                BedrockOfficialHandoffTarget.MinecraftForWindowsStore,
            MinecraftBedrockChannel.Preview =>
                BedrockOfficialHandoffTarget.MinecraftPreviewStore,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };

    internal static ProcessStartInfo CreateStartInfo(BedrockOfficialHandoffTarget target)
    {
        var uri = target switch
        {
            BedrockOfficialHandoffTarget.Minecraft => MinecraftUri,
            BedrockOfficialHandoffTarget.MicrosoftStore => MinecraftLauncherStoreUri,
            BedrockOfficialHandoffTarget.MinecraftForWindowsStore =>
                MinecraftForWindowsStoreUri,
            BedrockOfficialHandoffTarget.MinecraftPreviewStore => MinecraftPreviewStoreUri,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        return new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        };
    }

    private static bool TryStartWithWindowsShell(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            // A protocol activation may be handed to an already-running packaged app and
            // legitimately return no Process object. Lack of an exception means ShellExecute
            // accepted the fixed URI; missing protocol handlers fail with Win32Exception.
            return true;
        }
        catch (Exception error) when (error is Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
