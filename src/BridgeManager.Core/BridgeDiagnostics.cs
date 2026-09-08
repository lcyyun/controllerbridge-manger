namespace BridgeManager.Core;

public enum DiagnosticSeverity
{
    Info,
    Pass,
    Warning,
    Fail
}

public sealed record DiagnosticCheck(DiagnosticSeverity Severity, string Name, string Detail);

public static class BridgeDiagnostics
{
    public static IReadOnlyList<DiagnosticCheck> Evaluate(
        BridgeStatusSnapshot status,
        DeviceDescriptor? descriptor = null)
    {
        var checks = new List<DiagnosticCheck>();
        if (descriptor is not null)
        {
            checks.Add(new DiagnosticCheck(
                descriptor.DiagnosticOnly ? DiagnosticSeverity.Warning : DiagnosticSeverity.Pass,
                "管理传输",
                descriptor.DiagnosticOnly
                    ? "串口诊断模式；实时输入和 USB 重枚举验证不可用"
                    : $"USB HID {descriptor.VendorId:x4}:{descriptor.ProductId:x4}"));
        }

        checks.Add(status.UsbRole == BridgeUsbRole.Unknown
            ? new DiagnosticCheck(DiagnosticSeverity.Warning, "USB 角色", "固件未返回可识别角色")
            : new DiagnosticCheck(DiagnosticSeverity.Pass, "USB 角色", status.UsbRole.DisplayName()));

        if (status.UsbMounted is bool mounted)
        {
            checks.Add(new DiagnosticCheck(
                mounted && status.UsbSuspended != true ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning,
                "USB 状态",
                !mounted ? "未挂载" : status.UsbSuspended == true ? "已挂起" : "已挂载"));
        }

        var inputDetail = status.ActiveInput.DisplayName();
        var inputSeverity = status.InputValid == true && status.InputStale != true
            ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning;
        checks.Add(new DiagnosticCheck(inputSeverity, "物理输入", status.InputStale == true
            ? $"{inputDetail}，数据已过期" : status.InputValid == true ? $"{inputDetail}，输入有效" : $"{inputDetail}，等待有效输入"));

        if (status.FeedbackForwarded.HasValue || status.FeedbackFailed.HasValue)
        {
            checks.Add(new DiagnosticCheck(
                status.FeedbackFailed.GetValueOrDefault() == 0 ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning,
                "震动转发",
                $"活动输入 {inputDetail}；成功 {status.FeedbackForwarded.GetValueOrDefault()} / 失败 {status.FeedbackFailed.GetValueOrDefault()}"));
        }

        if (!status.RumbleOutputReports.HasValue &&
            (status.Ds5OutputReports.HasValue || status.Ds5OutputFailures.HasValue))
        {
            checks.Add(new DiagnosticCheck(
                status.Ds5OutputFailures.GetValueOrDefault() == 0 ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning,
                "DS5 输出",
                $"发送 {status.Ds5OutputReports.GetValueOrDefault()} / 排队 {status.Ds5OutputQueued.GetValueOrDefault()} / 忙 {status.Ds5OutputBusy.GetValueOrDefault()} / 失败 {status.Ds5OutputFailures.GetValueOrDefault()}"));
        }

        if (status.RumbleOutputReports.HasValue || status.RumbleOutputFailures.HasValue)
        {
            var backend = status.ActiveInput == BridgePhysicalInput.DualSense
                ? $"DS5 输出 ({status.RumbleOutputBackend ?? "unknown"})"
                : status.RumbleOutputBackend ?? "unknown";
            checks.Add(new DiagnosticCheck(
                status.RumbleOutputFailures.GetValueOrDefault() == 0 ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning,
                "活动震动后端",
                $"{backend}：发送 {status.RumbleOutputReports.GetValueOrDefault()} / 排队 {status.RumbleOutputQueued.GetValueOrDefault()} / 忙 {status.RumbleOutputBusy.GetValueOrDefault()} / 失败 {status.RumbleOutputFailures.GetValueOrDefault()}"));
        }

        if (status.UsbRole is BridgeUsbRole.DualSense or BridgeUsbRole.DualSenseEdge)
        {
            if (status.AudioCapable == false)
            {
                checks.Add(new DiagnosticCheck(DiagnosticSeverity.Warning, "DS5 USB Audio", "当前角色不支持音频"));
            }
            else if (status.AudioOutPackets.HasValue)
            {
                var errors = status.AudioOutErrors.GetValueOrDefault() +
                             status.AudioInErrors.GetValueOrDefault() +
                             status.AudioOpusErrors.GetValueOrDefault();
                checks.Add(new DiagnosticCheck(
                    errors == 0 ? DiagnosticSeverity.Pass : DiagnosticSeverity.Warning,
                    "DS5 USB Audio",
                    $"OUT {status.AudioOutPackets.GetValueOrDefault()} 包 / BT {status.AudioBtReports.GetValueOrDefault()} 包 / 错误 {errors}"));
            }
        }

        return checks;
    }
}
