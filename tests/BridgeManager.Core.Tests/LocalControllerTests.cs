using System.Buffers.Binary;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using Windows.Gaming.Input;

internal static class LocalControllerTests
{
    // The parent runner registers this entry point. No test discovers, opens, or
    // writes a physical controller; lifecycle tests inject in-memory readers.
    public static void Run()
    {
        CanonicalContract();
        GamepadMapping();
        NumericNormalization();
        DualSenseUsb();
        DualSenseBluetooth();
        DualSenseSimpleBluetooth();
        DualSenseDpadAndAxes();
        RejectsMalformedReports();
        PollingReaderIsPacedAsync().GetAwaiter().GetResult();
        EventReaderDrainsBeforePacingAsync().GetAwaiter().GetResult();
        SelectionAndUnsupportedAsync().GetAwaiter().GetResult();
        DisposeCancelsDiscoveryAsync().GetAwaiter().GetResult();
        StopReleasesAndDropsStaleAsync().GetAwaiter().GetResult();
        CancellationReleasesAsync().GetAwaiter().GetResult();
        SwitchingDropsOldInputAsync().GetAwaiter().GetResult();
        ReportsFailuresWithoutInputAsync().GetAwaiter().GetResult();
        StopDropsLateFailureAsync().GetAwaiter().GetResult();
    }

    private static void CanonicalContract()
    {
        Equal(string.Join(",", new[]
        {
            "south", "east", "west", "north",
            "dpad_up", "dpad_down", "dpad_left", "dpad_right",
            "left_shoulder", "right_shoulder", "left_trigger", "right_trigger",
            "back", "start", "left_stick", "right_stick", "guide", "touchpad",
            "mute", "capture", "left_paddle", "right_paddle",
            "left_function", "right_function", "c"
        }), string.Join(",", BridgeButtonMapping.ControlIds));
    }

    private static void GamepadMapping()
    {
        (GamepadButtons Flag, int Bit)[] mappings =
        [
            (GamepadButtons.A, 0), (GamepadButtons.B, 1),
            (GamepadButtons.X, 2), (GamepadButtons.Y, 3),
            (GamepadButtons.DPadUp, 4), (GamepadButtons.DPadDown, 5),
            (GamepadButtons.DPadLeft, 6), (GamepadButtons.DPadRight, 7),
            (GamepadButtons.LeftShoulder, 8), (GamepadButtons.RightShoulder, 9),
            (GamepadButtons.View, 12), (GamepadButtons.Menu, 13),
            (GamepadButtons.LeftThumbstick, 14), (GamepadButtons.RightThumbstick, 15)
        ];
        foreach (var (flag, bit) in mappings)
            Equal(1u << bit, LocalControllerInputConverter.FromGamepad(
                new GamepadReading { Buttons = flag }).Buttons);

        var reading = new GamepadReading
        {
            Buttons = (GamepadButtons)uint.MaxValue,
            LeftThumbstickX = -1,
            LeftThumbstickY = 1,
            RightThumbstickX = 0.5,
            RightThumbstickY = -0.5,
            LeftTrigger = 1,
            RightTrigger = 0.5
        };
        var snapshot = LocalControllerInputConverter.FromGamepad(reading);
        Equal(0xffffu, snapshot.Buttons);
        Equal(-32768, snapshot.LeftX);
        Equal(32767, snapshot.LeftY);
        Equal(16384, snapshot.RightX);
        Equal(-16384, snapshot.RightY);
        Equal(65535, snapshot.LeftTrigger);
        Equal(32768, snapshot.RightTrigger);
        Equal(false, snapshot.MotionValid);
        Equal<int?>(null, snapshot.BatteryPercent);
        Equal(0, snapshot.AccelX | snapshot.AccelY | snapshot.AccelZ |
            snapshot.GyroX | snapshot.GyroY | snapshot.GyroZ);
        Check(snapshot.Source.Contains("local", StringComparison.Ordinal), "Local source label");
        // WGI's four unlabelled paddles cannot be guessed into two canonical slots.
        Equal(0u, LocalControllerInputConverter.FromGamepad(new GamepadReading
        {
            Buttons = GamepadButtons.Paddle1 | GamepadButtons.Paddle2 |
                GamepadButtons.Paddle3 | GamepadButtons.Paddle4
        }).Buttons);
    }

    private static void NumericNormalization()
    {
        Equal(-32768, LocalControllerInputConverter.Axis(-2));
        Equal(32767, LocalControllerInputConverter.Axis(2));
        Equal(0, LocalControllerInputConverter.Axis(0));
        Equal(0, LocalControllerInputConverter.Trigger(-2));
        Equal(65535, LocalControllerInputConverter.Trigger(2));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Throws<InvalidDataException>(() => LocalControllerInputConverter.Axis(invalid));
            Throws<InvalidDataException>(() => LocalControllerInputConverter.Trigger(invalid));
        }
        var previous = int.MinValue;
        for (var i = -1000; i <= 1000; i++)
        {
            var value = LocalControllerInputConverter.Axis(i / 1000d);
            Check(value >= previous && value is >= -32768 and <= 32767, "Axis monotonic/range");
            previous = value;
        }
    }

    private static void DualSenseUsb()
    {
        var report = UsbReport();
        var input = Decode(report, true);
        Equal("DS5 USB input (local)", input.Source);
        var expected = ((1u << 19) - 1) & ~(1u << 5) & ~(1u << 6);
        Equal(expected | (0xfu << 20), input.Buttons);
        Equal(expected, Decode(report, false).Buttons);
        Equal(-32768, input.LeftX);
        Equal(32767, input.LeftY);
        Equal(32767, input.RightX);
        Equal(-32768, input.RightY);
        Equal(65535, input.LeftTrigger);
        Equal(32896, input.RightTrigger);
        Equal(-3000, input.GyroX);
        Equal(1234, input.GyroY);
        Equal(-2345, input.GyroZ);
        Equal(-32768, input.AccelX);
        Equal(0, input.AccelY);
        Equal(32767, input.AccelZ);
        Equal(true, input.MotionValid);
        Equal<int?>(75, input.BatteryPercent);

        foreach (var (status, percent) in new (byte, int?)[]
        {
            (0x00, 5), (0x0a, 100), (0x1a, 100), (0x20, 100),
            (0x0b, null), (0x1f, null), (0xa5, null), (0xff, null)
        })
        {
            report[53] = status;
            Equal(percent, Decode(report).BatteryPercent);
        }
        Array.Clear(report, 16, 12);
        Equal(true, Decode(report).MotionValid); // Presence, not nonzero magnitude.
    }

    private static void DualSenseBluetooth()
    {
        // Fixed HIDP CRC fixture (0x79ace183), not generated by the converter.
        var report = Convert.FromHexString(
            "31a60000ffffff8025f1fff7000000000048f4d204d7f600800000ff7f000000" +
            "0000000000000000000000000000000000000000000017000000000000000000" +
            "0000000000000000000083e1ac79");
        Equal(78, report.Length);
        var input = Decode(report, true);
        Equal(Decode(UsbReport(), true) with { Source = "DS5 Bluetooth input (local)" }, input);
        report[1] ^= 1; // The transport prefix participates in CRC too.
        Reject(report);
        report[1] ^= 1;
        report[20] ^= 1;
        Reject(report);
        report[20] ^= 1;
        report[^1] ^= 1;
        Reject(report);
    }

    private static void DualSenseSimpleBluetooth()
    {
        byte[] report = [0x01, 0, 0, 255, 255, 0xf1, 0xff, 0xff, 255, 128];
        var input = Decode(report, true);
        Equal(false, input.MotionValid);
        Equal<int?>(null, input.BatteryPercent);
        Equal(0, input.AccelX | input.AccelY | input.AccelZ |
            input.GyroX | input.GyroY | input.GyroZ);
        Equal(0u, input.Buttons & ~((1u << 18) - 1));
        Equal(65535, input.LeftTrigger);
        Equal(32896, input.RightTrigger);
        Equal(32767, input.LeftY);
        Equal(-32768, input.RightY);
        Equal(1u << 17, input.Buttons & (1u << 17));
        var padded = Enumerable.Repeat((byte)0xff, 78).ToArray();
        report.CopyTo(padded, 0);
        Equal(input, Decode(padded, true));
        for (var counter = 0; counter < 64; counter++)
        {
            report[7] = (byte)((counter << 2) | 3);
            Equal(input, Decode(report, true));
        }
    }

    private static void DualSenseDpadAndAxes()
    {
        uint[] hats =
        [
            1u << 4, (1u << 4) | (1u << 7), 1u << 7,
            (1u << 7) | (1u << 5), 1u << 5, (1u << 5) | (1u << 6),
            1u << 6, (1u << 6) | (1u << 4), 0
        ];
        var report = new byte[64];
        report[0] = 1;
        for (byte hat = 0; hat <= 15; hat++)
        {
            report[8] = hat;
            Equal(hat < hats.Length ? hats[hat] : 0u, Decode(report).Buttons);
        }
        var lastX = int.MinValue;
        var lastY = int.MaxValue;
        for (var value = 0; value <= 255; value++)
        {
            report[1] = report[2] = report[3] = report[4] = (byte)value;
            var input = Decode(report);
            Check(input.LeftX >= lastX && input.LeftY <= lastY, "DS5 axes monotonic");
            Check(input.LeftX is >= -32768 and <= 32767, "DS5 X range");
            Check(input.LeftY is >= -32768 and <= 32767, "DS5 Y range");
            Equal(input.LeftX, input.RightX);
            Equal(input.LeftY, input.RightY);
            if (value == 128)
            {
                Equal(0, input.LeftX);
                Equal(0, input.LeftY);
            }
            lastX = input.LeftX;
            lastY = input.LeftY;
        }
    }

    private static void RejectsMalformedReports()
    {
        foreach (var length in new[] { 0, 1, 9, 11, 53, 63, 65, 77, 79 })
        {
            var report = new byte[length];
            if (length > 0) report[0] = 1;
            Reject(report);
        }
        foreach (var id in new byte[] { 0, 5, 0x31, 0xff })
        {
            var report = UsbReport();
            report[0] = id;
            Reject(report);
        }
    }

    private static async Task PollingReaderIsPacedAsync()
    {
        using var reader = new ImmediateReader();
        Equal(TimeSpan.FromMilliseconds(8), ((ILocalControllerReader)reader).PollingInterval);
        var inputs = 0;
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paced = new TaskCompletionSource<(TimeSpan Interval, int Reads, int Inputs)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(reader, (interval, token) =>
        {
            paced.TrySetResult((interval, reader.ReadCount, Volatile.Read(ref inputs)));
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        service.InputReceived += (_, _) => Interlocked.Increment(ref inputs);
        service.ReadFailed += (_, error) => failed.TrySetResult(error);
        var device = (await service.GetDevicesAsync(CancellationToken.None))[0];
        await service.StartAsync(device, CancellationToken.None);
        var firstDelay = await paced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal((TimeSpan.FromMilliseconds(8), 1, 1), firstDelay);
        // The injected delay remains blocked until Stop, so these counts do not
        // depend on scheduler timing or the machine's clock resolution.
        Equal(1, reader.ReadCount);
        Equal(1, inputs);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, reader.DisposeCount);
        Equal(false, failed.Task.IsCompleted);
    }

    private static async Task EventReaderDrainsBeforePacingAsync()
    {
        // An empty burst also exercises timeout/null backoff without any input.
        foreach (var reportCount in new[] { 0, 64 })
        {
            using var reader = new BurstReader(reportCount);
            Equal(TimeSpan.Zero, ((ILocalControllerReader)reader).PollingInterval);
            var sources = new List<string>();
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var paced = new TaskCompletionSource<(TimeSpan Interval, int Reads, string[] Sources)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = Service(reader, (interval, token) =>
            {
                paced.TrySetResult((interval, reader.ReadCount, sources.ToArray()));
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            service.InputReceived += (_, input) => sources.Add(input.Source);
            service.ReadFailed += (_, error) => failed.TrySetResult(error);
            var device = (await service.GetDevicesAsync(CancellationToken.None))[0];
            Equal(false, device.SupportsRumble);
            await service.StartAsync(device, CancellationToken.None);
            var firstDelay = await paced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(TimeSpan.FromMilliseconds(8), firstDelay.Interval);
            Equal(reportCount + 1, firstDelay.Reads);
            Equal(reportCount, firstDelay.Sources.Length);
            for (var index = 0; index < reportCount; index++)
                Equal($"test:burst:{index}", firstDelay.Sources[index]);
            Equal(reportCount + 1, reader.ReadCount);
            Equal(false, await service.SetRumbleAsync(1, 1, CancellationToken.None));
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, reader.DisposeCount);
            Equal(false, failed.Task.IsCompleted);
        }
    }

    private static async Task SelectionAndUnsupportedAsync()
    {
        var opens = 0;
        var device = new LocalControllerDevice("test:unsupported", "Unsupported raw controller", "Test");
        await using var service = new LocalControllerService(_ =>
        [
            new(device, () =>
            {
                Interlocked.Increment(ref opens);
                throw new InvalidOperationException("Must not open");
            }, "No known normalized raw mapping.")
        ]);
        var inputs = 0;
        service.InputReceived += (_, _) => Interlocked.Increment(ref inputs);
        Equal(1, (await service.GetDevicesAsync(CancellationToken.None)).Count);
        Equal(0, opens);
        await ThrowsAsync<NotSupportedException>(() => service.StartAsync(device, CancellationToken.None));
        await ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            device with { Id = "test:unlisted" }, CancellationToken.None));
        Equal(false, await service.SetRumbleAsync(1, 1, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => service.GetDevicesAsync(canceled.Token));
        await ThrowsAsync<OperationCanceledException>(() => service.StartAsync(device, canceled.Token));
        Equal(0, opens);
        Equal(0, inputs);
        await service.DisposeAsync();
        await service.StopAsync();
        await ThrowsAsync<ObjectDisposedException>(() => service.GetDevicesAsync(CancellationToken.None));
        await ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(device, CancellationToken.None));
    }

    private static async Task StopReleasesAndDropsStaleAsync()
    {
        using var reader = new BlockingReader();
        await using var service = Service(reader);
        var inputs = 0;
        var failures = 0;
        service.InputReceived += (_, _) => Interlocked.Increment(ref inputs);
        service.ReadFailed += (_, _) => Interlocked.Increment(ref failures);
        var device = (await service.GetDevicesAsync(CancellationToken.None))[0];
        await service.StartAsync(device, CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, reader.DisposeCount);
        Equal(0, inputs);
        Equal(0, failures);
        await service.StopAsync();
        Equal(1, reader.DisposeCount);
    }

    private static async Task DisposeCancelsDiscoveryAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new LocalControllerService(_ =>
        {
            entered.TrySetResult();
            try
            {
                if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test discovery was not released.");
                return Array.Empty<LocalControllerEndpoint>();
            }
            finally { finished.TrySetResult(); }
        });
        var discovery = service.GetDevicesAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await ThrowsAsync<OperationCanceledException>(() => discovery);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task CancellationReleasesAsync()
    {
        using var reader = new BlockingReader();
        using var canceled = new CancellationTokenSource();
        await using var service = Service(reader);
        var inputs = 0;
        service.InputReceived += (_, _) => Interlocked.Increment(ref inputs);
        var device = (await service.GetDevicesAsync(CancellationToken.None))[0];
        await service.StartAsync(device, canceled.Token);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await reader.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, reader.DisposeCount);
        Equal(0, inputs);
    }

    private static async Task SwitchingDropsOldInputAsync()
    {
        using var first = new BlockingReader();
        using var second = new ImmediateReader();
        var firstDevice = new LocalControllerDevice("test:first", "First", "Test");
        var secondDevice = new LocalControllerDevice("test:second", "Second", "Test");
        await using var service = new LocalControllerService(_ =>
        [
            new(firstDevice, () => first), new(secondDevice, () => second)
        ]);
        var received = new TaskCompletionSource<ControllerInputSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.InputReceived += (_, input) => received.TrySetResult(input);
        await service.GetDevicesAsync(CancellationToken.None);
        await service.StartAsync(firstDevice, CancellationToken.None);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StartAsync(secondDevice, CancellationToken.None);
        var input = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal("test:new", input.Source);
        await service.StopAsync();
        Equal(1, first.DisposeCount);
        Equal(1, second.DisposeCount);
    }

    private static async Task ReportsFailuresWithoutInputAsync()
    {
        foreach (var failOpening in new[] { true, false })
        {
            using var reader = new ImmediateReader(failReading: true);
            var device = new LocalControllerDevice("test:failure", "Failure", "Test");
            await using var service = new LocalControllerService(_ =>
            [
                new(device, () => failOpening
                    ? throw new IOException("test open failure") : reader)
            ]);
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputs = 0;
            service.ReadFailed += (_, _) => throw new InvalidOperationException("Subscriber failure");
            service.ReadFailed += (_, error) => failed.TrySetResult(error);
            service.InputReceived += (_, _) => Interlocked.Increment(ref inputs);
            await service.GetDevicesAsync(CancellationToken.None);
            await service.StartAsync(device, CancellationToken.None);
            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(error.Contains(failOpening ? "test open failure" : "test read failure",
                StringComparison.Ordinal), "Actionable failure");
            await service.StopAsync();
            Equal(0, inputs);
            Equal(failOpening ? 0 : 1, reader.DisposeCount);
        }
    }

    private static async Task StopDropsLateFailureAsync()
    {
        using var reader = new BlockingReader(failOnClose: true);
        await using var service = Service(reader);
        var failures = 0;
        service.ReadFailed += (_, _) => Interlocked.Increment(ref failures);
        var device = (await service.GetDevicesAsync(CancellationToken.None))[0];
        await service.StartAsync(device, CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, reader.DisposeCount);
        Equal(0, failures);
    }

    private static LocalControllerService Service(ILocalControllerReader reader,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(_ =>
    [
        new(new LocalControllerDevice("test:memory", "Memory", "Test"), () => reader)
    ], delay);

    private static byte[] UsbReport()
    {
        var report = new byte[64];
        report[0] = 1;
        byte[] controls = [0, 0, 255, 255, 255, 128, 37, 0xf1, 0xff, 0xf7];
        controls.CopyTo(report, 1);
        var body = report.AsSpan(1);
        BinaryPrimitives.WriteInt16LittleEndian(body[15..], -3000);
        BinaryPrimitives.WriteInt16LittleEndian(body[17..], 1234);
        BinaryPrimitives.WriteInt16LittleEndian(body[19..], -2345);
        BinaryPrimitives.WriteInt16LittleEndian(body[21..], -32768);
        BinaryPrimitives.WriteInt16LittleEndian(body[23..], 0);
        BinaryPrimitives.WriteInt16LittleEndian(body[25..], 32767);
        body[52] = 0x17;
        return report;
    }

    private static ControllerInputSnapshot Decode(byte[] report, bool edge = false)
    {
        Check(LocalControllerInputConverter.TryDecodeDualSense(report, edge,
            out var input, out var error), error ?? "DualSense decode failed");
        Equal<string?>(null, error);
        return input ?? throw new InvalidOperationException("Missing snapshot");
    }

    private static void Reject(byte[] report)
    {
        Equal(false, LocalControllerInputConverter.TryDecodeDualSense(report, false,
            out var input, out var error));
        Equal<ControllerInputSnapshot?>(null, input);
        Check(!string.IsNullOrWhiteSpace(error), "Malformed report needs an explanation");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Local controller: expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Local controller: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Local controller: expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Local controller: expected {typeof(T).Name}.");
    }

    private sealed class BlockingReader(bool failOnClose = false) : ILocalControllerReader
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        internal int DisposeCount => Volatile.Read(ref _disposed);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControllerInputSnapshot Read(CancellationToken token)
        {
            Entered.TrySetResult();
            // Deliberately ignores cancellation and returns late input/failure.
            if (!_release.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test reader was not closed.");
            if (failOnClose) throw new IOException("test late failure");
            return Decode(UsbReport()) with { Source = "test:stale" };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.TrySetResult();
            Closed.TrySetResult();
        }
    }

    private sealed class ImmediateReader(bool failReading = false) : ILocalControllerReader
    {
        private int _disposed;
        private int _reads;
        internal int DisposeCount => Volatile.Read(ref _disposed);
        internal int ReadCount => Volatile.Read(ref _reads);

        public ControllerInputSnapshot Read(CancellationToken token)
        {
            Interlocked.Increment(ref _reads);
            if (failReading) throw new IOException("test read failure");
            return Decode(UsbReport()) with { Source = "test:new" };
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class BurstReader(int reportCount) : ILocalControllerReader
    {
        private readonly ControllerInputSnapshot _input = Decode(UsbReport());
        private int _disposed;
        private int _reads;
        public TimeSpan PollingInterval => TimeSpan.Zero;
        internal int DisposeCount => Volatile.Read(ref _disposed);
        internal int ReadCount => Volatile.Read(ref _reads);

        public ControllerInputSnapshot? Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _reads) - 1;
            return index < reportCount ? _input with { Source = $"test:burst:{index}" } : null;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
