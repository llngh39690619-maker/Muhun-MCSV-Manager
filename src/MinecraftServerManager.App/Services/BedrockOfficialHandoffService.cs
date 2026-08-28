using System.ComponentModel;
using System.Diagnostics;

namespace MinecraftServerManager.App.Services;

internal enum BedrockOfficialHandoffTarget
{
    Minecraft,
    MicrosoftStore,
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

    // Official Microsoft Store product ID for Minecraft Launcher.
    internal static readonly Uri MicrosoftStoreUri = new(
        "ms-windows-store://pdp/?ProductId=9PGW18NPBZV5",
        UriKind.Absolute);

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

    internal static ProcessStartInfo CreateStartInfo(BedrockOfficialHandoffTarget target)
    {
        var uri = target switch
        {
            BedrockOfficialHandoffTarget.Minecraft => MinecraftUri,
            BedrockOfficialHandoffTarget.MicrosoftStore => MicrosoftStoreUri,
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
