namespace BridgeManager.Core.FirmwareModules;

public static class BridgeModuleVersion
{
    public static bool IsValid(string value) => TryParse(value, out _);

    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var leftVersion) ||
            !TryParse(right, out var rightVersion))
        {
            throw new ArgumentException("Module versions must use Semantic Versioning.");
        }

        var core = leftVersion.Core.CompareTo(rightVersion.Core);
        if (core != 0)
        {
            return core;
        }
        if (leftVersion.PreRelease.Length == 0)
        {
            return rightVersion.PreRelease.Length == 0 ? 0 : 1;
        }
        if (rightVersion.PreRelease.Length == 0)
        {
            return -1;
        }

        var count = Math.Min(leftVersion.PreRelease.Length,
                             rightVersion.PreRelease.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = CompareIdentifier(leftVersion.PreRelease[index],
                                               rightVersion.PreRelease[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return leftVersion.PreRelease.Length.CompareTo(
            rightVersion.PreRelease.Length);
    }

    private static bool TryParse(string value, out ParsedVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return false;
        }

        var buildSplit = value.Split('+', 2);
        if (buildSplit.Length == 2 && !IdentifiersAreValid(
                buildSplit[1], allowLeadingZero: true))
        {
            return false;
        }
        var releaseSplit = buildSplit[0].Split('-', 2);
        var coreParts = releaseSplit[0].Split('.');
        if (coreParts.Length != 3 ||
            !TryParseCore(coreParts[0], out var major) ||
            !TryParseCore(coreParts[1], out var minor) ||
            !TryParseCore(coreParts[2], out var patch))
        {
            return false;
        }

        var preRelease = releaseSplit.Length == 2
            ? releaseSplit[1].Split('.') : [];
        if (releaseSplit.Length == 2 && !IdentifiersAreValid(
                releaseSplit[1], allowLeadingZero: false))
        {
            return false;
        }
        parsed = new ParsedVersion(new Version(major, minor, patch), preRelease);
        return true;
    }

    private static bool TryParseCore(string value, out int result)
    {
        result = 0;
        return value.Length > 0 &&
               (value == "0" || value[0] != '0') &&
               value.All(char.IsAsciiDigit) &&
               int.TryParse(value, out result);
    }

    private static bool IdentifiersAreValid(string value, bool allowLeadingZero)
    {
        var identifiers = value.Split('.');
        return identifiers.All(identifier => identifier.Length > 0 &&
            identifier.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') &&
            (allowLeadingZero || !identifier.All(char.IsAsciiDigit) ||
             identifier == "0" || identifier[0] != '0'));
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.CompareOrdinal(left, right);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.CompareOrdinal(left, right);
    }

    private readonly record struct ParsedVersion(
        Version Core,
        string[] PreRelease);
}
