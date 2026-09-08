using BridgeManager.Core.Protocol;

namespace BridgeManager.Core.FirmwareModules;

public abstract class BridgeFirmwareModuleBase : IBridgeFirmwareModule
{
    private static readonly IReadOnlyDictionary<string, BridgeModuleOperationDefinition>
        NoOperations = new Dictionary<string, BridgeModuleOperationDefinition>();

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string BoardFamily { get; }
    public abstract string Description { get; }
    public virtual string ModuleVersion => "builtin";
    public virtual int RuntimeApiVersion => BridgeFirmwareModuleRegistry.RuntimeApiVersion;
    public virtual string? PackageRoot => null;
    public virtual int Priority => 0;
    public abstract BridgeCapability Capabilities { get; }
    public virtual IReadOnlyList<BridgeBoardDefinition> Boards => [];
    public virtual IReadOnlyList<BridgeFirmwareDefinition> Firmware => [];
    public virtual IReadOnlyList<BridgeDeviceProfile> HidDevices => [];
    public virtual IReadOnlyList<BridgeUsbRoleOption> UsbRoles => [];
    public virtual IReadOnlyList<BridgeInputSourceOption> InputSources => [];
    public virtual IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers => [];
    public virtual IReadOnlyList<string> StatusCommands => [PrimaryStatusCommand];
    public virtual IReadOnlyList<string> SelfTestCommands => StatusCommands;
    public virtual string PrimaryStatusCommand => ManagerCommands.Status;
    public virtual string InputStatusCommand => ManagerCommands.BridgeInput;
    public virtual string? SaveSettingsCommand => null;
    public virtual IReadOnlyDictionary<string, BridgeModuleOperationDefinition> Operations =>
        NoOperations;
    public virtual IReadOnlyList<BridgeModulePageDefinition> Pages => [];

    public abstract bool Matches(DeviceDescriptor descriptor);

    public virtual IReadOnlyList<string> BuildSettingsCommands(
        BridgeModuleSettings settings) => [];

    public virtual string? BuildRumbleCommand(string target) => null;

    protected static BridgeUsbRoleOption Role(BridgeUsbRole role,
                                               string description) =>
        new(role, role.DisplayName(), description, ManagerCommands.SetRole(role));

    protected static BridgeInputSourceOption Input(BridgeInputPreference input,
                                                    string displayName,
                                                    string description) =>
        new(input, displayName, description, ManagerCommands.SetInput(input));
}

public sealed class Sf32UnifiedFirmwareModule : BridgeFirmwareModuleBase
{
    private static readonly IReadOnlyList<BridgeBoardDefinition> KnownBoards =
    [
        new("sf32lb52-devkit-nano", "SF32LB52-DevKit-Nano", "SF32LB52",
            "当前实测开发板，USB 设备口配合板载下载串口。")
    ];

    public override string Id => "sf32-unified";
    public override string DisplayName => "SF32 Unified Bridge";
    public override string BoardFamily => "SiFli SF32LB52";
    public override string Description =>
        "SF32LB52 上统一接收 DualSense 与 Nintendo NS2Pro。";
    public override int Priority => 100;
    public override BridgeCapability Capabilities =>
        BridgeCapability.DeviceStatus |
        BridgeCapability.LiveInput |
        BridgeCapability.UsbRoleSelection |
        BridgeCapability.InputSourceSelection |
        BridgeCapability.WirelessControllers |
        BridgeCapability.Rumble |
        BridgeCapability.UsbAudio |
        BridgeCapability.Motion |
        BridgeCapability.Settings |
        BridgeCapability.SelfTest |
        BridgeCapability.RawCommands |
        BridgeCapability.FirmwareFlashing;
    public override IReadOnlyList<BridgeBoardDefinition> Boards => KnownBoards;
    public override IReadOnlyList<BridgeFirmwareDefinition> Firmware =>
    [
        new("sf32-unified-current", "SF32 Unified Bridge", "开发版",
            "DS5 Classic、NS2Pro BLE、震动与 USB Audio。",
            ["sf32lb52-devkit-nano"],
            FirmwareFlashMethod.SifliSerial,
            "modules/sf32-unified/artifacts/sftool_param.json",
            "使用板载下载串口和 sftool 烧录；烧录后重新插拔 USB 设备口。")
    ];
    public override IReadOnlyList<BridgeDeviceProfile> HidDevices =>
    [
        BridgeDeviceProfiles.SonyDualSense,
        BridgeDeviceProfiles.NintendoNs2Pro with
        {
            ManagerSerialNumber = "HA2F83JI",
            Tags = ["sf32lb52", "ns2pro"]
        }
    ];
    public override IReadOnlyList<BridgeUsbRoleOption> UsbRoles =>
    [
        Role(BridgeUsbRole.DualSense, "音频、震动与触觉"),
        Role(BridgeUsbRole.NintendoNs2Pro, "原生 Nintendo 身份")
    ];
    public override IReadOnlyList<BridgeInputSourceOption> InputSources =>
    [
        Input(BridgeInputPreference.Auto, "自动选择", "优先使用已连接控制器"),
        Input(BridgeInputPreference.DualSense, "仅使用 DualSense", "Bluetooth Classic HID"),
        Input(BridgeInputPreference.NintendoNs2Pro, "仅使用 NS2Pro", "Bluetooth LE GATT")
    ];
    public override IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers =>
    [
        new("ds5", "DualSense / DS5", "Bluetooth Classic",
            ManagerCommands.Pair("ds5"), ManagerCommands.Connect("ds5"),
            ManagerCommands.Disconnect("ds5"), ManagerCommands.Forget("ds5")),
        new("ns2", "Nintendo NS2Pro", "Bluetooth LE",
            ManagerCommands.Pair("ns2"), ManagerCommands.Connect("ns2"),
            ManagerCommands.Disconnect("ns2"), ManagerCommands.Forget("ns2"))
    ];
    public override IReadOnlyList<string> StatusCommands =>
    [
        ManagerCommands.BridgeStatus,
        ManagerCommands.UsbStatus,
        ManagerCommands.Settings,
        ManagerCommands.RumbleStatus
    ];
    public override IReadOnlyList<string> SelfTestCommands =>
    [
        ManagerCommands.BridgeStatus,
        ManagerCommands.UsbStatus,
        ManagerCommands.Settings,
        ManagerCommands.RumbleStatus,
        ManagerCommands.BridgeInput,
        ManagerCommands.MotionStatus,
        ManagerCommands.Ns2Status
    ];
    public override string PrimaryStatusCommand => ManagerCommands.BridgeStatus;
    public override string InputStatusCommand => ManagerCommands.BridgeInput;
    public override string? SaveSettingsCommand => ManagerCommands.SaveSettings;

    public override bool Matches(DeviceDescriptor descriptor)
    {
        if (descriptor.TransportKind == DeviceTransportKind.Serial)
        {
            return descriptor.Platform.Contains("SF32LB52",
                StringComparison.OrdinalIgnoreCase);
        }

        if (descriptor.ProfileKey == "dualsense" &&
            string.Equals(descriptor.SerialNumber, "DualSense HID",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return descriptor.ProfileKey == "ns2pro-nintendo" &&
            string.Equals(descriptor.SerialNumber, "HA2F83JI",
                          StringComparison.OrdinalIgnoreCase);
    }

    public override IReadOnlyList<string> BuildSettingsCommands(
        BridgeModuleSettings settings) =>
    [
        ManagerCommands.RumbleTune(settings.RumbleScalePercent,
            settings.RumbleHoldMs, settings.RumbleTickMs,
            settings.RumbleStopPackets),
        ManagerCommands.UsbRate(settings.ReportRateHz),
        ManagerCommands.UsbRaw(settings.UsbRawPassthrough),
        ManagerCommands.WebParse(settings.LiveParsing)
    ];

    public override string? BuildRumbleCommand(string target) =>
        ManagerCommands.RumbleTest(target);
}

public sealed class Esp32S3Ns2BridgeFirmwareModule : BridgeFirmwareModuleBase
{
    private static readonly IReadOnlyList<BridgeBoardDefinition> KnownBoards =
    [
        new("esp32s3-n16r8", "ESP32-S3 N16R8", "ESP32-S3",
            "ESP32-S3 原生 USB 的 NS2Pro BLE 接收器。")
    ];

    public override string Id => "esp32s3-ns2-bridge";
    public override string DisplayName => "ESP32-S3 NS2Pro Bridge";
    public override string BoardFamily => "Espressif ESP32-S3";
    public override string Description =>
        "只接收 Nintendo NS2Pro，并通过原生 USB 输出 Nintendo HID。";
    public override int Priority => 95;
    public override BridgeCapability Capabilities =>
        BridgeCapability.DeviceStatus |
        BridgeCapability.LiveInput |
        BridgeCapability.WirelessControllers |
        BridgeCapability.Rumble |
        BridgeCapability.Motion |
        BridgeCapability.Settings |
        BridgeCapability.SelfTest |
        BridgeCapability.RawCommands;
    public override IReadOnlyList<BridgeBoardDefinition> Boards => KnownBoards;
    public override IReadOnlyList<BridgeFirmwareDefinition> Firmware =>
    [
        new("esp32s3-ns2-current", "ESP32-S3 NS2Pro Bridge", "0.1.0-dev",
            "NS2Pro BLE、Nintendo HID、输入、IMU 与 HD Rumble。",
            ["esp32s3-n16r8"], FirmwareFlashMethod.None, null,
            "当前模块只负责管理与识别；固件使用 ESP-IDF/esptool 烧录。")
    ];
    public override IReadOnlyList<BridgeDeviceProfile> HidDevices =>
        [BridgeDeviceProfiles.NintendoNs2Pro with
        {
            ManagerSerialNumber = "NS2BRIDGE-S3-N-MGR2",
            Tags = ["esp32s3", "ns2pro"]
        }];
    public override IReadOnlyList<BridgeUsbRoleOption> UsbRoles =>
        [Role(BridgeUsbRole.NintendoNs2Pro, "原生 Nintendo 身份")];
    public override IReadOnlyList<BridgeInputSourceOption> InputSources =>
        [Input(BridgeInputPreference.NintendoNs2Pro, "仅使用 NS2Pro", "Bluetooth LE")];
    public override IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers =>
    [
        new("ns2", "Nintendo NS2Pro", "Bluetooth LE",
            "ns2 pair", "ns2 reconnect", "ns2 disconnect", "ns2 forget")
    ];
    public override IReadOnlyList<string> StatusCommands =>
        ["status", "usb status", "settings", "motion status"];
    public override IReadOnlyList<string> SelfTestCommands =>
        ["status", "usb status", "settings", "motion status", "rumble"];
    public override string PrimaryStatusCommand => "status";
    public override string InputStatusCommand => "input status";
    public override string? SaveSettingsCommand => "settings save";

    public override bool Matches(DeviceDescriptor descriptor)
    {
        return descriptor.ProfileKey == "ns2pro-nintendo" &&
            string.Equals(descriptor.SerialNumber, "NS2BRIDGE-S3-N-MGR2",
                StringComparison.OrdinalIgnoreCase);
    }

    public override IReadOnlyList<string> BuildSettingsCommands(
        BridgeModuleSettings settings) =>
    [
        ManagerCommands.RumbleTune(settings.RumbleScalePercent,
            settings.RumbleHoldMs, settings.RumbleTickMs,
            settings.RumbleStopPackets),
        ManagerCommands.UsbRate(settings.ReportRateHz),
        ManagerCommands.UsbRaw(settings.UsbRawPassthrough),
        ManagerCommands.WebParse(settings.LiveParsing)
    ];

    public override string? BuildRumbleCommand(string target) =>
        ManagerCommands.RumbleTest(target);
}

public sealed class PicoUnifiedFirmwareModule : BridgeFirmwareModuleBase
{
    private static readonly IReadOnlyList<BridgeBoardDefinition> KnownBoards =
    [
        new("pico-2-w", "Raspberry Pi Pico 2 W", "RP2350",
            "原 DS5Dongle 与自动 NS2Pro/DS5 桥接目标。")
    ];

    public override string Id => "pico-unified-bridge";
    public override string DisplayName => "Pico 2 W Controller Bridge";
    public override string BoardFamily => "Raspberry Pi Pico 2 W";
    public override string Description =>
        "Pico 2 W 上统一接收 DS5 与 NS2Pro，并切换对应 USB 身份。";
    public override int Priority => 90;
    public override BridgeCapability Capabilities =>
        BridgeCapability.DeviceStatus |
        BridgeCapability.LiveInput |
        BridgeCapability.UsbRoleSelection |
        BridgeCapability.InputSourceSelection |
        BridgeCapability.WirelessControllers |
        BridgeCapability.Rumble |
        BridgeCapability.Motion |
        BridgeCapability.UsbAudio |
        BridgeCapability.Settings |
        BridgeCapability.SelfTest |
        BridgeCapability.RawCommands |
        BridgeCapability.FirmwareFlashing;
    public override IReadOnlyList<BridgeBoardDefinition> Boards => KnownBoards;
    public override IReadOnlyList<BridgeFirmwareDefinition> Firmware =>
    [
        new("pico-unified-current", "Pico 2 W Controller Bridge", "0.1.0",
            "自动识别 DualSense/NS2Pro，并切换对应 USB 身份。",
            ["pico-2-w"], FirmwareFlashMethod.PicoUf2,
            "modules/pico-unified-bridge/artifacts/pico-controller-bridge-0.1.uf2",
            "按住 BOOTSEL 接入电脑，然后将 UF2 写入 RPI-RP2 盘。")
    ];
    public override IReadOnlyList<BridgeDeviceProfile> HidDevices =>
        [BridgeDeviceProfiles.PicoAutoManager,
         BridgeDeviceProfiles.PicoDualSense];
    public override IReadOnlyList<BridgeUsbRoleOption> UsbRoles =>
    [
        Role(BridgeUsbRole.DualSense, "DualSense / DS5 身份"),
        Role(BridgeUsbRole.NintendoNs2Pro, "Nintendo NS2Pro 身份")
    ];
    public override IReadOnlyList<BridgeInputSourceOption> InputSources =>
    [
        Input(BridgeInputPreference.Auto, "自动选择", "优先使用已连接控制器"),
        Input(BridgeInputPreference.DualSense, "仅使用 DualSense", "Bluetooth Classic HID"),
        Input(BridgeInputPreference.NintendoNs2Pro, "仅使用 NS2Pro", "Bluetooth LE")
    ];
    public override IReadOnlyList<BridgeWirelessControllerOption> WirelessControllers =>
    [
        new("ds5", "DualSense / DS5", "Bluetooth Classic",
            "ds5 pair", "ds5 reconnect", "ds5 disconnect", "ds5 forget"),
        new("ns2", "Nintendo NS2Pro", "Bluetooth LE",
            "ns2 pair", "ns2 reconnect", "ns2 disconnect", "ns2 forget")
    ];
    public override IReadOnlyList<string> StatusCommands =>
        ["status", "usb status"];
    public override IReadOnlyList<string> SelfTestCommands =>
        ["status", "usb status", "input status", "motion status", "ds5 status"];
    public override string PrimaryStatusCommand => "status";
    public override string InputStatusCommand => "input status";
    public override string? SaveSettingsCommand => "settings save";

    public override bool Matches(DeviceDescriptor descriptor) =>
        descriptor.ProfileKey is "pico-auto-manager" or "pico-dualsense" ||
        (descriptor.ProfileKey == "ns2pro-nintendo" &&
         string.Equals(descriptor.SerialNumber, "HA2F83JF",
             StringComparison.OrdinalIgnoreCase));

    public override IReadOnlyList<string> BuildSettingsCommands(
        BridgeModuleSettings settings) =>
    [
        ManagerCommands.RumbleTune(settings.RumbleScalePercent,
            settings.RumbleHoldMs, settings.RumbleTickMs,
            settings.RumbleStopPackets),
        ManagerCommands.UsbRate(settings.ReportRateHz),
        ManagerCommands.UsbRaw(settings.UsbRawPassthrough),
        ManagerCommands.WebParse(settings.LiveParsing)
    ];

    public override string? BuildRumbleCommand(string target) =>
        ManagerCommands.RumbleTest(target);
}

public sealed class GenericManagerFirmwareModule : BridgeFirmwareModuleBase
{
    public override string Id => "generic-manager";
    public override string DisplayName => "通用 Manager 固件";
    public override string BoardFamily => "未知板卡";
    public override string Description =>
        "未匹配专用模块，仅开放状态读取和原始命令。";
    public override int Priority => int.MinValue;
    public override BridgeCapability Capabilities =>
        BridgeCapability.DeviceStatus | BridgeCapability.RawCommands;
    public override bool Matches(DeviceDescriptor descriptor) => true;
}
