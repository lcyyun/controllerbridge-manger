namespace BridgeManager.Core;

/// <summary>Measures received reports over monotonic time, independent of painting and configured rate.</summary>
public sealed class ReportRateMeter
{
    private long? _startedAt;
    private long _baseline;
    private long _lastCount;
    private long _lastTime;
    public double? Hertz { get; private set; }

    public void Observe(long reportCount, long monotonicMilliseconds)
    {
        if (reportCount < 0 || monotonicMilliseconds < 0) { Reset(); return; }
        // A device reboot or uint32 counter/clock wrap starts a new window.
        // Do not guess a modular delta that could instead be a new session.
        if (_startedAt is null || reportCount < _lastCount || monotonicMilliseconds < _lastTime)
        {
            _startedAt = monotonicMilliseconds;
            _baseline = _lastCount = reportCount;
            _lastTime = monotonicMilliseconds;
            Hertz = null;
            return;
        }
        _lastCount = reportCount;
        _lastTime = monotonicMilliseconds;
        var elapsed = monotonicMilliseconds - _startedAt.Value;
        if (elapsed < 1000) return;
        Hertz = (reportCount - _baseline) * 1000d / elapsed;
        _baseline = reportCount;
        _startedAt = monotonicMilliseconds;
    }

    public void Reset()
    {
        _startedAt = null;
        _baseline = _lastCount = _lastTime = 0;
        Hertz = null;
    }
}
