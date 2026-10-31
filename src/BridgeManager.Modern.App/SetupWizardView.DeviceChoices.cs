using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;

namespace BridgeManager.Modern;

// Presentation groups do not become board IDs in manifests or saved settings.
public sealed record WizardDeviceChoice(string Id, string DisplayName, string Family,
    string Description, IReadOnlyList<BridgeBoardDefinition> Boards)
{
    public bool ContainsBoard(string? id) => Boards.Any(board =>
        board.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record WizardBoardFirmwareChoice(BridgeBoardDefinition Board,
    WizardFirmwareChoice Choice, bool ShowBoardDetails)
{
    public string DisplayName => ShowBoardDetails ? Board.DisplayName : Choice.DisplayName;
    public string Description => ShowBoardDetails ? Board.Description : Choice.Description;
    public string Version => Choice.Version;
    public string Availability => Choice.Availability;
}

public sealed partial class SetupWizardView
{
    private const string Esp32DeviceId = "esp32s3-family";
    private WizardDeviceChoice? _wizardDevice;

    private BridgeBoardDefinition? GetDetectedWizardBoard(DeviceDescriptor descriptor,
        IBridgeFirmwareModule module) =>
        descriptor.TransportKind == DeviceTransportKind.Hid &&
        module.Id != _moduleRegistry.Fallback.Id &&
        // An ESP USB identity identifies its output role, not Flash/PSRAM.
        module.Id != "esp32s3-ns2-bridge" && module.Boards.Count == 1
            ? module.Boards[0] : null;

    private IReadOnlyList<WizardDeviceChoice> GetWizardDeviceChoices()
    {
        var boards = _moduleRegistry.GetBoards();
        var espIds = _moduleRegistry.Find("esp32s3-ns2-bridge")?.Boards
            .Select(board => board.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var espBoards = boards.Where(board => espIds.Contains(board.Id))
            .OrderBy(board => board.Id switch {
                "esp32s3-n4" => 0, "esp32s3-n8" => 1, "esp32s3-n8r2" => 2,
                "esp32s3-n8r8" => 3, "esp32s3-n16r8" => 4,
                "esp32s3-esp32supermini" => 5, _ => 6 })
            .ThenBy(board => board.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var result = new List<WizardDeviceChoice>();
        var converter = new CreationBoardDescriptionConverter();
        foreach (var board in boards)
        {
            if (espIds.Contains(board.Id))
            {
                if (!result.Any(item => item.Id == Esp32DeviceId))
                    result.Add(new(Esp32DeviceId, "ESP32-S3", board.Family,
                        "下一步选择板型版本与 Flash / PSRAM 容量", espBoards));
            }
            else
                result.Add(new(board.Id, board.DisplayName, board.Family,
                    (string)converter.Convert(board, typeof(string), null!, ""), [board]));
        }
        return result;
    }

    private void ReloadWizardDeviceChoices(string? boardId = null,
        string? moduleId = null, string? firmwareId = null, string? deviceId = null)
    {
        if (_flashBusy) return;
        var devices = GetWizardDeviceChoices();
        WizardBoardList.ItemsSource = devices;
        var device = devices.FirstOrDefault(item => item.ContainsBoard(boardId)) ??
            devices.FirstOrDefault(item => item.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault(item => moduleId == "esp32s3-ns2-bridge" && item.Id == Esp32DeviceId);
        WizardBoardList.SelectedItem = device;
        if (device is not null && boardId is not null)
            SelectWizardBoardFirmware(boardId, moduleId, firmwareId);
        if (_wizardStep >= 3 && _wizardDevice is null)
            ShowWizardStep(2);
        else if (_wizardStep >= 4 && (_wizardBoard is null || _wizardFirmware is null))
            ShowWizardStep(3);
    }

    private void SelectWizardBoardFirmware(string boardId,
        string? moduleId = null, string? firmwareId = null)
    {
        var choices = WizardFirmwareList.Items.OfType<WizardBoardFirmwareChoice>()
            .Where(item => item.Board.Id.Equals(boardId, StringComparison.OrdinalIgnoreCase) &&
                (moduleId is null || item.Choice.Module.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase)) &&
                (firmwareId is null || item.Choice.Firmware.Id.Equals(firmwareId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        // Ambiguous/missing saved or detected matches require a fresh selection.
        WizardFirmwareList.SelectedItem = choices.Length == 1 ? choices[0] : null;
    }
}
