namespace BridgeManager.Core;

public sealed record DeviceDescriptor(
    string Id,
    string DisplayName,
    ushort VendorId,
    ushort ProductId,
    DeviceTransportKind TransportKind,
    string Platform,
    bool SupportsFeatureReports,
    bool SupportsOutputReports,
    bool SupportsInputReports = false,
    BridgeUsbRole UsbRole = BridgeUsbRole.Unknown,
    bool DiagnosticOnly = false,
    string? ProfileKey = null,
    string? SerialNumber = null,
    string? ProductName = null,
    byte ManagerFeatureReportId = ManagerProtocol.FeatureReportId,
    byte? CommandOutputReportId = null)
{
    public string TransportLabel => DiagnosticOnly
        ? $"串口诊断：{Id}（无 USB 实时输入）"
        : $"USB HID：{VendorId:x4}:{ProductId:x4} · {UsbRole.DisplayName()}";
}

public enum DeviceTransportKind
{
    Hid,
    Ble,
    Wifi,
    Serial
}
