using BridgeManager.Core;
using System.Text.Json;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private readonly ReportRateMeter _receiverRate = new();
    private readonly ReportRateMeter _effectiveInputRate = new();
    private bool _effectiveRateSupported;
    private string? _effectiveRateSource;
    private DateTimeOffset? _effectiveRateReceivedAt;
    private int? _receiverBattery;
    private bool _hasBatteryTelemetry;
    private int? _reportedBattery;

    // The NS2 -> DS5 emulation uses a full-battery placeholder for games.
    // Only native DS5 input or explicit source telemetry is an actual reading.
    private int? ReceiverBatteryPercent => _hasBatteryTelemetry ? _reportedBattery :
        _status.ActiveInput == BridgePhysicalInput.DualSense ? _receiverBattery : null;

    private void ObserveEffectiveInputRate(JsonElement root)
    {
        // Read only fresh source-counter replies, never a merged/stale status.
        if (!root.TryGetProperty("active_input", out var source) || source.ValueKind != JsonValueKind.String) return;
        _hasBatteryTelemetry = root.TryGetProperty("input_battery_percent", out var battery);
        _reportedBattery = _hasBatteryTelemetry && battery.ValueKind == JsonValueKind.Number &&
            battery.TryGetInt32(out var percent) && percent is >= 0 and <= 100
            ? percent : null;
        var sourceName = source.GetString();
        _effectiveRateSupported = root.TryGetProperty("input_rate_supported", out var supported) &&
            supported.ValueKind == JsonValueKind.True;
        if (_effectiveRateSource != sourceName || !_effectiveRateSupported ||
            _status.InputValid != true || _status.InputStale == true)
            _effectiveInputRate.Reset();
        _effectiveRateSource = sourceName;
        _effectiveRateReceivedAt = DateTimeOffset.UtcNow;
        if (_effectiveRateSupported && _status.InputValid == true && _status.InputStale != true &&
            root.TryGetProperty("unique_input_reports", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var reports) &&
            root.TryGetProperty("input_rate_time_ms", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var milliseconds))
            _effectiveInputRate.Observe(reports, milliseconds);
        UpdateHomeTelemetry();
    }

    private void UpdateHomeTelemetry()
    {
        var receiving = _transport?.Descriptor.SupportsInputReports == true &&
            _lastUsbInputAt is { } received &&
            DateTimeOffset.UtcNow - received < TimeSpan.FromSeconds(2);
        if (!receiving) _receiverRate.Reset();
        var effectiveFresh = _client is not null && _status.InputValid == true && _status.InputStale != true &&
            _effectiveRateReceivedAt is { } observed && DateTimeOffset.UtcNow - observed < TimeSpan.FromSeconds(2);
        ActualReportRateText.Text = effectiveFresh && _effectiveRateSupported &&
            _effectiveInputRate.Hertz is double hz ? $"{hz:0} Hz" : "—";
        ActualReportRateDetail.Text = _client is null ? "连接接收器后测量" :
            !_effectiveRateSupported ? "固件未提供源帧去重计数" :
            effectiveFresh ? "接收器输入 · 按源帧时间戳去重" : "等待有效手柄输入";
        RawUsbReportRateText.Text = receiving && _receiverRate.Hertz is double rawHz
            ? $"USB 原始实收 {rawHz:0} 包/秒" : "USB 原始实收：暂不可测";
        HomeBatteryText.Text = effectiveFresh && ReceiverBatteryPercent is int battery ? $"{battery}%" : "—";
        HomeBatteryDetail.Text = effectiveFresh ? $"{_status.ActiveInput.DisplayName()} · {(ReceiverBatteryPercent.HasValue ? "设备上报" : "电量未知")}" : "等待有效手柄输入";
    }
}
