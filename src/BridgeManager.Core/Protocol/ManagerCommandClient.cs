using System.Text.Json;
using BridgeManager.Core.Transports;

namespace BridgeManager.Core.Protocol;

public sealed class ManagerCommandClient
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private readonly IDeviceTransport _transport;
    private readonly Func<int, CancellationToken, Task> _otaRetryDelay;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private uint _nsmTransaction = (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    public ManagerCommandClient(IDeviceTransport transport) : this(transport, Task.Delay)
    {
    }

    internal ManagerCommandClient(IDeviceTransport transport,
        Func<int, CancellationToken, Task> otaRetryDelay)
    {
        _transport = transport;
        _otaRetryDelay = otaRetryDelay;
    }

    public async Task<JsonDocument> SendCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var commandToken = timeout.Token;
        try
        {
            await _commandLock.WaitAsync(commandToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Manager command timed out while waiting for the transport.");
        }
        try
        {
            var descriptor = _transport.Descriptor;
            if (descriptor.ManagerWireProtocol == BridgeManagerWireProtocol.Nsm1)
                return await SendNsmCommandAsync(command, commandToken).ConfigureAwait(false);
            if (descriptor.CommandOutputReportId is byte outputReportId &&
                descriptor.SupportsOutputReports)
            {
                await _transport.WriteOutputReportAsync(
                    outputReportId,
                    FeatureReportProtocol.BuildOutputCommandPayload(command),
                    commandToken).ConfigureAwait(false);
                await Task.Delay(80, commandToken).ConfigureAwait(false);
            }
            else
            {
                var featureReportId = descriptor.ManagerFeatureReportId;
                await _transport.WriteFeatureReportAsync(
                    featureReportId,
                    FeatureReportProtocol.BuildFeatureCommandPayload(command, includeReportId: false),
                    commandToken).ConfigureAwait(false);
                // DS5 identities share this report id with a native feature.
                // Give the firmware task one scheduling slice to publish the
                // manager reply before the first GET_REPORT.
                await Task.Delay(20, commandToken).ConfigureAwait(false);
            }

            var json = await ReadJsonReplyAsync(
                descriptor.ManagerFeatureReportId,
                commandToken).ConfigureAwait(false);
            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Manager command timed out.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<JsonDocument> SendNsmCommandAsync(string command, CancellationToken token)
    {
        var transaction = unchecked(++_nsmTransaction);
        if (transaction == 0) transaction = ++_nsmTransaction;
        var reportId = _transport.Descriptor.ManagerFeatureReportId;
        foreach (var packet in NsmFeatureProtocol.BuildCommand(command, transaction))
            await _transport.WriteFeatureReportAsync(reportId, packet, token).ConfigureAwait(false);
        var result = new byte[NsmFeatureProtocol.MaxBodyLength];
        var received = 0;
        var total = -1;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var report = await _transport.ReadFeatureReportAsync(reportId, token).ConfigureAwait(false);
            var chunk = NsmFeatureProtocol.ParseReply(report, reportId);
            // A task-dispatched request may not have replaced the previous
            // reply yet. Never accept stale or unrelated transaction data.
            if (chunk.Transaction != transaction || chunk.Opcode != NsmFeatureProtocol.ExecuteCommand)
            {
                await Task.Delay(10, token).ConfigureAwait(false);
                continue;
            }
            if (chunk.Status == 8 && chunk.TotalLength == 0)
            {
                await Task.Delay(10, token).ConfigureAwait(false);
                continue;
            }
            if (chunk.Status != 0)
                throw new ManagerCommandException($"ESP32-S3 manager rejected the request (NSM1 status {chunk.Status}).");
            if (chunk.Offset != received || (total >= 0 && total != chunk.TotalLength))
                throw new ManagerCommandException("NSM1 reply fragment is out of order or changed length.");
            total = chunk.TotalLength;
            chunk.Payload.CopyTo(result, received);
            received += chunk.Payload.Length;
            if (!chunk.More)
                return JsonDocument.Parse(result.AsMemory(0, received));
        }
        throw new ManagerCommandException($"NSM1 reply incomplete ({received}/{total}).");
    }

    private async Task<string> ReadJsonReplyAsync(byte featureReportId,
        CancellationToken cancellationToken, bool otaReply = false)
    {
        var result = new byte[1024];
        var total = -1;
        var receivedCount = 0;
        var notReadyCount = 0;
        bool[]? received = null;

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var report = await _transport.ReadFeatureReportAsync(featureReportId, cancellationToken)
                .ConfigureAwait(false);
            FeatureReportChunk chunk;
            try
            {
                chunk = FeatureReportProtocol.ParseReplyChunk(report, featureReportId);
            }
            catch (ManagerCommandException ex) when (
                ex.Message.Equals("bad reply magic", StringComparison.Ordinal))
            {
                if (otaReply)
                    await _otaRetryDelay(OtaBackoffMilliseconds(notReadyCount++), cancellationToken).ConfigureAwait(false);
                else
                {
                    notReadyCount++;
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (total >= 0 && total != chunk.TotalLength)
                throw new ManagerCommandException("manager reply length changed; possible competing manager client");
            total = chunk.TotalLength;
            if (total < 0 || chunk.Offset < 0 || chunk.Offset + chunk.Payload.Length > total)
            {
                throw new ManagerCommandException("invalid reply chunk bounds");
            }
            if (total > result.Length)
            {
                Array.Resize(ref result, total);
            }
            if (received is null || received.Length != total)
            {
                received = new bool[total];
                receivedCount = 0;
            }

            if (chunk.Payload.Length > 0)
            {
                chunk.Payload.CopyTo(result, chunk.Offset);
                for (var index = 0; index < chunk.Payload.Length; index++)
                {
                    if (!received[chunk.Offset + index])
                    {
                        received[chunk.Offset + index] = true;
                        receivedCount++;
                    }
                }
            }

            if (total >= 0 && receivedCount >= total)
            {
                return FeatureReportProtocol.DecodeReply(result, total);
            }
        }

        throw new ManagerCommandException(
            $"manager reply was not ready or remained incomplete (received {receivedCount}/{total} bytes; not-ready reads {notReadyCount})");
    }

    public async Task<JsonDocument> SendBl616OtaPacketAsync(
        Bl616OtaOperation operation, ushort sequence, uint offset,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _commandLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var reportId = _transport.Descriptor.ManagerFeatureReportId;
            var packet = Bl616OtaProtocol.BuildPacket(operation, sequence, offset, payload.Span);
            // Retrying the exact packet is idempotent in the firmware, including
            // FINISH. Do not advance the counter until its ACK is received.
            for (var retry = 0; retry < 4; retry++)
            {
                await _transport.WriteFeatureReportAsync(reportId, packet, timeout.Token).ConfigureAwait(false);
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    // Most ACKs are ready when SET_REPORT completes. Read first;
                    // only yield if the firmware has not published this ACK yet.
                    // A fixed per-packet delay adds minutes to a 46-byte stream.
                    string json;
                    try { json = await ReadJsonReplyAsync(reportId, timeout.Token, otaReply: true).ConfigureAwait(false); }
                    catch (ManagerCommandException) { break; }
                    var reply = JsonDocument.Parse(json);
                    if (reply.RootElement.TryGetProperty("ota", out var ota) && ota.ValueKind == JsonValueKind.True &&
                        reply.RootElement.TryGetProperty("seq", out var seq) && seq.GetUInt16() == sequence &&
                        reply.RootElement.TryGetProperty("op", out var op) && op.GetByte() == (byte)operation &&
                        reply.RootElement.TryGetProperty("offset", out var ackOffset) && ackOffset.GetUInt32() == offset)
                        return reply;
                    reply.Dispose();
                    await _otaRetryDelay(OtaBackoffMilliseconds(attempt), timeout.Token).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("BL616 OTA acknowledgement was not received.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("BL616 OTA acknowledgement timed out.");
        }
        finally { _commandLock.Release(); }
    }

    // Yield briefly for a normal firmware scheduling slice, but retain the
    // previous polling budget for slow flash erase/hash/signature operations.
    private static int OtaBackoffMilliseconds(int attempt) =>
        Math.Min(20, 2 << Math.Min(attempt, 4));
}
