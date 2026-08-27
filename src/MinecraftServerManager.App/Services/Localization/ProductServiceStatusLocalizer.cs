using MinecraftServerManager.Client;

namespace MinecraftServerManager.App.Services.Localization;

/// <summary>
/// Keeps desktop Service-connection states on the versioned localization contract. The formatter
/// deliberately accepts only stable reason codes; exception messages and paths are never exposed.
/// </summary>
internal static class ProductServiceStatusLocalizer
{
    public static string Format(ProductServiceConnectionState state, string? reasonCode)
    {
        var localization = global::MinecraftServerManager.App.Services.LocalizationService.Current;
        return state switch
        {
            ProductServiceConnectionState.Connected => localization.Get("service.status.connected"),
            ProductServiceConnectionState.Unavailable => localization.Get("service.status.unavailable"),
            ProductServiceConnectionState.AccessDenied => localization.Get("service.status.accessDenied"),
            ProductServiceConnectionState.NotReady => localization.Get("service.status.connecting"),
            ProductServiceConnectionState.Incompatible => localization.Get("service.status.incompatible"),
            _ => localization.Get(
                "service.status.faulted",
                NormalizeReasonCode(reasonCode)),
        };
    }

    private static string NormalizeReasonCode(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return candidate.Length is > 0 and <= 80
               && candidate.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? candidate
            : "service.unknown";
    }
}
