using BridgeManager.Core.Protocol;
using BridgeManager.Core;
using BridgeManager.Core.Transports;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

internal static class Bl616OtaTests
{
    public static void Run()
    {
        var transport = new LostAckTransport();
        var client = new ManagerCommandClient(transport, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        using (var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                   12, 4096, new byte[] { 1 }).GetAwaiter().GetResult())
        {
            if (transport.Writes != 2 || ack.RootElement.GetProperty("offset").GetUInt32() != 4096)
                throw new Exception("OTA lost ACK was not retried safely.");
        }
        TestAckReadiness();
        TestRawHeader();
        var packet = Bl616OtaProtocol.BuildPacket(Bl616OtaOperation.Data,
            123, 4096, new byte[] { 1, 2, 3 });
        if (packet.Length != 63 || packet[4] != 1 || packet[5] != 3 ||
            packet[6] != 123 || packet[9] != 16 || packet[12] != 3)
            throw new Exception("Invalid OTA wire packet.");
        try
        {
            Bl616OtaProtocol.BuildPacket(Bl616OtaOperation.Data, 0, 0, new byte[47]);
            throw new Exception("Oversized OTA payload accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
        try
        {
            Bl616OtaProtocol.ValidatePackage(new byte[160], new byte[1024], 0);
            throw new Exception("Unsigned OTA package accepted.");
        }
        catch (InvalidDataException) { }

        // Optional signed fixture created by sign-ota.ps1; never accesses USB.
        var imagePath = Environment.GetEnvironmentVariable("CB_OTA_TEST_IMAGE");
        if (imagePath is null) return;
        var image = File.ReadAllBytes(imagePath);
        var manifest = File.ReadAllBytes(imagePath + ".manifest");
        var version = Bl616OtaProtocol.ValidatePackage(manifest, image, 0);
        image[512] ^= 1;
        Reject(manifest, image, 0);
        image[512] ^= 1;
        Reject(manifest, image, version);
        manifest[96] ^= 1;
        Reject(manifest, image, 0);
    }

    private static void TestAckReadiness()
    {
        var ready = new AckReadinessTransport();
        var delays = 0;
        var client = new ManagerCommandClient(ready, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays++;
            return Task.CompletedTask;
        });
        // No wall-clock assertion: a ready device must incur no artificial
        // sleeps, regardless of machine speed or Windows timer resolution.
        for (ushort sequence = 0; sequence < 1000; sequence++)
        {
            using var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                sequence, (uint)sequence * 46, new byte[46]).GetAwaiter().GetResult();
            if (ack.RootElement.GetProperty("offset").GetUInt32() != (uint)sequence * 46)
                throw new Exception("Fast OTA accepted an unrelated ACK.");
        }
        if (delays != 0 || ready.Writes != 1000 || ready.Reads != 2000)
            throw new Exception("Ready OTA packets still incur fixed polling delays or extra transfers.");

        // Native HID reply while SET_REPORT is being processed, followed by
        // stale operation, sequence and offset ACKs, must never advance data.
        var pending = new AckReadinessTransport { PendingReplies = 4 };
        delays = 0;
        client = new ManagerCommandClient(pending, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays++;
            return Task.CompletedTask;
        });
        using (var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                   12, 4096, new byte[] { 1 }).GetAwaiter().GetResult())
        {
            if (delays != 4 || pending.Writes != 1 || pending.PendingReplies != 0 ||
                ack.RootElement.GetProperty("offset").GetUInt32() != 4096)
                throw new Exception("OTA did not wait safely for the matching ACK.");
        }

        pending = new AckReadinessTransport { PendingReplies = 100 };
        var longestBackoff = 0;
        delays = 0;
        client = new ManagerCommandClient(pending, (milliseconds, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays++;
            longestBackoff = Math.Max(longestBackoff, milliseconds);
            return Task.CompletedTask;
        });
        using (var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                   12, 4096, new byte[] { 1 }).GetAwaiter().GetResult())
        {
            if (pending.Writes != 2 || delays != 100 || longestBackoff != 20)
                throw new Exception("Slow firmware processing lost its bounded polling/retry budget.");
        }

        using var cancellation = new CancellationTokenSource();
        pending = new AckReadinessTransport { PendingReplies = 4 };
        client = new ManagerCommandClient(pending, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        try
        {
            using var unexpected = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                12, 4096, new byte[] { 1 }, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("OTA ignored cancellation while waiting for firmware.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        pending.PendingReplies = 0;
        using var resumed = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
            12, 4096, new byte[] { 1 }).GetAwaiter().GetResult();
        if (pending.Writes != 2)
            throw new Exception("Cancelled OTA did not release the command lock.");
    }

    private sealed class AckReadinessTransport : IDeviceTransport
    {
        private byte[] _json = [];
        private int _offset;
        private ushort _sequence;
        private uint _packetOffset;
        private byte _operation;
        public int PendingReplies { get; set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public DeviceDescriptor Descriptor { get; } = new("offline", "offline", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "test", true, false, ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => throw new Exception("Offline fixture opened hardware.");
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token) => throw new NotSupportedException();
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes++;
            _sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.Span[6..]);
            _packetOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[8..]);
            _operation = data.Span[5];
            _offset = 0;
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            var bytes = new byte[64]; bytes[0] = id;
            if (_offset == 0)
            {
                var pending = PendingReplies;
                if (pending > 0) PendingReplies--;
                if (pending >= 4) return Task.FromResult(bytes); // native/not ready
                var op = pending == 3 ? 0 : _operation;
                var seq = pending == 2 ? _sequence + 1 : _sequence;
                var offset = pending == 1 ? _packetOffset + 1 : _packetOffset;
                _json = Encoding.UTF8.GetBytes($"{{\"ok\":true,\"ota\":true,\"op\":{op},\"seq\":{seq},\"next_seq\":{_sequence + 1},\"offset\":{offset}}}");
            }
            Encoding.ASCII.GetBytes(ManagerProtocol.ReplyMagic).CopyTo(bytes, 1);
            var count = Math.Min(52, _json.Length - _offset);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7), (ushort)_json.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), (ushort)_offset);
            bytes[11] = (byte)count;
            _json.AsSpan(_offset, count).CopyTo(bytes.AsSpan(12));
            _offset += count;
            if (_offset == _json.Length) _offset = 0;
            return Task.FromResult(bytes);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void TestRawHeader()
    {
        var image = new byte[1024];
        "BL60X_OTA_Ver1.0"u8.CopyTo(image);
        "RAW "u8.CopyTo(image.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 512);
        "CBFWID1\0CB-BL616\0\0\0\0\0\0\0\0"u8.CopyTo(image.AsSpan(600));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(624), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(628), 1);
        SHA256.HashData(image.AsSpan(512)).CopyTo(image, 64);
        Bl616OtaProtocol.ValidateRawImage(image, 100);
        foreach (var offset in new[] { 0, 16, 20, 64, 600, 608, 624, 628 })
        {
            image[offset] ^= 1;
            try { Bl616OtaProtocol.ValidateRawImage(image, 100); throw new Exception("Invalid RAW image accepted."); }
            catch (InvalidDataException) { }
            image[offset] ^= 1;
        }
        try { Bl616OtaProtocol.ValidateRawImage(image, 101); throw new Exception("Wrong image build accepted."); }
        catch (InvalidDataException) { }
    }

    private sealed class LostAckTransport : IDeviceTransport
    {
        private byte[]? _firstPacket;
        private byte[] _json = Encoding.UTF8.GetBytes("{\"ok\":true,\"ota\":true,\"op\":3,\"seq\":12,\"offset\":0}");
        private int _offset;
        public int Writes { get; private set; }
        public DeviceDescriptor Descriptor { get; } = new("fake", "fake", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "test", true, false, ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => Task.CompletedTask;
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token) => throw new NotSupportedException();
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            if (_firstPacket is not null && !data.Span.SequenceEqual(_firstPacket))
                throw new Exception("OTA retry changed the unacknowledged wire packet.");
            _firstPacket ??= data.ToArray();
            Writes++;
            if (Writes > 1) _json = Encoding.UTF8.GetBytes("{\"ok\":true,\"ota\":true,\"op\":3,\"seq\":12,\"offset\":4096}");
            _offset = 0;
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token)
        {
            var bytes = new byte[64]; bytes[0] = id;
            Encoding.ASCII.GetBytes(ManagerProtocol.ReplyMagic).CopyTo(bytes, 1);
            var count = Math.Min(52, _json.Length - _offset);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7), (ushort)_json.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), (ushort)_offset);
            bytes[11] = (byte)count; _json.AsSpan(_offset, count).CopyTo(bytes.AsSpan(12));
            _offset += count; if (_offset == _json.Length) _offset = 0;
            return Task.FromResult(bytes);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Reject(byte[] manifest, byte[] image, uint version)
    {
        try { Bl616OtaProtocol.ValidatePackage(manifest, image, version); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid/replayed OTA package accepted.");
    }
}
