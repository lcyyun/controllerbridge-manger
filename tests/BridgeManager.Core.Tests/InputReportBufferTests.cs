using BridgeManager.Core;

internal static class InputReportBufferTests
{
    public static void Run()
    {
        var buffer = new InputReportBuffer();
        var device = new object();
        var sample = new ControllerInputSnapshot("DS5 USB input", 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, null);
        for (var i = 0; i < 10000; i++)
            buffer.Publish(device, 1, sample with { LeftX = i, Buttons = i == 200 ? 1U : 0U });
        var frame = buffer.Drain() ?? throw new Exception("Missing input frame");
        Require(frame.ReportCount == 10000 && frame.Input.LeftX == 9999,
            "Frame was stale or count lost");
        Require(frame.Input.Buttons == 0 && frame.PressedButtons == 1,
            "Short press between paints was lost");
        Require(buffer.Drain() is null, "Draining left a backlog");
        buffer.Publish(device, 1, sample with { Buttons = 2U });
        Require(buffer.Drain()?.PressedButtons == 2, "Second press lost");
        buffer.Publish(device, 1, sample with { Buttons = 2U });
        Require(buffer.Drain()?.PressedButtons == 0, "Held button repeated as a press");
        buffer.Publish(device, 2, sample with { Buttons = 4U });
        var next = buffer.Drain()!;
        Require(next.Generation == 2 && next.ReportCount == 1 && next.PressedButtons == 4,
            "Connection generation did not isolate input");
        buffer.Publish(new object(), 2, sample);
        Require(buffer.Drain()?.ReportCount == 1, "Device replacement leaked counters");
        buffer.Reset();
        Require(buffer.Drain() is null, "Reset retained pending input");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
