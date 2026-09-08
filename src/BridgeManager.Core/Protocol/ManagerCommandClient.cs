using System.Text.Json;
using BridgeManager.Core.Transports;

namespace BridgeManager.Core.Protocol;

public sealed class ManagerCommandClient
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private readonly IDeviceTransport _transport;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    public ManagerCommandClient(IDeviceTransport transport)
    {
        _transport = transport;
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

    private async Task<string> ReadJsonReplyAsync(byte featureReportId,
                                                   CancellationToken cancellationToken)
    {
        var result = new byte[1024];
        var total = -1;
        var receivedCount = 0;
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
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                continue;
            }

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
            "manager reply was not ready or remained incomplete");
    }
}
