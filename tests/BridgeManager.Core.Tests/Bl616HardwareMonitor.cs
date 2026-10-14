using System.Diagnostics;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;

// On-demand, read-only hardware capture. Never invoked by the unit-test suite.
internal static class Bl616HardwareMonitor
{
    public static async Task RunAsync(string outputPath, int seconds, bool pollStatus = true)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var gate = new object();
        var clock = Stopwatch.StartNew();
        long reports = 0, decodeFailures = 0, previousInputMs = 0, maxGapMs = 0, sampleMs = -100;
        uint buttonsSeen = 0;
        ControllerInputSnapshot? previous = null;

        void Log(string kind, object value)
        {
            lock (gate) writer.WriteLine(JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow, elapsed_ms = clock.ElapsedMilliseconds,
                kind, value
            }));
        }

        var factory = new BridgeTransportFactory();
        var devices = await factory.GetDevicesAsync(stop.Token);
        var descriptor = devices.Single(device => device.TransportKind == DeviceTransportKind.Hid &&
            device.ProfileKey?.StartsWith("bl616-", StringComparison.OrdinalIgnoreCase) == true &&
            device.SupportsInputReports);
        await using var transport = await factory.OpenAsync(descriptor, stop.Token);
        var client = new ManagerCommandClient(transport);
        // The receiver has one feature-command reply buffer. An input-only
        // observer must not issue commands while Manager is using that buffer.
        JsonElement? version = null;
        if (pollStatus)
        {
            using var reply = await client.SendCommandAsync("version", stop.Token);
            if (reply.RootElement.GetProperty("hardware").GetString() != "CB-BL616")
                throw new InvalidOperationException("Receiver hardware identity is not BL616.");
            version = reply.RootElement.Clone();
        }
        Log("start", new { descriptor, version, seconds, pollStatus });
        if (transport is not IInputReportSource source)
            throw new InvalidOperationException("USB input reports unavailable.");

        source.InputReportReceived += (_, report) =>
        {
            lock (gate)
            {
                reports++;
                var now = clock.ElapsedMilliseconds;
                if (previousInputMs != 0) maxGapMs = Math.Max(maxGapMs, now - previousInputMs);
                previousInputMs = now;
                if (!ControllerInputDecoder.TryDecode(report.ReportId, report.Payload, out var input) || input is null)
                {
                    decodeFailures++;
                    if (decodeFailures <= 5) Log("usb_decode_failure", new { report.ReportId, hex = Convert.ToHexString(report.Payload) });
                    return;
                }
                buttonsSeen |= input.Buttons;
                if (previous?.Buttons != input.Buttons || now - sampleMs >= 100)
                {
                    Log("usb_input", new { report.ReportId, input, hex = Convert.ToHexString(report.Payload) });
                    sampleMs = now;
                }
                previous = input;
            }
        };
        source.InputReportReadFailed += (_, failure) => Log("usb_read_failure", failure.Error.ToString());
        await transport.OpenAsync(stop.Token);

        Console.WriteLine($"MONITOR_READY {path}");
        Console.WriteLine($"Receiver {descriptor.TransportLabel}; status polling {(pollStatus ? "enabled" : "disabled (USB input only)")}");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var command in pollStatus
                    ? new[] { "ns2 status", "bridge status", "usb status" }
                    : Array.Empty<string>())
                {
                    try
                    {
                        using var reply = await client.SendCommandAsync(command, stop.Token);
                        Log(command, reply.RootElement.Clone());
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (Exception error) { Log("status_failure", new { command, error = error.ToString() }); }
                }
                lock (gate)
                {
                    var summary = new { reports, decodeFailures, buttonsSeen, maxGapMs,
                        lastInputAgeMs = clock.ElapsedMilliseconds - previousInputMs };
                    Log("heartbeat", summary);
                    Console.WriteLine($"CAPTURE {clock.Elapsed.TotalSeconds:F0}s reports={reports} decode_errors={decodeFailures} buttons=0x{buttonsSeen:X} max_gap_ms={maxGapMs}");
                }
                await Task.Delay(5000, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            // Join the reader before closing its log sink.
            await transport.DisposeAsync();
            Log("complete", new { reports, decodeFailures, buttonsSeen, maxGapMs });
        }
    }
}
