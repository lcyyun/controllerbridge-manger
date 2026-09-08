namespace BridgeManager.Core.Transports;

public interface IDeviceTransport : IAsyncDisposable
{
    DeviceDescriptor Descriptor { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task WriteFeatureReportAsync(byte reportId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    Task WriteOutputReportAsync(byte reportId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    Task<byte[]> ReadFeatureReportAsync(byte reportId, CancellationToken cancellationToken);
}

public sealed class DeviceInputReportEventArgs : EventArgs
{
    public DeviceInputReportEventArgs(byte reportId, byte[] payload)
    {
        ReportId = reportId;
        Payload = payload;
    }

    public byte ReportId { get; }
    public byte[] Payload { get; }
}

public sealed class DeviceInputReportFailureEventArgs : EventArgs
{
    public DeviceInputReportFailureEventArgs(Exception error) => Error = error;

    public Exception Error { get; }
}

public interface IInputReportSource
{
    event EventHandler<DeviceInputReportEventArgs>? InputReportReceived;
    event EventHandler<DeviceInputReportFailureEventArgs>? InputReportReadFailed;
}
