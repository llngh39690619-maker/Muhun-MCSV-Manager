namespace MinecraftServerManager.Updater;

internal static class ProductSemanticVersion
{
    public static int Compare(string left, string right)
        => Parse(left).CompareTo(Parse(right));

    private static Value Parse(string value)
    {
        ProductUpdateManifestParser.ValidateVersion(value);
        var components = value.Split('-', 2);
        var numbers = components[0].Split('.');
        return new Value(
            numbers[0],
            numbers[1],
            numbers[2],
            components.Length == 2 ? components[1].Split('.') : null);
    }

    private sealed record Value(
        string Major,
        string Minor,
        string Patch,
        IReadOnlyList<string>? Prerelease) : IComparable<Value>
    {
        public int CompareTo(Value? other)
        {
            if (other is null)
            {
                return 1;
            }

            var comparison = CompareNumeric(Major, other.Major);
            if (comparison == 0) comparison = CompareNumeric(Minor, other.Minor);
            if (comparison == 0) comparison = CompareNumeric(Patch, other.Patch);
            if (comparison != 0) return comparison;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                comparison = ComparePrereleaseIdentifier(Prerelease[index], other.Prerelease[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            if (leftNumeric && rightNumeric) return CompareNumeric(left, right);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            return StringComparer.Ordinal.Compare(left, right);
        }

        private static int CompareNumeric(string left, string right)
        {
            left = left.TrimStart('0');
            right = right.TrimStart('0');
            if (left.Length == 0) left = "0";
            if (right.Length == 0) right = "0";
            var comparison = left.Length.CompareTo(right.Length);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
