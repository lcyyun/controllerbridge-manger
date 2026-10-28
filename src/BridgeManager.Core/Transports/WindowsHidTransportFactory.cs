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
        var profiles = OrderProfilesForDiscovery(Bl616XInputExperiment.Append(
            _profiles(), Environment.GetEnvironmentVariable(Bl616XInputExperiment.EnvironmentKey))).ToArray();
        foreach (var identity in profiles.GroupBy(profile => (profile.VendorId, profile.ProductId)))
        {
            foreach (var device in DeviceList.Local.GetHidDevices(identity.Key.VendorId, identity.Key.ProductId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // One snapshot per HID collection. Previously the same collection
                    // was re-read for every profile: a transient string failure could
                    // discard the verified match and later admit a generic profile.
                    var serial = GetSerialNumber(device);
                    var product = GetProductName(device);
                    var report = device.GetReportDescriptor();
                    var profile = identity.FirstOrDefault(candidate =>
                        candidate.MatchesManagerIdentity(serial, product) &&
                        report.DeviceItems.Any(item => item.Usages.ContainsValue(
                            ((uint)candidate.ManagerUsagePage << 16) | candidate.ManagerUsageId)) &&
                        report.FeatureReports.Any(feature =>
                            feature.ReportID == candidate.ManagerFeatureReportId &&
                            feature.Length >= ManagerProtocol.FeatureReportLength));
                    if (profile is null) continue;
                    descriptors.Add(new DeviceDescriptor(
                        device.DevicePath,
                        string.IsNullOrWhiteSpace(product) ? profile.DisplayName : $"{profile.DisplayName} - {product}",
                        profile.VendorId, profile.ProductId, DeviceTransportKind.Hid, profile.DisplayName,
                        SupportsFeatureReports: device.GetMaxFeatureReportLength() > 0,
                        SupportsOutputReports: profile.CommandOutputReportId.HasValue &&
                            device.GetMaxOutputReportLength() > 0,
                        SupportsInputReports: profile.SupportsInputReports &&
                            device.GetMaxInputReportLength() > 0,
                        UsbRole: profile.UsbRole, ProfileKey: profile.Key,
                        SerialNumber: serial, ProductName: product,
                        ManagerFeatureReportId: profile.ManagerFeatureReportId,
                        CommandOutputReportId: profile.CommandOutputReportId));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Removal during enumeration: retry with a fresh snapshot later.
                }
            }
        }
        IReadOnlyList<DeviceDescriptor> result = descriptors
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return result;
    }

    // A receiver can match both an identity-specific profile and a generic
    // VID/PID profile. Deduplication keeps the first match, so prefer verified
    // serial/product identities without excluding legacy generic receivers.
    internal static IEnumerable<BridgeDeviceProfile> OrderProfilesForDiscovery(
        IEnumerable<BridgeDeviceProfile> profiles) => profiles
        .OrderByDescending(profile => profile.ManagerSerialNumber is not null)
        .ThenByDescending(profile => profile.LegacyManagerProductName is not null);

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

}
