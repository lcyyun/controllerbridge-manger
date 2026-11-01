using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
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
        _managerInputTimer.Stop();
        var moduleDirectories = GetModuleDirectories().ToArray();
        SmokeCapture.Require(moduleDirectories.Length is >= 1 and <= 2 &&
            moduleDirectories[0] == Path.Combine(AppContext.BaseDirectory, "modules") &&
            moduleDirectories.All(path => path == moduleDirectories[0] ||
                path == InstalledModuleRoot),
            "Module loading escaped the bundled and verified-update roots.");
        _module = _moduleRegistry.Find("sf32-unified")!;
        InitializeModuleUi();
        await DisconnectAsync();
        MainNavigation.SelectedItem = _dynamicNavigationItems.Single(item => Equals(item.Tag, "module:mapping"));
        ActivateModule(_moduleRegistry.Find("bl616-unified")!);
        ActivateModule(_moduleRegistry.Find("sf32-unified")!);
        MainNavigation.SelectedItem = InputNavItem;
        await Task.Delay(80);
        SmokeCapture.Require(InputPage.Visibility == Visibility.Visible && HomePage.Visibility == Visibility.Collapsed,
            "Rebuilding module navigation broke subsequent page selection.");
        MainNavigation.SelectedItem = HomeNavItem;
        ShowManagerUpdate(new ManagerReleaseUpdate("9.9.9", "界面测试更新",
            "https://github.com/lcyyun/controllerbridge-manger/releases/tag/v9.9.9",
            "https://github.com/lcyyun/controllerbridge-manger/releases/download/v9.9.9/test.zip",
            "test.zip", 16 * 1024 * 1024, null));
        SmokeCapture.Require(BuildConnectionText(null, null, null, "connecting") == "尚未读取" &&
            WirelessControllerList.Items.OfType<WirelessControllerItem>().All(item =>
                !item.CanPair && !item.CanConnect && !item.CanDisconnect && !item.CanForget),
            "An unread wireless state was presented as unpaired or actionable.");
        var root = (FrameworkElement)Content;
        await VerifyDpiSmokeAsync(output);
        SmokeCapture.Require(Grid.GetRow(ManagerUpdateBanner) == 2 &&
            Grid.GetRow(StatusBar) == 3 && Grid.GetRow(PageContentHost) == 1,
            "Update availability and runtime notifications must stay below page content.");
        foreach (var (width, height, suffix) in new[]
        {
            (1640, 940, "maximized"), (1360, 940, "wide"),
            (860, 900, "narrow"), (620, 900, "compact"), (500, 900, "small")
        })
        {
            WindowSizing.ResizeInDips(this, width, height);
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
                await Task.Delay(80);
                var visiblePage = tag switch
                {
                    "input" => InputPage, "feedback" => FeedbackPage,
                    "firmware" => FirmwarePage, "advanced" => AdvancedPage,
                    _ => HomePage
                };
                SmokeCapture.Require(visiblePage.Visibility == Visibility.Visible &&
                    new FrameworkElement[] { HomePage, InputPage, FeedbackPage, FirmwarePage, AdvancedPage,
                        CreateReceiverPage, DynamicModulePage }
                        .Count(page => page.Visibility == Visibility.Visible) == 1,
                    $"Navigation did not display only {tag} at {width}px.");
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
                        _controllerInputDiagram.LeftStickOffset == new Windows.Foundation.Point(9, 4.5),
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
        WindowSizing.ResizeInDips(this, 960, 650);
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
        TestPendingUsbRoleSwitch();
        await TestEspManagerContractsAsync(root, output);
        await TestLiveControllerDiagramAsync(root, output);
        await TestPhysicalInputFallbackAsync(root, output);
        await CaptureConnectedDashboardAsync(root, output);
        await File.WriteAllTextAsync(Path.Combine(output, "shell-result.txt"),
            "PASS offline input navigation, local input without receiver, source isolation, wide/narrow/light/dark page rendering, OTA button busy/install/error/disconnect recovery, pending USB role ACK target preservation, ESP reset completion/error/deadline/reboot/generation isolation, ESP stop preserves feedback configuration, nullable audio state and role-aware audio selftest, module-specific tuning ranges, connected dashboard and shared device selection, footer actions without branding, live controller buttons/sticks/analog triggers/release/stale reset, XInput physical input fallback, duplicate snapshot expiry and USB timeout clearing");
        WindowSizing.ResizeInDips(this, 1360, 940);
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
        WindowSizing.ResizeInDips(this, 1360, 1000);
        MainNavigation.SelectedItem = InputNavItem;
        await Task.Delay(80);
        SmokeCapture.Require(InputPage.Visibility == Visibility.Visible &&
            HomePage.Visibility == Visibility.Collapsed,
            "Input selection highlighted the sidebar without displaying the input page.");
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
                SmokeCapture.Require(diagram.LeftStickOffset == new Windows.Foundation.Point(-9, -9) &&
                    diagram.RightStickOffset == new Windows.Foundation.Point(4.5, 9) &&
                    Math.Abs(diagram.LeftTriggerLevel - 0.75) < 0.001 &&
                    Math.Abs(diagram.RightTriggerLevel - 0.25) < 0.001 &&
                    diagram.IsHighlighted(10) && diagram.IsHighlighted(11) && !diagram.IsHighlighted(9),
                    $"{suffix} analog motion, trigger depth or simultaneous buttons were incorrect.");
                UpdateInputDisplay(pressed with { LeftX = 32767, LeftY = 32767 }, local: true);
                SmokeCapture.Require(Math.Abs(Math.Sqrt(Math.Pow(diagram.LeftStickOffset.X, 2) +
                    Math.Pow(diagram.LeftStickOffset.Y, 2)) - 18) < 0.001,
                    $"{suffix} diagonal stick motion escaped the circular gate.");
                UpdateInputDisplay(pressed, local: true);
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

    private async Task TestPhysicalInputFallbackAsync(FrameworkElement root, string output)
    {
        SmokeCapture.Require(_client is null && _transport is null, "Physical input fixture must be offline.");
        var previousDescriptor = _lastDescriptor;
        _sourceTelemetry.Reset();
        try
        {
            _transport = new FirmwareControlsSmokeTransport();
            _client = new ManagerCommandClient(_transport);
            _lastDescriptor = _transport.Descriptor with { UsbRole = BridgeUsbRole.Xbox };
            _status = new BridgeStatusSnapshot();
            MainNavigation.SelectedItem = InputNavItem;
            await Task.Delay(60);
            var snapshot = System.Text.Json.JsonSerializer.SerializeToElement(new {
                profile = "bridge_input", source = "ns2pro", valid = true, stale = false, snapshot_ok = true,
                buttons = (1u << 24) | 1u, left_x = 16384, left_y = -16384, right_x = 0, right_y = 0,
                left_trigger = 32768, right_trigger = 0, motion_valid = false,
                timestamp_valid = true, sensor_timestamp = 1u, battery_valid = false, battery_percent = 100
            });
            UpdateSummary(snapshot);
            var diagram = _controllerInputDiagram!;
            SmokeCapture.Require(diagram.IsHighlighted(24) && diagram.IsHighlighted(0) &&
                Math.Abs(diagram.LeftTriggerLevel - 0.5) < 0.001 && BatteryText.Text == "电量：未知" &&
                InputRateText.Text.Contains("管理快照刷新") && ActualReportRateText.Text == "—",
                "XInput management snapshot did not paint physical extended buttons/axes or invented telemetry.");
            await SmokeCapture.SaveAsync(root, output, "physical-input-xinput.png");
            _lastManagerInputAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
            UpdateSummary(snapshot);
            CheckReceiverInputFreshness();
            SmokeCapture.Require(diagram.PressedButtons == 0 && diagram.LeftTriggerLevel == 0 && ButtonsText.Text == "无",
                "Repeated physical snapshot extended stale input highlights.");

            _transport = new FirmwareControlsSmokeTransport(inputReports: true);
            _client = new ManagerCommandClient(_transport);
            _lastUsbInputAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
            var pressed = new ControllerInputSnapshot("NS2 USB input", 1, 10000, 0, 0, 0, 65535, 0,
                0, 0, 0, 0, 0, 0, false, 100);
            UpdateInputDisplay(pressed);
            CheckReceiverInputFreshness();
            SmokeCapture.Require(_controllerInputDiagram!.PressedButtons == 0 && LeftTriggerBar.Value == 0,
                "USB timeout kept receiver buttons or analog triggers pressed.");
            _localInputActive = true;
            UpdateInputDisplay(pressed, local: true);
            CheckReceiverInputFreshness();
            SmokeCapture.Require(_controllerInputDiagram.PressedButtons != 0,
                "Receiver timeout cleared the independently selected local controller.");
            _localInputActive = false;
            await CloseTransportAsync(clearSummary: false);
            SmokeCapture.Require(_controllerInputDiagram.PressedButtons == 0 && LeftTriggerBar.Value == 0,
                "Reconnect teardown retained the previous receiver state.");
        }
        finally
        {
            _localInputActive = false;
            _client = null;
            _transport = null;
            _lastDescriptor = previousDescriptor;
            _status = new BridgeStatusSnapshot();
            _sourceTelemetry.Reset();
            _lastManagerInput = null;
            _lastManagerInputAt = _lastUsbInputAt = null;
            ClearInputTest("物理输入离线验证结束");
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

    private void TestPendingUsbRoleSwitch()
    {
        var previousStatus = _status;
        var previousExpected = _expectedRole;
        try
        {
            foreach (var oldRole in BridgeRoles.UsbRoles)
            foreach (var requested in BridgeRoles.UsbRoles.Where(role => role != oldRole))
            {
                _status = new BridgeStatusSnapshot { UsbRole = oldRole };
                _expectedRole = requested;
                _roleSwitchInProgress = true;
                // Actual ESP role-set ACK reports the current and pending modes;
                // an already queued status command can then report the old role.
                UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                    ok = true, profile = "settings", output_mode = oldRole.CommandValue(),
                    pending_output_mode = requested.CommandValue()
                }));
                UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                    ok = true, role = oldRole.CommandValue()
                }));
                var previous = new FirmwareControlsSmokeTransport().Descriptor with { UsbRole = oldRole };
                var next = previous with { Id = "offline-new-role", UsbRole = requested };
                SmokeCapture.Require(_expectedRole == requested &&
                    DeviceReconnectSelector.Select([previous, next], previous, _expectedRole) == next,
                    "A deferred ACK or old status changed the requested USB reconnect target.");
                _roleSwitchInProgress = false;
                UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                    ok = true, role = requested.CommandValue()
                }));
                SmokeCapture.Require(_expectedRole == requested && _status.UsbRole == requested,
                    "Verified post-switch status was not adopted.");
            }
        }
        finally
        {
            _roleSwitchInProgress = false;
            _status = previousStatus;
            _expectedRole = previousExpected;
        }
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
            // USB-personality battery is never a physical source reading.
            UpdateHomeTelemetry();
            SmokeCapture.Require(ActualReportRateText.Text == "—" && HomeBatteryText.Text == "—",
                "Legacy USB target rate or emulated battery was presented as real telemetry.");
            UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                active_input = "ns2pro", input_valid = true, input_stale = false,
                input_rate_supported = true, unique_input_reports = 1000,
                input_rate_time_ms = 1000, input_battery_percent = -1
            }));
            UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                active_input = "ns2pro", input_valid = true, input_stale = false,
                input_rate_supported = true, unique_input_reports = 1125,
                input_rate_time_ms = 2000, input_battery_percent = -1
            }));
            SmokeCapture.Require(ActualReportRateText.Text == "125 Hz" && HomeBatteryText.Text == "—",
                "Source-frame rate or unknown battery telemetry was not rendered correctly.");
            // Polling the same cached response must not count it again.
            UpdateSummary(System.Text.Json.JsonSerializer.SerializeToElement(new {
                active_input = "ns2pro", input_valid = true, input_stale = false,
                input_rate_supported = true, unique_input_reports = 1125,
                input_rate_time_ms = 2000, input_battery_percent = 80
            }));
            SmokeCapture.Require(ActualReportRateText.Text == "125 Hz" && HomeBatteryText.Text == "80%",
                "Repeated source counters inflated the rate, or explicit battery was ignored.");
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
                WindowSizing.ResizeInDips(this, width, height);
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
            foreach (var (moduleId, method) in new[] {
                ("sf32-unified", FirmwareFlashMethod.SifliSerial),
                ("pico-unified-bridge", FirmwareFlashMethod.PicoUf2),
                ("esp32s3-ns2-bridge", FirmwareFlashMethod.EspressifSerial) })
            {
                _module = _moduleRegistry.Find(moduleId)!;
                RefreshFirmwareTargets();
                SmokeCapture.Require(FirmwareBoardBox.Items.Count > 0 &&
                    _firmwareSelection?.Module.Id == moduleId &&
                    _firmwareSelection.Firmware.FlashMethod == method &&
                    FirmwareCheckUpdatesButton.IsEnabled &&
                    FirmwareUsbOtaButton.Visibility == Visibility.Collapsed &&
                    FirmwarePortPanel.Visibility == (method == FirmwareFlashMethod.PicoUf2
                        ? Visibility.Collapsed : Visibility.Visible),
                    $"Firmware update/recovery controls excluded or confused {moduleId}.");
            }
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

    private async Task TestEspManagerContractsAsync(FrameworkElement root, string output)
    {
        SmokeCapture.Require(!_discoverDevices && _client is null && _transport is null,
            "ESP contract tests must be entirely offline.");
        var previousModule = _module;
        var previousStatus = _status;
        var previousExpected = _expectedRole;
        var previousDescriptor = _lastDescriptor;
        var previousReconnect = _autoReconnectEnabled;
        var esp = _moduleRegistry.Find("esp32s3-ns2-bridge")!;
        EspContractSmokeTransport UseTransport(Func<string, string> reply,
            BridgeUsbRole role = BridgeUsbRole.DualSense, bool? enabled = null)
        {
            var transport = new EspContractSmokeTransport(reply, role);
            _transport = transport;
            _client = new ManagerCommandClient(transport);
            _lastDescriptor = transport.Descriptor;
            _status = new BridgeStatusSnapshot { UsbRole = role, RumbleEnabled = enabled };
            _expectedRole = role;
            return transport;
        }
        try
        {
            _autoReconnectEnabled = false;
            ActivateModule(esp);
            SmokeCapture.Require(ScaleBox.Minimum == 0 && ScaleBox.Maximum == 100 &&
                HoldBox.Maximum == 2000 && TickBox.Minimum == 4 && StopsBox.Maximum == 10,
                "ESP settings allow values outside the actual firmware limits.");
            foreach (var id in new[] { "bl616-unified", "sf32-unified", "pico-unified-bridge" })
            {
                ActivateModule(_moduleRegistry.Find(id)!);
                SmokeCapture.Require(ScaleBox.Minimum == 5 && ScaleBox.Maximum == 250 &&
                    HoldBox.Maximum == 3000 && TickBox.Minimum == 5 && StopsBox.Maximum == 12,
                    $"ESP tuning limits leaked into {id}.");
            }
            ActivateModule(esp);
            _status = new BridgeStatusSnapshot();
            UpdateSummary(JsonSerializer.SerializeToElement(new {
                ok = true, role = "dualsense", audio_out_packets = 99, audio_in_packets = 0,
                audio_mic_open = false, audio_haptic_blocks = 7, audio_ns2_updates = 6,
                audio_ns2_stops = 1, audio_ns2_mixed_ticks = 20
            }));
            SmokeCapture.Require(AudioStatusText.Text.Contains("扬声器未知") &&
                AudioStatusText.Text.Contains("麦克风未打开") && AudioStatusText.Text.Contains("（未知）"),
                "Absent ESP audio booleans were rendered as false or inferred from cumulative counters.");
            MainNavigation.SelectedItem = FeedbackNavItem;
            await SmokeCapture.SaveAsync(root, output, "esp-audio-unknown.png");
            foreach (var role in BridgeRoles.UsbRoles)
            {
                UseTransport(_ => JsonSerializer.Serialize(new {
                    ok = true, role = role.CommandValue(), audio_out_packets = 0, audio_in_packets = 0,
                    audio_haptic_blocks = 0, audio_ns2_updates = 0, audio_ns2_stops = 0, audio_ns2_mixed_ticks = 0
                }), role);
                await RunSelfTestAsync();
                _pollTimer.Stop();
                var sony = role is BridgeUsbRole.DualSense or BridgeUsbRole.DualSenseEdge;
                SmokeCapture.Require(TestResultBox.Text.Contains("DS5 UAC 诊断字段") == sony &&
                    TestResultBox.Text.Contains("播放声音后音频 OUT 计数增长") == sony,
                    $"Audio selftest guidance does not match {role} USB capability.");
            }
            foreach (var (original, configured, stopEnabled, expected) in new[]
            {
                ((bool?)true, true, false, "rumble stop,rumble on"),
                ((bool?)false, false, false, "rumble stop"),
                ((bool?)null, true, false, "rumble config,rumble stop,rumble on"),
                ((bool?)null, false, false, "rumble config,rumble stop"),
                ((bool?)true, true, true, "rumble stop")
            })
            {
                var transport = UseTransport(command => JsonSerializer.Serialize(new {
                    ok = true, rumble_enabled = command == "rumble config" ? configured :
                        command == "rumble on" || stopEnabled
                }), enabled: original);
                await SendModuleRumbleAsync("stop");
                SmokeCapture.Require(string.Join(",", transport.Commands) == expected &&
                    _status.RumbleEnabled == (original ?? configured),
                    "Stopping ESP rumble changed the original feedback setting or sent an unnecessary enable.");
            }
            var rejectedStop = UseTransport(_ => "{\"ok\":false,\"rumble_enabled\":false}", enabled: true);
            await SendModuleRumbleAsync("stop");
            SmokeCapture.Require(rejectedStop.Commands.SequenceEqual(["rumble stop"]),
                "Rejected ESP stop must not issue rumble on.");

            foreach (var role in BridgeRoles.UsbRoles)
            {
                var polls = 0;
                var transport = UseTransport(command => command == "settings reset"
                    ? "{\"ok\":true,\"reset_pending\":true}"
                    : JsonSerializer.Serialize(new { ok = true, reset_pending = ++polls < 2,
                        reset_error = 0, role = role.CommandValue() }), role);
                var recovered = false;
                var result = await ResetEspSettingsAsync("settings reset", previous =>
                {
                    var next = previous with { Id = "offline-reset-nintendo", UsbRole = BridgeUsbRole.NintendoNs2Pro };
                    SmokeCapture.Require(polls == 2 && _roleSwitchInProgress &&
                        _expectedRole == BridgeUsbRole.NintendoNs2Pro &&
                        DeviceReconnectSelector.Select([next], previous, _expectedRole) == next,
                        "Reset recovered before completion or lost its Nintendo target to an old status.");
                    recovered = true;
                    return Task.FromResult(true);
                }, pollDelay: TimeSpan.FromMilliseconds(1));
                SmokeCapture.Require(recovered && result.GetProperty("reconnected").GetBoolean() &&
                    !_roleSwitchInProgress && transport.Commands.SequenceEqual([
                        "settings reset", "settings status", "settings status", "settings status"]),
                    "ESP reset did not wait for completion exactly once before recovery.");
            }
            foreach (var failure in new[] { "rejected", "firmware-error", "missing-fields", "command-timeout", "deadline", "generation", "generation-between-polls" })
            {
                var failureTransport = UseTransport(command =>
                {
                    if (command == "settings reset")
                    {
                        if (failure == "generation-between-polls") DispatcherQueue.TryEnqueue(() =>
                        {
                            Interlocked.Increment(ref _transportGeneration);
                            _expectedRole = BridgeUsbRole.Xbox;
                        });
                        return failure == "rejected"
                            ? "{\"ok\":false,\"error\":\"busy\"}"
                            : "{\"ok\":true,\"reset_pending\":true}";
                    }
                    if (failure == "generation")
                    {
                        Interlocked.Increment(ref _transportGeneration);
                        _expectedRole = BridgeUsbRole.Xbox;
                    }
                    if (failure == "command-timeout") throw new TimeoutException("Offline fixture: command timeout is not a reboot.");
                    return failure switch
                    {
                        "firmware-error" => "{\"ok\":true,\"reset_pending\":false,\"reset_error\":263}",
                        "missing-fields" => "{\"ok\":true,\"reset_pending\":false}",
                        _ => "{\"ok\":true,\"reset_pending\":true,\"reset_error\":0}"
                    };
                });
                var recovered = false;
                Exception? error = null;
                try
                {
                    await ResetEspSettingsAsync("settings reset", _ =>
                    {
                        recovered = true;
                        return Task.FromResult(true);
                    }, waitTimeout: TimeSpan.FromMilliseconds(80), pollDelay: TimeSpan.FromMilliseconds(1));
                }
                catch (Exception ex) { error = ex; }
                SmokeCapture.Require(error is not null && !recovered && !_roleSwitchInProgress &&
                    _expectedRole == (failure.StartsWith("generation", StringComparison.Ordinal) ? BridgeUsbRole.Xbox : BridgeUsbRole.DualSense) &&
                    (failure != "generation-between-polls" || failureTransport.Commands.SequenceEqual(["settings reset"])) &&
                    (failure != "deadline" || error is TimeoutException),
                    $"ESP reset failure {failure} was ignored or restored/reconnected the wrong transport.");
            }
            var disconnect = UseTransport(command => command == "settings reset"
                ? "{\"ok\":true,\"reset_pending\":true}"
                : throw new IOException("Offline fixture: accepted reset detached USB."));
            var afterDisconnect = false;
            await ResetEspSettingsAsync("settings reset", _ =>
            {
                afterDisconnect = _expectedRole == BridgeUsbRole.NintendoNs2Pro && _roleSwitchInProgress;
                UseTransport(_ => "{\"ok\":true,\"reset_pending\":false,\"reset_error\":0}", BridgeUsbRole.NintendoNs2Pro);
                Interlocked.Increment(ref _transportGeneration);
                return Task.FromResult(true);
            }, pollDelay: TimeSpan.FromMilliseconds(1));
            SmokeCapture.Require(afterDisconnect && disconnect.Commands.Count == 2,
                "Accepted reset disconnect did not recover using the protected Nintendo target.");
            foreach (var response in new[] {
                "{\"ok\":true,\"reset_pending\":true,\"reset_error\":0}",
                "{\"ok\":true,\"reset_pending\":false,\"reset_error\":263}",
                "{\"ok\":true,\"reset_pending\":false}" })
            {
                UseTransport(command => command == "settings reset"
                    ? "{\"ok\":true,\"reset_pending\":true}"
                    : throw new IOException("Offline fixture: transient I/O while Nintendo reset pending."),
                    BridgeUsbRole.NintendoNs2Pro);
                var confirmed = false;
                EspContractSmokeTransport? reopened = null;
                try
                {
                    await ResetEspSettingsAsync("settings reset", _ =>
                    {
                        reopened = UseTransport(_ => response, BridgeUsbRole.NintendoNs2Pro);
                        Interlocked.Increment(ref _transportGeneration);
                        return Task.FromResult(true);
                    }, pollDelay: TimeSpan.FromMilliseconds(1));
                    confirmed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { }
                SmokeCapture.Require(!confirmed && !_roleSwitchInProgress && reopened is not null &&
                    reopened.Commands.SequenceEqual(["settings status"]),
                    "Reopening Nintendo after I/O claimed reset success without a valid completed settings reply.");
            }
            UseTransport(command => command == "settings reset"
                ? "{\"ok\":true,\"reset_pending\":true}"
                : "{\"ok\":true,\"reset_pending\":false,\"reset_error\":0}");
            var recoveryFailed = false;
            try
            {
                await ResetEspSettingsAsync("settings reset", _ => Task.FromResult(false),
                    pollDelay: TimeSpan.FromMilliseconds(1));
            }
            catch (IOException) { recoveryFailed = true; }
            SmokeCapture.Require(recoveryFailed && !_roleSwitchInProgress,
                "Failed recovery after reset was incorrectly reported as successful.");
            await File.WriteAllTextAsync(Path.Combine(output, "esp-contract-result.txt"),
                "PASS NSM1 fake transport only: four-role reset completion, old-status target freeze, rejection/error/malformed/deadline/generation isolation, accepted-reset detach, stop enable preservation including unknown state and unchanged new firmware, unknown audio state, four-role audio selftest and tuning-limit isolation. Hardware not tested.");
        }
        finally
        {
            _pollTimer.Stop();
            _roleSwitchInProgress = false;
            _client = null;
            _transport = null;
            _lastDescriptor = previousDescriptor;
            _status = previousStatus;
            _expectedRole = previousExpected;
            _autoReconnectEnabled = previousReconnect;
            ActivateModule(previousModule);
        }
    }

    private sealed class EspContractSmokeTransport(Func<string, string> reply, BridgeUsbRole role) : IDeviceTransport
    {
        private readonly List<byte> _command = [];
        private readonly Queue<byte[]> _replies = new();
        public List<string> Commands { get; } = [];
        public DeviceDescriptor Descriptor { get; } = new("offline-esp", "ESP offline contract fixture",
            0x303a, 0x4002, DeviceTransportKind.Hid, "esp32s3", true, false,
            UsbRole: role, ManagerFeatureReportId: 0x7e) { ManagerWireProtocol = BridgeManagerWireProtocol.Nsm1 };
        public Task OpenAsync(CancellationToken token) => throw new InvalidOperationException("No hardware in UI smoke.");
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) =>
            throw new InvalidOperationException("NSM1 fixtures use Feature reports only.");
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SmokeCapture.Require(id == 0x7e && payload.Length == NsmFeatureProtocol.PayloadLength,
                "ESP UI command used the wrong Feature report contract.");
            var packet = payload.Span;
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(packet[18..]);
            if (offset == 0) _command.Clear();
            _command.AddRange(packet.Slice(NsmFeatureProtocol.HeaderLength, packet[20]).ToArray());
            if ((packet[7] & 1) != 0) return Task.CompletedTask;
            var command = Encoding.UTF8.GetString(_command.ToArray());
            Commands.Add(command);
            var json = reply(command);
            _replies.Clear();
            foreach (var chunk in NsmFeatureProtocol.BuildCommand(json, BinaryPrimitives.ReadUInt32LittleEndian(packet[8..])))
            {
                chunk[6] = 1;
                var report = new byte[chunk.Length + 1];
                report[0] = id;
                chunk.CopyTo(report, 1);
                _replies.Enqueue(report);
            }
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_replies.Dequeue());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FirmwareControlsSmokeTransport(bool inputReports = false) : IDeviceTransport
    {
        public DeviceDescriptor Descriptor { get; } = new("offline", "offline", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "bl616-dualsense", true, false, SupportsInputReports: inputReports,
            ManagerFeatureReportId: 0xf6);
        public Task OpenAsync(CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token) => throw new InvalidOperationException("No USB in UI smoke.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
