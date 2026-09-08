namespace BridgeManager.Core;

public static class DeviceReconnectSelector
{
    public static DeviceDescriptor? Select(
        IEnumerable<DeviceDescriptor> devices,
        DeviceDescriptor previous,
        BridgeUsbRole expectedRole = BridgeUsbRole.Unknown)
    {
        var available = devices.ToArray();
        if (previous.TransportKind == DeviceTransportKind.Serial)
        {
            return available.FirstOrDefault(device =>
                device.TransportKind == DeviceTransportKind.Serial &&
                string.Equals(device.Id, previous.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (expectedRole != BridgeUsbRole.Unknown)
        {
            return available.FirstOrDefault(device =>
                device.TransportKind == DeviceTransportKind.Hid &&
                device.UsbRole == expectedRole);
        }

        return available.FirstOrDefault(device =>
                   device.TransportKind == DeviceTransportKind.Hid &&
                   string.Equals(device.Id, previous.Id, StringComparison.OrdinalIgnoreCase))
               ?? available.FirstOrDefault(device =>
                   device.TransportKind == DeviceTransportKind.Hid &&
                   device.VendorId == previous.VendorId &&
                   device.ProductId == previous.ProductId);
    }

    public static IReadOnlyList<DeviceDescriptor> OrderForDisplay(
        IEnumerable<DeviceDescriptor> devices) => devices
        .OrderBy(device => device.DiagnosticOnly)
        .ThenBy(device => device.TransportKind == DeviceTransportKind.Hid ? 0 : 1)
        .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
