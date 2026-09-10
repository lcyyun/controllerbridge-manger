using HidSharp;

namespace BridgeManager.Core.Transports;

public sealed class WindowsHidTransportFactory : IDeviceTransportFactory
{
    private readonly Func<IReadOnlyList<BridgeDeviceProfile>> _profiles;

    public WindowsHidTransportFactory(
        Func<IReadOnlyList<BridgeDeviceProfile>>? profiles = null)
    {
        _profiles = profiles ?? (() => BridgeDeviceProfiles.All);
    }

    public Task<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken)
        => Task.Run(() => GetDevices(cancellationToken), cancellationToken);

    private IReadOnlyList<DeviceDescriptor> GetDevices(CancellationToken cancellationToken)
    {
        var descriptors = new List<DeviceDescriptor>();
        foreach (var profile in _profiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var device in FindDevices(profile))
            {
                descriptors.Add(new DeviceDescriptor(
                    device.DevicePath,
                    GetDisplayName(device, profile),
                    profile.VendorId,
                    profile.ProductId,
                    DeviceTransportKind.Hid,
                    profile.DisplayName,
                    SupportsFeatureReports: device.GetMaxFeatureReportLength() > 0,
                    SupportsOutputReports: profile.CommandOutputReportId.HasValue &&
                        device.GetMaxOutputReportLength() > 0,
                    SupportsInputReports: profile.SupportsInputReports &&
                        device.GetMaxInputReportLength() > 0,
                    UsbRole: profile.UsbRole,
                    ProfileKey: profile.Key,
                    SerialNumber: GetSerialNumber(device),
                    ProductName: GetProductName(device),
                    ManagerFeatureReportId: profile.ManagerFeatureReportId,
                    CommandOutputReportId: profile.CommandOutputReportId));
            }
        }

        IReadOnlyList<DeviceDescriptor> result = descriptors
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return result;
    }

    public Task<IDeviceTransport> OpenAsync(DeviceDescriptor descriptor,
                                             CancellationToken cancellationToken)
        => Task.Run(() => Open(descriptor, cancellationToken), cancellationToken);

    private static IDeviceTransport Open(DeviceDescriptor descriptor,
                                          CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = DeviceList.Local
            .GetHidDevices(descriptor.VendorId, descriptor.ProductId)
            .FirstOrDefault(candidate => string.Equals(
                candidate.DevicePath, descriptor.Id,
                StringComparison.OrdinalIgnoreCase));
        if (device is null || !device.TryOpen(out HidStream? stream) || stream is null)
        {
            throw new InvalidOperationException(
                "Unable to open HID device. Close other controller tools and reconnect the adapter.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new WindowsHidTransport(descriptor, device, stream);
    }

    private static IEnumerable<HidDevice> FindDevices(BridgeDeviceProfile profile)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(
                     profile.VendorId, profile.ProductId))
        {
            bool matches;
            try
            {
                var usage = ((uint)profile.ManagerUsagePage << 16) |
                            profile.ManagerUsageId;
                var reportDescriptor = device.GetReportDescriptor();
                matches = reportDescriptor.DeviceItems.Any(
                              item => item.Usages.ContainsValue(usage)) &&
                          reportDescriptor.FeatureReports.Any(report =>
                              report.ReportID == profile.ManagerFeatureReportId &&
                              report.Length >= ManagerProtocol.FeatureReportLength) &&
                          profile.MatchesManagerIdentity(
                              profile.ManagerSerialNumber is null
                                  ? null : GetSerialNumber(device),
                              profile.LegacyManagerProductName is null
                                  ? null : GetProductName(device));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                matches = false;
            }

            if (matches)
            {
                yield return device;
            }
        }
    }

    private static string? GetSerialNumber(HidDevice device)
    {
        try
        {
            return device.GetSerialNumber();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? GetProductName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string GetDisplayName(HidDevice device,
                                         BridgeDeviceProfile profile)
    {
        try
        {
            var product = device.GetProductName();
            return string.IsNullOrWhiteSpace(product)
                ? profile.DisplayName
                : $"{profile.DisplayName} - {product}";
        }
        catch (IOException)
        {
            return profile.DisplayName;
        }
    }
}
