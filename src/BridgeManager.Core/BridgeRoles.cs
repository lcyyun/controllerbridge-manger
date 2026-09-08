namespace BridgeManager.Core;

public enum BridgeUsbRole
{
    Unknown,
    Xbox,
    DualSense,
    DualSenseEdge,
    NintendoNs2Pro
}

public enum BridgeInputPreference
{
    Unknown,
    Auto,
    DualSense,
    NintendoNs2Pro
}

public enum BridgePhysicalInput
{
    Unknown,
    None,
    DualSense,
    NintendoNs2Pro
}

public static class BridgeRoles
{
    public static IReadOnlyList<BridgeUsbRole> UsbRoles { get; } =
        new[]
        {
            BridgeUsbRole.Xbox,
            BridgeUsbRole.DualSense,
            BridgeUsbRole.DualSenseEdge,
            BridgeUsbRole.NintendoNs2Pro
        };

    public static IReadOnlyList<BridgeInputPreference> InputPreferences { get; } =
        new[]
        {
            BridgeInputPreference.Auto,
            BridgeInputPreference.DualSense,
            BridgeInputPreference.NintendoNs2Pro
        };

    public static BridgeUsbRole ParseUsbRole(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "xbox" or "xbox360" or "xinput" => BridgeUsbRole.Xbox,
            "ds5" or "dualsense" => BridgeUsbRole.DualSense,
            "dse" or "dualsense_edge" or "dualsense-edge" => BridgeUsbRole.DualSenseEdge,
            "ns2" or "ns2pro" or "nintendo" => BridgeUsbRole.NintendoNs2Pro,
            _ => BridgeUsbRole.Unknown
        };

    public static BridgeInputPreference ParseInputPreference(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "auto" => BridgeInputPreference.Auto,
            "ds5" or "dualsense" => BridgeInputPreference.DualSense,
            "ns2" or "ns2pro" or "nintendo" => BridgeInputPreference.NintendoNs2Pro,
            _ => BridgeInputPreference.Unknown
        };

    public static BridgePhysicalInput ParsePhysicalInput(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => BridgePhysicalInput.None,
            "ds5" or "dualsense" => BridgePhysicalInput.DualSense,
            "ns2" or "ns2pro" or "nintendo" => BridgePhysicalInput.NintendoNs2Pro,
            _ => BridgePhysicalInput.Unknown
        };

    public static string CommandValue(this BridgeUsbRole role) => role switch
    {
        BridgeUsbRole.Xbox => "xbox",
        BridgeUsbRole.DualSense => "ds5",
        BridgeUsbRole.DualSenseEdge => "dse",
        BridgeUsbRole.NintendoNs2Pro => "ns2pro",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown USB role cannot be sent to firmware.")
    };

    public static string CommandValue(this BridgeInputPreference input) => input switch
    {
        BridgeInputPreference.Auto => "auto",
        BridgeInputPreference.DualSense => "ds5",
        BridgeInputPreference.NintendoNs2Pro => "ns2pro",
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown input preference cannot be sent to firmware.")
    };

    public static string DisplayName(this BridgeUsbRole role) => role switch
    {
        BridgeUsbRole.Xbox => "Xbox 360",
        BridgeUsbRole.DualSense => "DualSense / DS5",
        BridgeUsbRole.DualSenseEdge => "DualSense Edge",
        BridgeUsbRole.NintendoNs2Pro => "Nintendo NS2Pro",
        _ => "未知"
    };

    public static string DisplayName(this BridgeInputPreference input) => input switch
    {
        BridgeInputPreference.Auto => "自动",
        BridgeInputPreference.DualSense => "DS5",
        BridgeInputPreference.NintendoNs2Pro => "NS2Pro",
        _ => "未知"
    };

    public static string DisplayName(this BridgePhysicalInput input) => input switch
    {
        BridgePhysicalInput.None => "无",
        BridgePhysicalInput.DualSense => "DS5",
        BridgePhysicalInput.NintendoNs2Pro => "NS2Pro",
        _ => "未知"
    };
}
