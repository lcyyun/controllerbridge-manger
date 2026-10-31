using BridgeManager.Core;

internal static class ReportRateMeterTests
{
    public static void Run()
    {
        var meter = new ReportRateMeter();
        meter.Observe(4000, 1000);
        if (meter.Hertz is not null) throw new Exception("No completed sample window yet.");
        // The renderer may only paint a few times while hundreds of reports arrive.
        meter.Observe(4250, 2000);
        if (meter.Hertz != 250) throw new Exception("Must count reports, not paint events.");
        meter.Observe(4625, 3500);
        if (meter.Hertz != 250) throw new Exception("Use actual elapsed time when the UI is delayed.");
        meter.Observe(2, 3600);
        if (meter.Hertz is not null) throw new Exception("Reconnect must not produce a negative rate.");
        meter.Observe(1002, 4600);
        if (meter.Hertz != 1000) throw new Exception("Measure the new connection independently.");
        meter.Reset();
        if (meter.Hertz is not null) throw new Exception("Disconnected rate must be unknown.");
    }
}
