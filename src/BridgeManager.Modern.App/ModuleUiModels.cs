using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BridgeManager.Modern;

public sealed record RoleOptionItem(
    BridgeUsbRoleOption Option,
    Brush AccentBrush,
    Brush BorderBrush,
    Brush Background,
    Thickness BorderThickness);

public sealed record InputOptionItem(
    BridgeInputSourceOption Option,
    Brush BorderBrush,
    Brush Background,
    Thickness BorderThickness);

public sealed record WirelessCommandAction(string Command);

public sealed record WirelessControllerItem(
    BridgeWirelessControllerOption Option,
    string StatusText,
    Brush AccentBrush,
    WirelessCommandAction? PairAction,
    WirelessCommandAction? ConnectAction,
    WirelessCommandAction? DisconnectAction,
    WirelessCommandAction? ForgetAction)
{
    public bool CanPair => PairAction is not null;
    public bool CanConnect => ConnectAction is not null;
    public bool CanDisconnect => DisconnectAction is not null;
    public bool CanForget => ForgetAction is not null;
}

public sealed record WizardFirmwareChoice(
    IBridgeFirmwareModule Module,
    BridgeFirmwareDefinition Firmware)
{
    public FirmwareArtifactDetails Artifact { get; } =
        FirmwareFlashService.DescribeArtifact(Module, Firmware);
    public string DisplayName => Firmware.DisplayName;
    public string ModuleName => $"{Module.DisplayName} 模块 {Module.ModuleVersion}";
    public string Description => Firmware.Description;
    public string Version => Firmware.Version;
    public string Availability => Artifact.Message;
}

public sealed record ModuleListItem(
    string DisplayName,
    string VersionLabel,
    IBridgeFirmwareModule Module);

public sealed record WizardApplyRequest(
    IBridgeFirmwareModule Module,
    BridgeUsbRoleOption? Role,
    BridgeInputSourceOption? Input,
    int ReportRateHz,
    bool SaveToDevice);

public sealed record WizardApplyResult(bool Applied, string Message);

public sealed record DetectedSetupDeviceItem(
    DeviceDescriptor Descriptor,
    BridgeBoardDefinition? Board,
    string DisplayName,
    string Detail)
{
    public bool IdentifiesBoard => Board is not null;
}
