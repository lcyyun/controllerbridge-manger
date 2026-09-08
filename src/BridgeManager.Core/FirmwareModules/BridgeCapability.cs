namespace BridgeManager.Core.FirmwareModules;

[Flags]
public enum BridgeCapability
{
    None = 0,
    DeviceStatus = 1 << 0,
    LiveInput = 1 << 1,
    UsbRoleSelection = 1 << 2,
    InputSourceSelection = 1 << 3,
    WirelessControllers = 1 << 4,
    Rumble = 1 << 5,
    UsbAudio = 1 << 6,
    Motion = 1 << 7,
    Settings = 1 << 8,
    SelfTest = 1 << 9,
    RawCommands = 1 << 10,
    FirmwareFlashing = 1 << 11
}

public static class BridgeCapabilityExtensions
{
    public static bool Has(this BridgeCapability capabilities,
                           BridgeCapability capability) =>
        (capabilities & capability) == capability;
}
