using System.Globalization;

namespace BridgeManager.Core.Transports;

// Custom IDs remain a process-local lab opt-in. The separately declared,
// serial-gated FFFE:CB16 development-preview profile is the only bundled exception.
internal static class Bl616XInputExperiment
{
    internal const string EnvironmentKey = "CONTROLLERBRIDGE_BL616_XINPUT_TEST_ID";

    internal static BridgeDeviceProfile? ParseProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 2 || parts.Any(part => part.Length != 4) ||
            !ushort.TryParse(parts[0], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var vid) ||
            !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var pid) ||
            vid is 0 or ushort.MaxValue || pid is 0 or ushort.MaxValue ||
            (vid == 0x045e && pid == 0x028e))
            throw new ArgumentException($"{EnvironmentKey} must be an explicit lab VID:PID, e.g. FFFE:CB16; this is not a production USB allocation.");

        return BridgeDeviceProfiles.Bl616Xbox360 with
        {
            VendorId = vid,
            ProductId = pid,
            DisplayName = "BL616 XInput Experiment Manager",
            ManagerSerialNumber = "CB616XINPUT-TEST-0001",
            LegacyManagerProductName = null,
            SupportsInputReports = false
        };
    }

    internal static IEnumerable<BridgeDeviceProfile> Append(
        IEnumerable<BridgeDeviceProfile> profiles, string? value)
    {
        var experiment = ParseProfile(value);
        return experiment is null ? profiles : profiles.Append(experiment);
    }
}
