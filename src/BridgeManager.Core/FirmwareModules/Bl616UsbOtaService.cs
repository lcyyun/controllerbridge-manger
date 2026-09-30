using System.Text.Json;
using BridgeManager.Core.Protocol;

namespace BridgeManager.Core.FirmwareModules;

/// <summary>Updates an already verified BL616 receiver through its Manager HID channel.</summary>
public sealed class Bl616UsbOtaService
{
    public async Task UpdateAsync(ManagerCommandClient client, byte[] manifest,
        byte[] image, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var versionReply = await client.SendCommandAsync("version", cancellationToken);
        var version = versionReply.RootElement;
        if (!version.TryGetProperty("hardware", out var hardware) ||
            hardware.GetString() != "CB-BL616" ||
            !version.TryGetProperty("firmware_build_number", out var build))
            throw new InvalidDataException("Connected receiver is not an OTA-capable BL616.");
        Bl616OtaProtocol.ValidatePackage(manifest, image, build.GetUInt32());
        // STATUS is exempt from sequence checking, allowing a fresh Manager
        // session to resume the protocol counter without accepting stale ACKs.
        using var status = await client.SendBl616OtaPacketAsync(
            Bl616OtaOperation.Status, 0, StatusNonce(), ReadOnlyMemory<byte>.Empty, cancellationToken);
        if (status.RootElement.GetProperty("state").GetString() is "ready" or "locked")
            throw new IOException("Receiver has a pending OTA activation. Restart it before starting another update.");
        var sequence = status.RootElement.GetProperty("next_seq").GetUInt16();
        var activated = false;
        try
        {
            await Send(Bl616OtaOperation.Abort, 0, ReadOnlyMemory<byte>.Empty);
            for (var offset = 0; offset < manifest.Length; offset += Bl616OtaProtocol.MaximumPayload)
                await Send(Bl616OtaOperation.Manifest, (uint)offset,
                    manifest.AsMemory(offset, Math.Min(Bl616OtaProtocol.MaximumPayload, manifest.Length - offset)));
            // MANIFEST puts firmware into transient update mode. It stops both
            // radios itself, without ever changing the saved auto-connect flag.
            for (var attempt = 0; ; attempt++)
            {
                using var begin = await client.SendBl616OtaPacketAsync(
                    Bl616OtaOperation.Begin, sequence, 0, ReadOnlyMemory<byte>.Empty, cancellationToken);
                if (begin.RootElement.GetProperty("ok").GetBoolean())
                {
                    sequence = begin.RootElement.GetProperty("next_seq").GetUInt16();
                    break;
                }
                if (begin.RootElement.GetProperty("error").GetInt32() != -14 || attempt >= 39)
                    throw new IOException($"BL616 OTA Begin rejected: {begin.RootElement.GetProperty("error").GetInt32()}.");
                await Task.Delay(200, cancellationToken);
            }
            for (var offset = 0; offset < image.Length; offset += Bl616OtaProtocol.MaximumPayload)
            {
                var count = Math.Min(Bl616OtaProtocol.MaximumPayload, image.Length - offset);
                await Send(Bl616OtaOperation.Data, (uint)offset, image.AsMemory(offset, count));
                progress?.Report((double)(offset + count) / image.Length);
            }
            await Send(Bl616OtaOperation.Finish, 0, ReadOnlyMemory<byte>.Empty);
            activated = true;
            await Send(Bl616OtaOperation.Reboot, 0, ReadOnlyMemory<byte>.Empty);
        }
        finally
        {
            if (!activated)
            {
                // Cancellation must not prevent best-effort cleanup. A lost
                // device also times out its receive state independently.
                try
                {
                    using var latest = await client.SendBl616OtaPacketAsync(
                        Bl616OtaOperation.Status, 0, StatusNonce(), ReadOnlyMemory<byte>.Empty);
                    if (latest.RootElement.GetProperty("state").GetString() is not ("ready" or "locked"))
                    {
                        using var reply = await client.SendBl616OtaPacketAsync(
                            Bl616OtaOperation.Abort, latest.RootElement.GetProperty("next_seq").GetUInt16(),
                            0, ReadOnlyMemory<byte>.Empty);
                    }
                }
                catch (Exception) { }
            }
        }

        async Task Send(Bl616OtaOperation operation, uint offset, ReadOnlyMemory<byte> payload)
        {
            using var reply = await client.SendBl616OtaPacketAsync(
                operation, sequence, offset, payload, cancellationToken);
            if (!reply.RootElement.GetProperty("ok").GetBoolean())
                throw new IOException($"BL616 OTA {operation} rejected: {reply.RootElement.GetProperty("error").GetInt32()}.");
            sequence = reply.RootElement.GetProperty("next_seq").GetUInt16();
        }
    }

    private static uint StatusNonce() =>
        (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
}
