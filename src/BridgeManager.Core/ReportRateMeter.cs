namespace BridgeManager.Core;

/// <summary>Measures received reports over monotonic time, independent of painting and configured rate.</summary>
public sealed class ReportRateMeter
{
    private long? _startedAt;
    private long _baseline;
    private long _lastCount;
    public double? Hertz { get; private set; }

    public void Observe(long reportCount, long monotonicMilliseconds)
    {
        if (_startedAt is null || reportCount < _lastCount || monotonicMilliseconds < _startedAt.Value)
        {
            _startedAt = monotonicMilliseconds;
            _baseline = _lastCount = reportCount;
            Hertz = null;
            return;
        }
        _lastCount = reportCount;
        var elapsed = monotonicMilliseconds - _startedAt.Value;
        if (elapsed < 1000) return;
        Hertz = (reportCount - _baseline) * 1000d / elapsed;
        _baseline = reportCount;
        _startedAt = monotonicMilliseconds;
    }

    public void Reset()
    {
        _startedAt = null;
        _baseline = _lastCount = 0;
        Hertz = null;
    }
}
