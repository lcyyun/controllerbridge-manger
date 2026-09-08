namespace BridgeManager.Core.Transports;

public interface IDeviceTransportFactory
{
    Task<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<IDeviceTransport> OpenAsync(DeviceDescriptor descriptor, CancellationToken cancellationToken);
}
