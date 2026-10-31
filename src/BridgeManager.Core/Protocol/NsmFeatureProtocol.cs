using System.Buffers.Binary;
using System.Text;

namespace BridgeManager.Core.Protocol;

// ESP32-S3 shares its existing vendor HID collection with legacy report 0x7f.
// NSM1 commands and physical telemetry use feature report 0x7e; the existing
// interrupt endpoint and emulated gamepad reports are not used by this client.
public static class NsmFeatureProtocol
{
    public const int PayloadLength = 63;
    public const int HeaderLength = 22;
    public const int MaxBodyLength = 4096;
    public const ushort ExecuteCommand = 0x0010;

    public static IReadOnlyList<byte[]> BuildCommand(string command, uint transaction)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        var body = Encoding.UTF8.GetBytes(command.Trim());
        if (body.Length > 512 || body.Contains((byte)0))
            throw new ArgumentException("NSM1 command must be 1–512 UTF-8 bytes without NUL.", nameof(command));
        var result = new List<byte[]>();
        for (var offset = 0; offset < body.Length; offset += PayloadLength - HeaderLength)
        {
            var chunk = Math.Min(PayloadLength - HeaderLength, body.Length - offset);
            var packet = new byte[PayloadLength];
            "NSM1"u8.CopyTo(packet);
            packet[4] = 1;
            packet[7] = (byte)(offset + chunk < body.Length ? 1 : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), transaction);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), ExecuteCommand);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), (ushort)body.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18), (ushort)offset);
            packet[20] = (byte)chunk;
            packet[21] = HeaderLength;
            body.AsSpan(offset, chunk).CopyTo(packet.AsSpan(HeaderLength));
            result.Add(packet);
        }
        return result;
    }

    public static NsmFeatureChunk ParseReply(ReadOnlySpan<byte> report, byte reportId)
    {
        if (report.Length == PayloadLength + 1 && report[0] == reportId)
            report = report[1..];
        if (report.Length != PayloadLength || !report[..4].SequenceEqual("NSM1"u8) ||
            report[4] != 1 || report[6] != 1 || report[21] != HeaderLength || (report[7] & ~3) != 0)
            throw new ManagerCommandException("Invalid NSM1 response header.");
        var total = BinaryPrimitives.ReadUInt16LittleEndian(report[16..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(report[18..]);
        var length = report[20];
        var status = BinaryPrimitives.ReadUInt16LittleEndian(report[14..]);
        var more = (report[7] & 1) != 0;
        if (total > MaxBodyLength || length > PayloadLength - HeaderLength ||
            offset + length > total || more != (offset + length < total) ||
            (total > 0 && length == 0) || ((report[7] & 2) != 0) != (status != 0))
            throw new ManagerCommandException("Invalid NSM1 response bounds or flags.");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(report[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(report[12..]), status, total,
            offset, more, report.Slice(HeaderLength, length).ToArray());
    }
}

public sealed record NsmFeatureChunk(uint Transaction, ushort Opcode, ushort Status,
    int TotalLength, int Offset, bool More, byte[] Payload);
