using System.IO.Ports;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardWindow
{
    private BridgeFirmwareModuleRegistry _moduleRegistry;
    private readonly FirmwareFlashService _flashService = new();
    private readonly BridgeTransportFactory _transportFactory;
    private readonly GithubModuleReleaseService _githubModules = new();
    private readonly Func<BridgeStatusSnapshot> _getStatus;
    private readonly Func<WizardApplyRequest, Task<WizardApplyResult>>
        _applySettingsAsync;
    private readonly Func<string, BridgeFirmwareModuleRegistry>
        _installModulePackage;
    private readonly Action _completed;
    private readonly Func<Task> _prepareFlashAsync;
    private readonly bool _discoverDevices;
    private bool _flashBusy;
    public bool IsFlashing => _flashBusy;
    private int _wizardStep = 1;
    private BridgeBoardDefinition? _wizardBoard;
    private WizardFirmwareChoice? _wizardFirmware;
    private WizardSetupState? _wizardState;

    private sealed class WizardSetupState
    {
        public bool Completed { get; init; }
        public string? BoardId { get; init; }
        public string? ModuleId { get; init; }
        public string? FirmwareId { get; init; }
        public string? UsbRole { get; init; }
        public string? InputPreference { get; init; }
        public int ReportRateHz { get; init; } = 250;
        public bool SaveToDevice { get; init; } = true;
    }

    public SetupWizardWindow(
        BridgeFirmwareModuleRegistry moduleRegistry,
        Func<BridgeStatusSnapshot> getStatus,
        Func<WizardApplyRequest, Task<WizardApplyResult>> applySettingsAsync,
        Func<string, BridgeFirmwareModuleRegistry> installModulePackage,
        Action completed,
        Func<Task>? prepareFlashAsync = null,
        bool discoverDevices = true)
    {
        _moduleRegistry = moduleRegistry;
        _transportFactory = new BridgeTransportFactory(
            () => _moduleRegistry.GetHidDeviceProfiles());
        _getStatus = getStatus;
        _applySettingsAsync = applySettingsAsync;
        _installModulePackage = installModulePackage;
        _completed = completed;
        _prepareFlashAsync = prepareFlashAsync ?? (() => Task.CompletedTask);
        _discoverDevices = discoverDevices;
        InitializeComponent();
        PageScrolling.Attach(WizardScroller);
        AppWindow.Resize(new SizeInt32(1040, 760));
        InitializeWizard();
        AppWindow.Closing += (_, args) =>
        {
            if (!_flashBusy) return;
            args.Cancel = true;
            ShowStatus("烧录尚未结束，请保持连接并等待结果。", true);
        };
        if (_discoverDevices) _ = RefreshDetectedDevicesAsync();
    }

    public static bool HasCompletedSetup()
    {
        var state = LoadWizardState();
        return state?.Completed == true || File.Exists(LegacyWizardStatePath);
    }

    private void InitializeWizard()
    {
        var boards = _moduleRegistry.GetBoards();
        WizardBoardList.ItemsSource = boards;
        RefreshWizardPorts();
        _wizardState = LoadWizardState();
        if (_wizardState is not null)
        {
            var board = boards.FirstOrDefault(item => item.Id.Equals(
                _wizardState.BoardId, StringComparison.OrdinalIgnoreCase));
            if (board is not null)
            {
                WizardBoardList.SelectedItem = board;
                var choices = (WizardFirmwareList.ItemsSource as
                    IEnumerable<WizardFirmwareChoice>)?.ToArray() ?? [];
                var savedChoice = choices.FirstOrDefault(choice =>
                    choice.Module.Id.Equals(_wizardState.ModuleId,
                        StringComparison.OrdinalIgnoreCase) &&
                    choice.Firmware.Id.Equals(_wizardState.FirmwareId,
                        StringComparison.OrdinalIgnoreCase));
                if (savedChoice is not null)
                {
                    WizardFirmwareList.SelectedItem = savedChoice;
                }
            }
        }
        ShowWizardStep(1);
    }

    private async Task RefreshDetectedDevicesAsync()
    {
        if (_flashBusy) return;
        DetectedDeviceStatusText.Text = "正在读取 Windows 设备枚举结果...";
        try
        {
            var descriptors = await _transportFactory.GetDevicesAsync(
                CancellationToken.None);
            if (_flashBusy) return;
            var items = descriptors.Select(descriptor =>
            {
                var module = _moduleRegistry.Resolve(descriptor);
                var board = descriptor.TransportKind == DeviceTransportKind.Hid &&
                            module.Id != _moduleRegistry.Fallback.Id &&
                            module.Boards.Count == 1
                    ? module.Boards[0]
                    : null;
                var identity = descriptor.TransportKind == DeviceTransportKind.Hid
                    ? $"USB HID {descriptor.VendorId:x4}:{descriptor.ProductId:x4}" +
                      (string.IsNullOrWhiteSpace(descriptor.SerialNumber)
                          ? "" : $" · S/N {descriptor.SerialNumber}")
                    : $"{descriptor.Id} · Windows 串口枚举";
                var result = board is null
                    ? "板型未知，不会自动选择"
                    : $"匹配 {board.DisplayName}";
                return new DetectedSetupDeviceItem(
                    descriptor, board, descriptor.DisplayName,
                    $"{identity} · {result}");
            }).ToArray();
            DetectedDeviceList.ItemsSource = items;
            var identified = items.Count(item => item.IdentifiesBoard);
            DetectedDeviceStatusText.Text = items.Length == 0
                ? "Windows 当前没有枚举到管理 HID 或串口。"
                : $"Windows 实际枚举到 {items.Length} 个候选，其中 " +
                  $"{identified} 个可由 USB 身份确定板型。";
            var firstIdentified = items.FirstOrDefault(item =>
                item.IdentifiesBoard);
            if (firstIdentified is not null &&
                DetectedDeviceList.SelectedItem is null)
            {
                DetectedDeviceList.SelectedItem = firstIdentified;
            }
        }
        catch (Exception ex)
        {
            DetectedDeviceStatusText.Text = $"Windows 设备枚举失败：{ex.Message}";
            ShowStatus(DetectedDeviceStatusText.Text, true);
        }
    }

    private async void RefreshDetectedDevices_Click(
        object sender, RoutedEventArgs e) =>
        await RefreshDetectedDevicesAsync();

    private void DetectedDeviceList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_flashBusy) return;
        if (DetectedDeviceList.SelectedItem is not DetectedSetupDeviceItem item)
        {
            return;
        }
        if (item.Board is null)
        {
            ShowStatus(
                $"已真实检测到 {item.Descriptor.DisplayName}，但其描述符不能确定板型，请手动选择。",
                false);
            return;
        }
        var board = (_moduleRegistry.GetBoards()).FirstOrDefault(candidate =>
            candidate.Id.Equals(item.Board.Id,
                StringComparison.OrdinalIgnoreCase));
        if (board is not null)
        {
            WizardBoardList.SelectedItem = board;
            ShowStatus(
                $"依据 USB {item.Descriptor.VendorId:x4}:{item.Descriptor.ProductId:x4}" +
                $" 的真实枚举信息选择了 {board.DisplayName}。",
                false);
        }
    }

    private async void RefreshGithubModules_Click(
        object sender, RoutedEventArgs e) =>
        await RefreshGithubModulesAsync();

    private async Task RefreshGithubModulesAsync()
    {
        GithubModulesPanel.Visibility = Visibility.Visible;
        GithubProgressRing.IsActive = true;
        GithubStatusText.Text = "正在读取 GitHub Releases...";
        try
        {
            var assets = await _githubModules.GetModuleAssetsAsync(
                cancellationToken: CancellationToken.None);
            GithubModuleList.ItemsSource = assets;
            GithubStatusText.Text = assets.Count == 0
                ? "当前 GitHub Releases 中还没有 .cbmodule 单文件兼容包。"
                : $"找到 {assets.Count} 个由 GitHub Release 实际返回的兼容包。";
        }
        catch (Exception ex)
        {
            GithubModuleList.ItemsSource = null;
            GithubStatusText.Text = $"GitHub 读取失败：{ex.Message}";
            ShowStatus(GithubStatusText.Text, true);
        }
        finally
        {
            GithubProgressRing.IsActive = false;
        }
    }

    private void GithubModuleList_SelectionChanged(
        object sender, SelectionChangedEventArgs e) =>
        InstallGithubModuleButton.IsEnabled =
            GithubModuleList.SelectedItem is GithubModuleAsset;

    private async void InstallGithubModule_Click(
        object sender, RoutedEventArgs e)
    {
        if (GithubModuleList.SelectedItem is not GithubModuleAsset asset)
        {
            return;
        }
        GithubProgressRing.IsActive = true;
        InstallGithubModuleButton.IsEnabled = false;
        string? packagePath = null;
        try
        {
            var progress = new Progress<double>(value =>
                GithubStatusText.Text =
                    $"正在从 GitHub 下载 {asset.Name}：{value:P0}");
            packagePath = await _githubModules.DownloadAsync(asset, progress,
                CancellationToken.None);
            InstallAndReloadModule(packagePath);
            GithubStatusText.Text = $"已安装 {asset.Name}。";
            ShowStatus(
                "兼容包已安装；新增固件支持不需要更新管理器。", false);
        }
        catch (Exception ex)
        {
            GithubStatusText.Text = $"兼容包安装失败：{ex.Message}";
            ShowStatus(GithubStatusText.Text, true);
        }
        finally
        {
            GithubProgressRing.IsActive = false;
            InstallGithubModuleButton.IsEnabled =
                GithubModuleList.SelectedItem is GithubModuleAsset;
            if (packagePath is not null && File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
    }

    private async void ImportModuleFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".cbmodule");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            InstallAndReloadModule(file.Path);
            ShowStatus(
                $"已导入单文件兼容包 {file.Name}，无需更新管理器。", false);
        }
        catch (Exception ex)
        {
            ShowStatus($"兼容包安装失败：{ex.Message}", true);
        }
    }

    private void InstallAndReloadModule(string packagePath)
    {
        var selectedBoardId = _wizardBoard?.Id;
        _moduleRegistry = _installModulePackage(packagePath);
        var boards = _moduleRegistry.GetBoards();
        WizardBoardList.ItemsSource = boards;
        var board = boards.FirstOrDefault(item => item.Id.Equals(
            selectedBoardId, StringComparison.OrdinalIgnoreCase));
        if (board is not null)
        {
            WizardBoardList.SelectedItem = board;
        }
        _ = RefreshDetectedDevicesAsync();
    }

    private void ShowWizardStep(int step)
    {
        if (_flashBusy) return;
        _wizardStep = Math.Clamp(step, 1, 6);
        WizardStepCounter.Text = $"步骤 {_wizardStep} / 6";
        WizardWelcomePanel.Visibility = _wizardStep == 1
            ? Visibility.Visible : Visibility.Collapsed;
        WizardBoardPanel.Visibility = _wizardStep == 2
            ? Visibility.Visible : Visibility.Collapsed;
        WizardFirmwarePanel.Visibility = _wizardStep == 3
            ? Visibility.Visible : Visibility.Collapsed;
        WizardFlashPanel.Visibility = _wizardStep == 4
            ? Visibility.Visible : Visibility.Collapsed;
        WizardSettingsPanel.Visibility = _wizardStep == 5
            ? Visibility.Visible : Visibility.Collapsed;
        WizardDonePanel.Visibility = _wizardStep == 6
            ? Visibility.Visible : Visibility.Collapsed;
        WizardBackButton.Visibility = _wizardStep is >= 2 and <= 5
            ? Visibility.Visible : Visibility.Collapsed;
        WizardNextButton.Visibility = _wizardStep is >= 2 and <= 5
            ? Visibility.Visible : Visibility.Collapsed;

        (WizardStepTitle.Text, WizardStepSubtitle.Text) = _wizardStep switch
        {
            2 => ("选择板卡", "先确认硬件型号，向导会过滤不兼容固件"),
            3 => ("选择固件", "当前板卡的随包固件"),
            4 => ("烧录固件", "检查下载环境后写入，已有固件也可以跳过"),
            5 => ("基本设置", "选择 USB 身份、无线输入与回报率"),
            6 => ("设置完成", "返回管理器连接设备并测试控制器"),
            _ => ("欢迎使用 Controller Bridge", "用几步完成板卡、固件和手柄设置")
        };

        var indicators = new FrameworkElement[]
        {
            Step1Indicator, Step2Indicator, Step3Indicator,
            Step4Indicator, Step5Indicator, Step6Indicator
        };
        for (var index = 0; index < indicators.Length; index++)
        {
            indicators[index].Opacity = index + 1 <= _wizardStep ? 1 : 0.45;
        }
        if (_wizardStep == 5) PopulateWizardSettings();
        if (_wizardStep == 4) RefreshWizardPorts();
    }

    private void WizardStart_Click(object sender, RoutedEventArgs e) =>
        ShowWizardStep(2);

    private void WizardBack_Click(object sender, RoutedEventArgs e) =>
        ShowWizardStep(_wizardStep - 1);

    private async void WizardNext_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardStep == 2 && _wizardBoard is null)
        {
            ShowStatus("请选择板卡型号。", true);
            return;
        }
        if (_wizardStep == 3 && _wizardFirmware is null)
        {
            ShowStatus("请选择固件。", true);
            return;
        }
        if (_wizardStep == 5)
        {
            await ApplyWizardSettingsAsync();
            ShowWizardStep(6);
            return;
        }
        ShowWizardStep(_wizardStep + 1);
    }

    private void WizardBoardList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_flashBusy) return;
        _wizardBoard = WizardBoardList.SelectedItem as BridgeBoardDefinition;
        _wizardFirmware = null;
        if (_wizardBoard is null)
        {
            WizardFirmwareList.ItemsSource = null;
            return;
        }
        var choices = _moduleRegistry.GetFirmwareForBoard(_wizardBoard.Id)
            .Select(item => new WizardFirmwareChoice(item.Module, item.Firmware))
            .ToArray();
        WizardFirmwareList.ItemsSource = choices;
        if (choices.Length == 1) WizardFirmwareList.SelectedItem = choices[0];
    }

    private void WizardFirmwareList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_flashBusy) return;
        _wizardFirmware = WizardFirmwareList.SelectedItem as WizardFirmwareChoice;
        if (_wizardFirmware is null)
        {
            WizardFlashButton.IsEnabled = false;
            return;
        }

        var firmware = _wizardFirmware.Firmware;
        WizardFlashMethodText.Text = firmware.FlashMethod switch
        {
            FirmwareFlashMethod.PicoUf2 => "Pico UF2 / BOOTSEL",
            FirmwareFlashMethod.SifliSerial => "SiFli sftool / 串口",
            _ => "手动烧录"
        };
        WizardFlashHintText.Text = firmware.FlashHint;
        var artifact = _wizardFirmware.Artifact;
        WizardArtifactText.Text = artifact.Available
            ? $"内置版本：{firmware.Version} · {artifact.FileCount} 个文件 · {artifact.TotalBytes / 1024d:N0} KiB"
            : artifact.Message;
        WizardFirmwareHashText.Text = artifact.Available
            ? $"主程序 SHA-256：{artifact.MainSha256}\n文件时间：{artifact.FileTimeUtc?.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "";
        var tool = firmware.FlashMethod == FirmwareFlashMethod.SifliSerial
            ? FirmwareFlashService.ResolveSifliTool() : null;
        var bundledTool = Path.Combine(AppContext.BaseDirectory, "tools", "sftool", "sftool.exe");
        WizardFlashToolText.Text = firmware.FlashMethod switch
        {
            FirmwareFlashMethod.SifliSerial => tool is null ? "烧录工具：缺失"
                : Path.GetFullPath(tool).Equals(Path.GetFullPath(bundledTool), StringComparison.OrdinalIgnoreCase)
                    ? "烧录工具：App 内置 sftool" : $"烧录工具：{tool}",
            FirmwareFlashMethod.PicoUf2 => "烧录工具：内置 UF2 写入",
            _ => "当前仅支持管理，暂不提供 App 烧录"
        };
        WizardFlashButton.IsEnabled = artifact.Available &&
            firmware.FlashMethod != FirmwareFlashMethod.None &&
            (firmware.FlashMethod != FirmwareFlashMethod.SifliSerial || tool is not null);
        WizardPortPanel.Visibility = firmware.FlashMethod ==
                                   FirmwareFlashMethod.SifliSerial
            ? Visibility.Visible : Visibility.Collapsed;
        WizardFlashStatusText.Text = "尚未检查下载连接";
    }

    private void RefreshWizardPorts()
    {
        if (_flashBusy) return;
        var previous = WizardPortBox.SelectedItem?.ToString();
        var ports = _discoverDevices ? SerialPort.GetPortNames()
            .OrderBy(port => port, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        WizardPortBox.ItemsSource = ports;
        WizardPortBox.SelectedItem = ports.FirstOrDefault(port =>
            port.Equals(previous, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshWizardPorts_Click(object sender, RoutedEventArgs e) =>
        RefreshWizardPorts();

    private void PopulateWizardSettings()
    {
        if (_wizardFirmware is null) return;
        var module = _wizardFirmware.Module;
        var status = _getStatus();
        WizardRoleBox.ItemsSource = module.UsbRoles;
        WizardRoleBox.Visibility = module.UsbRoles.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        WizardRoleBox.SelectedItem = module.UsbRoles.FirstOrDefault(option =>
            _wizardState?.ModuleId == module.Id &&
            option.Role.ToString().Equals(_wizardState.UsbRole,
                StringComparison.OrdinalIgnoreCase)) ??
            module.UsbRoles.FirstOrDefault(option => option.Role == status.UsbRole) ??
            module.UsbRoles.FirstOrDefault(option =>
                option.Role == BridgeUsbRole.NintendoNs2Pro) ??
            module.UsbRoles.FirstOrDefault();

        WizardInputBox.ItemsSource = module.InputSources;
        WizardInputBox.Visibility = module.InputSources.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        WizardInputBox.SelectedItem = module.InputSources.FirstOrDefault(option =>
            _wizardState?.ModuleId == module.Id &&
            option.Preference.ToString().Equals(_wizardState.InputPreference,
                StringComparison.OrdinalIgnoreCase)) ??
            module.InputSources.FirstOrDefault(option =>
                option.Preference == status.InputPreference) ??
            module.InputSources.FirstOrDefault(option =>
                option.Preference == BridgeInputPreference.Auto) ??
            module.InputSources.FirstOrDefault();
        WizardRateBox.Value = _wizardState?.ModuleId == module.Id
            ? Math.Clamp(_wizardState.ReportRateHz, 60, 1000)
            : 250;
        WizardSaveSettingsCheck.IsChecked = _wizardState?.ModuleId == module.Id
            ? _wizardState.SaveToDevice
            : true;
    }

    private async Task ApplyWizardSettingsAsync()
    {
        if (_wizardFirmware is null) return;
        _wizardState = CaptureWizardState(completed: false);
        await SaveWizardStateAsync(_wizardState);
        try
        {
            var result = await _applySettingsAsync(new WizardApplyRequest(
                _wizardFirmware.Module,
                WizardRoleBox.SelectedItem as BridgeUsbRoleOption,
                WizardInputBox.SelectedItem as BridgeInputSourceOption,
                (int)Math.Round(WizardRateBox.Value),
                WizardSaveSettingsCheck.IsChecked == true));
            WizardDoneText.Text = result.Message;
            ShowStatus(result.Message, false);
        }
        catch (Exception ex)
        {
            WizardDoneText.Text = "设置已保存在本机，但当前设备应用失败。";
            ShowStatus($"应用设置失败：{ex.Message}", true);
        }
    }

    private void WizardCheckFlash_Click(object sender, RoutedEventArgs e)
    {
        if (_flashBusy) return;
        if (_wizardFirmware is null)
        {
            ShowStatus("请先选择固件。", true);
            return;
        }
        var check = _flashService.Check(_wizardFirmware.Module,
            _wizardFirmware.Firmware, WizardPortBox.SelectedItem?.ToString());
        WizardFlashStatusText.Text = check.Message;
        ShowStatus(check.Message, !check.Ready);
    }

    private async void WizardFlash_Click(object sender, RoutedEventArgs e)
    {
        if (_flashBusy) return;
        if (_wizardFirmware is null)
        {
            ShowStatus("请先选择固件。", true);
            return;
        }
        var selection = _wizardFirmware;
        var board = _wizardBoard;
        var port = WizardPortBox.SelectedItem?.ToString();
        var check = _flashService.Check(selection.Module, selection.Firmware, port);
        if (!check.Ready)
        {
            WizardFlashStatusText.Text = check.Message;
            ShowStatus(check.Message, true);
            return;
        }

        SetFlashBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "确认烧录固件",
                Content = $"{board?.DisplayName}\n{selection.Firmware.DisplayName} {selection.Firmware.Version}\n" +
                          $"写入目标：{check.Target}\n\n将暂时断开管理连接并更新固件。烧录期间请勿断电或拔线。",
                PrimaryButtonText = "开始烧录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _prepareFlashAsync();
            var progress = new Progress<string>(message =>
                WizardFlashStatusText.Text = message);
            var result = await _flashService.FlashAsync(selection.Module,
                selection.Firmware, port,
                progress, CancellationToken.None);
            WizardFlashStatusText.Text = result.Message;
            ShowStatus(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            WizardFlashStatusText.Text = ex.Message;
            ShowStatus($"烧录失败：{ex.Message}", true);
        }
        finally { SetFlashBusy(false); }
    }

    private void SetFlashBusy(bool busy)
    {
        _flashBusy = busy;
        foreach (var control in new Control[] {
            WizardBoardList, WizardFirmwareList, WizardPortBox, WizardRefreshPortsButton,
            WizardCheckFlashButton, WizardSkipFlashButton, WizardBackButton, WizardNextButton,
            DetectedDeviceList })
            control.IsEnabled = !busy;
        WizardFlashButton.IsEnabled = !busy && _wizardFirmware?.Artifact.Available == true &&
            _wizardFirmware.Firmware.FlashMethod != FirmwareFlashMethod.None &&
            (_wizardFirmware.Firmware.FlashMethod != FirmwareFlashMethod.SifliSerial ||
             FirmwareFlashService.ResolveSifliTool() is not null);
        WizardFlashProgress.IsActive = busy;
        WizardFlashProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WizardSkipFlash_Click(object sender, RoutedEventArgs e) =>
        ShowWizardStep(5);

    private async void WizardFinish_Click(object sender, RoutedEventArgs e)
    {
        _wizardState = CaptureWizardState(completed: true);
        await SaveWizardStateAsync(_wizardState);
        _completed();
        Close();
    }

    private WizardSetupState CaptureWizardState(bool completed) => new()
    {
        Completed = completed,
        BoardId = _wizardBoard?.Id,
        ModuleId = _wizardFirmware?.Module.Id,
        FirmwareId = _wizardFirmware?.Firmware.Id,
        UsbRole = (WizardRoleBox.SelectedItem as BridgeUsbRoleOption)?.Role.ToString(),
        InputPreference = (WizardInputBox.SelectedItem as
            BridgeInputSourceOption)?.Preference.ToString(),
        ReportRateHz = (int)Math.Round(WizardRateBox.Value),
        SaveToDevice = WizardSaveSettingsCheck.IsChecked == true
    };

    private static WizardSetupState? LoadWizardState()
    {
        try
        {
            return File.Exists(WizardStatePath)
                ? JsonSerializer.Deserialize<WizardSetupState>(
                    File.ReadAllText(WizardStatePath))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SaveWizardStateAsync(WizardSetupState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WizardStatePath)!);
        await File.WriteAllTextAsync(WizardStatePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    private void ShowStatus(string message, bool error)
    {
        WizardInfoBar.Message = message;
        WizardInfoBar.Severity = error
            ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        WizardInfoBar.IsOpen = true;
    }

    private static string WizardStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControllerBridge", "setup-state-v1.json");

    private static string LegacyWizardStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControllerBridge", "setup-complete-v1");
}
