using System.Text.Json;

namespace BridgeManager.Core;

/// <summary>Tracks firmware source-frame counters, never USB sends or UI samples.</summary>
public sealed class SourceInputTelemetry
{
    public const long StaleAfterMilliseconds = 2000;
    private readonly ReportRateMeter _rate = new();
    private long? _lastCount;
    private long? _lastDeviceTime;
    private long? _observedAt;
    private long? _lastFrameAt;
    private long? _batteryAt;
    private bool _expired;

    public BridgePhysicalInput Source { get; private set; }
    public uint? InputEpoch { get; private set; }
    public bool RateSupported { get; private set; }
    public double? Hertz => _rate.Hertz;
    public int? BatteryPercent { get; private set; }

    public bool IsFresh(long now) => IsRecent(_observedAt, now) && IsRecent(_lastFrameAt, now);
    public bool IsBatteryFresh(long now) => IsRecent(_batteryAt, now);

    public void Observe(JsonElement root, BridgeStatusSnapshot status, long now)
    {
        if (Source != status.ActiveInput)
        {
            Reset();
            Source = status.ActiveInput;
        }
        if (status.InputValid != true || status.InputStale == true ||
            Source is BridgePhysicalInput.None or BridgePhysicalInput.Unknown)
        {
            Reset();
            return;
        }
        Expire(now);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("active_input", out var source) ||
            source.ValueKind != JsonValueKind.String) return;
        if (BridgeRoles.ParsePhysicalInput(source.GetString()) != Source)
        {
            Reset();
            return;
        }
        if (!ManagerInputDecoder.TryReadEpoch(root, out var epoch))
        {
            Reset();
            return;
        }
        if (epoch.HasValue && InputEpoch != epoch)
        {
            Reset();
            Source = status.ActiveInput;
            InputEpoch = epoch;
        }

        // Other replies may carry active_input without a counter. They must
        // neither erase a supported counter nor keep an old sample alive.
        var hasSupport = root.TryGetProperty("input_rate_supported", out var supported);
        if (hasSupport)
        {
            RateSupported = supported.ValueKind == JsonValueKind.True;
            if (!RateSupported)
            {
                _rate.Reset();
                _observedAt = _lastFrameAt = _lastCount = _lastDeviceTime = null;
            }
        }
        long count = 0, deviceTime = 0;
        var complete = RateSupported &&
            TryNonnegativeInt64(root, "unique_input_reports", out count) &&
            TryNonnegativeInt64(root, "input_rate_time_ms", out deviceTime);
        if (complete)
        {
            // Retain the last tuple after expiry: replaying that tuple cannot
            // revive the rate. A progressing clock with no new frames is 0 Hz.
            if (_lastCount != count || _lastDeviceTime != deviceTime)
            {
                _observedAt = now;
                if (_lastCount is null || _lastCount != count)
                {
                    _lastFrameAt = now;
                    _expired = false;
                }
                // A clock that keeps ticking while the physical source has
                // stopped can report 0 Hz briefly, but cannot keep input alive.
                if (!_expired) _rate.Observe(count, deviceTime);
                _lastCount = count;
                _lastDeviceTime = deviceTime;
            }
        }
        else if (RateSupported && (hasSupport || root.TryGetProperty("unique_input_reports", out _) ||
                 root.TryGetProperty("input_rate_time_ms", out _)))
        {
            _rate.Reset();
            _observedAt = null;
        }

        if (root.TryGetProperty("input_battery_percent", out var battery))
        {
            BatteryPercent = battery.ValueKind == JsonValueKind.Number &&
                battery.TryGetInt32(out var value) && value is >= 0 and <= 100
                ? value : null;
            // If counters are supported, cached counter replies cannot keep
            // their battery reading alive either. Legacy explicit source
            // battery telemetry is still usable independently of rate support.
            _batteryAt = RateSupported ? (IsFresh(now) ? _lastFrameAt : null) : now;
            if (!IsBatteryFresh(now)) BatteryPercent = null;
        }
    }

    public void Expire(long now)
    {
        if (!IsFresh(now) && !_expired)
        {
            _rate.Reset();
            _expired = true;
        }
        if (!IsBatteryFresh(now)) BatteryPercent = null;
    }

    public void Reset()
    {
        _rate.Reset();
        _lastCount = _lastDeviceTime = _observedAt = _lastFrameAt = _batteryAt = null;
        _expired = false;
        Source = BridgePhysicalInput.Unknown;
        InputEpoch = null;
        RateSupported = false;
        BatteryPercent = null;
    }

    private static bool IsRecent(long? observed, long now) =>
        observed is long value && now >= value && now - value < StaleAfterMilliseconds;

    private static bool TryNonnegativeInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt64(out value) && value >= 0;
    }
}
