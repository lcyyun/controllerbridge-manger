namespace BridgeManager.Core.FirmwareModules;

public interface IBridgeFirmwareModule
{
    string Id { get; }
    string DisplayName { get; }
    string BoardFamily { get; }
    string Description { get; }
    string ModuleVersion { get; }
    int RuntimeApiVersion { get; }
    string? PackageRoot { get; }
    int Priority { get; }
    BridgeCapability Capabilities { get; }
    IReadOnlyList<BridgeBoardDefinition> Boards { get; }
    IReadOnlyList<BridgeFirmwareDefinition> Firmware { get; }
    IReadOnlyList<BridgeDeviceProfile> HidDevices { get; }
    IReadOnlyList<BridgeUsbRoleOption> UsbRoles { get; }
    IReadOnlyList<BridgeInputSourceOption> InputSources { get; }
    IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers { get; }
    IReadOnlyList<string> StatusCommands { get; }
    IReadOnlyList<string> SelfTestCommands { get; }
    string PrimaryStatusCommand { get; }
    string InputStatusCommand { get; }
    string? SaveSettingsCommand { get; }
    IReadOnlyDictionary<string, BridgeModuleOperationDefinition> Operations { get; }
    IReadOnlyList<BridgeModulePageDefinition> Pages { get; }

    bool Matches(DeviceDescriptor descriptor);
    IReadOnlyList<string> BuildSettingsCommands(BridgeModuleSettings settings);
    string? BuildRumbleCommand(string target);
}
