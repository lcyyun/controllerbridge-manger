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
        var client = new ManagerCommandClient(transport);
        using (var ack = client.SendBl616OtaPacketAsync(Bl616OtaOperation.Data,
                   12, 4096, new byte[] { 1 }).GetAwaiter().GetResult())
        {
            if (transport.Writes != 2 || ack.RootElement.GetProperty("offset").GetUInt32() != 4096)
                throw new Exception("OTA lost ACK was not retried safely.");
        }
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
        private byte[] _json = Encoding.UTF8.GetBytes("{\"ok\":true,\"ota\":true,\"op\":3,\"seq\":12,\"offset\":0}");
        private int _offset;
        public int Writes { get; private set; }
        public DeviceDescriptor Descriptor { get; } = new("fake", "fake", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "test", true, false, ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => Task.CompletedTask;
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token) => throw new NotSupportedException();
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken token)
        {
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
