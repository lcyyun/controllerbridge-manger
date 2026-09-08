using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace BridgeManager.Core.Transports;

public sealed class SerialBridgeTransport : IDeviceTransport
{
    private const int BaudRate = 1_000_000;
    private readonly object _sync = new();
    private SerialPort? _port;
    private byte[] _pendingReply = Array.Empty<byte>();
    private int _replyOffset;

    public SerialBridgeTransport(DeviceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public DeviceDescriptor Descriptor { get; }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = new SerialPort(Descriptor.Id, BaudRate, Parity.None, 8, StopBits.One)
        {
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 250,
            WriteTimeout = 1000,
            NewLine = "\n"
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        _port = port;
        return Task.CompletedTask;
    }

    public Task WriteFeatureReportAsync(byte reportId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        ExecuteAsync(DecodeCommand(payload.Span), cancellationToken);

    public Task WriteOutputReportAsync(byte reportId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        ExecuteAsync(DecodeCommand(payload.Span), cancellationToken);

    public Task<byte[]> ReadFeatureReportAsync(byte reportId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var report = new byte[ManagerProtocol.FeatureReportLength];
            report[0] = reportId;
            Encoding.ASCII.GetBytes(ManagerProtocol.ReplyMagic).CopyTo(report, 1);
            var total = _pendingReply.Length;
            var chunkLength = Math.Min(total - _replyOffset, report.Length - 12);
            report[7] = (byte)(total & 0xff);
            report[8] = (byte)(total >> 8);
            report[9] = (byte)(_replyOffset & 0xff);
            report[10] = (byte)(_replyOffset >> 8);
            report[11] = (byte)chunkLength;
            if (chunkLength > 0)
            {
                Array.Copy(_pendingReply, _replyOffset, report, 12, chunkLength);
                _replyOffset += chunkLength;
            }
            return Task.FromResult(report);
        }
    }

    public ValueTask DisposeAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            port.Close();
            port.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private Task ExecuteAsync(string command, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = _port ?? throw new InvalidOperationException("Serial device is not open.");
        port.DiscardInBuffer();
        port.WriteLine($"sf32_mgr {command}");

        byte[]? reply = null;
        bool[]? received = null;
        var receivedCount = 0;
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line;
            try
            {
                line = port.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                continue;
            }
            if (!line.StartsWith("SF32MGR:", StringComparison.Ordinal))
            {
                continue;
            }
            var fields = line.Split(':', 4);
            if (fields.Length != 4 ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                total < 0 || offset < 0 || offset > total || (fields[3].Length & 1) != 0)
            {
                continue;
            }
            reply ??= new byte[total];
            received ??= new bool[total];
            if (reply.Length != total)
            {
                throw new InvalidDataException("Manager reply length changed during transfer.");
            }
            var bytes = Convert.FromHexString(fields[3]);
            if (offset + bytes.Length > total)
            {
                throw new InvalidDataException("Manager reply segment exceeds declared length.");
            }
            bytes.CopyTo(reply, offset);
            for (var index = 0; index < bytes.Length; index++)
            {
                if (!received[offset + index])
                {
                    received[offset + index] = true;
                    receivedCount++;
                }
            }
            if (receivedCount >= total)
            {
                lock (_sync)
                {
                    _pendingReply = reply;
                    _replyOffset = 0;
                }
                return;
            }
        }
        throw new TimeoutException($"No complete SF32LB52 manager reply received from {Descriptor.Id}.");
    }, cancellationToken);

    private static string DecodeCommand(ReadOnlySpan<byte> report)
    {
        var offset = report.Length > 0 && report[0] == ManagerProtocol.FeatureReportId ? 1 : 0;
        var magic = Encoding.ASCII.GetBytes(ManagerProtocol.SetMagic);
        if (report.Length - offset < magic.Length || !report.Slice(offset, magic.Length).SequenceEqual(magic))
        {
            throw new InvalidDataException("Invalid manager command framing.");
        }
        var command = report.Slice(offset + magic.Length);
        var zero = command.IndexOf((byte)0);
        if (zero >= 0)
        {
            command = command[..zero];
        }
        return Encoding.UTF8.GetString(command).Trim();
    }
}
