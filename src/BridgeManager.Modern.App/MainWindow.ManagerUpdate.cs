using System.Diagnostics;
using System.Text.Json;
using BridgeManager.Core;
using Microsoft.UI.Xaml;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private readonly ManagerReleaseService _managerReleaseService = new();
    private ManagerReleaseUpdate? _managerReleaseUpdate;
    private bool _managerPreferencesLoaded;

    private void InitializeManagerUpdate()
    {
        var preferences = LoadManagerPreferences();
        ManagerUpdateChecksEnabledCheckBox.IsChecked =
            preferences.ShowManagerUpdates;
        _managerPreferencesLoaded = true;
        ManagerCurrentVersionText.Text =
            $"当前 Manager：{ManagerReleaseService.CurrentVersion}";
        if (_discoverDevices && preferences.ShowManagerUpdates)
            _ = CheckManagerUpdateBannerAsync();
    }

    private async Task CheckManagerUpdateBannerAsync()
    {
        try
        {
            var update = await _managerReleaseService.GetLatestUpdateAsync();
            if (ManagerUpdateChecksEnabledCheckBox.IsChecked != true) return;
            if (update is null)
            {
                ManagerUpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }
            ShowManagerUpdate(update);
        }
        catch (Exception ex)
        {
            AppendLog($"manager update check failed: {ex.Message}");
        }
    }

    private void ShowManagerUpdate(ManagerReleaseUpdate update)
    {
        _managerReleaseUpdate = update;
        ManagerUpdateTitleText.Text = $"ControllerBridge Manager {update.Version} 可用";
        ManagerUpdateDetailText.Text =
            $"{update.Name} · {Math.Max(1, update.AssetSize / 1024 / 1024)} MB";
        ManagerUpdateNeverCheckBox.IsChecked = false;
        ManagerUpdateBanner.Visibility = Visibility.Visible;
    }

    private void ManagerUpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_managerReleaseUpdate is null) return;
        Process.Start(new ProcessStartInfo(_managerReleaseUpdate.DownloadUrl)
        {
            UseShellExecute = true
        });
    }

    private void ManagerUpdateLater_Click(object sender, RoutedEventArgs e) =>
        ManagerUpdateBanner.Visibility = Visibility.Collapsed;

    private async void ManagerUpdateNever_Checked(object sender,
        RoutedEventArgs e)
    {
        if (!_managerPreferencesLoaded) return;
        ManagerUpdateChecksEnabledCheckBox.IsChecked = false;
        ManagerUpdateBanner.Visibility = Visibility.Collapsed;
        await SaveManagerPreferencesAsync(false);
    }

    private async void ManagerUpdateChecksEnabled_Changed(object sender,
        RoutedEventArgs e)
    {
        if (!_managerPreferencesLoaded) return;
        var enabled = ManagerUpdateChecksEnabledCheckBox.IsChecked == true;
        await SaveManagerPreferencesAsync(enabled);
        if (enabled) await CheckManagerUpdateBannerAsync();
        else ManagerUpdateBanner.Visibility = Visibility.Collapsed;
    }

    private static ManagerPreferences LoadManagerPreferences()
    {
        try
        {
            return File.Exists(ManagerPreferencesPath)
                ? JsonSerializer.Deserialize<ManagerPreferences>(
                    File.ReadAllText(ManagerPreferencesPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static async Task SaveManagerPreferencesAsync(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManagerPreferencesPath)!);
        await File.WriteAllTextAsync(ManagerPreferencesPath,
            JsonSerializer.Serialize(new ManagerPreferences
            {
                ShowManagerUpdates = enabled
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ManagerPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControllerBridge", "manager-preferences.json");

    private sealed class ManagerPreferences
    {
        public bool ShowManagerUpdates { get; init; } = true;
    }
}
