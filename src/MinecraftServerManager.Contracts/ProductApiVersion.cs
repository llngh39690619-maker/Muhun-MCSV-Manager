using System.Text.Json.Serialization;

namespace MinecraftServerManager.Contracts;

public readonly record struct ProductApiVersion : IComparable<ProductApiVersion>
{
    [JsonConstructor]
    public ProductApiVersion(int major, int minor)
    {
        if (major < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "API major version must be positive.");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), "API minor version cannot be negative.");
        }

        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public int CompareTo(ProductApiVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    public override string ToString() => $"{Major}.{Minor}";
}

public enum ProductApiNegotiationStatus
{
    Compatible,
    ClientTooOld,
    ClientTooNew,
}

public sealed record ProductApiNegotiationResult(
    ProductApiNegotiationStatus Status,
    ProductApiVersion? SelectedVersion,
    ProductApiVersion MinimumSupportedVersion,
    ProductApiVersion CurrentVersion)
{
    public bool IsCompatible => Status == ProductApiNegotiationStatus.Compatible;
}

public static class ProductApiProtocol
{
    public static ProductApiVersion MinimumSupportedVersion { get; } = new(1, 0);

    public static ProductApiVersion CurrentVersion { get; } = new(1, 5);

    public const string RestBasePath = "/api/v1";

    public const string IpcPackage = "muhun.mcsv.ipc.v1";

    public static ProductApiNegotiationResult Negotiate(
        ProductApiVersion clientMinimum,
        ProductApiVersion clientMaximum)
    {
        if (clientMinimum.CompareTo(clientMaximum) > 0)
        {
            throw new ArgumentException("Client minimum API version cannot exceed its maximum version.");
        }

        if (clientMaximum.CompareTo(MinimumSupportedVersion) < 0)
        {
            return new ProductApiNegotiationResult(
                ProductApiNegotiationStatus.ClientTooOld,
                null,
                MinimumSupportedVersion,
                CurrentVersion);
        }

        if (clientMinimum.CompareTo(CurrentVersion) > 0)
        {
            return new ProductApiNegotiationResult(
                ProductApiNegotiationStatus.ClientTooNew,
                null,
                MinimumSupportedVersion,
                CurrentVersion);
        }

        var selected = clientMaximum.CompareTo(CurrentVersion) < 0
            ? clientMaximum
            : CurrentVersion;
        var minimum = clientMinimum.CompareTo(MinimumSupportedVersion) > 0
            ? clientMinimum
            : MinimumSupportedVersion;
        if (selected.Major != CurrentVersion.Major || selected.CompareTo(minimum) < 0)
        {
            return new ProductApiNegotiationResult(
                clientMinimum.CompareTo(CurrentVersion) > 0
                    ? ProductApiNegotiationStatus.ClientTooNew
                    : ProductApiNegotiationStatus.ClientTooOld,
                null,
                MinimumSupportedVersion,
                CurrentVersion);
        }

        return new ProductApiNegotiationResult(
            ProductApiNegotiationStatus.Compatible,
            selected,
            MinimumSupportedVersion,
            CurrentVersion);
    }
}

public sealed record ProductHandshakeResponse(
    string Product,
    string ProductVersion,
    ProductApiVersion ApiVersion,
    ProductApiVersion MinimumApiVersion,
    bool Ready);
