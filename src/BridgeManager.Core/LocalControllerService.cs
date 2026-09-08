using System.Diagnostics;
using HidSharp;
using Windows.Gaming.Input;

namespace BridgeManager.Core;

public sealed record LocalControllerDevice(
    string Id, string DisplayName, string TransportLabel, bool SupportsRumble = false);

/// <summary>
/// Read-only PC input, independent of receiver discovery. Events run on a worker
/// thread; UI handlers should dispatch asynchronously. Start schedules opening;
/// open/read/disconnection failures arrive through ReadFailed without neutral input.
/// </summary>
public sealed class LocalControllerService : IAsyncDisposable
{
    private readonly Func<CancellationToken, IReadOnlyList<LocalControllerEndpoint>> _discover;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _events = new();
    private Dictionary<string, LocalControllerEndpoint> _devices = new(StringComparer.Ordinal);
    private Session? _active;
    private bool _disposed;

    public LocalControllerService() : this(Discover) { }

    internal LocalControllerService(
        Func<CancellationToken, IReadOnlyList<LocalControllerEndpoint>> discover,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _discover = discover;
        _delay = delay ?? ((interval, token) => Task.Delay(interval, token));
    }

    public event EventHandler<ControllerInputSnapshot>? InputReceived;
    public event EventHandler<string>? ReadFailed;

    public async Task<IReadOnlyList<LocalControllerDevice>> GetDevicesAsync(
        CancellationToken cancellationToken)
    {
        CancellationTokenSource query;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            query = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
        using (query)
        {
            var token = query.Token;
            // Discovery can enter a native driver. Do not hold the lifecycle
            // gate while waiting, and let disposal interrupt the wait.
            var found = await Task.Run(() => _discover(token), token)
                .WaitAsync(token).ConfigureAwait(false);
            await _lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                _devices = found.GroupBy(item => item.Device.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                return _devices.Values.Select(item => item.Device)
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    public async Task StartAsync(LocalControllerDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_devices.TryGetValue(device.Id, out var endpoint))
                throw new InvalidOperationException(
                    "The selected controller is no longer listed. Refresh devices and select it again.");
            if (endpoint.UnsupportedReason is { } reason)
                throw new NotSupportedException(reason);

            var session = new Session(cancellationToken);
            lock (_events) _active = session;
            // Do not pass the token to Task.Run: finally must execute even if
            // cancellation occurs before the worker gets its first timeslice.
            session.Worker = Task.Run(() => ReadAsync(session, endpoint));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public Task<bool> SetRumbleAsync(double left, double right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // This input-only backend never writes motors, including on stop/dispose.
        return Task.FromResult(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            await StopCoreAsync().ConfigureAwait(false);
            _devices.Clear();
            _shutdown.Dispose();
        }
        finally
        {
            // Keep the semaphore usable for concurrent/repeated Stop/Dispose calls.
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        Session? session;
        lock (_events)
        {
            session = _active;
            _active = null;
        }
        if (session is null) return;
        session.Cancel();
        try
        {
            await session.Worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A misbehaving driver cannot block shutdown. The worker owns its
            // final cleanup and all of its later events are already invalidated.
        }
    }

    private async Task ReadAsync(Session session, LocalControllerEndpoint endpoint)
    {
        var token = session.Cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            using var reader = endpoint.Open();
            using var interrupt = token.Register(() =>
            {
                try { reader.Dispose(); }
                catch (Exception) { /* A failed driver close must not break cancellation. */ }
            });
            token.ThrowIfCancellationRequested();
            var lastInput = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                var input = reader.Read(token);
                token.ThrowIfCancellationRequested();
                if (input is not null)
                {
                    lastInput.Restart();
                    Publish(session, input);
                }
                else if (lastInput.Elapsed > TimeSpan.FromSeconds(3))
                {
                    throw new IOException(
                        "No valid controller input arrived for 3 seconds. The device may be disconnected, " +
                        "not initialized, or held exclusively by another application.");
                }
                var interval = reader.PollingInterval;
                // Blocking HID reads already pace valid reports. Only polling
                // readers and empty/timeout results need an additional delay.
                if (input is null && interval < TimeSpan.FromMilliseconds(8))
                    interval = TimeSpan.FromMilliseconds(8);
                if (interval > TimeSpan.Zero)
                    await _delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PublishFailure(session, $"{endpoint.Device.DisplayName}: {ex.Message}");
        }
        finally
        {
            session.Cancellation.Dispose();
        }
    }

    private void Publish(Session session, ControllerInputSnapshot input)
    {
        lock (_events)
        {
            if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                return;
            if (InputReceived is not { } handlers) return;
            foreach (EventHandler<ControllerInputSnapshot> handler in handlers.GetInvocationList())
            {
                if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                    break;
                try { handler(this, input); }
                catch (Exception) { /* Subscriber failures do not change device state. */ }
            }
        }
    }

    private void PublishFailure(Session session, string error)
    {
        lock (_events)
        {
            if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                return;
            if (ReadFailed is not { } handlers) return;
            foreach (EventHandler<string> handler in handlers.GetInvocationList())
            {
                if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                    break;
                try { handler(this, error); }
                catch (Exception) { /* Preserve reader cleanup even if a subscriber fails. */ }
            }
        }
    }

    private static IReadOnlyList<LocalControllerEndpoint> Discover(CancellationToken token)
    {
        var found = new List<LocalControllerEndpoint>();
        var failures = new List<Exception>();
        try
        {
            foreach (var raw in RawGameController.RawGameControllers.ToArray())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var pad = Gamepad.FromGameController(raw);
                    var id = raw.NonRoamableId;
                    if (string.IsNullOrWhiteSpace(id))
                        throw new IOException("Windows did not supply a stable controller ID.");
                    var name = string.IsNullOrWhiteSpace(raw.DisplayName)
                        ? $"Controller {raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4}"
                        : raw.DisplayName;
                    var transport = raw.IsWireless ? "Wireless" : "Wired";
                    var device = new LocalControllerDevice("wgi:" + id, name,
                        $"{transport} (Windows {(pad is null ? "RawGameController" : "Gamepad")})");
                    found.Add(pad is null
                        ? new LocalControllerEndpoint(device,
                            () => throw new NotSupportedException(),
                            "Windows has no normalized Gamepad mapping for this raw controller. " +
                            "Raw axis/button order is device-specific and is not guessed. " +
                            "For DualSense, select its direct HID entry.")
                        : new LocalControllerEndpoint(device, () => new GamepadReader(pad)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(ex);
        }

        try
        {
            foreach (var hid in DeviceList.Local.GetHidDevices())
            {
                token.ThrowIfCancellationRequested();
                var sony = hid.VendorID == 0x054c && hid.ProductID is 0x0ce6 or 0x0df2;
                var nintendo = hid.VendorID == 0x057e && hid.ProductID == 0x2069;
                if (!sony && !nintendo) continue;
                try
                {
                    var descriptor = hid.GetReportDescriptor();
                    if (!descriptor.DeviceItems.Any(item => item.Usages.ContainsValue(0x00010005)))
                        continue;
                    // These are bridge-management collections, not native PC controllers.
                    if (descriptor.FeatureReports.Any(report =>
                        (report.ReportID is ManagerProtocol.FeatureReportId or
                            ManagerProtocol.DualSenseFeatureReportId) &&
                        report.Length >= ManagerProtocol.FeatureReportLength))
                        continue;
                    var bluetooth = sony && descriptor.InputReports.Any(report => report.ReportID == 0x31);
                    var transport = sony
                        ? bluetooth ? "Bluetooth (direct HID)" : "USB (direct HID)"
                        : "HID (native protocol unsupported)";
                    var fallback = sony
                        ? hid.ProductID == 0x0df2 ? "DualSense Edge" : "DualSense"
                        : "Nintendo Switch 2 Pro Controller";
                    string name;
                    try { name = hid.GetProductName(); }
                    catch (IOException) { name = fallback; }
                    var device = new LocalControllerDevice("hid:" + hid.DevicePath,
                        string.IsNullOrWhiteSpace(name) ? fallback : name, transport);
                    found.Add(nintendo
                        ? new LocalControllerEndpoint(device,
                            () => throw new NotSupportedException(),
                            "Native Switch 2 Pro input requires device-specific initialization, " +
                            "which this read-only backend does not implement. No input state was inferred.")
                        : new LocalControllerEndpoint(device, () => new DualSenseReader(hid)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(ex);
        }
        token.ThrowIfCancellationRequested();
        if (found.Count == 0 && failures.Count > 0)
            throw new IOException("Controller enumeration failed. " +
                string.Join(" ", failures.Select(ex => ex.Message)), failures[0]);
        return found;
    }

    private sealed class Session(CancellationToken token)
    {
        internal CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        internal Task Worker { get; set; } = Task.CompletedTask;

        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class GamepadReader(Gamepad pad) : ILocalControllerReader
    {
        private int _disposed;

        public ControllerInputSnapshot? Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!Gamepad.Gamepads.Contains(pad))
                throw new IOException("The selected Windows Gamepad was disconnected.");
            var reading = pad.GetCurrentReading();
            return reading.Timestamp == 0 ? null : LocalControllerInputConverter.FromGamepad(reading);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class DualSenseReader : ILocalControllerReader
    {
        private readonly HidStream _stream;
        private readonly byte[] _buffer;
        private readonly bool _isEdge;
        private int _disposed;

        public TimeSpan PollingInterval => TimeSpan.Zero;

        internal DualSenseReader(HidDevice device)
        {
            _buffer = new byte[device.GetMaxInputReportLength()];
            if (_buffer.Length is < 10 or > 4096)
                throw new NotSupportedException("Unsupported DualSense HID input report size.");
            _isEdge = device.ProductID == 0x0df2;
            if (!device.TryOpen(out HidStream? stream) || stream is null)
                throw new IOException("Cannot open the selected controller. It may be disconnected " +
                    "or held exclusively by another application.");
            _stream = stream;
            try { _stream.ReadTimeout = 250; }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public ControllerInputSnapshot? Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int length;
            try { length = _stream.Read(_buffer, 0, _buffer.Length); }
            catch (TimeoutException) { return null; }
            token.ThrowIfCancellationRequested();
            if (length == 0) throw new IOException("The selected DualSense was disconnected.");
            if (!LocalControllerInputConverter.TryDecodeDualSense(
                _buffer.AsSpan(0, length), _isEdge, out var input, out var error))
                throw new InvalidDataException(error);
            return input;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose();
        }
    }
}

internal sealed record LocalControllerEndpoint(
    LocalControllerDevice Device, Func<ILocalControllerReader> Open, string? UnsupportedReason = null);

internal interface ILocalControllerReader : IDisposable
{
    // Zero is opt-in for blocking/event readers; immediate readers poll safely.
    TimeSpan PollingInterval => TimeSpan.FromMilliseconds(8);
    ControllerInputSnapshot? Read(CancellationToken token);
}
