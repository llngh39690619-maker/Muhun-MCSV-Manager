using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

public sealed record PlayerPresenceChange(string PlayerName, bool IsOnline);

/// <summary>
/// Parses passive player join/leave events without issuing a command to the server.
/// Session and stream validation remain the caller's responsibility.
/// </summary>
public static class PlayerPresenceEventParser
{
    private const int MaximumConsoleLineLength = 4_096;

    private static readonly Regex AnsiSgrPattern = new(
        @"\x1B\[[0-9;:]{0,48}m",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Vanilla and several loaders use a clock, thread, level and optional logger. Thread/logger
    // validation is deliberately performed in code so the non-backtracking regex automaton stays
    // small and adding another known core logger does not multiply the regex state space.
    private static readonly Regex ThreadedMinecraftEnvelopePattern = new(
        @"\A
          \[\d{2}:\d{2}:\d{2}(?:\.\d{3})?\][ \t]+
          \[(?<thread>[^\]\r\n]{1,128})/INFO\]
          (?:[ \t]+\[(?<logger>[^\]\r\n]{1,256})\])?
          (?:[ \t]*:[ \t]*|[ \t]+)
          (?<payload>[^\r\n]{1,1024}?)
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // Bukkit/Paper-family compact console layout.
    private static readonly Regex CompactInfoMinecraftEnvelopePattern = new(
        @"\A
          \[\d{2}:\d{2}:\d{2}(?:\.\d{3})?[ \t]+INFO\]:
          [ \t]*(?<payload>[^\r\n]{1,1024}?)[ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // Forge/NeoForge-derived Log4j layouts use ddMMMyyyy. MMM is localized by Java,
    // therefore an English-only [A-Za-z]{3} token incorrectly rejects zh-TW values such as 8月.
    // The date token is locale-neutral but bounded; the Minecraft logger remains allow-listed.
    private static readonly Regex LoaderMinecraftEnvelopePattern = new(
        @"\A
          \[[^\]\r\n\x00-\x20]{1,48}[ \t]+\d{2}:\d{2}:\d{2}\.\d{3}\][ \t]+
          \[(?<thread>[^\]\r\n]{1,128})/INFO\][ \t]+
          \[(?<logger>[^\]\r\n]{1,256})\]
          (?:[ \t]*:[ \t]*|[ \t]+)
          (?<payload>[^\r\n]{1,1024}?)
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // Very old dedicated servers used an ISO date followed by [INFO]. Keep the accepted shape
    // exact so arbitrary plugin text without a server envelope is not treated as presence data.
    private static readonly Regex LegacyMinecraftEnvelopePattern = new(
        @"\A
          \d{4}-\d{2}-\d{2}[ \t]+\d{2}:\d{2}:\d{2}[ \t]+\[INFO\]
          (?:[ \t]*:[ \t]*|[ \t]+)
          (?<payload>[^\r\n]{1,1024}?)
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    private static readonly Regex StandardMinecraftPresencePattern = new(
        @"\A
          (?<player>[A-Za-z0-9_]{1,16})[ \t]+
          (?:
            (?<online>joined[ \t]+the[ \t]+game)
            |
            (?<offline>left[ \t]+the[ \t]+game)
            |
            (?<offline>lost[ \t]+connection:[ \t]+\S[^\r\n]{0,1023})
          )
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // Minecraft/Paper 1.12.x can complete login without a separate "joined the game" line.
    // A bracketed IPv6 endpoint may itself contain ']', so the bounded endpoint deliberately
    // consumes through the final bracket immediately preceding the fixed login phrase.
    private static readonly Regex MinecraftLoggedInPattern = new(
        @"\A
          (?<player>[A-Za-z0-9_]{1,16})
          \[[^\r\n\x00-\x1F]{1,255}\][ \t]+
          logged[ \t]+in[ \t]+with[ \t]+entity[ \t]+id[ \t]+-?\d{1,10}[ \t]+
          (?:in[ \t]+world[ \t]+-?\d{1,10}[ \t]+)?
          at[ \t]+\([^\r\n\x00-\x1F]{1,512}\)
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    private static readonly Regex RenamedMinecraftJoinPattern = new(
        @"\A
          (?<player>[A-Za-z0-9_]{1,16})[ \t]+
          \(formerly[ \t]+known[ \t]+as[ \t]+[A-Za-z0-9_]{1,16}\)[ \t]+
          joined[ \t]+the[ \t]+game
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // Velocity exposes proxy-wide client sessions with [connected player]. Backend
    // [server connection] events are intentionally ignored because switching servers must not
    // remove a player from the proxy-wide roster.
    private static readonly Regex VelocityPresencePattern = new(
        @"\A
          (?:
            \[\d{2}:\d{2}:\d{2}(?:\.\d{3})?[ \t]+INFO\]:
            |
            \[\d{2}:\d{2}:\d{2}(?:\.\d{3})?\][ \t]+
            \[(?:Netty[^\]\r\n]{1,96}|main)/INFO\]
            (?:[ \t]+(?:\([^\)\r\n]{1,192}\)|\[[^\]\r\n]{1,192}\]))?
            [ \t]*:?
          )
          [ \t]*\[connected[ \t]+player\][ \t]+
          (?<player>[A-Za-z0-9_]{1,16})[ \t]+
          \([^\r\n\x00-\x1F]{1,192}\)[ \t]+has[ \t]+
          (?:(?<online>connected)|(?<offline>disconnected)(?::[ \t]+[^\r\n]{1,512})?)
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    // BungeeCord/Waterfall InitialHandler represents the client joining the proxy; an
    // UpstreamBridge disconnect represents leaving it. ServerConnector/DownstreamBridge are
    // backend switches and are deliberately ignored.
    private static readonly Regex BungeePresencePattern = new(
        @"\A
          (?:
            \d{2}:\d{2}:\d{2}(?:\.\d{3})?[ \t]+\[INFO\]
            |
            \[\d{2}:\d{2}:\d{2}(?:\.\d{3})?[ \t]+INFO\]:
          )
          [ \t]+\[(?<player>[A-Za-z0-9_]{1,16})\][ \t]+
          (?:
            <->[ \t]+InitialHandler[ \t]+has[ \t]+(?<online>connected)
            |
            ->[ \t]+UpstreamBridge[ \t]+has[ \t]+(?<offline>disconnected)
          )
          [ \t]*\z",
        RegexOptions.CultureInvariant
        | RegexOptions.IgnorePatternWhitespace
        | RegexOptions.NonBacktracking);

    public static bool TryParse(string? text, out PlayerPresenceChange change) =>
        TryParse(text, CoreType.Unknown, out change);

    public static bool TryParse(
        string? text,
        CoreType coreType,
        out PlayerPresenceChange change)
    {
        change = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumConsoleLineLength)
        {
            return false;
        }

        // Paper/Spigot and several proxy consoles can add Jansi SGR colour sequences. Most
        // output has no escape byte, so do not run a regex replacement for every ordinary log
        // line. Other control sequences remain unmatched as before.
        var normalizedText = text.Contains('\x1B')
            ? AnsiSgrPattern.Replace(text, string.Empty)
            : text;
        if (!MayContainPresenceEvent(normalizedText))
        {
            return false;
        }

        return coreType switch
        {
            CoreType.Velocity => TryParseProxy(normalizedText, VelocityPresencePattern, out change),
            CoreType.Waterfall or CoreType.BungeeCord =>
                TryParseProxy(normalizedText, BungeePresencePattern, out change),
            CoreType.Unknown or CoreType.CustomJar =>
                TryParseMinecraft(normalizedText, out change)
                || TryParseProxy(normalizedText, VelocityPresencePattern, out change)
                || TryParseProxy(normalizedText, BungeePresencePattern, out change),
            _ => TryParseMinecraft(normalizedText, out change),
        };
    }

    private static bool MayContainPresenceEvent(string text)
    {
        // These are deliberately necessary-token gates rather than stricter mini-parsers. The
        // accepted regexes allow tabs and repeated whitespace, so exact phrase checks would
        // silently reject previously valid output. False positives merely continue to the
        // existing trusted-envelope regexes; false negatives are avoided.
        return (text.Contains("joined", StringComparison.Ordinal)
                && text.Contains("game", StringComparison.Ordinal))
            || (text.Contains("left", StringComparison.Ordinal)
                && text.Contains("game", StringComparison.Ordinal))
            || (text.Contains("lost", StringComparison.Ordinal)
                && text.Contains("connection:", StringComparison.Ordinal))
            || (text.Contains("logged", StringComparison.Ordinal)
                && text.Contains("entity", StringComparison.Ordinal)
                && text.Contains("id", StringComparison.Ordinal))
            || (text.Contains("connected", StringComparison.Ordinal)
                && text.Contains("player", StringComparison.Ordinal))
            || text.Contains("InitialHandler", StringComparison.Ordinal)
            || text.Contains("UpstreamBridge", StringComparison.Ordinal);
    }

    private static bool TryParseMinecraft(string text, out PlayerPresenceChange change)
    {
        change = null!;
        if (!TryExtractMinecraftPayload(text, out var payload))
        {
            return false;
        }

        var standardMatch = StandardMinecraftPresencePattern.Match(payload);
        if (standardMatch.Success)
        {
            change = ToPresenceChange(standardMatch);
            return true;
        }

        var loggedInMatch = MinecraftLoggedInPattern.Match(payload);
        if (loggedInMatch.Success)
        {
            change = new PlayerPresenceChange(
                loggedInMatch.Groups["player"].Value,
                IsOnline: true);
            return true;
        }

        var renamedJoinMatch = RenamedMinecraftJoinPattern.Match(payload);
        if (renamedJoinMatch.Success)
        {
            change = new PlayerPresenceChange(
                renamedJoinMatch.Groups["player"].Value,
                IsOnline: true);
            return true;
        }

        return false;
    }

    private static bool TryExtractMinecraftPayload(string text, out string payload)
    {
        var threadedMatch = ThreadedMinecraftEnvelopePattern.Match(text);
        if (threadedMatch.Success
            && IsTrustedMinecraftThread(threadedMatch.Groups["thread"].Value)
            && IsTrustedMinecraftLogger(threadedMatch.Groups["logger"].Value, allowEmpty: true))
        {
            payload = threadedMatch.Groups["payload"].Value;
            return true;
        }

        var compactInfoMatch = CompactInfoMinecraftEnvelopePattern.Match(text);
        if (compactInfoMatch.Success)
        {
            payload = compactInfoMatch.Groups["payload"].Value;
            return true;
        }

        var loaderMatch = LoaderMinecraftEnvelopePattern.Match(text);
        if (loaderMatch.Success
            && IsTrustedMinecraftThread(loaderMatch.Groups["thread"].Value)
            && IsTrustedMinecraftLogger(loaderMatch.Groups["logger"].Value, allowEmpty: false))
        {
            payload = loaderMatch.Groups["payload"].Value;
            return true;
        }

        var legacyMatch = LegacyMinecraftEnvelopePattern.Match(text);
        if (legacyMatch.Success)
        {
            payload = legacyMatch.Groups["payload"].Value;
            return true;
        }

        payload = string.Empty;
        return false;
    }

    private static bool IsTrustedMinecraftThread(string thread)
    {
        if (thread is "Server thread" or "Global Region Scheduler Thread")
        {
            return true;
        }

        const string foliaPrefix = "Region Scheduler Thread #";
        if (!thread.StartsWith(foliaPrefix, StringComparison.Ordinal)
            || thread.Length <= foliaPrefix.Length
            || thread.Length > foliaPrefix.Length + 4)
        {
            return false;
        }

        return thread.AsSpan(foliaPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsTrustedMinecraftLogger(string logger, bool allowEmpty)
    {
        if (logger.Length == 0)
        {
            return allowEmpty;
        }

        var normalized = logger.EndsWith("/", StringComparison.Ordinal)
            ? logger[..^1]
            : logger;
        return normalized is
            "net.minecraft.server.MinecraftServer" or
            "net.minecraft.server.dedicated.DedicatedServer" or
            "net.minecraft.server.players.PlayerList" or
            "net.minecraft.server.management.PlayerList" or
            "net.minecraft.server.network.ServerGamePacketListenerImpl" or
            "net.minecraft.server.network.ServerConfigurationPacketListenerImpl" or
            "net.minecraft.network.NetHandlerPlayServer" or
            "minecraft/DedicatedServer" or
            "minecraft/MinecraftServer" or
            "minecraft/PlayerList" or
            "minecraft/ServerGamePacketListenerImpl" or
            "minecraft/ServerConfigurationPacketListenerImpl" or
            "minecraft/NetHandlerPlayServer";
    }

    private static bool TryParseProxy(
        string text,
        Regex pattern,
        out PlayerPresenceChange change)
    {
        var match = pattern.Match(text);
        if (!match.Success)
        {
            change = null!;
            return false;
        }

        change = ToPresenceChange(match);
        return true;
    }

    private static PlayerPresenceChange ToPresenceChange(Match match) => new(
        match.Groups["player"].Value,
        match.Groups["online"].Success);
}
