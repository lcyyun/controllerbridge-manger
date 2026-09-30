using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BridgeManager.Core.Protocol;

public enum Bl616OtaOperation : byte
{
    Status, Manifest, Begin, Data, Finish, Abort, Reboot
}

public static class Bl616OtaProtocol
{
    public const int MaximumPayload = 46;
    private static readonly byte[] PublicKey = Convert.FromHexString(
        "98AADE6355630A293A6DEBA982D9033968A63AF2F54F4470170538B292688261C" +
        "61777007C6BC62A198CF3D67BFF3844A2FABEEDC49D5D3823A54A23CABADFDF");

    public static byte[] BuildPacket(Bl616OtaOperation operation,
        ushort sequence, uint offset, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayload)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[63];
        "CBOT"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)operation;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), offset);
        packet[12] = (byte)payload.Length;
        payload.CopyTo(packet.AsSpan(13));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(13 + payload.Length),
            Crc32(packet.AsSpan(0, 13 + payload.Length)));
        return packet;
    }

    public static uint ValidatePackage(ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> image, uint currentBuild)
    {
        if (manifest.Length != 160 || !manifest[..8].SequenceEqual("CBOTA01\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(manifest[8..]) != 1 ||
            !manifest.Slice(12, 16).SequenceEqual("CB-BL616\0\0\0\0\0\0\0\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(manifest[28..]) != image.Length ||
            image.Length is <= 512 or > 0x168200 ||
            BinaryPrimitives.ReadUInt32LittleEndian(manifest[36..]) > 1 ||
            !manifest.Slice(88, 8).SequenceEqual(new byte[8]))
            throw new InvalidDataException("BL616 OTA manifest or hardware identity is invalid.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(manifest[32..]);
        if (version <= currentBuild)
            throw new InvalidDataException("OTA firmware must be newer than the running build.");
        ValidateRawImage(image, version);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(image), manifest.Slice(40, 32)) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(PublicKey).AsSpan(0, 16), manifest.Slice(72, 16)))
            throw new InvalidDataException("OTA image digest or signing key ID is invalid.");
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = PublicKey[..32], Y = PublicKey[32..] }
        });
        if (!key.VerifyData(manifest[..96], manifest[96..], HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("OTA release signature is invalid.");
        return version;
    }

    public static void ValidateRawImage(ReadOnlySpan<byte> image, uint build)
    {
        if (image.Length is <= 512 or > 0x168200 ||
            !image[..16].SequenceEqual("BL60X_OTA_Ver1.0"u8) ||
            !(image.Slice(16, 4).SequenceEqual("RAW "u8) || image.Slice(16, 4).SequenceEqual("RAW\0"u8)) ||
            BinaryPrimitives.ReadUInt32LittleEndian(image[20..]) != image.Length - 512 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(image[512..]), image.Slice(64, 32)))
            throw new InvalidDataException("Invalid BL616 RAW OTA image header, size or body digest.");
        var body = image[512..];
        var offset = body.IndexOf("CBFWID1\0"u8);
        if (offset < 0 || offset + 32 > body.Length ||
            body[(offset + 1)..].IndexOf("CBFWID1\0"u8) >= 0 ||
            !body.Slice(offset + 8, 16).SequenceEqual("CB-BL616\0\0\0\0\0\0\0\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 24)..]) != build ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 28)..]) != 1)
            throw new InvalidDataException("OTA image hardware/build identity does not match the signed manifest.");
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
        }
        return ~crc;
    }
}
