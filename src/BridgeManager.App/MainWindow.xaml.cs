using System.Text;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BridgeManager.App;

public sealed partial class MainWindow
{
    private static readonly string[] ButtonNames =
    {
        "South (A / Cross / B)", "East (B / Circle / A)",
        "West (X / Square / Y)", "North (Y / Triangle / X)",
        "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right",
        "Left Shoulder", "Right Shoulder", "Left Trigger", "Right Trigger",
        "Back / Create / Minus", "Start / Options / Plus",
        "Left Stick", "Right Stick", "Guide / PS / Home", "Touchpad",
        "Mute", "Capture", "Left Paddle / GL", "Right Paddle / GR",
        "Left Function", "Right Function", "C"
    };

    private readonly IDeviceTransportFactory _transportFactory = new BridgeTransportFactory();
    private readonly DispatcherTimer _pollTimer = new();
    private IDeviceTransport? _transport;
    private ManagerCommandClient? _client;
    private bool _polling;
    private bool _pollFailureReported;
    private int _consecutivePollFailures;
    private bool _connecting;
    private bool _reconnecting;
    private bool _autoReconnectEnabled;
    private int _connectionGeneration;
    private uint _usbInputReports;
    private DateTimeOffset? _lastUsbInputAt;
    private DeviceDescriptor? _lastDescriptor;
    private BridgeUsbRole _expectedRole = BridgeUsbRole.Unknown;
    private BridgeStatusSnapshot _status = new();

    public MainWindow()
    {
        InitializeComponent();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
        _pollTimer.Tick += PollTimer_Tick;
        Closed += MainWindow_Closed;
        _ = RefreshDevicesAsync();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _connectionGeneration++;
        _autoReconnectEnabled = false;
        await CloseTransportAsync(clearSummary: false);
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceDescriptor descriptor)
        {
            ShowStatus("请选择 SF32LB52 USB HID 设备。", true);
            return;
        }

        _autoReconnectEnabled = true;
        _lastDescriptor = descriptor;
        _expectedRole = descriptor.UsbRole;
        await ConnectDescriptorAsync(descriptor);
    }

    private async Task<bool> ConnectDescriptorAsync(DeviceDescriptor descriptor, bool reconnecting = false)
    {
        if (_connecting)
        {
            return _client is not null;
        }

        _connecting = true;
        await CloseTransportAsync(clearSummary: !reconnecting);
        try
        {
            _transport = await _transportFactory.OpenAsync(descriptor, CancellationToken.None);
            await _transport.OpenAsync(CancellationToken.None);
            if (descriptor.SupportsInputReports &&
                _transport is IInputReportSource inputSource)
            {
                inputSource.InputReportReceived += Transport_InputReportReceived;
                inputSource.InputReportReadFailed += Transport_InputReportReadFailed;
            }
            _client = new ManagerCommandClient(_transport);
            _lastDescriptor = descriptor;
            _autoReconnectEnabled = true;
            if (descriptor.UsbRole != BridgeUsbRole.Unknown)
            {
                _expectedRole = descriptor.UsbRole;
            }
            ConnectionText.Text = reconnecting ? "已自动重连" : "已连接";
            TransportText.Text = descriptor.TransportLabel;
            InputHealthText.Text = descriptor.SupportsInputReports
                ? "等待 USB Input report"
                : "当前管理 HID 没有 USB Input report";
            _lastUsbInputAt = null;
            _consecutivePollFailures = 0;
            AppendLog($"connected {descriptor.DisplayName}");
            ShowStatus(descriptor.DiagnosticOnly
                ? "串口诊断已连接。实时输入仍需使用 USB HID。"
                : reconnecting ? "USB HID 已在重枚举后自动恢复。" : "USB HID 已连接，可以开始完整功能测试。", false);
            await RefreshStatusWithRetryAsync(logCommand: true);
            _pollTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
            AppendLog(ex.ToString());
            await CloseTransportAsync(clearSummary: !reconnecting);
            return false;
        }
        finally
        {
            _connecting = false;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(ManagerCommands.SaveSettings);

    private async void SetXboxRole_Click(object sender, RoutedEventArgs e) => await SetRoleAsync(BridgeUsbRole.Xbox);
    private async void SetDs5Role_Click(object sender, RoutedEventArgs e) => await SetRoleAsync(BridgeUsbRole.DualSense);
    private async void SetDseRole_Click(object sender, RoutedEventArgs e) => await SetRoleAsync(BridgeUsbRole.DualSenseEdge);
    private async void SetNs2Role_Click(object sender, RoutedEventArgs e) => await SetRoleAsync(BridgeUsbRole.NintendoNs2Pro);
    private async void SetInputAuto_Click(object sender, RoutedEventArgs e) => await SetInputAsync(BridgeInputPreference.Auto);
    private async void SetInputDs5_Click(object sender, RoutedEventArgs e) => await SetInputAsync(BridgeInputPreference.DualSense);
    private async void SetInputNs2_Click(object sender, RoutedEventArgs e) => await SetInputAsync(BridgeInputPreference.NintendoNs2Pro);

    private async void PairDs5_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Pair("ds5"));
    private async void ConnectDs5_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Connect("ds5"));
    private async void DisconnectDs5_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Disconnect("ds5"));
    private async void ForgetDs5_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Forget("ds5"));
    private async void PairNs2_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Pair("ns2"));
    private async void ConnectNs2_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Connect("ns2"));
    private async void DisconnectNs2_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Disconnect("ns2"));
    private async void ForgetNs2_Click(object sender, RoutedEventArgs e) => await RunConnectionCommandAsync(ManagerCommands.Forget("ns2"));

    private void RefreshInput_Click(object sender, RoutedEventArgs e)
    {
        if (_transport?.Descriptor.SupportsInputReports == true &&
            _transport is IInputReportSource)
        {
            ShowStatus($"实时输入来自 USB HID，已收到 {_usbInputReports} 包。", false);
            return;
        }
        ShowStatus("当前管理 HID 没有 USB Input report，实时面板不可用。", false);
    }

    private async void RefreshUsbAudio_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(ManagerCommands.UsbStatus);

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        await SendAndRenderAsync(ManagerCommands.RumbleTune(
            (int)ScaleBox.Value, (int)HoldBox.Value,
            (int)TickBox.Value, (int)StopsBox.Value));
        await SendAndRenderAsync(ManagerCommands.UsbRate((int)RateBox.Value));
        await SendAndRenderAsync(ManagerCommands.UsbRaw(RawSwitch.IsOn));
        await SendAndRenderAsync(ManagerCommands.WebParse(ParseSwitch.IsOn));
        await RefreshStatusAsync();
    }

    private async void RumbleLeft_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(ManagerCommands.RumbleTest("left"));
    private async void RumbleRight_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(ManagerCommands.RumbleTest("right"));
    private async void RumbleTest_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(ManagerCommands.RumbleTest("both"));
    private async void RumbleStop_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync("rumble stop");

    private async void SendRawCommand_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(CommandBox.Text);

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null && !await ConnectSelectedDeviceAsync())
        {
            return;
        }

        _pollTimer.Stop();
        var commands = new[]
        {
            ManagerCommands.BridgeStatus,
            ManagerCommands.UsbStatus,
            ManagerCommands.Settings,
            ManagerCommands.RumbleStatus,
            ManagerCommands.BridgeInput,
            ManagerCommands.MotionStatus,
            ManagerCommands.Ns2Status
        };
        var result = new StringBuilder();
        var replies = new Dictionary<string, JsonElement>();
        var passed = 0;
        foreach (var command in commands)
        {
            try
            {
                var root = await SendReadOnlyCommandWithRetryAsync(command);
                replies[command] = root;
                var ok = TryGetBool(root, "ok", out var value) && value;
                result.AppendLine($"{(ok ? "PASS" : "FAIL")}  {command}");
                if (ok) passed++;
            }
            catch (Exception ex)
            {
                result.AppendLine($"FAIL  {command}: {ex.Message}");
            }
        }
        result.AppendLine();
        result.AppendLine($"自动检查 {passed}/{commands.Length} 通过");
        if (replies.TryGetValue(ManagerCommands.UsbStatus, out var usb))
        {
            var hasAudioCounters = usb.TryGetProperty("audio_out_packets", out _) &&
                usb.TryGetProperty("audio_in_packets", out _) &&
                usb.TryGetProperty("audio_haptic_blocks", out _) &&
                usb.TryGetProperty("audio_ns2_updates", out _) &&
                usb.TryGetProperty("audio_ns2_stops", out _) &&
                usb.TryGetProperty("audio_ns2_mixed_ticks", out _);
            result.AppendLine($"{(hasAudioCounters ? "PASS" : "FAIL")}  DS5 UAC 诊断字段");
            if (hasAudioCounters)
            {
                var outPackets = GetUInt32(usb, "audio_out_packets");
                var outBytes = GetUInt32(usb, "audio_out_bytes");
                var hapticBlocks = GetUInt32(usb, "audio_haptic_blocks");
                var ns2Updates = GetUInt32(usb, "audio_ns2_updates");
                var ns2Stops = GetUInt32(usb, "audio_ns2_stops");
                var mixedTicks = GetUInt32(usb, "audio_ns2_mixed_ticks");
                result.AppendLine($"INFO  音频 OUT: {outPackets} 包 / {outBytes} 字节（播放声音后应增长）");
                result.AppendLine($"INFO  触觉分析: {hapticBlocks} 块 / NS2 更新 {ns2Updates} 次 / 停止 {ns2Stops} 次 / 混合 {mixedTicks} tick");
            }
        }
        result.AppendLine("人工检查：按键/摇杆变化、左右震动、陀螺仪方向；DS5 播放声音后确认音频 OUT 计数增长。");
        TestResultBox.Text = result.ToString();
        ShowStatus(passed == commands.Length ? "自动接口检查全部通过。" : "存在未通过的自动检查。", passed != commands.Length);
        _pollTimer.Start();
    }

    private async void PollTimer_Tick(object? sender, object e)
    {
        if (_polling || _client is null || _reconnecting) return;
        _polling = true;
        try
        {
            await RefreshStatusAsync(logCommand: false);
            if (_transport?.Descriptor.SupportsInputReports == true &&
                (_lastUsbInputAt is null ||
                 DateTimeOffset.UtcNow - _lastUsbInputAt.Value >
                     TimeSpan.FromSeconds(1)))
            {
                InputHealthText.Text = _lastUsbInputAt is null
                    ? "等待 USB Input report"
                    : "USB Input report 已超时";
            }
            _pollFailureReported = false;
            _consecutivePollFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutivePollFailures++;
            if (!_pollFailureReported)
            {
                _pollFailureReported = true;
                ShowStatus($"状态轮询暂时失败：{ex.Message}", true);
                AppendLog(ex.ToString());
            }
            if (_consecutivePollFailures >= 3 && _autoReconnectEnabled &&
                _lastDescriptor is not null)
            {
                await RecoverConnectionAsync(_lastDescriptor, "USB 连接中断");
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task SetRoleAsync(BridgeUsbRole role)
    {
        if (_client is null || _transport is null)
        {
            ShowStatus("设备未连接。", true);
            return;
        }
        _expectedRole = role;
        if (_transport.Descriptor.TransportKind == DeviceTransportKind.Serial)
        {
            await SendAndRenderAsync(ManagerCommands.SetRole(role));
            await Task.Delay(700);
            await RefreshStatusAsync();
            return;
        }

        var previous = _transport.Descriptor;
        try
        {
            await SendCommandAsync(ManagerCommands.SetRole(role),
                                   render: true, logCommand: true);
        }
        catch (Exception ex)
        {
            AppendLog($"role switch ACK unavailable: {ex}");
        }
        await RecoverConnectionAsync(previous, $"切换到 {role.DisplayName()}",
                                     immediateDelayMs: 700);
    }

    private async Task SetInputAsync(BridgeInputPreference source)
    {
        await SendAndRenderAsync(ManagerCommands.SetInput(source));
        await RefreshStatusAsync();
    }

    private async Task RunConnectionCommandAsync(string command)
    {
        await SendAndRenderAsync(command);
        await Task.Delay(300);
        await RefreshStatusAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var selectedId = (DeviceList.SelectedItem as DeviceDescriptor)?.Id;
            var devices = await _transportFactory.GetDevicesAsync(CancellationToken.None);
            DeviceList.ItemsSource = devices;
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(device =>
                device.TransportKind == DeviceTransportKind.Hid)
                ?? devices.FirstOrDefault(device => device.TransportKind == DeviceTransportKind.Serial)
                ?? devices.FirstOrDefault();
            DeviceList.SelectedItem = selected;
            var hidCount = devices.Count(device => device.TransportKind == DeviceTransportKind.Hid);
            var serialCount = devices.Count(device => device.TransportKind == DeviceTransportKind.Serial);
            ShowStatus($"扫描完成：USB HID {hidCount} 个，串口诊断候选 {serialCount} 个。", false);

            if (_client is null && !_reconnecting &&
                selected?.TransportKind == DeviceTransportKind.Hid)
            {
                await ConnectSelectedDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
            AppendLog(ex.ToString());
        }
    }

    private async Task<bool> ConnectSelectedDeviceAsync()
    {
        if (_client is not null)
        {
            return true;
        }
        if (DeviceList.SelectedItem is not DeviceDescriptor descriptor)
        {
            ShowStatus("请选择 SF32LB52 USB HID 设备。", true);
            return false;
        }

        _autoReconnectEnabled = true;
        _lastDescriptor = descriptor;
        _expectedRole = descriptor.UsbRole;
        return await ConnectDescriptorAsync(descriptor);
    }

    private async Task RefreshStatusAsync(bool logCommand = false)
    {
        if (_client is null) return;
        await SendCommandAsync(ManagerCommands.BridgeStatus, render: true, logCommand);
        await SendCommandAsync(ManagerCommands.UsbStatus, render: false, logCommand: false);
        await SendCommandAsync(ManagerCommands.RumbleStatus, render: false, logCommand: false);
    }

    private async Task RefreshStatusWithRetryAsync(bool logCommand = false)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await RefreshStatusAsync(logCommand);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastError = ex;
                AppendLog($"initial status attempt {attempt}/3 failed: {ex.Message}");
                await Task.Delay(250 * attempt);
            }
        }

        throw lastError ?? new TimeoutException("无法读取 SF32LB52 状态。");
    }

    private async Task SendAndRenderAsync(string command, bool logCommand = true)
    {
        if (_client is null)
        {
            ShowStatus("设备未连接。", true);
            return;
        }
        try
        {
            await SendCommandAsync(command, render: true, logCommand);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
            AppendLog(ex.ToString());
        }
    }

    private async Task<JsonElement> SendCommandAsync(string command, bool render, bool logCommand)
    {
        if (_client is null) throw new InvalidOperationException("设备未连接。");
        using var doc = await _client.SendCommandAsync(command, CancellationToken.None);
        var root = doc.RootElement.Clone();
        UpdateSummary(root);
        if (render)
        {
            var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            JsonBox.Text = json;
            if (logCommand) AppendLog($"> {command}\r\n{json}");
        }
        return root;
    }

    private async Task<JsonElement> SendReadOnlyCommandWithRetryAsync(string command)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendCommandAsync(command, render: false, logCommand: false);
            }
            catch (TimeoutException) when (attempt < 3)
            {
                await Task.Delay(150 * attempt);
            }
        }
    }

    private void UpdateSummary(JsonElement root)
    {
        _status = _status.Merge(BridgeStatusSnapshot.Parse(root));
        if (_status.UsbRole != BridgeUsbRole.Unknown)
        {
            _expectedRole = _status.UsbRole;
            RoleText.Text = _status.UsbRole.DisplayName();
        }
        if (_status.ActiveInput != BridgePhysicalInput.Unknown)
        {
            InputText.Text = _status.ActiveInput.DisplayName();
        }
        if (_status.InputPreference != BridgeInputPreference.Unknown)
        {
            InputPreferenceText.Text = $"偏好：{_status.InputPreference.DisplayName()}";
        }
        if (_transport?.Descriptor.SupportsInputReports == true &&
            _status.InputValid is bool inputValid)
        {
            var inputStale = _status.InputStale == true;
            if (!inputValid || inputStale)
            {
                InputHealthText.Text = inputStale
                    ? "手柄输入已失效"
                    : "等待有效手柄输入";
                AccelText.Text = "Accel：等待有效数据";
                GyroText.Text = "Gyro：等待有效数据";
            }
        }
        if (_status.ReportRateHz is double rate)
        {
            var mountedText = _status.UsbMounted is bool mounted
                ? mounted ? " · 已挂载" : " · 未挂载" : "";
            UsbText.Text = $"{rate:0} Hz{mountedText}";
            RateBox.Value = rate;
        }
        else if (_status.UsbMounted is bool mounted)
        {
            UsbText.Text = mounted ? "已挂载" : "未挂载";
        }

        if (_status.Ds5Connected.HasValue || _status.Ds5Saved.HasValue)
        {
            Ds5StatusText.Text = BuildConnectionText(
                _status.Ds5Connected, _status.Ds5Pairing, _status.Ds5Saved,
                "正在配对");
            Ns2StatusText.Text = BuildConnectionText(
                _status.Ns2Connected, _status.Ns2Scanning, _status.Ns2Saved,
                "正在扫描");
        }

        SetNumber(root, "rumble_scale_percent", ScaleBox);
        SetNumber(root, "rumble_hold_ms", HoldBox);
        SetNumber(root, "rumble_tick_ms", TickBox);
        SetNumber(root, "rumble_stop_packets", StopsBox);
        if (TryGetBool(root, "usb_raw_passthrough", out var raw)) RawSwitch.IsOn = raw;
        if (TryGetBool(root, "web_parse_reports", out var parse)) ParseSwitch.IsOn = parse;
        RumbleStatusText.Text = BuildRumbleStatus(_status);
        if (_status.AudioOutPackets.HasValue || _status.AudioSpeakerOpen.HasValue)
        {
            var errors = _status.AudioOutErrors.GetValueOrDefault() +
                         _status.AudioInErrors.GetValueOrDefault() +
                         _status.AudioOpusErrors.GetValueOrDefault();
            AudioStatusText.Text = $"DS5 音频：扬声器{(_status.AudioSpeakerOpen == true ? "已打开" : "未打开")} / 麦克风{(_status.AudioMicOpen == true ? "已打开" : "未打开")}；" +
                $"OUT {_status.AudioOutPackets.GetValueOrDefault()} 包, {_status.AudioOutBytes.GetValueOrDefault()} 字节；" +
                $"BT {_status.AudioBtReports.GetValueOrDefault()} 包 / 丢弃 {_status.AudioBtDropped.GetValueOrDefault()}；" +
                $"触觉 {_status.AudioHapticBlocks.GetValueOrDefault()} 块 -> NS2 更新 {_status.AudioNs2Updates.GetValueOrDefault()} / 停止 {_status.AudioNs2Stops.GetValueOrDefault()} / 混合 {_status.AudioNs2MixedTicks.GetValueOrDefault()} tick（{(_status.AudioNs2Active == true ? "活动" : "静止")}）；" +
                $"IN {_status.AudioInPackets.GetValueOrDefault()} 包, 错误 {errors}";
        }
        UpdateRoleControls(_status);
        var diagnostics = BridgeDiagnostics.Evaluate(_status, _transport?.Descriptor);
        DiagnosticsBox.Text = string.Join("\r\n", diagnostics.Select(check =>
            $"{check.Severity.ToString().ToUpperInvariant(),-7} {check.Name}: {check.Detail}"));
    }

    private void Transport_InputReportReceived(object? sender,
                                                DeviceInputReportEventArgs e)
    {
        if (!ControllerInputDecoder.TryDecode(e.ReportId, e.Payload,
                                               out var snapshot) ||
            snapshot is null)
        {
            return;
        }
        _usbInputReports++;
        _lastUsbInputAt = DateTimeOffset.UtcNow;
        DispatcherQueue.TryEnqueue(() => UpdateInputDisplay(snapshot));
    }

    private void Transport_InputReportReadFailed(
        object? sender,
        DeviceInputReportFailureEventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var previous = _lastDescriptor;
            if (!ReferenceEquals(sender, _transport) ||
                !_autoReconnectEnabled || previous is null) return;

            InputHealthText.Text = "USB Input report 读取失败，正在恢复";
            AppendLog($"USB input read failed: {e.Error}");
            await RecoverConnectionAsync(previous,
                "USB Input report 读取中断");
        });
    }

    private void UpdateInputDisplay(ControllerInputSnapshot input)
    {
        if (_status.InputValid != true || _status.InputStale == true)
        {
            InputHealthText.Text =
                $"USB 报告正常（{_usbInputReports} 包），但无线手柄未连接";
            ButtonsText.Text = "无";
            LeftStickText.Text = "X - / Y -";
            RightStickText.Text = "X - / Y -";
            TriggersText.Text = "L - / R -";
            BatteryText.Text = "电量：未知";
            AccelText.Text = "Accel：等待有效数据";
            GyroText.Text = "Gyro：等待有效数据";
            return;
        }

        InputHealthText.Text = $"输入正常：{input.Source}（USB 包 {_usbInputReports}）";
        var pressed = ButtonNames.Where((_, index) =>
            (input.Buttons & (1u << index)) != 0).ToArray();
        ButtonsText.Text = pressed.Length == 0 ? "无" : string.Join(" · ", pressed);
        LeftStickText.Text = $"X {input.LeftX} / Y {input.LeftY}";
        RightStickText.Text = $"X {input.RightX} / Y {input.RightY}";
        TriggersText.Text = $"L {input.LeftTrigger} / R {input.RightTrigger}";
        BatteryText.Text = input.BatteryPercent is int battery
            ? $"电量：{battery}%" : "电量：未知";
        AccelText.Text = input.MotionValid
            ? $"Accel X {input.AccelX,6} / Y {input.AccelY,6} / Z {input.AccelZ,6}"
            : "Accel：等待有效数据";
        GyroText.Text = input.MotionValid
            ? $"Gyro  X {input.GyroX,6} / Y {input.GyroY,6} / Z {input.GyroZ,6}"
            : "Gyro：等待有效数据";
    }

    private static string BuildConnectionText(
        bool? connected,
        bool? connecting,
        bool? saved,
        string connectingText)
    {
        return connected == true ? "已连接" : connecting == true ? connectingText :
            saved == true ? "已保存，未连接" : "未配对";
    }

    private async Task DisconnectAsync()
    {
        _connectionGeneration++;
        _autoReconnectEnabled = false;
        _lastDescriptor = null;
        _expectedRole = BridgeUsbRole.Unknown;
        await CloseTransportAsync(clearSummary: true);
        ShowStatus("管理连接已断开。", false);
    }

    private async Task CloseTransportAsync(bool clearSummary)
    {
        _pollTimer.Stop();
        _client = null;
        if (_transport is not null)
        {
            if (_transport is IInputReportSource inputSource)
            {
                inputSource.InputReportReceived -= Transport_InputReportReceived;
                inputSource.InputReportReadFailed -= Transport_InputReportReadFailed;
            }
            await _transport.DisposeAsync();
            _transport = null;
        }
        _usbInputReports = 0;
        _lastUsbInputAt = null;
        if (clearSummary)
        {
            _status = new BridgeStatusSnapshot();
            ConnectionText.Text = "未连接";
            TransportText.Text = "-";
            InputHealthText.Text = "未连接";
            RoleText.Text = "-";
            InputText.Text = "-";
            InputPreferenceText.Text = "偏好：-";
            UsbText.Text = "-";
            RumbleStatusText.Text = "活动输入：未读取；震动计数：不可用";
            DiagnosticsBox.Text = "";
            UpdateRoleControls(_status);
        }
    }

    private async Task RecoverConnectionAsync(
        DeviceDescriptor previous,
        string reason,
        int immediateDelayMs = 250)
    {
        if (_reconnecting || !_autoReconnectEnabled)
        {
            return;
        }

        _reconnecting = true;
        var generation = ++_connectionGeneration;
        try
        {
            _pollTimer.Stop();
            await CloseTransportAsync(clearSummary: false);
            ConnectionText.Text = "正在重新连接";
            ShowStatus($"{reason}，正在等待 USB HID 重新枚举。", false);
            AppendLog($"reconnect started: {reason}; expected role={_expectedRole}");

            for (var attempt = 1; attempt <= 20; attempt++)
            {
                if (!_autoReconnectEnabled || generation != _connectionGeneration)
                {
                    return;
                }

                await Task.Delay(attempt == 1 ? immediateDelayMs : 500);
                var devices = await _transportFactory.GetDevicesAsync(CancellationToken.None);
                DeviceList.ItemsSource = devices;
                var target = DeviceReconnectSelector.Select(devices, previous, _expectedRole);
                if (target is null)
                {
                    continue;
                }

                DeviceList.SelectedItem = target;
                if (await ConnectDescriptorAsync(target, reconnecting: true))
                {
                    AppendLog($"reconnect completed on attempt {attempt}: {target.TransportLabel}");
                    return;
                }
            }

            ConnectionText.Text = "等待设备重新出现";
            ShowStatus($"{reason}后未找到匹配的 USB HID。重新插拔后点击扫描或连接。", true);
            AppendLog("reconnect exhausted after 20 attempts");
        }
        catch (Exception ex)
        {
            ConnectionText.Text = "自动重连失败";
            ShowStatus($"自动重连失败：{ex.Message}", true);
            AppendLog(ex.ToString());
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void UpdateRoleControls(BridgeStatusSnapshot status)
    {
        XboxRoleButton.IsEnabled = status.UsbRole != BridgeUsbRole.Xbox;
        Ds5RoleButton.IsEnabled = status.UsbRole != BridgeUsbRole.DualSense;
        DseRoleButton.IsEnabled = status.UsbRole != BridgeUsbRole.DualSenseEdge;
        Ns2RoleButton.IsEnabled = status.UsbRole != BridgeUsbRole.NintendoNs2Pro;
        AutoInputButton.IsEnabled = status.InputPreference != BridgeInputPreference.Auto;
        Ds5InputButton.IsEnabled = status.InputPreference != BridgeInputPreference.DualSense;
        Ns2InputButton.IsEnabled = status.InputPreference != BridgeInputPreference.NintendoNs2Pro;
    }

    private static string BuildRumbleStatus(BridgeStatusSnapshot status)
    {
        var parts = new List<string>
        {
            $"活动输入：{status.ActiveInput.DisplayName()}"
        };
        if (status.FeedbackForwarded.HasValue || status.FeedbackFailed.HasValue)
        {
            parts.Add($"转发 {status.FeedbackForwarded.GetValueOrDefault()} / 失败 {status.FeedbackFailed.GetValueOrDefault()}");
        }
        if (status.RumbleWrites.HasValue || status.RumbleErrors.HasValue)
        {
            parts.Add($"震动写入 {status.RumbleWrites.GetValueOrDefault()} / 错误 {status.RumbleErrors.GetValueOrDefault()}");
        }
        if (status.AudioNs2Updates.HasValue || status.AudioNs2Stops.HasValue)
        {
            parts.Add($"音频触觉更新 {status.AudioNs2Updates.GetValueOrDefault()} / 停止 {status.AudioNs2Stops.GetValueOrDefault()} / 混合 {status.AudioNs2MixedTicks.GetValueOrDefault()} tick");
        }
        if (!status.RumbleOutputReports.HasValue &&
            (status.Ds5OutputReports.HasValue || status.Ds5OutputFailures.HasValue))
        {
            parts.Add($"DS5 输出 {status.Ds5OutputReports.GetValueOrDefault()} / 排队 {status.Ds5OutputQueued.GetValueOrDefault()} / 忙 {status.Ds5OutputBusy.GetValueOrDefault()} / 失败 {status.Ds5OutputFailures.GetValueOrDefault()}");
        }
        if (status.RumbleOutputReports.HasValue || status.RumbleOutputFailures.HasValue)
        {
            var connected = status.RumbleTransportConnected is bool value
                ? value ? "已连接" : "未连接" : "连接状态未知";
            var backend = status.ActiveInput == BridgePhysicalInput.DualSense
                ? $"DS5 输出 ({status.RumbleOutputBackend ?? "unknown"})"
                : status.RumbleOutputBackend ?? "震动后端";
            parts.Add($"{backend} {connected}：输出 {status.RumbleOutputReports.GetValueOrDefault()} / 排队 {status.RumbleOutputQueued.GetValueOrDefault()} / 忙 {status.RumbleOutputBusy.GetValueOrDefault()} / 失败 {status.RumbleOutputFailures.GetValueOrDefault()}");
        }
        if (parts.Count == 1)
        {
            parts.Add("震动计数：当前固件未提供");
        }
        return string.Join("；", parts);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? line : $"{LogBox.Text}\r\n{line}";
        if (LogBox.Text.Length > 24000) LogBox.Text = LogBox.Text[^16000..];
    }

    private void ShowStatus(string message, bool error)
    {
        StatusBar.Severity = error ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        StatusBar.Message = message;
    }

    private static void SetNumber(JsonElement root, string name, Microsoft.UI.Xaml.Controls.NumberBox box)
    {
        if (TryGetNumber(root, name, out var value)) box.Value = value;
    }

    private static uint GetUInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.TryGetUInt32(out var value) ? value : 0;

    private static bool TryGetNumber(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetDouble(out value);
    }

    private static bool TryGetBool(JsonElement root, string name, out bool value)
    {
        value = false;
        return root.TryGetProperty(name, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False) &&
            (value = property.GetBoolean()) == value;
    }
}
