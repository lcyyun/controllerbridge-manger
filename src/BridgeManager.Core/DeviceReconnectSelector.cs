namespace BridgeManager.Core;

public static class DeviceReconnectSelector
{
    public static DeviceDescriptor? Select(
        IEnumerable<DeviceDescriptor> devices,
        DeviceDescriptor previous,
        BridgeUsbRole expectedRole = BridgeUsbRole.Unknown,
        Func<DeviceDescriptor, bool>? matchesReceiver = null)
    {
        var available = devices.Where(device => matchesReceiver?.Invoke(device) != false).ToArray();
        if (previous.TransportKind == DeviceTransportKind.Serial)
        {
            return available.FirstOrDefault(device =>
                device.TransportKind == DeviceTransportKind.Serial &&
                string.Equals(device.Id, previous.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (expectedRole != BridgeUsbRole.Unknown)
        {
            var candidates = available.Where(device =>
                device.TransportKind == DeviceTransportKind.Hid &&
                device.UsbRole == expectedRole).ToArray();
            return candidates.FirstOrDefault(device =>
                       device.Id.Equals(previous.Id, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Length == 1 ? candidates[0] : null);
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
