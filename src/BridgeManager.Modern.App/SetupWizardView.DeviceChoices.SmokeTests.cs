using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardView
{
    private async Task RunEsp32DeviceSelectionSmokeAsync(string output)
    {
        var devices = WizardBoardList.Items.OfType<WizardDeviceChoice>().ToArray();
        var esp = devices.Single(item => item.Id == Esp32DeviceId);
        string[] ids = ["esp32s3-n4", "esp32s3-n8", "esp32s3-n8r2", "esp32s3-n8r8",
            "esp32s3-n16r8", "esp32s3-esp32supermini"];
        SmokeCapture.Require(esp.Boards.Select(board => board.Id).SequenceEqual(ids) &&
            devices.Count(item => item.Boards.Any(board => ids.Contains(board.Id))) == 1 &&
            devices.All(item => !ids.Contains(item.Id)),
            "Initial picker did not collapse the six ESP32 boards into one family card.");
        WizardBoardList.SelectedItem = esp;
        SmokeCapture.Require(_wizardBoard is null && _wizardFirmware is null &&
            WizardFirmwareList.SelectedItem is null && !WizardFlashButton.IsEnabled,
            "A fresh ESP32 family selection guessed a memory variant.");
        DetectedDeviceExpander.IsExpanded = false;
        WindowSizing.ResizeInDips(_ownerWindow, 1360, 940);
        await Task.Delay(80);
        ShowWizardStep(2);
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "esp32-device-selection.png");
        WizardNext_Click(WizardNextButton, new RoutedEventArgs());
        SmokeCapture.Require(_wizardStep == 3 && WizardStepTitle.Text == "选择 ESP32-S3 版本",
            "Family selection could not advance to the explicit version page.");
        var versions = WizardFirmwareList.Items.OfType<WizardBoardFirmwareChoice>().ToArray();
        SmokeCapture.Require(versions.Select(item => item.Board.Id).SequenceEqual(ids) &&
            versions.All(item => item.Description.Contains("Flash") && item.Description.Contains("PSRAM") &&
                item.DisplayName == item.Board.DisplayName && item.Choice.Firmware.BoardIds.Contains(item.Board.Id)),
            "Version cards lost board memory labels, ordering or compatible firmware.");
        WizardNext_Click(WizardNextButton, new RoutedEventArgs());
        SmokeCapture.Require(_wizardStep == 3, "Missing physical variant was accepted.");
        var status = (InfoBar)((FrameworkElement)_ownerWindow.Content).FindName("StatusBar");
        status.IsOpen = false;
        foreach (var version in versions)
        {
            WizardFirmwareList.SelectedItem = version;
            var selection = CaptureWizardState(completed: false);
            SmokeCapture.Require(_wizardBoard?.Id == version.Board.Id &&
                selection.BoardId == version.Board.Id && selection.FirmwareId == version.Choice.Firmware.Id &&
                _wizardFirmware?.Firmware.FlashMethod == FirmwareFlashMethod.EspressifSerial,
                "A variant selection did not retain its concrete board/firmware IDs.");
        }
        WizardFirmwareList.SelectedItem = versions.Single(item => item.Board.Id == "esp32s3-n8r2");
        var saved = CaptureWizardState(completed: false);
        ReloadWizardDeviceChoices(saved.BoardId, saved.ModuleId, saved.FirmwareId);
        SmokeCapture.Require(_wizardDevice?.Id == Esp32DeviceId && _wizardBoard?.Id == saved.BoardId &&
            _wizardFirmware?.Firmware.Id == saved.FirmwareId, "Saved concrete variant did not restore through its family.");
        WizardBack_Click(WizardBackButton, new RoutedEventArgs());
        WizardNext_Click(WizardNextButton, new RoutedEventArgs());
        SmokeCapture.Require(_wizardStep == 3 && _wizardBoard?.Id == saved.BoardId,
            "Back/forward navigation lost the concrete variant.");
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "esp32-version-selection.png");
        WindowSizing.ResizeInDips(_ownerWindow, 620, 900);
        await Task.Delay(80);
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "esp32-version-selection-compact.png");
        ShowWizardStep(2);
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "esp32-device-selection-compact.png");
        ShowWizardStep(3);

        // A package may declare all supported boards but ship only a subset of
        // images. Reload must not substitute its sole image for a saved board.
        var registry = _moduleRegistry;
        var espModule = registry.Find("esp32s3-ns2-bridge")!;
        var onlyFirmware = espModule.Firmware.Single(item => item.BoardIds.Contains("esp32s3-n16r8"));
        var singleton = new ManifestFirmwareModule(new BridgeModulePackageDefinition {
            Id = espModule.Id, Boards = [espModule.Boards.Single(board => board.Id == "esp32s3-n16r8")]
        }, espModule.PackageRoot!);
        var detection = new DeviceDescriptor("offline-esp", "ESP32-S3", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "ESP32-S3", true, false);
        SmokeCapture.Require(GetDetectedWizardBoard(detection, espModule) is null &&
            GetDetectedWizardBoard(detection, singleton) is null,
            "USB output identity was used to guess ESP32 memory, including a singleton package.");
        try
        {
            ShowWizardStep(4);
            WizardFirmwareHashText.Text = "offline previous artifact hash";
            var subset = new ManifestFirmwareModule(new BridgeModulePackageDefinition {
                Id = espModule.Id, ModuleVersion = espModule.ModuleVersion, DisplayName = espModule.DisplayName,
                Boards = espModule.Boards.ToArray(), Firmware = [onlyFirmware]
            }, espModule.PackageRoot!);
            _moduleRegistry = new BridgeFirmwareModuleRegistry(registry.Modules.Select(module =>
                module.Id == espModule.Id ? subset : module));
            ReloadWizardDeviceChoices(saved.BoardId, saved.ModuleId, saved.FirmwareId, Esp32DeviceId);
            SmokeCapture.Require(_wizardDevice?.Id == Esp32DeviceId && WizardFirmwareList.Items.Count == 1 &&
                _wizardBoard is null && _wizardFirmware is null && !WizardFlashButton.IsEnabled &&
                _wizardStep == 3 && WizardFirmwareHashText.Text == "" &&
                WizardArtifactText.Text == "固件文件：-" && WizardFlashStatusText.Text == "尚未选择固件",
                "Subset reload kept a stale flash page/artifact or replaced an unavailable saved variant.");
            WizardNext_Click(WizardNextButton, new RoutedEventArgs());
            SmokeCapture.Require(_wizardStep == 3, "A lost version advanced beyond selection after package reload.");
            status.IsOpen = false;
            WizardFirmwareList.SelectedIndex = 0;
            SmokeCapture.Require(_wizardBoard?.Id == "esp32s3-n16r8",
                "Explicit selection of an available subset image failed.");
        }
        finally
        {
            _moduleRegistry = registry;
            ReloadWizardDeviceChoices(saved.BoardId, saved.ModuleId, saved.FirmwareId);
        }
        ShowWizardStep(4);
        var boardBefore = _wizardBoard;
        var firmwareBefore = _wizardFirmware;
        SetFlashBusy(true);
        try
        {
            SmokeCapture.Require(!WizardBoardList.IsEnabled && !WizardFirmwareList.IsEnabled &&
                !WizardBackButton.IsEnabled && !WizardNextButton.IsEnabled,
                "A device or variant picker remained enabled during flashing.");
            ShowWizardStep(2);
            ReloadWizardDeviceChoices(deviceId: Esp32DeviceId);
            var rejectedInstall = false;
            try { InstallAndReloadModule("offline-must-not-be-opened.cbmodule"); }
            catch (InvalidOperationException) { rejectedInstall = true; }
            SmokeCapture.Require(_wizardStep == 4 && ReferenceEquals(_wizardBoard, boardBefore) &&
                ReferenceEquals(_wizardFirmware, firmwareBefore) && rejectedInstall,
                "Busy navigation or module reload changed the concrete flash target.");
        }
        finally { SetFlashBusy(false); }
        SelectWizardBoardFirmware("esp32s3-n16r8");
    }
}
