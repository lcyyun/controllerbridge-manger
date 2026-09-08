using HidSharp;

namespace BridgeManager.Core.Transports;

public sealed class WindowsHidTransport : IDeviceTransport, IInputReportSource
{
    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly CancellationTokenSource _readCancellation = new();
    private Task? _readTask;
    private bool _disposed;

    public WindowsHidTransport(DeviceDescriptor descriptor,
                               HidDevice device,
                               HidStream stream)
    {
        Descriptor = descriptor;
        _device = device;
        _stream = stream;
        _stream.ReadTimeout = Timeout.Infinite;
        _stream.WriteTimeout = 3000;
    }

    public DeviceDescriptor Descriptor { get; }
    public event EventHandler<DeviceInputReportEventArgs>? InputReportReceived;
    public event EventHandler<DeviceInputReportFailureEventArgs>? InputReportReadFailed;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Descriptor.SupportsInputReports && _readTask is null)
        {
            _readTask = Task.Run(() => ReadInputReportsAsync(_readCancellation.Token));
        }
        return Task.CompletedTask;
    }

    public Task WriteFeatureReportAsync(byte reportId,
                                        ReadOnlyMemory<byte> payload,
                                        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.SetFeature(BuildReport(
            reportId, payload.Span, _device.GetMaxFeatureReportLength(),
            "feature"));
        return Task.CompletedTask;
    }

    public async Task WriteOutputReportAsync(byte reportId,
                                             ReadOnlyMemory<byte> payload,
                                             CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var report = BuildReport(
            reportId, payload.Span, _device.GetMaxOutputReportLength(),
            "output");
        await _stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> ReadFeatureReportAsync(byte reportId,
                                               CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var length = _device.GetMaxFeatureReportLength();
        if (length <= 0)
        {
            throw new IOException("The selected HID collection has no feature report.");
        }
        var report = new byte[length];
        report[0] = reportId;
        _stream.GetFeature(report);
        return Task.FromResult(report);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _readCancellation.Cancel();
        _stream.Dispose();
        if (_readTask is not null)
        {
            try
            {
                await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or
                                       TimeoutException or IOException or
                                       ObjectDisposedException)
            {
            }
        }
        _readCancellation.Dispose();
    }

    private async Task ReadInputReportsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[_device.GetMaxInputReportLength()];
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = await _stream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (length <= 0) continue;
                var reportId = buffer[0];
                var payload = buffer.AsSpan(1, length - 1).ToArray();
                InputReportReceived?.Invoke(this,
                    new DeviceInputReportEventArgs(reportId, payload));
            }
        }
        catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            InputReportReadFailed?.Invoke(this,
                new DeviceInputReportFailureEventArgs(ex));
        }
    }

    private static byte[] BuildReport(byte reportId,
                                      ReadOnlySpan<byte> payload,
                                      int reportLength,
                                      string reportKind)
    {
        if (reportLength <= 0)
        {
            throw new IOException(
                $"The selected HID collection has no {reportKind} report.");
        }
        if (payload.Length + 1 > reportLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"The {reportKind} payload is larger than the HID report.");
        }

        var report = new byte[reportLength];
        report[0] = reportId;
        payload.CopyTo(report.AsSpan(1));
        return report;
    }
}
