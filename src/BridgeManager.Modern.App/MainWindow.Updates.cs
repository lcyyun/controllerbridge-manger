using System.Diagnostics;
using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private bool _checkingManagerUpdate;

    private async Task CheckManagerUpdateAsync()
    {
        if (_checkingManagerUpdate) return;
        if (_setupWizardWindow?.IsFlashing == true)
        {
            ShowStatus("请等待固件烧录结束后再更新管理器。", true);
            return;
        }
        _checkingManagerUpdate = true;
        UpdateNavItem.IsEnabled = false;
        try
        {
            var preview = new CheckBox { Content = "包含预发布版本", IsChecked = false };
            var options = new ContentDialog
            {
                XamlRoot = MainNavigation.XamlRoot,
                Title = $"管理器更新 · {ManagerUpdateService.CurrentTag}",
                Content = preview,
                PrimaryButtonText = "检查更新",
                CloseButtonText = "取消"
            };
            if (await options.ShowAsync() != ContentDialogResult.Primary) return;
            ShowStatus("正在检查 GitHub 发布版本…", false);
            var service = new ManagerUpdateService();
            var update = await service.CheckAsync(preview.IsChecked == true, _windowCancellation.Token);
            if (_windowCancellation.IsCancellationRequested) return;
            if (update is null)
            {
                ShowStatus("所选更新通道暂无更高版本。", false);
                return;
            }
            var confirm = new ContentDialog
            {
                XamlRoot = MainNavigation.XamlRoot,
                Title = $"发现 {update.Tag}",
                Content = $"{(update.Prerelease ? "预发布版本" : "正式版本")} · {update.Size / 1024 / 1024} MB\n下载后将进行 SHA-256 校验，安装前需要再次确认。",
                PrimaryButtonText = "下载",
                CloseButtonText = "取消"
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            ShowStatus("正在下载安装包并校验…", false);
            var path = await service.DownloadAsync(update, _windowCancellation.Token);
            if (_windowCancellation.IsCancellationRequested) return;
            var install = new ContentDialog
            {
                XamlRoot = MainNavigation.XamlRoot,
                Title = "安装包校验通过",
                Content = "现在安装将关闭管理器，未保存的修改会丢失。安装包尚未进行代码签名。",
                PrimaryButtonText = "关闭并安装",
                CloseButtonText = "稍后"
            };
            if (await install.ShowAsync() != ContentDialogResult.Primary)
            {
                ShowStatus($"安装包已保存：{path}", false);
                return;
            }
            if (_setupWizardWindow?.IsFlashing == true)
            {
                ShowStatus("固件烧录中，暂不启动管理器安装。", true);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _setupWizardWindow?.Close();
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_windowCancellation.IsCancellationRequested)
                ShowStatus($"管理器更新失败：{ex.Message}", true);
        }
        finally
        {
            _checkingManagerUpdate = false;
            if (!_windowCancellation.IsCancellationRequested) UpdateNavItem.IsEnabled = true;
        }
    }
}
