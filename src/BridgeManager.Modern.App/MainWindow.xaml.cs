using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;

namespace BridgeManager.Modern;

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

    private readonly BridgeTransportFactory _transportFactory;
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DynamicModulePageRenderer _dynamicPageRenderer;
    private readonly List<NavigationViewItem> _dynamicNavigationItems = [];
    private BridgeFirmwareModuleRegistry _moduleRegistry;
    private IBridgeFirmwareModule _module;
    private IDeviceTransport? _transport;
    private ManagerCommandClient? _client;
    private bool _polling;
    private bool _pollFailureReported;
    private int _consecutivePollFailures;
    private bool _connecting;
    private bool _reconnecting;
    private bool _autoReconnectEnabled;
    private int _connectionGeneration;
    private long _usbInputReports;
    private DateTimeOffset? _lastUsbInputAt;
    private DeviceDescriptor? _lastDescriptor;
    private BridgeUsbRole _expectedRole = BridgeUsbRole.Unknown;
    private BridgeStatusSnapshot _status = new();
    private SetupWizardWindow? _setupWizardWindow;
    private readonly bool _discoverDevices;
    private readonly InputReportBuffer _usbFrames = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private int _transportGeneration;
    private bool _refreshingDevices;
    private string? _roleControlsKey;
    private string? _wirelessControlsKey;

    public MainWindow(bool discoverDevices = true)
    {
        _discoverDevices = discoverDevices;
        _moduleRegistry = CreateModuleRegistry();
        _transportFactory = new BridgeTransportFactory(
            () => _moduleRegistry.GetHidDeviceProfiles());
        _module = _moduleRegistry.Fallback;
        InitializeComponent();
        foreach (var page in new[] { HomePage, InputPage, FeedbackPage, AdvancedPage, DynamicModulePage })
            PageScrolling.Attach(page);
        _dynamicPageRenderer = new DynamicModulePageRenderer(
            ExecuteModuleOperationAsync, ShowStatus);
        _dynamicPageRenderer.MappingWriteStateChanged += MappingWriteStateChanged;
        InitializeIndependentInput();
        AppWindow.Resize(new SizeInt32(1260, 820));
        _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
        _pollTimer.Tick += PollTimer_Tick;
        Closed += MainWindow_Closed;
        AppWindow.Closing += (_, args) =>
        {
            if (_setupWizardWindow?.IsFlashing != true) return;
            args.Cancel = true;
            ShowStatus("固件烧录尚未结束，请保持连接并等待结果。", true);
        };
        InitializeModuleUi();
        MainNavigation.SelectedItem = HomeNavItem;
        ShowPage("home");
        if (_discoverDevices)
        {
            _ = RefreshDevicesAsync();
            _ = RefreshInputSourcesAsync();
        }
        if (_discoverDevices && !SetupWizardWindow.HasCompletedSetup())
        {
            DispatcherQueue.TryEnqueue(OpenSetupWizard);
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _inputClosing = true;
        _windowCancellation.Cancel();
        _pollTimer.Stop();
        _inputPaintTimer.Stop();
        _setupWizardWindow?.Close();
        _setupWizardWindow = null;
        _connectionGeneration++;
        _autoReconnectEnabled = false;
        try
        {
            await DisposeIndependentInputAsync();
            await CloseTransportAsync(clearSummary: false);
        }
        catch (Exception ex) { AppDiagnostics.Write("window-close", ex); }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceDescriptor descriptor)
        {
            ShowStatus("请选择一个管理设备。", true);
            return;
        }

        _autoReconnectEnabled = true;
        _lastDescriptor = descriptor;
        _expectedRole = descriptor.UsbRole;
        await ConnectDescriptorAsync(descriptor);
    }

    private async Task<bool> ConnectDescriptorAsync(DeviceDescriptor descriptor, bool reconnecting = false)
    {
        if (_inputClosing || _setupWizardWindow?.IsFlashing == true)
        {
            ShowStatus("固件烧录期间暂不建立管理连接。", false);
            return false;
        }
        if (_connecting)
        {
            return _client is not null;
        }

        _connecting = true;
        await CloseTransportAsync(clearSummary: !reconnecting);
        try
        {
            ActivateModule(_moduleRegistry.Resolve(descriptor));
            var opened = await _transportFactory.OpenAsync(descriptor, _windowCancellation.Token);
            if (_inputClosing)
            {
                await opened.DisposeAsync();
                return false;
            }
            _transport = opened;
            await _transport.OpenAsync(_windowCancellation.Token);
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
            SetConnectionAppearance(true,
                reconnecting ? "已重新连接" : "设备已连接");
            if (!_localInputActive) InputHealthText.Text = descriptor.SupportsInputReports
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
            if (_inputClosing) return false;
            SetConnectionAppearance(false, "连接失败");
            ShowStatus(FriendlyError(ex), true);
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
        await SendAndRenderAsync(_module.SaveSettingsCommand ?? "settings save");

    private async void RoleOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BridgeUsbRoleOption option })
        {
            await SetRoleAsync(option);
        }
    }

    private async void InputOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BridgeInputSourceOption option })
        {
            await SetInputAsync(option);
        }
    }

    private async void WirelessCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WirelessCommandAction action })
        {
            await RunConnectionCommandAsync(action.Command);
        }
    }

    private void RefreshInput_Click(object sender, RoutedEventArgs e)
    {
        if (_localInputActive)
        {
            _ = RefreshInputSourcesAsync();
            return;
        }
        if (_transport?.Descriptor.SupportsInputReports == true &&
            _transport is IInputReportSource)
        {
            ShowStatus($"实时输入来自 USB HID，已收到 {_usbInputReports} 包。", false);
            return;
        }
        ShowStatus("当前管理 HID 没有 USB Input report，实时面板不可用。", false);
    }

    private async void RefreshUsbAudio_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(_module.StatusCommands.FirstOrDefault(command =>
            string.Equals(command, ManagerCommands.UsbStatus,
                          StringComparison.OrdinalIgnoreCase)) ??
            _module.PrimaryStatusCommand);

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        var commands = _module.BuildSettingsCommands(GetModuleSettings());
        if (commands.Count == 0)
        {
            ShowStatus("当前固件模块没有提供通用设置命令。", false);
            return;
        }
        foreach (var command in commands)
        {
            await SendAndRenderAsync(command);
        }
        await RefreshStatusSafelyAsync();
    }

    private async void RumbleLeft_Click(object sender, RoutedEventArgs e) =>
        await SendModuleRumbleAsync("left");
    private async void RumbleRight_Click(object sender, RoutedEventArgs e) =>
        await SendModuleRumbleAsync("right");
    private async void RumbleTest_Click(object sender, RoutedEventArgs e) =>
        await SendModuleRumbleAsync("both");
    private async void RumbleStop_Click(object sender, RoutedEventArgs e) =>
        await SendModuleRumbleAsync("stop");

    private async void SendRawCommand_Click(object sender, RoutedEventArgs e) =>
        await SendAndRenderAsync(CommandBox.Text);

    private void NavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        _dynamicPageRenderer?.SuspendCapture();
        var page = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString()
                   ?? "home";
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        InputPage.Visibility = page == "input" ? Visibility.Visible : Visibility.Collapsed;
        FeedbackPage.Visibility = page == "feedback" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        var modulePage = page.StartsWith("module:", StringComparison.Ordinal);
        DynamicModulePage.Visibility = modulePage
            ? Visibility.Visible : Visibility.Collapsed;
        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "input" => ("手柄测试", "本机手柄与接收器输入"),
            "feedback" => ("震动与音频", "反馈测试与触觉状态"),
            "advanced" => ("高级诊断", "设备选择、检查结果与原始数据"),
            _ when modulePage => DynamicPageTitle(page["module:".Length..]),
            _ => ("概览", "设备、角色与无线连接")
        };
        if (modulePage)
        {
            _ = RenderDynamicPageAsync(page["module:".Length..]);
        }
        if (page == "input") _ = RefreshInputSourcesAsync();
        else _ = StopLocalRumbleAsync();
    }

    private void NavigationView_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag?.ToString() ==
            "wizard")
        {
            OpenSetupWizard();
        }
    }

    private void OpenSetupWizard()
    {
        if (_setupWizardWindow is not null)
        {
            _setupWizardWindow.Activate();
            return;
        }

        var window = new SetupWizardWindow(
            _moduleRegistry,
            () => _status,
            ApplySetupWizardRequestAsync,
            InstallModulePackageFromWizard,
            () =>
            {
                MainNavigation.SelectedItem = HomeNavItem;
                ShowPage("home");
                _ = RefreshDevicesAsync();
            }, prepareFlashAsync: DisconnectAsync);
        _setupWizardWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_setupWizardWindow, window))
            {
                _setupWizardWindow = null;
            }
        };
        window.Activate();
    }

    private BridgeFirmwareModuleRegistry InstallModulePackageFromWizard(
        string packagePath)
    {
        var installed = BridgeModulePackageInstaller.Install(
            packagePath, InstalledModuleRoot);
        var previousModuleId = _module.Id;
        _moduleRegistry = CreateModuleRegistry();
        _module = _lastDescriptor is null
            ? _moduleRegistry.Find(previousModuleId) ?? _moduleRegistry.Fallback
            : _moduleRegistry.Resolve(_lastDescriptor);
        InitializeModuleUi();
        ShowStatus(
            $"已安装单文件兼容包 {installed.ModuleId} {installed.ModuleVersion}。",
            false);
        return _moduleRegistry;
    }

    private async Task<WizardApplyResult> ApplySetupWizardRequestAsync(
        WizardApplyRequest request)
    {
        RateBox.Value = request.ReportRateHz;
        if (_client is null || !_module.Id.Equals(request.Module.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(false,
                $"{request.Module.DisplayName} 的选择已保存在本机。连接对应设备后可在概览中应用设置。");
        }

        var success = true;
        if (request.Input is not null)
        {
            success &= await SendAndRenderAsync(request.Input.Command);
        }
        foreach (var command in _module.BuildSettingsCommands(
                     GetModuleSettings()))
        {
            success &= await SendAndRenderAsync(command);
        }
        if (request.SaveToDevice &&
            !string.IsNullOrWhiteSpace(_module.SaveSettingsCommand))
        {
            success &= await SendAndRenderAsync(_module.SaveSettingsCommand!);
        }
        if (request.Role is not null && request.Role.Role != _status.UsbRole)
        {
            await SetRoleAsync(request.Role);
        }
        return success
            ? new(true, "基本设置已应用，返回管理器即可开始测试。")
            : new(false, "部分设置未收到设备确认，选择已经保存在本机。");
    }

    private async void OpenClassicManager_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "classic-manager", "BridgeManager.App.exe"),
                Path.Combine(directory.FullName, "BridgeManager-classic-win-x64", "BridgeManager.App.exe"),
                Path.Combine(directory.FullName, "dist", "BridgeManager-classic-win-x64", "BridgeManager.App.exe")
            };
            var executable = candidates.FirstOrDefault(File.Exists);
            if (executable is not null)
            {
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                ShowStatus("经典诊断版已打开。", false);
                return;
            }
            directory = directory.Parent;
        }
        ShowStatus("未找到经典诊断版，请从 classic 文件夹启动。", true);
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null && !await ConnectSelectedDeviceAsync())
        {
            return;
        }

        _pollTimer.Stop();
        var commands = _module.SelfTestCommands;
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
        result.AppendLine($"自动检查 {passed}/{commands.Count} 通过");
        if (_module.Capabilities.Has(BridgeCapability.UsbAudio) &&
            replies.TryGetValue(ManagerCommands.UsbStatus, out var usb))
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
        var manualChecks = new List<string>();
        if (_module.Capabilities.Has(BridgeCapability.LiveInput))
            manualChecks.Add("按键、摇杆与扳机变化");
        if (_module.Capabilities.Has(BridgeCapability.Rumble))
            manualChecks.Add("左右震动");
        if (_module.Capabilities.Has(BridgeCapability.Motion))
            manualChecks.Add("陀螺仪方向");
        if (_module.Capabilities.Has(BridgeCapability.UsbAudio))
            manualChecks.Add("播放声音后音频 OUT 计数增长");
        result.AppendLine($"人工检查：{string.Join("、", manualChecks)}。");
        TestResultBox.Text = result.ToString();
        ShowStatus(passed == commands.Count ? "自动接口检查全部通过。" : "存在未通过的自动检查。", passed != commands.Count);
        _pollTimer.Start();
    }

    private async void PollTimer_Tick(object? sender, object e)
    {
        if (_inputClosing || _polling || _client is null || _reconnecting) return;
        _polling = true;
        try
        {
            await RefreshStatusAsync(logCommand: false);
            if (!_localInputActive && _transport?.Descriptor.SupportsInputReports == true &&
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

    private async Task SetRoleAsync(BridgeUsbRoleOption option)
    {
        if (_client is null || _transport is null)
        {
            ShowStatus("设备未连接。", true);
            return;
        }
        _expectedRole = option.Role;
        if (_transport.Descriptor.TransportKind == DeviceTransportKind.Serial)
        {
            await SendAndRenderAsync(option.Command);
            await Task.Delay(700);
            await RefreshStatusSafelyAsync();
            return;
        }

        var previous = _transport.Descriptor;
        try
        {
            await SendCommandAsync(option.Command,
                                   render: true, logCommand: true);
        }
        catch (Exception ex)
        {
            AppendLog($"role switch ACK unavailable: {ex}");
        }
        await RecoverConnectionAsync(previous, $"切换到 {option.DisplayName}",
                                     immediateDelayMs: 700);
    }

    private async Task SetInputAsync(BridgeInputSourceOption option)
    {
        if (await SendAndRenderAsync(option.Command))
        {
            await RefreshStatusSafelyAsync();
        }
    }

    private async Task RunConnectionCommandAsync(string command)
    {
        if (await SendAndRenderAsync(command))
        {
            await Task.Delay(300);
            await RefreshStatusSafelyAsync();
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_inputClosing || _refreshingDevices || !_discoverDevices ||
            _setupWizardWindow?.IsFlashing == true) return;
        _refreshingDevices = true;
        try
        {
            var selectedId = (DeviceList.SelectedItem as DeviceDescriptor)?.Id;
            var devices = await _transportFactory.GetDevicesAsync(_windowCancellation.Token);
            if (_inputClosing || _setupWizardWindow?.IsFlashing == true) return;
            DeviceList.ItemsSource = devices;
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(device =>
                device.TransportKind == DeviceTransportKind.Hid)
                ?? devices.FirstOrDefault(device => device.TransportKind == DeviceTransportKind.Serial)
                ?? devices.FirstOrDefault();
            DeviceList.SelectedItem = selected;
            if (_client is null && selected is not null)
            {
                ActivateModule(_moduleRegistry.Resolve(selected));
            }
            var hidCount = devices.Count(device => device.TransportKind == DeviceTransportKind.Hid);
            var serialCount = devices.Count(device => device.TransportKind == DeviceTransportKind.Serial);
            if (_client is null && hidCount == 0)
            {
                ConnectionText.Text = "未连接接收器";
                TransportText.Text = "可先测试本机手柄";
                SetConnectionAppearance(false, "未连接接收器");
            }
            ShowStatus($"扫描完成：USB HID {hidCount} 个，串口诊断候选 {serialCount} 个；当前模块 {_module.DisplayName}。", false);

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
        finally { _refreshingDevices = false; }
    }

    private async Task<bool> ConnectSelectedDeviceAsync()
    {
        if (_client is not null)
        {
            return true;
        }
        if (DeviceList.SelectedItem is not DeviceDescriptor descriptor)
        {
            ShowStatus("请选择一个管理设备。", true);
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
        for (var index = 0; index < _module.StatusCommands.Count; index++)
        {
            await SendCommandAsync(_module.StatusCommands[index],
                render: index == 0, logCommand: index == 0 && logCommand);
        }
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

        throw lastError ?? new TimeoutException("无法读取固件状态。");
    }

    private async Task<bool> SendAndRenderAsync(string command,
                                                bool logCommand = true)
    {
        if (_client is null)
        {
            ShowStatus("设备未连接。", true);
            return false;
        }
        try
        {
            await SendCommandAsync(command, render: true, logCommand);
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
            AppendLog(ex.ToString());
            return false;
        }
    }

    private async Task RefreshStatusSafelyAsync()
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(FriendlyError(ex), true);
            AppendLog(ex.ToString());
        }
    }

    private async Task<JsonElement> SendCommandAsync(string command, bool render, bool logCommand)
    {
        if (_inputClosing || _client is not { } client)
            throw new InvalidOperationException("设备未连接。");
        var generation = _transportGeneration;
        using var doc = await client.SendCommandAsync(command, _windowCancellation.Token);
        if (_inputClosing || generation != _transportGeneration || !ReferenceEquals(client, _client))
            throw new OperationCanceledException("管理连接已更换，忽略旧回复。");
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
        if (_inputClosing) return;
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
        if (!_localInputActive && _transport?.Descriptor.SupportsInputReports == true &&
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

        UpdateWirelessControllers();

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
        SetConnectionAppearance(_client is not null,
            _client is null ? "未连接" : "设备已连接");
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
        if (_inputClosing || sender is null || !ReferenceEquals(sender, _transport)) return;
        _usbFrames.Publish(sender, Volatile.Read(ref _transportGeneration), snapshot);
    }

    private void Transport_InputReportReadFailed(
        object? sender,
        DeviceInputReportFailureEventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var previous = _lastDescriptor;
            if (_inputClosing || !ReferenceEquals(sender, _transport) ||
                !_autoReconnectEnabled || previous is null) return;

            if (!_localInputActive) InputHealthText.Text = "USB Input report 读取失败，正在恢复";
            AppendLog($"USB input read failed: {e.Error}");
            await RecoverConnectionAsync(previous,
                "USB Input report 读取中断");
        });
    }

    private void UpdateInputDisplay(ControllerInputSnapshot input, bool local = false)
    {
        if (!local && (_status.InputValid != true || _status.InputStale == true))
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
            ResetInputVisuals();
            return;
        }

        var sourceName = input.Source.StartsWith("DS5", StringComparison.Ordinal)
            ? "DualSense" : input.Source.StartsWith("NS2", StringComparison.Ordinal)
                ? "NS2Pro" : input.Source;
        InputHealthText.Text = local ? $"输入正常：{_selectedLocalDevice?.DisplayName ?? sourceName}"
            : $"输入正常：{sourceName}（USB 包 {_usbInputReports}）";
        if (!local) InputRateText.Text = $"USB 输入 · {_usbInputReports} 包";
        SetControllerArtwork(input.Source);
        var pressed = ButtonNames.Where((_, index) =>
            (input.Buttons & (1u << index)) != 0).ToArray();
        ButtonsText.Text = pressed.Length == 0 ? "无" : string.Join(" · ", pressed);
        LeftStickText.Text = $"X {input.LeftX} / Y {input.LeftY}";
        RightStickText.Text = $"X {input.RightX} / Y {input.RightY}";
        TriggersText.Text = $"L {input.LeftTrigger} / R {input.RightTrigger}";
        UpdateStickVisual(LeftStickDot, input.LeftX, input.LeftY);
        UpdateStickVisual(RightStickDot, input.RightX, input.RightY);
        LeftTriggerBar.Value = input.LeftTrigger;
        RightTriggerBar.Value = input.RightTrigger;
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
            saved == true ? "已保存，未连接" : saved == false ? "未配对" : "尚未读取";
    }

    private static void UpdateStickVisual(FrameworkElement dot, int x, int y)
    {
        const double center = 80.0;
        const double travel = 70.0;
        Canvas.SetLeft(dot, center + Math.Clamp(x / 32768.0, -1.0, 1.0) * travel);
        Canvas.SetTop(dot, center - Math.Clamp(y / 32768.0, -1.0, 1.0) * travel);
    }

    private void ResetInputVisuals()
    {
        Canvas.SetLeft(LeftStickDot, 80.0);
        Canvas.SetTop(LeftStickDot, 80.0);
        Canvas.SetLeft(RightStickDot, 80.0);
        Canvas.SetTop(RightStickDot, 80.0);
        LeftTriggerBar.Value = 0;
        RightTriggerBar.Value = 0;
    }

    private void SetConnectionAppearance(bool connected, string label)
    {
        ConnectionBadgeText.Text = label;
        ConnectionDot.Fill = new SolidColorBrush(connected
            ? Color.FromArgb(255, 38, 166, 154)
            : Color.FromArgb(255, 126, 126, 126));
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
        _dynamicPageRenderer.InvalidateConnection();
        _pollTimer.Stop();
        _client = null;
        Interlocked.Increment(ref _transportGeneration);
        var previousTransport = _transport;
        _transport = null;
        _usbFrames.Reset();
        if (previousTransport is not null)
        {
            if (previousTransport is IInputReportSource inputSource)
            {
                inputSource.InputReportReceived -= Transport_InputReportReceived;
                inputSource.InputReportReadFailed -= Transport_InputReportReadFailed;
            }
            await previousTransport.DisposeAsync();
        }
        _usbInputReports = 0;
        _lastUsbInputAt = null;
        if (clearSummary && !_inputClosing)
        {
            _status = new BridgeStatusSnapshot();
            ConnectionText.Text = "未连接";
            TransportText.Text = "-";
            if (!_localInputActive) ClearInputTest("接收器未连接，可选择本机手柄");
            RoleText.Text = "-";
            InputText.Text = "-";
            InputPreferenceText.Text = "偏好：-";
            UsbText.Text = "-";
            RumbleStatusText.Text = "活动输入：未读取；震动计数：不可用";
            DiagnosticsBox.Text = "";
            SetConnectionAppearance(false, "未连接");
            if (!_localInputActive) ResetInputVisuals();
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
            SetConnectionAppearance(false, "正在重新连接");
            ShowStatus($"{reason}，正在等待 USB HID 重新枚举。", false);
            AppendLog($"reconnect started: {reason}; expected role={_expectedRole}");

            for (var attempt = 1; attempt <= 20; attempt++)
            {
                if (!_autoReconnectEnabled || generation != _connectionGeneration)
                {
                    return;
                }

                await Task.Delay(attempt == 1 ? immediateDelayMs : 500, _windowCancellation.Token);
                var devices = await _transportFactory.GetDevicesAsync(_windowCancellation.Token);
                if (_inputClosing || !_autoReconnectEnabled || generation != _connectionGeneration) return;
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
            SetConnectionAppearance(false, "等待设备");
            ShowStatus($"{reason}后未找到匹配的 USB HID。重新插拔后点击扫描或连接。", true);
            AppendLog("reconnect exhausted after 20 attempts");
        }
        catch (Exception ex)
        {
            if (_inputClosing) return;
            ConnectionText.Text = "自动重连失败";
            SetConnectionAppearance(false, "重连失败");
            ShowStatus($"自动重连失败：{ex.Message}", true);
            AppendLog(ex.ToString());
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private static BridgeFirmwareModuleRegistry CreateModuleRegistry() =>
        new(moduleDirectories: GetModuleDirectories());

    private static IEnumerable<string> GetModuleDirectories()
    {
        var packagedModules = Path.Combine(AppContext.BaseDirectory, "modules");
        if (Directory.Exists(packagedModules))
        {
            yield return packagedModules;
            yield break;
        }

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var sourceModules = Path.Combine(cursor.FullName, "apps",
                "BridgeManager", "modules");
            if (Directory.Exists(sourceModules))
            {
                yield return sourceModules;
                break;
            }
            cursor = cursor.Parent;
        }

        // External module installation is intentionally not part of the current
        // product surface. The package loader remains available for first-party
        // release tooling and can be enabled later with an explicit policy.
    }

    private static string InstalledModuleRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControllerBridge", "modules");

    private void InitializeModuleUi()
    {
        ModuleList.ItemsSource = _moduleRegistry.Modules
            .Where(module => module.Id != _moduleRegistry.Fallback.Id)
            .Select(module => new ModuleListItem(module.DisplayName,
                $"{module.BoardFamily} · {module.ModuleVersion} · API {module.RuntimeApiVersion}",
                module))
            .ToArray();
        ActivateModule(_module);

        foreach (var issue in _moduleRegistry.LoadIssues)
        {
            AppendLog($"module load skipped: {issue.Path}: {issue.Message}");
        }
    }

    private void ActivateModule(IBridgeFirmwareModule module)
    {
        _module = module;
        ModulePaneText.Text = module.Id == _moduleRegistry.Fallback.Id
            ? "通用管理器" : module.BoardFamily;
        ActiveModuleText.Text =
            $"模块：{module.DisplayName} {module.ModuleVersion}";

        var hasRoles = module.Capabilities.Has(
                           BridgeCapability.UsbRoleSelection) &&
                       module.UsbRoles.Count > 0;
        var hasInputs = module.Capabilities.Has(
                            BridgeCapability.InputSourceSelection) &&
                        module.InputSources.Count > 0;
        var hasWireless = module.Capabilities.Has(
                              BridgeCapability.WirelessControllers) &&
                          module.WirelessControllers.Count > 0;
        UsbRoleSection.Visibility = hasRoles
            ? Visibility.Visible : Visibility.Collapsed;
        RoleOptionsList.Visibility = hasRoles
            ? Visibility.Visible : Visibility.Collapsed;
        WirelessSection.Visibility = hasWireless
            ? Visibility.Visible : Visibility.Collapsed;
        RoleAndWirelessSection.Visibility = hasRoles || hasWireless
            ? Visibility.Visible : Visibility.Collapsed;
        InputSelectionSection.Visibility = hasInputs
            ? Visibility.Visible : Visibility.Collapsed;
        SaveSettingsButton.Visibility = module.Capabilities.Has(
            BridgeCapability.Settings) ? Visibility.Visible : Visibility.Collapsed;
        InputNavItem.Visibility = Visibility.Visible;
        FeedbackNavItem.Visibility = module.Capabilities.Has(BridgeCapability.Rumble) ||
                                     module.Capabilities.Has(BridgeCapability.UsbAudio)
            ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = module.Capabilities.Has(BridgeCapability.UsbAudio)
            ? Visibility.Visible : Visibility.Collapsed;
        SettingsControls.Visibility = module.Capabilities.Has(BridgeCapability.Settings)
            ? Visibility.Visible : Visibility.Collapsed;

        RebuildDynamicNavigation(module);

        UpdateRoleControls(_status);
        UpdateWirelessControllers();
    }

    private BridgeModuleSettings GetModuleSettings() => new(
        (int)RateBox.Value,
        RawSwitch.IsOn,
        ParseSwitch.IsOn,
        (int)ScaleBox.Value,
        (int)HoldBox.Value,
        (int)TickBox.Value,
        (int)StopsBox.Value);

    private async Task SendModuleRumbleAsync(string target)
    {
        var command = target == "stop"
            ? "rumble stop"
            : _module.BuildRumbleCommand(target);
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowStatus("当前固件模块没有提供震动测试命令。", false);
            return;
        }
        await SendAndRenderAsync(command);
    }

    private void ShowPage(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        InputPage.Visibility = page == "input" ? Visibility.Visible : Visibility.Collapsed;
        FeedbackPage.Visibility = page == "feedback" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        DynamicModulePage.Visibility = page.StartsWith(
            "module:", StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildDynamicNavigation(IBridgeFirmwareModule module)
    {
        foreach (var item in _dynamicNavigationItems)
        {
            MainNavigation.MenuItems.Remove(item);
        }
        _dynamicNavigationItems.Clear();

        var insertion = MainNavigation.MenuItems.IndexOf(AdvancedNavItem);
        if (insertion < 0) insertion = MainNavigation.MenuItems.Count;
        foreach (var page in module.Pages)
        {
            if (IsMappingPage(page) && _dynamicNavigationItems.Any(item =>
                Equals(item.Tag, "module:mapping"))) continue;
            var item = new NavigationViewItem
            {
                Content = IsMappingPage(page) ? "按键映射" : page.Label,
                Tag = IsMappingPage(page) ? "module:mapping" : $"module:{page.Id}",
                Icon = new FontIcon { Glyph = ModulePageGlyph(page.Icon) }
            };
            MainNavigation.MenuItems.Insert(insertion++, item);
            _dynamicNavigationItems.Add(item);
        }
    }

    private async Task RenderDynamicPageAsync(string pageId)
    {
        if (pageId == "mapping")
        {
            await RenderMappingWorkspaceAsync();
            return;
        }
        var page = _module.Pages.FirstOrDefault(candidate =>
            candidate.Id.Equals(pageId, StringComparison.OrdinalIgnoreCase));
        if (page is null)
        {
            ShowStatus($"模块页面 {pageId} 不存在。", true);
            return;
        }
        await _dynamicPageRenderer.RenderAsync(page, DynamicModulePageContent);
    }

    private (string Title, string Subtitle) DynamicPageTitle(string pageId)
    {
        if (pageId == "mapping") return ("按键映射", "手柄与 USB 身份的独立配置");
        var page = _module.Pages.FirstOrDefault(candidate =>
            candidate.Id.Equals(pageId, StringComparison.OrdinalIgnoreCase));
        return page is null
            ? ("模块页面", _module.DisplayName)
            : (page.Label, page.Description ?? _module.DisplayName);
    }

    private async Task<JsonElement> ExecuteModuleOperationAsync(
        string operationId,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!_module.Operations.TryGetValue(operationId, out var operation))
        {
            throw new InvalidOperationException(
                $"模块没有定义操作 {operationId}。" );
        }
        if (operation.Transport != BridgeModuleOperationTransport.ManagerCommand)
        {
            throw new NotSupportedException(
                $"当前管理器不支持操作传输 {operation.Transport}。" );
        }

        var request = BridgeModuleOperationEngine.BuildRequest(
            operation, parameters);
        var response = await SendCommandAsync(request, render: true,
                                               logCommand: true);
        return BridgeModuleOperationEngine.SelectSuccessfulResponse(
            operationId, operation, response);
    }

    private static string ModulePageGlyph(string? icon) => icon switch
    {
        "GameController" => "\uE7FC",
        "Settings" => "\uE713",
        "Color" => "\uE790",
        "Keyboard" => "\uE765",
        _ => "\uE8A5"
    };

    private async void InstallModule_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".cbmodule");
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            var installed = BridgeModulePackageInstaller.Install(
                file.Path, InstalledModuleRoot);
            ReloadModuleRegistry(installed.ModuleId);
            ShowStatus(
                $"模块 {installed.ModuleId} {installed.ModuleVersion} 已安装，无需更新管理器。",
                false);
        }
        catch (Exception ex)
        {
            ShowStatus($"模块安装失败：{ex.Message}", true);
            AppendLog(ex.ToString());
        }
    }

    private void ReloadModules_Click(object sender, RoutedEventArgs e) =>
        ReloadModuleRegistry(_module.Id);

    private void ReloadModuleRegistry(string preferredModuleId)
    {
        _setupWizardWindow?.Close();
        _setupWizardWindow = null;
        _moduleRegistry = CreateModuleRegistry();
        _module = _moduleRegistry.Find(preferredModuleId) ??
                  (_lastDescriptor is null
                      ? _moduleRegistry.Fallback
                      : _moduleRegistry.Resolve(_lastDescriptor));
        InitializeModuleUi();
        ShowStatus($"已重新加载 {_moduleRegistry.Modules.Count} 个固件模块。", false);
    }

    private void UpdateRoleControls(BridgeStatusSnapshot status)
    {
        var key = $"{_module.Id}:{_module.ModuleVersion}:{status.UsbRole}:{status.InputPreference}";
        if (_roleControlsKey == key) return;
        _roleControlsKey = key;
        RoleOptionsList.ItemsSource = _module.UsbRoles.Select(option =>
        {
            var accent = RoleColor(option.Role);
            var selected = status.UsbRole == option.Role;
            return new RoleOptionItem(option,
                new SolidColorBrush(accent),
                new SolidColorBrush(selected ? accent :
                    Color.FromArgb(90, 128, 128, 128)),
                new SolidColorBrush(selected
                    ? Color.FromArgb(28, accent.R, accent.G, accent.B)
                    : Color.FromArgb(0, 0, 0, 0)),
                new Thickness(selected ? 2 : 1));
        }).ToArray();

        InputOptionsList.ItemsSource = _module.InputSources.Select(option =>
        {
            var accent = InputColor(option.Preference);
            var selected = status.InputPreference == option.Preference;
            return new InputOptionItem(option,
                new SolidColorBrush(selected ? accent :
                    Color.FromArgb(90, 128, 128, 128)),
                new SolidColorBrush(selected
                    ? Color.FromArgb(28, accent.R, accent.G, accent.B)
                    : Color.FromArgb(0, 0, 0, 0)),
                new Thickness(selected ? 2 : 1));
        }).ToArray();
    }

    private void UpdateWirelessControllers()
    {
        var key = $"{_module.Id}:{_module.ModuleVersion}:{_client is not null}:" +
                  $"{GetWirelessStatus("ds5")}:{GetWirelessStatus("ns2")}";
        if (_wirelessControlsKey == key) return;
        _wirelessControlsKey = key;
        WirelessControllerList.ItemsSource = _module.WirelessControllers
            .Select(option => new WirelessControllerItem(
                option,
                GetWirelessStatus(option.Id),
                new SolidColorBrush(option.Id.Equals("ds5",
                    StringComparison.OrdinalIgnoreCase)
                    ? RoleColor(BridgeUsbRole.DualSense)
                    : RoleColor(BridgeUsbRole.NintendoNs2Pro)),
                Action(option.PairCommand),
                Action(option.ConnectCommand),
                Action(option.DisconnectCommand),
                Action(option.ForgetCommand)))
            .ToArray();

        WirelessCommandAction? Action(string? command) =>
            _client is null || string.IsNullOrWhiteSpace(command) ? null : new(command);
    }

    private string GetWirelessStatus(string id)
    {
        if (id.Equals("ds5", StringComparison.OrdinalIgnoreCase))
        {
            return BuildConnectionText(_status.Ds5Connected, _status.Ds5Pairing,
                _status.Ds5Saved, "正在配对");
        }
        if (id.Equals("ns2", StringComparison.OrdinalIgnoreCase))
        {
            return BuildConnectionText(_status.Ns2Connected, _status.Ns2Scanning,
                _status.Ns2Saved, "正在扫描");
        }
        return "状态由固件模块提供";
    }

    private static Color RoleColor(BridgeUsbRole role) => role switch
    {
        BridgeUsbRole.Xbox => Color.FromArgb(255, 63, 163, 77),
        BridgeUsbRole.DualSense => Color.FromArgb(255, 47, 128, 237),
        BridgeUsbRole.DualSenseEdge => Color.FromArgb(255, 142, 107, 190),
        BridgeUsbRole.NintendoNs2Pro => Color.FromArgb(255, 232, 74, 95),
        _ => Color.FromArgb(255, 38, 166, 154)
    };

    private static Color InputColor(BridgeInputPreference input) => input switch
    {
        BridgeInputPreference.DualSense => RoleColor(BridgeUsbRole.DualSense),
        BridgeInputPreference.NintendoNs2Pro =>
            RoleColor(BridgeUsbRole.NintendoNs2Pro),
        _ => Color.FromArgb(255, 38, 166, 154)
    };

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
        if (_inputClosing) return;
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? line : $"{LogBox.Text}\r\n{line}";
        if (LogBox.Text.Length > 24000) LogBox.Text = LogBox.Text[^16000..];
    }

    private void ShowStatus(string message, bool error)
    {
        if (_inputClosing) return;
        StatusBar.Severity = error ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        StatusBar.Message = message;
    }

    private static string FriendlyError(Exception error)
    {
        return error.Message.Contains("reply length changed", StringComparison.OrdinalIgnoreCase)
            ? "设备正被另一个管理器占用。请关闭经典版后重试。"
            : error.Message;
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
