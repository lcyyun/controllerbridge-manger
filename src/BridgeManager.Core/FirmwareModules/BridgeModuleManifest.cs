namespace BridgeManager.Core.FirmwareModules;

public enum FirmwareFlashMethod
{
    None,
    PicoUf2,
    SifliSerial,
    BouffaloUart
}

public sealed record BridgeBoardDefinition(
    string Id,
    string DisplayName,
    string Family,
    string Description);

public sealed record BridgeFirmwareDefinition(
    string Id,
    string DisplayName,
    string Version,
    string Description,
    IReadOnlyList<string> BoardIds,
    FirmwareFlashMethod FlashMethod,
    string? ArtifactRelativePath,
    string FlashHint);

public sealed record BridgeUsbRoleOption(
    BridgeUsbRole Role,
    string DisplayName,
    string Description,
    string Command);

public sealed record BridgeInputSourceOption(
    BridgeInputPreference Preference,
    string DisplayName,
    string Description,
    string Command);

public sealed record BridgeWirelessControllerOption(
    string Id,
    string DisplayName,
    string TransportLabel,
    string? PairCommand,
    string? ConnectCommand,
    string? DisconnectCommand,
    string? ForgetCommand);

public sealed record BridgeModuleSettings(
    int ReportRateHz,
    bool UsbRawPassthrough,
    bool LiveParsing,
    int RumbleScalePercent,
    int RumbleHoldMs,
    int RumbleTickMs,
    int RumbleStopPackets);
