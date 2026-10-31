using System.Buffers.Binary;
using System.Text;
using BridgeManager.Core;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;

internal static class Bl616OtaReplyGateTests
{
    public static void Run()
    {
        var transport = new LegacySonyReplyGate();
        long requestedDelay = 0;
        var client = new ManagerCommandClient(transport, (milliseconds, token) =>
        {
            token.ThrowIfCancellationRequested();
            requestedDelay += milliseconds;
            return Task.CompletedTask;
        });
        const int packets = 1000;
        for (ushort sequence = 1; sequence <= packets; sequence++)
        {
            using var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                sequence, (uint)(sequence - 1) * 46, new byte[46]).GetAwaiter().GetResult();
            if (ack.RootElement.GetProperty("next_seq").GetUInt16() != sequence + 1)
                throw new Exception("Legacy Sony gate returned an unrelated OTA acknowledgement.");
        }
        Console.WriteLine($"OTA legacy Sony gate: {packets} packets, {transport.Writes} writes, " +
            $"{transport.Reads} reads, {transport.NativeReads} native reads, " +
            $"{requestedDelay}ms scheduled backoff, {transport.Commits} unique commits.");
        if (transport.Commits != packets)
            throw new Exception("Lost reply recovery wrote firmware data more than once.");
        if (requestedDelay != 0 || transport.Writes != packets * 2 ||
            transport.Reads > packets * 4)
            throw new Exception("Consumed Sony reply gate still causes a polling timeout per OTA packet.");
    }

    // Models the shipped BL616 USB/feature cursor ordering, not an ideal ACK:
    // SET_REPORT opens manager_reply_pending before the task processes CBOT.
    // GET_REPORT can return the consumed tail of the previous reply, then
    // clear manager_reply_pending. The task publishes the new JSON afterwards
    // without reopening that gate. Sony native replies continue until the host
    // resends the *identical* CBOT packet. The cursor does NOT wrap to zero.
    private sealed class LegacySonyReplyGate : IDeviceTransport
    {
        private byte[] _json = Encoding.UTF8.GetBytes("{\"ok\":true}");
        private int _cursor = 11;
        private bool _gate;
        private byte[]? _queued;
        private byte[]? _committed;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public int NativeReads { get; private set; }
        public int Commits { get; private set; }
        public DeviceDescriptor Descriptor { get; } = new("offline", "offline", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "test", true, false, ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => throw new Exception("Fixture opened hardware.");
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes++;
            _gate = true;
            _queued = data.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            var report = new byte[64];
            report[0] = id;
            if (!_gate)
            {
                NativeReads++;
                return Task.FromResult(report);
            }
            Encoding.ASCII.GetBytes(ManagerProtocol.ReplyMagic).CopyTo(report, 1);
            var count = Math.Min(52, _json.Length - _cursor);
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(7), (ushort)_json.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(9), (ushort)_cursor);
            report[11] = (byte)count;
            _json.AsSpan(_cursor, count).CopyTo(report.AsSpan(12));
            _cursor += count;
            if (_cursor == _json.Length)
            {
                _gate = false;
                ProcessQueuedPacket();
            }
            return Task.FromResult(report);
        }

        private void ProcessQueuedPacket()
        {
            if (_queued is null) return;
            var packet = _queued;
            _queued = null;
            if (_committed is not null && packet.AsSpan().SequenceEqual(_committed))
                return; // The duplicate CBOT is acknowledged, never applied twice.
            var sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
            if (sequence != Commits + 1 || offset != (uint)Commits * 46)
                throw new Exception("OTA advanced an unacknowledged or noncontiguous packet.");
            _committed = packet;
            Commits++;
            _json = Encoding.UTF8.GetBytes(
                $"{{\"ok\":true,\"ota\":true,\"op\":3,\"seq\":{sequence},\"next_seq\":{sequence + 1},\"offset\":{offset},\"error\":0}}");
            _cursor = 0;
        }
    }
}
