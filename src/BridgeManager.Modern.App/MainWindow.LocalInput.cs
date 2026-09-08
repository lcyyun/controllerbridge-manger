using BridgeManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private readonly LocalControllerService _localControllers = new();
    private readonly SemaphoreSlim _inputSwitchLock = new(1, 1);
    private readonly object _localInputLock = new();
    private (int Version, ControllerInputSnapshot Input)? _pendingLocalInput;
    private bool _localInputDispatchQueued;
    private readonly DispatcherTimer _inputWatchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _localRumbleTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _selectingInput;
    private bool _inputClosing;
    private bool _refreshingInput;
    private int _inputSelectionVersion;
    private long _localSamples;
    private long _rateSamples;
    private DateTimeOffset _rateStart = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastLocalInput;
    private string? _artworkProfile;
    private bool _artworkInitialized;
    private bool _localInputActive;
    private string? _localReadError;
    private LocalControllerDevice? _selectedLocalDevice;

    private sealed record InputSourceChoice(string DisplayName, LocalControllerDevice? Device = null);

    private void InitializeIndependentInput()
    {
        _localControllers.InputReceived += LocalInputReceived;
        _localControllers.ReadFailed += LocalInputFailed;
        _inputWatchdog.Tick += (_, _) =>
        {
            if (!_localInputActive || _localReadError is not null) return;
            var now = DateTimeOffset.UtcNow;
            if (_lastLocalInput is null || now - _lastLocalInput > TimeSpan.FromSeconds(2))
            {
                ClearInputTest("等待本机手柄数据");
                return;
            }
            var elapsed = (now - _rateStart).TotalSeconds;
            if (elapsed >= 1)
            {
                var samples = Interlocked.Read(ref _localSamples);
                var unit = _selectedLocalDevice?.Id.StartsWith("hid:", StringComparison.Ordinal) == true
                    ? "包/秒 · HID 实收" : "次/秒 · API 采样";
                InputRateText.Text = $"{(samples - _rateSamples) / elapsed:0} {unit}";
                _rateSamples = samples;
                _rateStart = now;
            }
        };
        _localRumbleTimer.Tick += async (_, _) => await StopLocalRumbleAsync();
        InputSourceBox.ItemsSource = new[] { new InputSourceChoice("接收器 USB 输入") };
        InputSourceBox.SelectedIndex = 0;
        SetControllerArtwork(null);
        _inputWatchdog.Start();
    }

    private async void RefreshInputSources_Click(object sender, RoutedEventArgs e) =>
        await RefreshInputSourcesAsync();

    private async Task RefreshInputSourcesAsync()
    {
        if (_refreshingInput || _inputClosing || !_discoverDevices) return;
        _refreshingInput = true;
        var old = (InputSourceBox.SelectedItem as InputSourceChoice)?.Device?.Id;
        var selectionVersion = _inputSelectionVersion;
        try
        {
            var devices = await _localControllers.GetDevicesAsync(CancellationToken.None);
            if (_inputClosing || selectionVersion != _inputSelectionVersion) return;
            var choices = new[] { new InputSourceChoice("接收器 USB 输入") }
                .Concat(devices.Select(device => new InputSourceChoice(device.DisplayName, device))).ToArray();
            var selected = old is null ? choices[0] : choices.FirstOrDefault(choice => choice.Device?.Id == old);
            _selectingInput = true;
            InputSourceBox.ItemsSource = choices;
            InputSourceBox.SelectedItem = selected;
            _selectingInput = false;
            if (old is not null && selected is null)
            {
                await SwitchInputSourceAsync(null);
                ClearInputTest("所选本机手柄已断开");
            }
            else if (!_localInputActive)
                InputSourceDetailText.Text = devices.Count == 0
                    ? "Windows 当前未枚举到可测试的本机手柄"
                    : $"Windows 检测到 {devices.Count} 个可测试的本机手柄";
        }
        catch (Exception ex)
        {
            InputSourceDetailText.Text = $"手柄枚举失败：{ex.Message}";
        }
        finally
        {
            _selectingInput = false;
            _refreshingInput = false;
        }
    }

    private async void InputSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingInput || _inputClosing) return;
        await SwitchInputSourceAsync(InputSourceBox.SelectedItem as InputSourceChoice);
    }

    private async Task SwitchInputSourceAsync(InputSourceChoice? choice)
    {
        var version = ++_inputSelectionVersion;
        await _inputSwitchLock.WaitAsync();
        try
        {
            if (version != _inputSelectionVersion || _inputClosing) return;
            _localInputActive = false;
            _selectedLocalDevice = null;
            await StopLocalRumbleAsync();
            await _localControllers.StopAsync();
            if (version != _inputSelectionVersion || _inputClosing) return;
            Interlocked.Exchange(ref _localSamples, 0);
            _rateSamples = 0;
            _rateStart = DateTimeOffset.UtcNow;
            _lastLocalInput = null;
            _localReadError = null;
            InputRateText.Text = "";
            SetControllerArtwork(null);
            LocalRumbleButton.IsEnabled = false;
            LocalRumbleStopButton.IsEnabled = false;
            if (choice?.Device is not { } device)
            {
                ClearInputTest(choice is null ? "请选择输入来源"
                    : _client is null ? "接收器未连接，可选择本机手柄" : "等待接收器 USB 输入");
                InputSourceDetailText.Text = choice is null ? "未选择设备" : "数据来源：接收器 USB HID";
                return;
            }
            _selectedLocalDevice = device;
            _localInputActive = true;
            ClearInputTest("正在连接本机手柄");
            InputSourceDetailText.Text = device.TransportLabel;
            await _localControllers.StartAsync(device, CancellationToken.None);
            if (version != _inputSelectionVersion || _inputClosing) return;
            LocalRumbleButton.IsEnabled = device.SupportsRumble;
            LocalRumbleStopButton.IsEnabled = device.SupportsRumble;
        }
        catch (Exception ex)
        {
            _localInputActive = false;
            _selectedLocalDevice = null;
            ClearInputTest($"手柄读取失败：{ex.Message}");
        }
        finally { _inputSwitchLock.Release(); }
    }

    private void LocalInputReceived(object? sender, ControllerInputSnapshot input)
    {
        var version = Volatile.Read(ref _inputSelectionVersion);
        Interlocked.Increment(ref _localSamples);
        lock (_localInputLock)
        {
            _pendingLocalInput = (version, input);
            if (_localInputDispatchQueued) return;
            _localInputDispatchQueued = true;
        }
        // Drain every HID report, but only paint the newest state when the UI is busy.
        if (DispatcherQueue.TryEnqueue(() =>
        {
            (int Version, ControllerInputSnapshot Input)? pending;
            lock (_localInputLock)
            {
                pending = _pendingLocalInput;
                _pendingLocalInput = null;
                _localInputDispatchQueued = false;
            }
            if (pending is not { } current || !_localInputActive || _inputClosing ||
                current.Version != _inputSelectionVersion) return;
            _lastLocalInput = DateTimeOffset.UtcNow;
            _localReadError = null;
            UpdateInputDisplay(current.Input, local: true);
        })) return;
        lock (_localInputLock)
        {
            _pendingLocalInput = null;
            _localInputDispatchQueued = false;
        }
    }

    private void LocalInputFailed(object? sender, string error)
    {
        var version = _inputSelectionVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_localInputActive || _inputClosing || version != _inputSelectionVersion) return;
            _lastLocalInput = null;
            _localReadError = error;
            LocalRumbleButton.IsEnabled = false;
            LocalRumbleStopButton.IsEnabled = false;
            ClearInputTest($"本机手柄读取中断：{error}");
        });
    }

    private async void LocalRumble_Click(object sender, RoutedEventArgs e)
    {
        if (!_localInputActive || _selectedLocalDevice?.SupportsRumble != true) return;
        try
        {
            if (await _localControllers.SetRumbleAsync(0.35, 0.35, CancellationToken.None))
            {
                _localRumbleTimer.Stop();
                _localRumbleTimer.Start();
            }
            else ShowStatus("此手柄当前不支持本机震动测试。", false);
        }
        catch (Exception ex) { ShowStatus($"本机震动失败：{ex.Message}", true); }
    }

    private async void LocalRumbleStop_Click(object sender, RoutedEventArgs e) =>
        await StopLocalRumbleAsync();

    private async Task StopLocalRumbleAsync()
    {
        var wasRunning = _localRumbleTimer.IsEnabled;
        _localRumbleTimer.Stop();
        if (!wasRunning) return;
        try { await _localControllers.SetRumbleAsync(0, 0, CancellationToken.None); }
        catch (Exception ex) { AppendLog($"local rumble stop: {ex.Message}"); }
    }

    private void ClearInputTest(string message)
    {
        InputHealthText.Text = message;
        ButtonsText.Text = "无";
        LeftStickText.Text = "X - / Y -";
        RightStickText.Text = "X - / Y -";
        TriggersText.Text = "L - / R -";
        BatteryText.Text = "电量：未知";
        AccelText.Text = "加速度：无数据";
        GyroText.Text = "陀螺仪：无数据";
        InputRateText.Text = "";
        ResetInputVisuals();
    }

    private void SetControllerArtwork(string? source)
    {
        var profile = source is null ? null
            : source.Contains("DS5", StringComparison.OrdinalIgnoreCase) ||
              source.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ? "ds5"
            : source.Contains("NS2", StringComparison.OrdinalIgnoreCase) ||
              source.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) ? "ns2pro" : null;
        if (_artworkInitialized && profile == _artworkProfile) return;
        _artworkInitialized = true;
        _artworkProfile = profile;
        InputControllerArtworkHost.Children.Clear();
        if (profile is not null)
        {
            InputControllerArtworkHost.Children.Add(new Viewbox
            {
                Child = ControllerArtwork.Create(profile),
                Stretch = Stretch.Uniform,
                MaxWidth = 550,
                MaxHeight = 330
            });
        }
        else
        {
            InputControllerArtworkHost.Children.Add(new FontIcon
            {
                Glyph = "\uE7FC", FontSize = 92, Opacity = 0.22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
    }

    private async Task DisposeIndependentInputAsync()
    {
        _inputClosing = true;
        _inputSelectionVersion++;
        _inputWatchdog.Stop();
        await StopLocalRumbleAsync();
        await _inputSwitchLock.WaitAsync();
        try
        {
            _localControllers.InputReceived -= LocalInputReceived;
            _localControllers.ReadFailed -= LocalInputFailed;
            await _localControllers.DisposeAsync();
        }
        finally { _inputSwitchLock.Release(); }
    }
}
