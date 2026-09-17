using System.IO.Ports;
using System.Text.RegularExpressions;
using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private readonly GithubModuleReleaseService _firmwareReleaseService = new();
    private readonly FirmwareFlashService _firmwareFlashService = new();
    private WizardFirmwareChoice? _firmwareSelection;
    private bool _firmwareUpdateBusy;
    private bool _firmwareFlashBusy;

    private void InitializeFirmwarePage()
    {
        RefreshFirmwareTargets();
        if (_discoverDevices) _ = RefreshFirmwarePortsAsync();
    }

    private void RefreshFirmwareTargets()
    {
        var previousId = (FirmwareBoardBox.SelectedItem as BridgeBoardDefinition)?.Id;
        var connectedModule = _client is not null && _lastDescriptor is not null &&
            _module.Id is "bl616-unified" or "sf32-unified"
            ? _module : null;
        var boardIds = connectedModule?.Boards.Select(board => board.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boards = _moduleRegistry.GetBoards().Where(board =>
            boardIds.Contains(board.Id))
            .ToArray();
        FirmwareBoardBox.ItemsSource = boards;
        FirmwareBoardBox.SelectedItem = boards.FirstOrDefault(board =>
            board.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase)) ??
            boards.FirstOrDefault();
        FirmwareCheckUpdatesButton.IsEnabled = connectedModule is not null &&
                                                !_firmwareUpdateBusy;
        if (connectedModule is null)
        {
            FirmwareReleaseList.ItemsSource = null;
            FirmwareInstallUpdateButton.IsEnabled = false;
            FirmwareUpdateStatusText.Text =
                "请先在概览页连接 BL616 或 SF32 接收器；这里只显示当前设备的固件。";
        }
    }

    private async void FirmwareCheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckFirmwareUpdatesAsync();

    private async Task CheckFirmwareUpdatesAsync()
    {
        if (_firmwareUpdateBusy) return;
        if (_client is null || _lastDescriptor is null ||
            _module.Id is not ("bl616-unified" or "sf32-unified"))
        {
            const string message =
                "请先连接 BL616 或 SF32 接收器，再检查该设备的固件更新。";
            FirmwareUpdateStatusText.Text = message;
            ShowFirmwareInfo(message, true);
            RefreshFirmwareTargets();
            return;
        }
        var connectedModuleId = _module.Id;
        _firmwareUpdateBusy = true;
        FirmwareCheckUpdatesButton.IsEnabled = false;
        FirmwareInstallUpdateButton.IsEnabled = false;
        FirmwareUpdateProgress.IsActive = true;
        FirmwareUpdateProgress.Visibility = Visibility.Visible;
        FirmwareUpdateStatusText.Text = "正在读取 GitHub Releases...";
        try
        {
            var assets = await _firmwareReleaseService.GetModuleAssetsAsync(
                cancellationToken: CancellationToken.None);
            var matchingAssets = assets.Where(asset =>
                AssetMatchesModule(asset, connectedModuleId)).ToArray();
            if (_client is null || !_module.Id.Equals(connectedModuleId,
                    StringComparison.OrdinalIgnoreCase))
            {
                RefreshFirmwareTargets();
                return;
            }
            FirmwareReleaseList.ItemsSource = matchingAssets;
            FirmwareUpdateStatusText.Text = matchingAssets.Length == 0
                ? $"GitHub Releases 暂无 {_module.DisplayName} 的更新。"
                : $"找到 {matchingAssets.Length} 个 {_module.DisplayName} 固件包。";
            ShowFirmwareInfo(FirmwareUpdateStatusText.Text, false);
        }
        catch (Exception ex)
        {
            FirmwareReleaseList.ItemsSource = null;
            FirmwareUpdateStatusText.Text = $"GitHub 更新检查失败：{ex.Message}";
            ShowFirmwareInfo(FirmwareUpdateStatusText.Text, true);
        }
        finally
        {
            _firmwareUpdateBusy = false;
            FirmwareCheckUpdatesButton.IsEnabled = _client is not null &&
                _module.Id is "bl616-unified" or "sf32-unified";
            FirmwareUpdateProgress.IsActive = false;
            FirmwareUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    internal static bool AssetMatchesModule(GithubModuleAsset asset,
        string moduleId) => asset.Name.StartsWith(moduleId + "-",
            StringComparison.OrdinalIgnoreCase) &&
        asset.Name.EndsWith(".cbmodule", StringComparison.OrdinalIgnoreCase);

    private void FirmwareReleaseList_SelectionChanged(object sender,
        SelectionChangedEventArgs e) =>
        FirmwareInstallUpdateButton.IsEnabled = !_firmwareUpdateBusy &&
            FirmwareReleaseList.SelectedItem is GithubModuleAsset;

    private async void FirmwareInstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareUpdateBusy ||
            FirmwareReleaseList.SelectedItem is not GithubModuleAsset asset) return;
        _firmwareUpdateBusy = true;
        FirmwareCheckUpdatesButton.IsEnabled = false;
        FirmwareInstallUpdateButton.IsEnabled = false;
        FirmwareUpdateProgress.IsActive = true;
        FirmwareUpdateProgress.Visibility = Visibility.Visible;
        string? packagePath = null;
        try
        {
            var progress = new Progress<double>(value =>
                FirmwareUpdateStatusText.Text =
                    $"正在从 GitHub 下载 {asset.Name}：{value:P0}");
            packagePath = await _firmwareReleaseService.DownloadAsync(asset,
                progress, CancellationToken.None);
            var installed = InstallModulePackageFromWizard(packagePath);
            RefreshFirmwareTargets();
            FirmwareUpdateStatusText.Text =
                $"已安装 {asset.Name}；活动模块已重新加载。";
            ShowFirmwareInfo(FirmwareUpdateStatusText.Text, false);
        }
        catch (Exception ex)
        {
            FirmwareUpdateStatusText.Text = $"更新安装失败：{ex.Message}";
            ShowFirmwareInfo(FirmwareUpdateStatusText.Text, true);
        }
        finally
        {
            if (packagePath is not null && File.Exists(packagePath))
                File.Delete(packagePath);
            _firmwareUpdateBusy = false;
            FirmwareCheckUpdatesButton.IsEnabled = true;
            FirmwareInstallUpdateButton.IsEnabled =
                FirmwareReleaseList.SelectedItem is GithubModuleAsset;
            FirmwareUpdateProgress.IsActive = false;
            FirmwareUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void FirmwareBoardBox_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        _firmwareSelection = null;
        var board = FirmwareBoardBox.SelectedItem as BridgeBoardDefinition;
        var choices = board is null ? [] : _moduleRegistry.GetFirmwareForBoard(board.Id)
            .Where(item => item.Firmware.FlashMethod is FirmwareFlashMethod.SifliSerial
                or FirmwareFlashMethod.BouffaloUart)
            .Select(item => new WizardFirmwareChoice(item.Module, item.Firmware))
            .ToArray();
        FirmwareImageBox.ItemsSource = choices;
        FirmwareImageBox.SelectedItem = choices.FirstOrDefault();
    }

    private void FirmwareImageBox_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        _firmwareSelection = FirmwareImageBox.SelectedItem as WizardFirmwareChoice;
        FirmwareFlashButton.IsEnabled = false;
        if (_firmwareSelection is null)
        {
            FirmwareArtifactText.Text = "该板卡没有可由 Manager 烧录的固件。";
            FirmwareHashText.Text = "";
            FirmwareToolText.Text = "";
            FirmwarePreflightButton.IsEnabled = false;
            return;
        }
        var artifact = _firmwareSelection.Artifact;
        FirmwareArtifactText.Text = artifact.Available
            ? $"{_firmwareSelection.Firmware.DisplayName} {_firmwareSelection.Firmware.Version} · " +
              $"{artifact.FileCount} 个文件 · {artifact.TotalBytes / 1024d:N0} KiB"
            : artifact.Message;
        FirmwareHashText.Text = artifact.Available
            ? $"主程序 SHA-256\n{artifact.MainSha256}" : "";
        FirmwareToolText.Text = _firmwareSelection.Firmware.FlashMethod switch
        {
            FirmwareFlashMethod.SifliSerial =>
                $"SF32 工具：{FirmwareFlashService.ResolveSifliTool() ?? "未找到 sftool"}",
            FirmwareFlashMethod.BouffaloUart =>
                $"BL616 工具：{FirmwareFlashService.ResolveBouffaloTool() ?? "未找到 BLFlashCommand"}",
            _ => "不支持自动烧录"
        };
        FirmwarePreflightButton.IsEnabled = artifact.Available;
        FirmwareFlashStatusText.Text = "尚未检查烧录环境";
        SelectPreferredFirmwarePort();
    }

    private async void FirmwareRefreshPorts_Click(object sender, RoutedEventArgs e)
        => await RefreshFirmwarePortsAsync();

    private async Task RefreshFirmwarePortsAsync()
    {
        if (_firmwareFlashBusy) return;
        var previous = (FirmwarePortBox.SelectedItem as FirmwarePortItem)?.PortName;
        var ports = SerialPort.GetPortNames().OrderBy(port => port,
            StringComparer.OrdinalIgnoreCase).ToArray();
        var deviceDetails = new List<(string Name, string Id)>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                SerialDevice.GetDeviceSelector());
            deviceDetails.AddRange(devices.Select(device => (device.Name, device.Id)));
        }
        catch { }
        var items = ports.Select(port =>
        {
            var detail = deviceDetails.FirstOrDefault(device =>
                Regex.IsMatch(device.Name, $@"\({Regex.Escape(port)}\)",
                    RegexOptions.IgnoreCase) ||
                device.Id.Contains(port, StringComparison.OrdinalIgnoreCase));
            var bl616DownloadPort = detail.Id?.Contains("VID_349B&PID_6160",
                StringComparison.OrdinalIgnoreCase) == true;
            var name = string.IsNullOrWhiteSpace(detail.Name) ? port : detail.Name;
            return new FirmwarePortItem(port,
                bl616DownloadPort ? $"{name} · BL616 ROM 下载口" : name,
                bl616DownloadPort);
        }).ToArray();
        FirmwarePortBox.ItemsSource = items;
        FirmwarePortBox.SelectedItem = items.FirstOrDefault(item =>
            item.PortName.Equals(previous, StringComparison.OrdinalIgnoreCase));
        SelectPreferredFirmwarePort();
    }

    private void SelectPreferredFirmwarePort()
    {
        if (FirmwarePortBox.ItemsSource is not IEnumerable<FirmwarePortItem> ports) return;
        var items = ports.ToArray();
        var wantsBl616 = _firmwareSelection?.Firmware.FlashMethod ==
                         FirmwareFlashMethod.BouffaloUart;
        FirmwarePortBox.SelectedItem ??= wantsBl616
            ? items.FirstOrDefault(item => item.IsBl616DownloadPort)
            : items.Length == 1 ? items[0] : null;
    }

    private void FirmwarePortBox_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        FirmwareFlashButton.IsEnabled = false;
        FirmwareFlashStatusText.Text = "串口已变更，请重新检查烧录环境。";
    }

    private void FirmwarePreflight_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareSelection is null) return;
        var check = _firmwareFlashService.Check(_firmwareSelection.Module,
            _firmwareSelection.Firmware,
            (FirmwarePortBox.SelectedItem as FirmwarePortItem)?.PortName);
        FirmwareFlashStatusText.Text = check.Message;
        FirmwareFlashButton.IsEnabled = check.Ready;
        ShowFirmwareInfo(check.Message, !check.Ready);
    }

    private async void FirmwareFlash_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareFlashBusy || _firmwareSelection is null) return;
        var port = (FirmwarePortBox.SelectedItem as FirmwarePortItem)?.PortName;
        var check = _firmwareFlashService.Check(_firmwareSelection.Module,
            _firmwareSelection.Firmware, port);
        if (!check.Ready)
        {
            FirmwareFlashStatusText.Text = check.Message;
            ShowFirmwareInfo(check.Message, true);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "确认烧录固件",
            Content = $"{(FirmwareBoardBox.SelectedItem as BridgeBoardDefinition)?.DisplayName}\n" +
                      $"{_firmwareSelection.Firmware.DisplayName} {_firmwareSelection.Firmware.Version}\n" +
                      $"目标：{check.Target}\n\n已验证固件清单和文件哈希。烧录期间请勿断电或拔线。",
            PrimaryButtonText = "开始烧录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SetFirmwareBusy(true);
        try
        {
            await DisconnectAsync();
            var progress = new Progress<string>(message =>
                FirmwareFlashStatusText.Text = message);
            var result = await _firmwareFlashService.FlashAsync(
                _firmwareSelection.Module, _firmwareSelection.Firmware, port,
                progress, CancellationToken.None);
            FirmwareFlashStatusText.Text = result.Message;
            ShowFirmwareInfo(result.Message, !result.Success);
            if (result.Success) _ = RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            FirmwareFlashStatusText.Text = $"烧录失败：{ex.Message}";
            ShowFirmwareInfo(FirmwareFlashStatusText.Text, true);
        }
        finally { SetFirmwareBusy(false); }
    }

    private void SetFirmwareBusy(bool busy)
    {
        _firmwareFlashBusy = busy;
        foreach (var control in new Control[] { FirmwareBoardBox,
                     FirmwareImageBox, FirmwarePortBox, FirmwareRefreshPortsButton,
                     FirmwarePreflightButton, FirmwareCheckUpdatesButton,
                     FirmwareInstallUpdateButton })
            control.IsEnabled = !busy;
        FirmwareFlashButton.IsEnabled = false;
        FirmwareFlashProgress.IsActive = busy;
        FirmwareFlashProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFirmwareInfo(string message, bool error)
    {
        FirmwarePageInfoBar.Message = message;
        FirmwarePageInfoBar.Severity = error ? InfoBarSeverity.Error :
            InfoBarSeverity.Success;
        FirmwarePageInfoBar.IsOpen = true;
    }
}
