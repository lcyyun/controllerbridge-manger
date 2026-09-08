namespace BridgeManager.Core.Transports;

public sealed class BridgeTransportFactory : IDeviceTransportFactory
{
    private readonly WindowsHidTransportFactory _hid;
    private readonly SerialBridgeTransportFactory _serial = new();

    public BridgeTransportFactory(
        Func<IReadOnlyList<BridgeDeviceProfile>>? profiles = null)
    {
        _hid = new WindowsHidTransportFactory(profiles);
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var serialTask = _serial.GetDevicesAsync(cancellationToken);
        var hidTask = _hid.GetDevicesAsync(cancellationToken);
        await Task.WhenAll(serialTask, hidTask).ConfigureAwait(false);
        return DeviceReconnectSelector.OrderForDisplay(
            hidTask.Result.Concat(serialTask.Result));
    }

    public Task<IDeviceTransport> OpenAsync(DeviceDescriptor descriptor, CancellationToken cancellationToken) =>
        descriptor.TransportKind == DeviceTransportKind.Serial
            ? _serial.OpenAsync(descriptor, cancellationToken)
            : _hid.OpenAsync(descriptor, cancellationToken);
}
