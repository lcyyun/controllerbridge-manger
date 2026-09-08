using System.Text;

namespace BridgeManager.Core.Protocol;

public static class FeatureReportProtocol
{
    private static readonly Encoding TextEncoding = Encoding.UTF8;
    private static readonly byte[] SetMagicBytes = TextEncoding.GetBytes(ManagerProtocol.SetMagic);
    private static readonly byte[] ReplyMagicBytes = TextEncoding.GetBytes(ManagerProtocol.ReplyMagic);

    public static byte[] BuildFeatureCommandPayload(string command,
                                                    bool includeReportId,
                                                    byte reportId = ManagerProtocol.FeatureReportId)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        }

        var payloadLength = includeReportId
            ? ManagerProtocol.FeatureReportLength
            : ManagerProtocol.CommandPayloadLength;
        var payload = new byte[payloadLength];
        var offset = includeReportId ? 1 : 0;

        if (includeReportId)
        {
            payload[0] = reportId;
        }

        SetMagicBytes.CopyTo(payload, offset);
        var commandBytes = TextEncoding.GetBytes(command.Trim());
        var maxCommandBytes = payload.Length - offset - SetMagicBytes.Length;
        if (commandBytes.Length > maxCommandBytes)
        {
            throw new ArgumentException($"Command is too long; maximum UTF-8 length is {maxCommandBytes} bytes.", nameof(command));
        }
        Array.Copy(commandBytes, 0, payload, offset + SetMagicBytes.Length, commandBytes.Length);
        return payload;
    }

    public static byte[] BuildOutputCommandPayload(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        }

        var payload = new byte[ManagerProtocol.CommandPayloadLength];
        SetMagicBytes.CopyTo(payload, 0);
        var commandBytes = TextEncoding.GetBytes(command.Trim());
        var maxCommandBytes = payload.Length - SetMagicBytes.Length;
        if (commandBytes.Length > maxCommandBytes)
        {
            throw new ArgumentException($"Command is too long; maximum UTF-8 length is {maxCommandBytes} bytes.", nameof(command));
        }
        Array.Copy(commandBytes, 0, payload, SetMagicBytes.Length, commandBytes.Length);
        return payload;
    }

    public static FeatureReportChunk ParseReplyChunk(
        ReadOnlySpan<byte> report,
        byte reportId = ManagerProtocol.FeatureReportId)
    {
        var baseOffset = HasReplyMagic(report, 1) && report[0] == reportId ? 1 : 0;
        if (!HasReplyMagic(report, baseOffset))
        {
            throw new ManagerCommandException("bad reply magic");
        }

        if (report.Length < baseOffset + 11)
        {
            throw new ManagerCommandException("short reply report");
        }

        var total = report[baseOffset + 6] | (report[baseOffset + 7] << 8);
        var offset = report[baseOffset + 8] | (report[baseOffset + 9] << 8);
        var chunkLength = report[baseOffset + 10];
        var chunkStart = baseOffset + 11;
        if (chunkStart + chunkLength > report.Length)
        {
            throw new ManagerCommandException("reply chunk exceeds report length");
        }

        return new FeatureReportChunk(total, offset, report.Slice(chunkStart, chunkLength).ToArray());
    }

    public static string DecodeReply(byte[] bytes, int length) =>
        TextEncoding.GetString(bytes, 0, length);

    private static bool HasReplyMagic(ReadOnlySpan<byte> report, int offset)
    {
        if (report.Length < offset + ReplyMagicBytes.Length)
        {
            return false;
        }

        return report.Slice(offset, ReplyMagicBytes.Length).SequenceEqual(ReplyMagicBytes);
    }
}

public sealed record FeatureReportChunk(int TotalLength, int Offset, byte[] Payload);
