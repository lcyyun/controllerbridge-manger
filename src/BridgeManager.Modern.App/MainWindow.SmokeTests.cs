using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    internal async Task RunShellSmokeAsync(string output)
    {
        SmokeCapture.Require(!_discoverDevices, "Shell smoke must not discover/open real devices.");
        Title = "Controller Bridge UI Test - offline fixtures";
        _inputWatchdog.Stop();
        var moduleDirectories = GetModuleDirectories().ToArray();
        SmokeCapture.Require(moduleDirectories.Length is >= 1 and <= 2 &&
            moduleDirectories[0] == Path.Combine(AppContext.BaseDirectory, "modules") &&
            moduleDirectories.All(path => path == moduleDirectories[0] ||
                path == InstalledModuleRoot),
            "Module loading escaped the bundled and verified-update roots.");
        _module = _moduleRegistry.Find("sf32-unified")!;
        InitializeModuleUi();
        await DisconnectAsync();
        ShowManagerUpdate(new ManagerReleaseUpdate("9.9.9", "界面测试更新",
            "https://github.com/lcyyun/controllerbridge-manger/releases/tag/v9.9.9",
            "https://github.com/lcyyun/controllerbridge-manger/releases/download/v9.9.9/test.zip",
            "test.zip", 16 * 1024 * 1024, null));
        SmokeCapture.Require(BuildConnectionText(null, null, null, "connecting") == "尚未读取" &&
            WirelessControllerList.Items.OfType<WirelessControllerItem>().All(item =>
                !item.CanPair && !item.CanConnect && !item.CanDisconnect && !item.CanForget),
            "An unread wireless state was presented as unpaired or actionable.");
        var root = (FrameworkElement)Content;
        SmokeCapture.Require(Grid.GetRow(ManagerUpdateBanner) == 2 &&
            Grid.GetRow(StatusBar) == 3 && Grid.GetRow(PageContentHost) == 1,
            "Update availability and runtime notifications must stay below page content.");
        foreach (var (width, height, suffix) in new[]
        {
            (1640, 940, "maximized"), (1360, 940, "wide"),
            (860, 900, "narrow"), (620, 900, "compact"), (500, 900, "small")
        })
        {
            AppWindow.Resize(new SizeInt32(width, height));
            await Task.Delay(250);
            await SmokeCapture.SaveAsync(root, output, $"sidebar-{suffix}.png");
            SmokeCapture.Require(MainNavigation.PaneHeader is null &&
                MainNavigation.PaneFooter is null &&
                MainNavigation.FooterMenuItems.Count == 2 &&
                MainNavigation.FooterMenuItems.Contains(WizardNavItem) &&
                MainNavigation.FooterMenuItems.Contains(UpdateNavItem),
                "Sidebar branding was not removed or footer actions were lost.");
            var contentWidth = Math.Min(1180, PageContentHost.ActualWidth - 12);
            var singleColumn = contentWidth < 880;
            var compact = contentWidth < 600;
            var stackedUpdate = contentWidth < 760;
            SmokeCapture.Require(
                !singleColumn
                    ? Grid.GetRow(InputDetailPanel) == 0 && Grid.GetColumn(InputDetailPanel) == 1
                    : Grid.GetRow(InputDetailPanel) == 1 && Grid.GetColumn(InputDetailPanel) == 0,
                $"Input layout did not adapt at {width}px.");
            SmokeCapture.Require(
                !singleColumn
                    ? Grid.GetRow(DeviceSettingsPanel) == 0 && Grid.GetColumn(DeviceSettingsPanel) == 1
                    : Grid.GetRow(DeviceSettingsPanel) == 1 && Grid.GetColumn(DeviceSettingsPanel) == 0,
                $"Advanced layout did not adapt at {width}px.");
            SmokeCapture.Require(
                !singleColumn
                    ? Grid.GetRow(FirmwareFlashCard) == 0 && Grid.GetColumn(FirmwareFlashCard) == 1
                    : Grid.GetRow(FirmwareFlashCard) == 1 && Grid.GetColumn(FirmwareFlashCard) == 0,
                $"Firmware layout did not adapt at {width}px.");
            SmokeCapture.Require(
                !compact
                    ? Grid.GetRow(HeaderActions) == 0
                    : Grid.GetRow(HeaderActions) == 1 && Grid.GetRow(UsbMetricPanel) == 1,
                $"Shell layout did not adapt at {width}px.");
            SmokeCapture.Require(ManagerUpdateBanner.Visibility == Visibility.Visible &&
                (!stackedUpdate
                    ? Grid.GetRow(ManagerUpdateActions) == 0 &&
                      Grid.GetColumn(ManagerUpdateActions) == 2
                    : Grid.GetRow(ManagerUpdateActions) == 1 &&
                      Grid.GetColumn(ManagerUpdateActions) == 1),
                $"Manager update banner did not adapt at {width}px.");
            SmokeCapture.Require(width >= 720 ||
                MainNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact,
                $"Compact navigation disappeared at {width}px.");
            SmokeCapture.Require(width < 1640 || Math.Abs(HomePageContent.Width - 1180) < 0.5,
                "Maximized page content did not retain its centered maximum width.");
            SmokeCapture.Require(Grid.GetRow(WirelessSection) == (singleColumn ? 1 : 0) &&
                Grid.GetColumn(WirelessSection) == (singleColumn ? 0 : 1),
                "Dashboard did not adapt to the actual content width.");
            root.RequestedTheme = ElementTheme.Light;
            foreach (var (item, tag) in new[] {
                (HomeNavItem, "home"), (InputNavItem, "input"),
                (FeedbackNavItem, "feedback"), (FirmwareNavItem, "firmware"),
                (AdvancedNavItem, "advanced") })
            {
                MainNavigation.SelectedItem = item;
                ShowPage(tag);
                PageSubtitleText.Text = "界面测试 · 离线数据 · 未连接设备";
                if (tag == "input")
                {
                    await SwitchInputSourceAsync(new InputSourceChoice("接收器 USB 输入"));
                    SmokeCapture.Require(InputNavItem.Visibility == Visibility.Visible,
                        "Controller test navigation hidden without a receiver.");
                    SmokeCapture.Require(InputSourceBox.Items.Count == 1 &&
                        _client is null && _transport is null, "Offline test touched a hardware transport.");
                    await SmokeCapture.SaveAsync(root, output, $"input-empty-{suffix}.png");
                    _selectedLocalDevice = new LocalControllerDevice("fixture", "DualSense · 离线测试样本", "模拟输入");
                    _localInputActive = true;
                    var fixture = new ControllerInputSnapshot("DS5 USB input (local)", 1u,
                        16384, -8192, 0, 0, 32768, 0, 0, 0, 8192, 0, 0, 0, true, 80);
                    UpdateInputDisplay(fixture, local: true);
                    SmokeCapture.Require(ButtonsText.Text.Contains("Cross") &&
                        LeftTriggerBar.Value == 32768 && _status.InputValid != true,
                        "Local input was incorrectly gated on the receiver connection.");
                    SmokeCapture.Require(_controllerInputDiagram?.IsHighlighted(0) == true &&
                        Math.Abs(_controllerInputDiagram.LeftTriggerLevel - 0.5) < 0.001 &&
                        _controllerInputDiagram.LeftStickOffset == new Windows.Foundation.Point(11, 5.5),
                        "Live controller artwork did not follow the displayed input sample.");
                    var previous = InputHealthText.Text;
                    UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        input_valid = false, input_stale = true
                    }));
                    SmokeCapture.Require(InputHealthText.Text == previous,
                        "Receiver status overwrote the local controller display.");
                    var samples = Interlocked.Read(ref _localSamples);
                    LocalInputReceived(_localControllers, fixture with { Buttons = 2u });
                    LocalInputReceived(_localControllers, fixture);
                    await Task.Delay(100);
                    SmokeCapture.Require(ButtonsText.Text.Contains("Cross") &&
                        Interlocked.Read(ref _localSamples) == samples + 2,
                        "Coalesced local input lost the newest state or packet count.");
                }
                else if (tag == "firmware")
                {
                    SmokeCapture.Require(FirmwareSourceBox.SelectedIndex == 0 &&
                        FirmwareSourceBox.Items.Count == 1,
                        "Firmware update source was not GitHub-only by default.");
                    SmokeCapture.Require(FirmwareBoardBox.Items.Count == 0 &&
                        FirmwareReleaseList.ItemsSource is null &&
                        !FirmwareCheckUpdatesButton.IsEnabled,
                        "Disconnected firmware page exposed another receiver's firmware.");
                    SmokeCapture.Require(AssetMatchesModule(new GithubModuleAsset(
                            "bl616-unified-0.2.0.cbmodule", "https://github.com/x/y",
                            "v0.2.0", 1, null), "bl616-unified") &&
                        !AssetMatchesModule(new GithubModuleAsset(
                            "sf32-unified-0.5.0.cbmodule", "https://github.com/x/y",
                            "v0.5.0", 1, null), "bl616-unified"),
                        "Firmware release filtering crossed receiver modules.");
                }
                await SmokeCapture.SaveAsync(root, output, $"{tag}-{suffix}.png");
            }
            MainNavigation.SelectedItem = InputNavItem;
            root.RequestedTheme = ElementTheme.Dark;
            PageSubtitleText.Text = "界面测试 · 离线数据 · 未连接设备";
            await SmokeCapture.SaveAsync(root, output, $"input-dark-{suffix}.png");
        }
        MainNavigation.SelectedItem = HomeNavItem;
        AppWindow.Resize(new SizeInt32(960, 650));
        await SmokeCapture.SaveAsync(root, output, "home-short.png");
        SmokeCapture.Require(HomePage.ScrollableHeight > 0, "Home page cannot scroll at short height.");
        HomePage.ChangeView(null, HomePage.ScrollableHeight, null, disableAnimation: true);
        await SmokeCapture.SaveAsync(root, output, "home-scrolled.png");
        SmokeCapture.Require(HomePage.VerticalOffset > 0, "Home page scroll offset did not change.");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
            await SmokeCapture.SaveAsync(root, output, "home-maximized.png");
            SmokeCapture.Require(PageShell.ActualWidth <= MainNavigation.ActualWidth, "Content exceeded available width.");
            presenter.Restore();
        }
        await RenderMappingWorkspaceAsync();
        SmokeCapture.Require(_dynamicNavigationItems.Count(item =>
            Equals(item.Tag, "module:mapping")) == 1, "Mapping navigation was not consolidated.");
        _mappingInputChoice!.SelectedValue = "ns2pro";
        _mappingOutputChoice!.SelectedValue = "ds5";
        await Task.Delay(80);
        MainNavigation.SelectedItem = _dynamicNavigationItems.Single(item => Equals(item.Tag, "module:mapping"));
        await SmokeCapture.SaveAsync(root, output, "mapping-workspace.png");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter mappingPresenter)
        {
            DynamicModulePage.ChangeView(0, DynamicModulePage.ScrollableHeight, null, disableAnimation: true);
            await Task.Delay(80);
            DynamicModulePage.ChangeView(0, 0, null, disableAnimation: true);
            mappingPresenter.Maximize();
            await SmokeCapture.SaveAsync(root, output, "mapping-maximized.png");
            var contentOrigin = DynamicModulePageContent.TransformToVisual(DynamicModulePage)
                .TransformPoint(new Windows.Foundation.Point());
            SmokeCapture.Require(contentOrigin.X >= -1 &&
                contentOrigin.X + DynamicModulePageContent.ActualWidth <= DynamicModulePage.ActualWidth + 1,
                $"Maximized mapping content clipped: x={contentOrigin.X}, " +
                $"content={DynamicModulePageContent.ActualWidth}, viewport={DynamicModulePage.ActualWidth}, " +
                $"extent={DynamicModulePage.ExtentWidth}, offset={DynamicModulePage.HorizontalOffset}.");
            mappingPresenter.Restore();
            await SmokeCapture.SaveAsync(root, output, "mapping-restored.png");
        }
        var roles = RoleOptionsList.ItemsSource;
        var wireless = WirelessControllerList.ItemsSource;
        for (var iteration = 0; iteration < 100; iteration++)
            UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new { input_valid = false }));
        SmokeCapture.Require(ReferenceEquals(roles, RoleOptionsList.ItemsSource) &&
            ReferenceEquals(wireless, WirelessControllerList.ItemsSource),
            "Unchanged status rebuilt option controls.");
        _localInputActive = false;
        TestFirmwareUpdateControls();
        TestReenumeratedModuleControls();
        await TestLiveControllerDiagramAsync(root, output);
        await CaptureConnectedDashboardAsync(root, output);
        await File.WriteAllTextAsync(Path.Combine(output, "shell-result.txt"),
            "PASS offline input navigation, local input without receiver, source isolation, wide/narrow/light/dark page rendering, OTA button busy/install/error/disconnect recovery, connected dashboard and shared device selection, footer actions without branding, live controller buttons/sticks/analog triggers/release/stale reset");
        AppWindow.Resize(new SizeInt32(1360, 940));
        root.RequestedTheme = ElementTheme.Light;
        OpenCreateReceiver();
        var wizard = _setupWizardView!;
        SmokeCapture.Require(ReferenceEquals(CreateReceiverHost.Child, wizard) &&
            ReferenceEquals(MainNavigation.SelectedItem, WizardNavItem) &&
            CreateReceiverPage.Visibility == Visibility.Visible,
            "Create receiver did not open in the right-hand page.");
        await SmokeCapture.SaveAsync(root, output, "create-receiver-inline.png");
        SmokeCapture.Require(ReferenceEquals(wizard.XamlRoot, MainNavigation.XamlRoot),
            "Create receiver opened in a different window.");
        SmokeCapture.Require(MainNavigation.FooterMenuItems.Contains(WizardNavItem) &&
            !MainNavigation.MenuItems.Contains(WizardNavItem),
            "Create receiver entry was not placed in the bottom-left navigation.");
        await wizard.RunWizardSmokeAsync(output);
        MainNavigation.SelectedItem = HomeNavItem;
        MainNavigation.SelectedItem = WizardNavItem;
        SmokeCapture.Require(ReferenceEquals(wizard, _setupWizardView),
            "Navigation discarded the in-progress create-receiver page.");
    }

    private async Task TestLiveControllerDiagramAsync(FrameworkElement root, string output)
    {
        AppWindow.Resize(new SizeInt32(1360, 1000));
        MainNavigation.SelectedItem = InputNavItem;
        _localInputActive = true;
        try
        {
            foreach (var (source, suffix, extraBits) in new[]
            {
                ("DS5 USB input (local)", "ds5", new[] { 17, 18 }),
                ("NS2 USB input (local)", "ns2pro", new[] { 19, 20, 21, 24 }),
                ("XInput (local)", "xbox", Array.Empty<int>()),
                ("DualSense Edge (local)", "edge", new[] { 17, 18, 20, 21, 22, 23 })
            })
            {
                var neutral = new ControllerInputSnapshot(source, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 8192, 0, 0, 0, true, 80);
                UpdateInputDisplay(neutral, local: true);
                var diagram = _controllerInputDiagram!;
                foreach (var bit in Enumerable.Range(0, 17).Concat(extraBits))
                {
                    UpdateInputDisplay(neutral with { Buttons = 1u << bit }, local: true);
                    SmokeCapture.Require(ReferenceEquals(diagram, _controllerInputDiagram) &&
                        diagram.IsHighlighted(bit), $"{suffix} button {bit} did not highlight.");
                    UpdateInputDisplay(neutral, local: true);
                    SmokeCapture.Require(!diagram.IsHighlighted(bit), $"{suffix} button {bit} remained highlighted after release.");
                }
                var pressed = neutral with
                {
                    Buttons = (1u << 0) | (1u << 4) | (1u << 7) | (1u << 8) | (1u << 14) | (1u << 16),
                    LeftX = -16384, LeftY = 16384, RightX = 8192, RightY = -16384,
                    LeftTrigger = 49151, RightTrigger = 16384
                };
                UpdateInputDisplay(pressed, local: true);
                SmokeCapture.Require(diagram.LeftStickOffset == new Windows.Foundation.Point(-11, -11) &&
                    diagram.RightStickOffset == new Windows.Foundation.Point(5.5, 11) &&
                    Math.Abs(diagram.LeftTriggerLevel - 0.75) < 0.001 &&
                    Math.Abs(diagram.RightTriggerLevel - 0.25) < 0.001 &&
                    diagram.IsHighlighted(10) && diagram.IsHighlighted(11) && !diagram.IsHighlighted(9),
                    $"{suffix} analog motion, trigger depth or simultaneous buttons were incorrect.");
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    root.RequestedTheme = theme;
                    InputPage.ChangeView(0, 0, null, true);
                    await SmokeCapture.SaveAsync(root, output, $"live-input-{suffix}-{theme}.png");
                }
                ClearInputTest("离线测试：停止输入");
                SmokeCapture.Require(diagram.PressedButtons == 0 && !diagram.IsHighlighted(0) &&
                    !diagram.IsHighlighted(10) && diagram.LeftStickOffset == new Windows.Foundation.Point(),
                    $"{suffix} disconnected state retained live highlights.");
                UpdateInputDisplay(pressed, local: true);
                _localInputActive = false;
                _status = new BridgeStatusSnapshot { InputValid = false, InputStale = true };
                UpdateInputDisplay(pressed);
                SmokeCapture.Require(diagram.PressedButtons == 0 && diagram.RightTriggerLevel == 0,
                    $"{suffix} stale receiver input retained live highlights.");
                _localInputActive = true;
            }
        }
        finally
        {
            _localInputActive = false;
            _status = new BridgeStatusSnapshot();
            ClearInputTest("离线图形验证结束");
        }
    }

    private void TestReenumeratedModuleControls()
    {
        var previousModule = _module;
        try
        {
            foreach (var profile in _moduleRegistry.GetHidDeviceProfiles()
                .Where(profile => profile.Key.StartsWith("bl616-")))
            {
                ActivateModule(_moduleRegistry.Fallback);
                var descriptor = new DeviceDescriptor("offline", "unclassified", profile.VendorId,
                    profile.ProductId, DeviceTransportKind.Hid, "generic", true, true,
                    ProfileKey: "ns2pro-nintendo", SerialNumber: profile.ManagerSerialNumber,
                    ManagerFeatureReportId: profile.ManagerFeatureReportId);
                ActivateModule(_moduleRegistry.Resolve(descriptor));
                SmokeCapture.Require(_module.Id == "bl616-unified" &&
                    RoleAndWirelessSection.Visibility == Visibility.Visible &&
                    UsbRoleSection.Visibility == Visibility.Visible && WirelessSection.Visibility == Visibility.Visible &&
                    RoleOptionsList.Items.Count == _module.UsbRoles.Count &&
                    WirelessControllerList.Items.Count == _module.WirelessControllers.Count,
                    "Verified BL616 re-enumeration lost USB roles or wireless controller controls.");
            }
        }
        finally { ActivateModule(previousModule); }
    }

    private async Task CaptureConnectedDashboardAsync(FrameworkElement root, string output)
    {
        var previousModule = _module;
        try
        {
            ActivateModule(_moduleRegistry.Find("bl616-unified")!);
            var transport = new FirmwareControlsSmokeTransport();
            _client = new ManagerCommandClient(transport);
            _lastDescriptor = transport.Descriptor;
            DeviceList.ItemsSource = new[] { transport.Descriptor };
            DeviceList.SelectedItem = transport.Descriptor;
            await Task.Delay(60);
            SmokeCapture.Require(Equals(HomeDeviceBox.SelectedItem, DeviceList.SelectedItem),
                "Dashboard device selection did not follow the shared device selector.");
            HomeDeviceBox.SelectedItem = null;
            await Task.Delay(60);
            SmokeCapture.Require(DeviceList.SelectedItem is null,
                "Dashboard selection did not update the shared device selector.");
            HomeDeviceBox.SelectedItem = transport.Descriptor;
            SetConnectionAppearance(true, "设备已连接");
            ConnectionText.Text = "BL616 已连接";
            TransportText.Text = "USB HID · DualSense / DS5";
            _status = new BridgeStatusSnapshot();
            UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                role = "ds5", active_input = "ns2pro", input_valid = true,
                usb_mounted = true, report_rate_hz = 250, ds5_saved = true,
                ds5_connected = false, ds5_pairing = false, ns2_saved = true,
                ns2_connected = true, ns2_scanning = false
            }));
            RefreshFirmwareTargets();
            SmokeCapture.Require(HomeConnectionOptions.Visibility == Visibility.Collapsed &&
                !HomeConnectionOptions.IsExpanded &&
                InputPreferenceText.Visibility == Visibility.Collapsed &&
                !FirmwareRomExpander.IsExpanded && FirmwareUsbOtaButton.IsEnabled,
                "Connected dashboard did not simplify its connection or update actions.");
            ShowReceiverSelector_Click(new Button(), new RoutedEventArgs());
            SmokeCapture.Require(HomeConnectionOptions.Visibility == Visibility.Visible &&
                HomeConnectionOptions.IsExpanded, "Connected device selector could not be reopened.");
            SetConnectionAppearance(true, "设备已连接");
            ManagerUpdateBanner.Visibility = Visibility.Collapsed;
            StatusBar.IsOpen = false;
            foreach (var (width, height, suffix) in new[]
            {
                (1360, 940, "wide"), (860, 900, "narrow"), (500, 900, "small")
            })
            {
                AppWindow.Resize(new SizeInt32(width, height));
                await Task.Delay(150);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    root.RequestedTheme = theme;
                    MainNavigation.SelectedItem = HomeNavItem;
                    HomePage.ChangeView(0, 0, null, true);
                    await SmokeCapture.SaveAsync(root, output, $"dashboard-connected-{suffix}-{theme}.png");
                    var contentOrigin = HomePageContent.TransformToVisual(HomePage)
                        .TransformPoint(new Windows.Foundation.Point());
                    SmokeCapture.Require(contentOrigin.X >= -1 &&
                        contentOrigin.X + HomePageContent.ActualWidth <= HomePage.ActualWidth + 1,
                        "Dashboard content escaped the viewport.");
                    MainNavigation.SelectedItem = FirmwareNavItem;
                    FirmwarePage.ChangeView(0, 0, null, true);
                    await SmokeCapture.SaveAsync(root, output, $"firmware-connected-{suffix}-{theme}.png");
                }
            }
            NavigateShortcut_Click(new Button { Tag = "input" }, new RoutedEventArgs());
            SmokeCapture.Require(Equals(MainNavigation.SelectedItem, InputNavItem) &&
                InputPage.Visibility == Visibility.Visible, "Dashboard shortcut did not navigate to input tests.");
            ActivateModule(_moduleRegistry.Find("sf32-unified")!);
            RefreshFirmwareTargets();
            SmokeCapture.Require(FirmwareUsbOtaButton.Visibility == Visibility.Collapsed &&
                FirmwareRomExpander.IsExpanded,
                "SF32 update UI exposed BL616 OTA instead of its supported serial path.");
        }
        finally
        {
            _client = null;
            _lastDescriptor = null;
            _status = new BridgeStatusSnapshot();
            DeviceList.ItemsSource = null;
            ActivateModule(previousModule);
            SetConnectionAppearance(false, "未连接");
            RefreshFirmwareTargets();
        }
    }

    private void TestFirmwareUpdateControls()
    {
        var previousModule = _module;
        var previousDescriptor = _lastDescriptor;
        SmokeCapture.Require(_client is null, "Firmware smoke must use only an offline client.");
        try
        {
            _module = _moduleRegistry.Find("bl616-unified")!;
            var transport = new FirmwareControlsSmokeTransport();
            _client = new ManagerCommandClient(transport);
            _lastDescriptor = transport.Descriptor;
            RefreshFirmwareTargets();
            SmokeCapture.Require(FirmwareUsbOtaButton.IsEnabled,
                "Signed bundled BL616 OTA was not available to the connected fixture.");
            for (var iteration = 0; iteration < 2; iteration++)
            {
                SetFirmwareUpdateBusy(true);
                try
                {
                    RefreshFirmwareTargets(); // selection is rebuilt during installation
                    SmokeCapture.Require(!FirmwareUsbOtaButton.IsEnabled &&
                        !FirmwareCheckUpdatesButton.IsEnabled,
                        "Firmware actions remained enabled during package work.");
                    if (iteration == 1) throw new IOException("Simulated package download failure.");
                }
                catch (IOException) when (iteration == 1) { }
                finally { SetFirmwareUpdateBusy(false); }
                SmokeCapture.Require(FirmwareUsbOtaButton.IsEnabled &&
                    FirmwareCheckUpdatesButton.IsEnabled &&
                    !FirmwareUpdateProgress.IsActive,
                    "OTA stayed disabled after package completion without reconnecting.");
            }
            SetFirmwareBusy(true);
            SetFirmwareUpdateBusy(false);
            SmokeCapture.Require(!FirmwareUsbOtaButton.IsEnabled &&
                !FirmwareCheckUpdatesButton.IsEnabled,
                "Package completion re-enabled actions during flashing.");
            SetFirmwareBusy(false);
            SetFirmwareUpdateBusy(true);
            _client = null;
            SetFirmwareUpdateBusy(false);
            SmokeCapture.Require(!FirmwareUsbOtaButton.IsEnabled &&
                !FirmwareCheckUpdatesButton.IsEnabled && !FirmwareInstallUpdateButton.IsEnabled,
                "Disconnected receiver gained update actions after package completion.");
        }
        finally
        {
            _client = null;
            _lastDescriptor = previousDescriptor;
            _module = previousModule;
            SetFirmwareUpdateBusy(false);
            RefreshFirmwareTargets();
        }
    }

    private sealed class FirmwareControlsSmokeTransport : IDeviceTransport
    {
        public DeviceDescriptor Descriptor { get; } = new("offline", "offline", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "bl616-dualsense", true, false, ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
