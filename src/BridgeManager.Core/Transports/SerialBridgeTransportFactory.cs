using System.IO.Ports;

namespace BridgeManager.Core.Transports;

public sealed class SerialBridgeTransportFactory : IDeviceTransportFactory
{
    public Task<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeviceDescriptor> devices = SerialPort.GetPortNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(CreateCandidateDescriptor)
            .ToArray();
        return Task.FromResult(devices);
    }

    public static DeviceDescriptor CreateCandidateDescriptor(string portName) =>
        new(
            portName,
            $"Windows 串口候选 ({portName})",
            0,
            0,
            DeviceTransportKind.Serial,
            "Windows Serial（板型未知，仅作手动诊断/烧录候选）",
            SupportsFeatureReports: true,
            SupportsOutputReports: false,
            SupportsInputReports: false,
            DiagnosticOnly: true);

    public Task<IDeviceTransport> OpenAsync(DeviceDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IDeviceTransport>(new SerialBridgeTransport(descriptor));
    }
}
