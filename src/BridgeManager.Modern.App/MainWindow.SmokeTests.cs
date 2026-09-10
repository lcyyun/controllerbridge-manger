using BridgeManager.Core;
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
        SmokeCapture.Require(GetModuleDirectories().Count() == 1 &&
            GetModuleDirectories().Single() == Path.Combine(AppContext.BaseDirectory, "modules"),
            "A source checkout can override the application's bundled modules.");
        _module = _moduleRegistry.Find("sf32-unified")!;
        InitializeModuleUi();
        await DisconnectAsync();
        SmokeCapture.Require(BuildConnectionText(null, null, null, "connecting") == "尚未读取" &&
            WirelessControllerList.Items.OfType<WirelessControllerItem>().All(item =>
                !item.CanPair && !item.CanConnect && !item.CanDisconnect && !item.CanForget),
            "An unread wireless state was presented as unpaired or actionable.");
        var root = (FrameworkElement)Content;
        foreach (var (width, height, suffix) in new[] { (1360, 940, "wide"), (860, 700, "narrow"), (1920, 1080, "large") })
        {
            AppWindow.Resize(new SizeInt32(width, height));
            root.RequestedTheme = ElementTheme.Light;
            foreach (var (item, tag) in new[] {
                (HomeNavItem, "home"), (InputNavItem, "input"),
                (FeedbackNavItem, "feedback"), (AdvancedNavItem, "advanced") })
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
            SmokeCapture.Require(PageFrame.ActualWidth <= 1241, "Maximized content exceeded stable width.");
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
        await File.WriteAllTextAsync(Path.Combine(output, "shell-result.txt"),
            "PASS offline input navigation, local input without receiver, source isolation, wide/narrow/light/dark page rendering");
        var wizard = new SetupWizardWindow(_moduleRegistry, () => new(),
            _ => throw new InvalidOperationException("No writes in wizard smoke."),
            _ => throw new InvalidOperationException("No installs in wizard smoke."),
            () => { }, discoverDevices: false);
        wizard.Activate();
        try { await wizard.RunWizardSmokeAsync(output); }
        finally { wizard.Close(); }
    }
}
