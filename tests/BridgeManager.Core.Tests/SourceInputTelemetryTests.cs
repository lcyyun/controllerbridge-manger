using System.Text.Json;
using BridgeManager.Core;

internal static class SourceInputTelemetryTests
{
    public static void Run()
    {
        var tracker = new SourceInputTelemetry();
        var status = new BridgeStatusSnapshot { ActiveInput = BridgePhysicalInput.NintendoNs2Pro,
            InputValid = true, InputStale = false };
        void Observe(long count, long clock, long host, string source = "ns2pro", int battery = -1) =>
            tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = source,
                input_rate_supported = true, unique_input_reports = count, input_rate_time_ms = clock,
                input_battery_percent = battery }), status, host);

        Observe(1000, 1000, 0);
        Observe(1125, 2000, 1000);
        Require(tracker.Hertz == 125 && tracker.IsFresh(1000), "Source counter/time measure effective Hz.");
        Observe(1125, 2000, 2800, battery: 80);
        Require(tracker.Hertz == 125 && tracker.BatteryPercent == 80, "Duplicate tuple must not inflate Hz.");
        tracker.Expire(3000);
        Require(!tracker.IsFresh(3000) && tracker.Hertz is null && tracker.BatteryPercent is null,
            "Replaying a cached tuple cannot extend its freshness or battery.");
        Observe(1125, 2000, 3500, battery: 80);
        Require(!tracker.IsFresh(3500) && tracker.Hertz is null && tracker.BatteryPercent is null,
            "An expired duplicate cannot revive a measurement.");
        Observe(1250, 5000, 4000);
        Require(tracker.Hertz is null, "Expired windows must not include the disconnected interval.");
        Observe(1375, 6000, 5000);
        Require(tracker.Hertz == 125, "A progressing stream recovers after a new complete window.");
        Observe(1375, 7000, 6000);
        Require(tracker.Hertz == 0, "Clock progression without source frames is zero, not the last Hz.");
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ns2pro", feedback_forwarded = 4 }), status, 6500);
        Require(tracker.RateSupported && tracker.Hertz == 0, "Partial status must not erase complete telemetry.");

        var stopped = new SourceInputTelemetry();
        void Stopped(long clock, long host) => stopped.Observe(JsonSerializer.SerializeToElement(new
            { active_input = "ns2pro", input_rate_supported = true, unique_input_reports = 10,
                input_rate_time_ms = clock, input_battery_percent = 80 }), status, host);
        Stopped(100, 0);
        Stopped(1100, 1000);
        Require(stopped.Hertz == 0 && stopped.IsFresh(1000), "A short idle interval measures zero.");
        Stopped(2100, 2000);
        Stopped(3100, 3000);
        Require(!stopped.IsFresh(3000) && stopped.Hertz is null && stopped.BatteryPercent is null,
            "A progressing firmware clock alone cannot keep frozen physical input or battery alive.");

        status = status with { ActiveInput = BridgePhysicalInput.DualSense };
        Observe(1375, 7000, 6700, "ds5");
        Require(tracker.Hertz is null && tracker.IsFresh(6700), "Source switch resets even with the same counter tuple.");
        Observe(1625, 8000, 7700, "ds5", 65);
        Require(tracker.Hertz == 250 && tracker.BatteryPercent == 65, "The new source owns its rate and battery.");
        Observe(1875, 9000, 8700, "ns2pro", 100);
        Require(tracker.Hertz is null && tracker.BatteryPercent is null,
            "Raw telemetry source must match the merged active source.");

        Observe(uint.MaxValue - 100L, uint.MaxValue - 1000L, 9000, "ds5");
        Observe(20, 200, 9500, "ds5");
        Require(tracker.Hertz is null, "Counter and uptime wrap start a safe new window.");
        Observe(270, 1200, 10500, "ds5");
        Require(tracker.Hertz == 250, "Measurement recovers after wrap.");
        status = status with { InputValid = false };
        Observe(270, 1200, 10600, "ds5");
        Require(!tracker.RateSupported && tracker.Hertz is null && tracker.BatteryPercent is null,
            "Invalid input clears all telemetry.");
        status = status with { InputValid = true };
        Observe(270, 1200, 10700, "ds5");
        Require(tracker.Hertz is null, "A reconnect starts independently even when its first tuple repeats.");
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", input_rate_supported = true,
            unique_input_reports = "bad", input_rate_time_ms = 2000, input_battery_percent = 101 }), status, 10800);
        Require(!tracker.IsFresh(10800) && tracker.BatteryPercent is null, "Malformed counters and battery stay unknown.");
        tracker.Reset();
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", input_battery_percent = 55 }), status, 11000);
        Require(!tracker.RateSupported && tracker.Hertz is null && tracker.BatteryPercent == 55,
            "Explicit physical battery remains useful with legacy rate-unsupported firmware.");
        tracker.Reset();
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", battery_percent = 100 }), status, 11000);
        Require(tracker.BatteryPercent is null, "USB-personality battery is never source telemetry.");
        void Epoch(uint epoch, long count, long clock, long host, int? battery) =>
            tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", input_epoch = epoch,
                input_rate_supported = true, unique_input_reports = count, input_rate_time_ms = clock,
                input_battery_percent = battery }), status, host);
        Epoch(uint.MaxValue, 100, 1000, 12000, 80);
        Epoch(uint.MaxValue, 350, 2000, 13000, 80);
        Require(tracker.Hertz == 250, "Epoch-enabled source measures normally.");
        Epoch(0, 350, 2000, 13500, null);
        Require(tracker.Hertz is null && tracker.BatteryPercent is null && tracker.InputEpoch == 0 &&
            tracker.IsFresh(13500), "Same-source reconnect/epoch wrap resets even an identical tuple and battery.");
        Epoch(0, 600, 3000, 14500, 70);
        Require(tracker.Hertz == 250 && tracker.BatteryPercent == 70, "New epoch owns an independent sample window.");
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", source_epoch = 1u,
            input_rate_supported = true, unique_input_reports = 600, input_rate_time_ms = 3000,
            input_battery_percent = -1 }), status, 15000);
        Require(tracker.Hertz is null && tracker.InputEpoch == 1 && tracker.BatteryPercent is null,
            "ESP source_epoch alias must reset same-source counters and battery.");
        tracker.Observe(JsonSerializer.SerializeToElement(new { active_input = "ds5", source_epoch = 1u, input_epoch = 2u,
            input_rate_supported = true, unique_input_reports = 850, input_rate_time_ms = 4000,
            input_battery_percent = 80 }), status, 16000);
        Require(tracker.Hertz is null && tracker.InputEpoch is null && tracker.BatteryPercent is null,
            "Conflicting epoch fields must not join different connection sessions.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
