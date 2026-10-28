using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardView
{
    internal async Task RunWizardSmokeAsync(string output)
    {
        SmokeCapture.Require(!_discoverDevices, "Wizard smoke must not discover hardware.");
        _ownerWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 900));
        await Task.Delay(80);
        SmokeCapture.Require(WizardProgress.Orientation == Orientation.Horizontal &&
            WizardContent.Padding.Left == 18,
            "Creation page did not switch to its compact horizontal-progress layout.");
        ShowWizardStep(1);
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "creation-welcome-narrow.png");
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
        var navigation = (NavigationView)((FrameworkElement)_ownerWindow.Content).FindName("MainNavigation");
        var home = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Tag, "home"));
        var create = navigation.FooterMenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Tag, "create"));
        SmokeCapture.Require(!home.IsEnabled && ReferenceEquals(navigation.SelectedItem, create),
            "Embedded flashing did not lock the main navigation.");
        navigation.SelectedItem = home;
        SmokeCapture.Require(ReferenceEquals(navigation.SelectedItem, create),
            "Programmatic navigation escaped an in-progress receiver flash.");
        ShowWizardStep(5);
        SmokeCapture.Require(_wizardStep == before && !WizardNextButton.IsEnabled &&
            !WizardBoardList.IsEnabled && !WizardFlashButton.IsEnabled && !WizardPortBox.IsEnabled,
            "Flash controls or target could change while busy.");
        SetFlashBusy(false);
        SmokeCapture.Require(home.IsEnabled, "Main navigation did not recover after flashing.");
        SmokeCapture.Require(!new FirmwareFlashService().Check(
                _wizardFirmware!.Module, _wizardFirmware.Firmware, null).Ready,
            "Wizard allowed flashing without a port.");
        await SmokeCapture.SaveAsync((FrameworkElement)Content, output, "firmware-flash.png");
        _ownerWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 940));
        await Task.Delay(80);
        SmokeCapture.Require(WizardContent.Padding.Left == 28,
            "Creation page did not restore its wide layout.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            ((FrameworkElement)_ownerWindow.Content).RequestedTheme = theme;
            foreach (var step in Enumerable.Range(1, 6))
            {
                ShowWizardStep(step);
                await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, $"creation-step-{step}-{theme}.png");
                if (step == 2)
                    SmokeCapture.Require(WizardBoardList.ContainerFromItem(WizardBoardList.SelectedItem)
                        is GridViewItem { BorderThickness.Left: 2 },
                        "Selected board was not outlined after its hidden page became visible.");
                var primaryAction = step == 1 ? WizardStartButton : WizardNextButton;
                SmokeCapture.Require(primaryAction.ActualHeight > 0 &&
                    primaryAction.TransformToVisual(WizardContent).TransformPoint(new()).Y +
                        primaryAction.ActualHeight <= WizardContent.ActualHeight,
                    "Primary action left the visible creation viewport.");
                SmokeCapture.Require(WizardProgress.Children.Count == 6 &&
                    WizardProgress.Children.OfType<Border>().All(dot => dot.Width == dot.Height &&
                        dot.Background is null) &&
                    (step != 1 || (WizardHeader.Visibility == Visibility.Collapsed &&
                        WizardFooterActions.Visibility == Visibility.Collapsed &&
                        WizardWelcomePanel.VerticalAlignment == VerticalAlignment.Center)),
                    "Creation must use hollow circles and a single centered welcome action.");
            }
        }
        ((FrameworkElement)_ownerWindow.Content).RequestedTheme = ElementTheme.Light;
        ShowWizardStep(2);
        var first = (FrameworkElement)WizardBoardList.ContainerFromIndex(0);
        var second = (FrameworkElement)WizardBoardList.ContainerFromIndex(1);
        SmokeCapture.Require(first is not null && second is not null &&
            Math.Abs(first.TransformToVisual(WizardBoardList).TransformPoint(new()).Y -
                second.TransformToVisual(WizardBoardList).TransformPoint(new()).Y) < 2 &&
            second.TransformToVisual(WizardBoardList).TransformPoint(new()).X >
                first.TransformToVisual(WizardBoardList).TransformPoint(new()).X,
            "Wide board picker did not use a two-column card grid.");
        ShowStatus("测试通知：请检查接收器连接。", true);
        var status = (InfoBar)((FrameworkElement)_ownerWindow.Content).FindName("StatusBar");
        SmokeCapture.Require(status.IsOpen && status.Severity == InfoBarSeverity.Error &&
            Grid.GetRow(status) == 3 && status.Parent is Grid { Name: "PageShell" },
            "Creation errors did not use the single bottom notification bar.");
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "creation-bottom-notification.png");
        status.IsOpen = false;
        _ownerWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 640));
        await Task.Delay(100);
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "creation-board-small.png");
        SmokeCapture.Require(WizardScroller.ScrollableHeight > 0 && WizardFooter.ActualHeight > 0,
            "Short creation page did not keep its footer and scrolling content.");
        WizardScroller.ChangeView(null, 0, null, true);
        await Task.Delay(80);
        var selectedBoard = WizardBoardList.SelectedItem;
        var wheelSource = (DependencyObject)WizardBoardList.ContainerFromIndex(0);
        SmokeCapture.Require(PageScrolling.RouteVerticalWheel(WizardScroller, wheelSource, -120, true),
            "Handled wheel over a non-scrolling card presenter did not reach the page.");
        await Task.Delay(100);
        SmokeCapture.Require(WizardScroller.VerticalOffset > 0 &&
            ReferenceEquals(selectedBoard, WizardBoardList.SelectedItem),
            "Wheel did not move the page or changed the selected board.");
        await SmokeCapture.SaveAsync((FrameworkElement)_ownerWindow.Content, output, "creation-card-wheel-scrolled.png");
        SmokeCapture.Require(PageScrolling.RouteVerticalWheel(WizardScroller, wheelSource, 120, false),
            "Upward wheel did not return towards the top.");
        await Task.Delay(80);
        SmokeCapture.Require(WizardScroller.VerticalOffset < 1 &&
            !PageScrolling.RouteVerticalWheel(WizardScroller, wheelSource, 120, true),
            "Wheel escaped the page's top boundary.");
        for (var tick = 0; tick < 8; tick++)
            SmokeCapture.Require(PageScrolling.RouteVerticalWheel(WizardScroller, wheelSource, -15, true),
                "A rapid high-resolution wheel tick was lost.");
        await Task.Delay(100);
        SmokeCapture.Require(Math.Abs(WizardScroller.VerticalOffset - 96) < 2,
            "Continuous wheel input did not accumulate against the pending target.");
        for (var tick = 0; tick < 4; tick++)
            PageScrolling.RouteVerticalWheel(WizardScroller, wheelSource, 15, false);
        await Task.Delay(100);
        SmokeCapture.Require(Math.Abs(WizardScroller.VerticalOffset - 48) < 2,
            "Rapid wheel reversal used stale layout offsets.");
        DetectedDeviceExpander.IsExpanded = true;
        var previousDevices = DetectedDeviceList.ItemsSource;
        try
        {
            DetectedDeviceList.ItemsSource = Enumerable.Range(1, 20).Select(index => new
            { DisplayName = $"Offline device {index}", Detail = "Nested scrolling fixture; no hardware" }).ToArray();
            WizardContent.UpdateLayout();
            await Task.Delay(100);
            var inner = FindScrollViewer(DetectedDeviceList);
            SmokeCapture.Require(inner is { ScrollableHeight: > 0 } &&
                !PageScrolling.RouteVerticalWheel(WizardScroller,
                    (DependencyObject)DetectedDeviceList.ContainerFromIndex(0), -120, true),
                "Page wheel handler stole input from a genuinely scrollable nested device list.");
        }
        finally { DetectedDeviceList.ItemsSource = previousDevices; }
        WizardBoardList.SelectedItem = _moduleRegistry.GetBoards().Single(item => item.Id == "esp32s3-n16r8");
        SmokeCapture.Require(!_wizardFirmware!.Artifact.Available && !WizardFlashButton.IsEnabled,
            "ESP32 management metadata advertised a nonexistent bundled image.");
        await File.WriteAllTextAsync(Path.Combine(output, "wizard-result.txt"),
            "PASS centered welcome and board text, hollow step rings, two-column cards, responsive footer, card wheel down/up/boundaries without changing selection, nested list ownership, single bottom notification, board-specific firmware, honest missing artifacts, no guessed COM, busy target and main-navigation lock, no device writes");
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }
}
