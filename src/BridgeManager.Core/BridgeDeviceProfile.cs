namespace BridgeManager.Core;

public sealed record BridgeDeviceProfile(
    string Key,
    string DisplayName,
    ushort VendorId,
    ushort ProductId,
    ushort ManagerUsagePage,
    ushort ManagerUsageId,
    byte ManagerFeatureReportId,
    byte? CommandOutputReportId,
    BridgeUsbRole UsbRole,
    string? ManagerSerialNumber,
    string? LegacyManagerProductName,
    string[] Tags)
{
    public bool Matches(ushort vendorId, ushort productId) =>
        VendorId == vendorId && ProductId == productId;

    public bool SupportsInputReports { get; init; }

    public bool MatchesManagerIdentity(string? serialNumber,
                                       string? productName) =>
        (ManagerSerialNumber is not null &&
         string.Equals(ManagerSerialNumber, serialNumber,
                       StringComparison.Ordinal)) ||
        (LegacyManagerProductName is not null &&
         string.Equals(LegacyManagerProductName, productName,
                       StringComparison.Ordinal)) ||
        (ManagerSerialNumber is null && LegacyManagerProductName is null);

}

public static class BridgeHidUsages
{
    public const ushort GenericDesktopPage = 0x0001;
    public const ushort Gamepad = 0x0005;
    public const ushort VendorDefinedPage = 0xff00;
    public const ushort Manager = 0x0001;
}

public static class BridgeDeviceProfiles
{
    public static readonly BridgeDeviceProfile PicoAutoManager = new(
        "pico-auto-manager",
        "Pico 2 W Controller Bridge Manager",
        0xcafe,
        0x4012,
        BridgeHidUsages.VendorDefinedPage,
        BridgeHidUsages.Manager,
        ManagerProtocol.FeatureReportId,
        null,
        BridgeUsbRole.Unknown,
        null,
        null,
        new[] { "pico2w", "unified", "ds5", "ns2pro" })
        { SupportsInputReports = true };

    public static readonly BridgeDeviceProfile MicrosoftXbox360 = new(
        "sf32lb52-xbox360",
        "SF32LB52 Xbox 360 Manager",
        0x045e,
        0x028e,
        BridgeHidUsages.VendorDefinedPage,
        BridgeHidUsages.Manager,
        ManagerProtocol.FeatureReportId,
        null,
        BridgeUsbRole.Xbox,
        null,
        null,
        new[] { "sf32lb52", "xbox360", "xinput" });

    public static readonly BridgeDeviceProfile SonyDualSense = new(
        "dualsense",
        "DualSense Manager",
        0x054c,
        0x0ce6,
        BridgeHidUsages.GenericDesktopPage,
        BridgeHidUsages.Gamepad,
        ManagerProtocol.DualSenseFeatureReportId,
        null,
        BridgeUsbRole.DualSense,
        "DualSense HID",
        "DualSense HID",
        new[] { "sf32lb52", "ds5", "dualsense" })
        { SupportsInputReports = true };

    public static readonly BridgeDeviceProfile SonyDualSenseEdge = new(
        "dualsense-edge",
        "DualSense Edge Manager",
        0x054c,
        0x0df2,
        BridgeHidUsages.GenericDesktopPage,
        BridgeHidUsages.Gamepad,
        ManagerProtocol.DualSenseFeatureReportId,
        null,
        BridgeUsbRole.DualSenseEdge,
        "DualSense Edge HID",
        "DualSense Edge HID",
        new[] { "sf32lb52", "ds5", "dse", "dualsense-edge" })
        { SupportsInputReports = true };

    public static readonly BridgeDeviceProfile PicoDualSense = new(
        "pico-dualsense",
        "Pico 2 W DualSense Manager",
        0x054c,
        0x0ce6,
        BridgeHidUsages.GenericDesktopPage,
        BridgeHidUsages.Gamepad,
        ManagerProtocol.DualSenseFeatureReportId,
        null,
        BridgeUsbRole.DualSense,
        null,
        "Pico 2 W DualSense HID",
        new[] { "pico2w", "ds5", "dualsense" })
        { SupportsInputReports = true };

    public static readonly BridgeDeviceProfile NintendoNs2Pro = new(
        "ns2pro-nintendo",
        "NS2Pro Nintendo HID Manager",
        0x057e,
        0x2069,
        BridgeHidUsages.VendorDefinedPage,
        BridgeHidUsages.Manager,
        ManagerProtocol.FeatureReportId,
        ManagerProtocol.NintendoCommandOutputReportId,
        BridgeUsbRole.NintendoNs2Pro,
        null,
        null,
        new[] { "ns2pro", "pico2w", "sf32lb52" })
        { SupportsInputReports = true };

    public static readonly BridgeDeviceProfile Bl616Xbox360 = MicrosoftXbox360 with
    {
        Key = "bl616-xbox360",
        DisplayName = "BL616 Xbox 360 Manager",
        ManagerSerialNumber = "BL616X360",
        Tags = ["bl616", "xbox360", "xinput"]
    };

    public static readonly BridgeDeviceProfile Bl616DualSense = SonyDualSense with
    {
        Key = "bl616-dualsense",
        DisplayName = "BL616 DualSense Manager",
        ManagerSerialNumber = "ControllerBridge BL616",
        LegacyManagerProductName = null,
        Tags = ["bl616", "ds5", "dualsense"]
    };

    public static readonly BridgeDeviceProfile Bl616DualSenseEdge = SonyDualSenseEdge with
    {
        Key = "bl616-dualsense-edge",
        DisplayName = "BL616 DualSense Edge Manager",
        ManagerSerialNumber = "ControllerBridge BL616 Edge",
        LegacyManagerProductName = null,
        Tags = ["bl616", "ds5", "dse", "dualsense-edge"]
    };

    public static readonly BridgeDeviceProfile Bl616NintendoNs2Pro = NintendoNs2Pro with
    {
        Key = "bl616-ns2pro-nintendo",
        DisplayName = "BL616 NS2Pro Manager",
        ManagerSerialNumber = "CB616NS2-0002",
        Tags = ["bl616", "ns2pro"]
    };

    public static IReadOnlyList<BridgeDeviceProfile> All { get; } =
    new[]
    {
        PicoAutoManager,
        MicrosoftXbox360,
        SonyDualSense,
        SonyDualSenseEdge,
        PicoDualSense,
        NintendoNs2Pro,
        Bl616Xbox360,
        Bl616DualSense,
        Bl616DualSenseEdge,
        Bl616NintendoNs2Pro
    };

    public static BridgeDeviceProfile? Find(ushort vendorId, ushort productId) =>
        All.FirstOrDefault(profile => profile.Matches(vendorId, productId));
}
