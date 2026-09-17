using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardWindow
{
    internal async Task RunWizardSmokeAsync(string output)
    {
        SmokeCapture.Require(!_discoverDevices, "Wizard smoke must not discover hardware.");
        Title = "Firmware Wizard UI Test - no device writes";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 760));
        await Task.Delay(80);
        SmokeCapture.Require(WizardSidebar.Visibility == Visibility.Collapsed &&
            Grid.GetColumn(WizardContent) == 0 && Grid.GetColumnSpan(WizardContent) == 2,
            "Wizard did not switch to its compact layout.");
        var board = _moduleRegistry.GetBoards().Single(item => item.Id == "sf32lb52-devkit-nano");
        WizardBoardList.SelectedItem = board;
        SmokeCapture.Require(_wizardFirmware?.Firmware.BoardIds.Contains(board.Id) == true,
            "Nano selection did not select its matching firmware.");
        ShowWizardStep(3);
        await SmokeCapture.SaveAsync((FrameworkElement)Content, output, "firmware-selection.png");
        ShowWizardStep(4);
        SmokeCapture.Require(WizardPortBox.SelectedItem is null, "Wizard invented a COM port.");
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "sftool", "sftool.exe")))
        {
            SmokeCapture.Require(_wizardFirmware!.Artifact.Available &&
                _wizardFirmware.Artifact.FileCount == 3 &&
                WizardArtifactText.Text.Contains("内置版本") &&
                WizardFlashToolText.Text.Contains("App 内置"),
                "Complete package does not surface its bundled firmware/tool.");
        }
        var before = _wizardStep;
        SetFlashBusy(true);
        ShowWizardStep(5);
        SmokeCapture.Require(_wizardStep == before && !WizardNextButton.IsEnabled &&
            !WizardBoardList.IsEnabled && !WizardFlashButton.IsEnabled && !WizardPortBox.IsEnabled,
            "Flash controls or target could change while busy.");
        SetFlashBusy(false);
        SmokeCapture.Require(!new FirmwareFlashService().Check(
                _wizardFirmware!.Module, _wizardFirmware.Firmware, null).Ready,
            "Wizard allowed flashing without a port.");
        await SmokeCapture.SaveAsync((FrameworkElement)Content, output, "firmware-flash.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));
        await Task.Delay(80);
        SmokeCapture.Require(WizardSidebar.Visibility == Visibility.Visible &&
            Grid.GetColumn(WizardContent) == 1,
            "Wizard did not restore its wide layout.");
        WizardBoardList.SelectedItem = _moduleRegistry.GetBoards().Single(item => item.Id == "esp32s3-n16r8");
        SmokeCapture.Require(!_wizardFirmware!.Artifact.Available && !WizardFlashButton.IsEnabled,
            "ESP32 management metadata advertised a nonexistent bundled image.");
        await File.WriteAllTextAsync(Path.Combine(output, "wizard-result.txt"),
            "PASS board-specific firmware, honest missing artifacts, no guessed COM, busy target lock, no device writes");
    }
}
