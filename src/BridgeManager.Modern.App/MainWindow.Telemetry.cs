using BridgeManager.Core;
using System.Text.Json;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private readonly ReportRateMeter _receiverRate = new();
    private readonly SourceInputTelemetry _sourceTelemetry = new();
    private DateTimeOffset? _lastManagerInputAt;
    private ManagerInputFrame? _lastManagerInput;
    private static readonly ControllerInputSnapshot EmptyReceiverInput = new(
        "Expired receiver input", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, null);
    private bool ReceiverSourceIsFresh => _status.InputValid == true && _status.InputStale != true &&
        (!_sourceTelemetry.RateSupported || _sourceTelemetry.IsFresh(Environment.TickCount64));

    // USB personalities may contain placeholder battery values. Only explicit
    // physical-source telemetry is eligible for the receiver battery display.
    private int? ReceiverBatteryPercent => _sourceTelemetry.IsBatteryFresh(Environment.TickCount64)
        ? _sourceTelemetry.BatteryPercent : null;

    private void ObserveEffectiveInputRate(JsonElement root)
    {
        var previousEpoch = _sourceTelemetry.InputEpoch;
        _sourceTelemetry.Observe(root, _status, Environment.TickCount64);
        if (previousEpoch != _sourceTelemetry.InputEpoch)
        {
            _lastManagerInput = null;
            _lastManagerInputAt = null;
            _usbFrames.Reset();
            _lastUsbInputAt = null;
            _receiverRate.Reset();
            if (!_localInputActive) ClearInputTest("等待新连接的手柄输入");
        }
        UpdateHomeTelemetry();
    }

    private void ObserveManagerInput(JsonElement root)
    {
        if (_transport?.Descriptor.SupportsInputReports != false ||
            !ManagerInputDecoder.TryDecode(root, out var frame) || frame is null) return;
        if (frame.SampleIdentity.HasValue && _lastManagerInput?.Source == frame.Source &&
            _lastManagerInput.SampleIdentity == frame.SampleIdentity && _lastManagerInput.InputEpoch == frame.InputEpoch) return;
        _lastManagerInput = frame;
        _lastManagerInputAt = DateTimeOffset.UtcNow;
        if (!_localInputActive && InputPage.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
            UpdateInputDisplay(frame.Input, physicalSnapshot: true);
        _dynamicPageRenderer.OnInputSnapshot(frame.Input, frame.Source);
    }

    private void CheckReceiverInputFreshness()
    {
        if (_localInputActive) return;
        var received = _transport?.Descriptor.SupportsInputReports == true
            ? _lastUsbInputAt : _lastManagerInputAt;
        var stale = _client is null || !ReceiverSourceIsFresh ||
            received is null || DateTimeOffset.UtcNow - received >= TimeSpan.FromSeconds(2);
        if (stale)
        {
            ClearInputTest(_client is null ? "接收器未连接，可选择本机手柄" : "等待有效手柄输入");
            _dynamicPageRenderer.OnInputSnapshot(EmptyReceiverInput, BridgePhysicalInput.None);
            _dynamicPageRenderer.SuspendCapture();
        }
    }

    private void UpdateHomeTelemetry()
    {
        var now = Environment.TickCount64;
        _sourceTelemetry.Expire(now);
        var receiving = _transport?.Descriptor.SupportsInputReports == true &&
            _lastUsbInputAt is { } received &&
            DateTimeOffset.UtcNow - received < TimeSpan.FromSeconds(2);
        if (!receiving) _receiverRate.Reset();
        var valid = _client is not null && _status.InputValid == true && _status.InputStale != true;
        var effectiveFresh = valid && _sourceTelemetry.IsFresh(now);
        ActualReportRateText.Text = effectiveFresh && _sourceTelemetry.RateSupported &&
            _sourceTelemetry.Hertz is double hz ? $"{hz:0} Hz" : "—";
        ActualReportRateDetail.Text = _client is null ? "连接接收器后测量" :
            !_sourceTelemetry.RateSupported ? "固件未提供源帧去重计数" :
            effectiveFresh ? "接收器输入 · 按源帧时间戳去重" : "等待有效手柄输入";
        RawUsbReportRateText.Text = receiving && _receiverRate.Hertz is double rawHz
            ? $"USB 原始实收 {rawHz:0} 包/秒" : "USB 原始实收：暂不可测";
        HomeBatteryText.Text = valid && ReceiverBatteryPercent is int battery ? $"{battery}%" : "—";
        HomeBatteryDetail.Text = valid ? $"{_status.ActiveInput.DisplayName()} · {(ReceiverBatteryPercent.HasValue ? "设备上报" : "电量未知")}" : "等待有效手柄输入";
    }
}
